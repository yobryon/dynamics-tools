using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Xpp.Service.Bridge;
using Xpp.Service.Storage;

namespace Xpp.Service.Indexing;

/// <summary>
/// Walks the AOT via the bridge and populates the cache database. Phase 1
/// only today — models + objects inventory. Methods and structural refs
/// come in the next pass.
///
/// Progress is reported through an IProgress&lt;IndexProgressEvent&gt; so
/// the calling gRPC handler can stream updates to clients without coupling
/// the indexer to gRPC types.
///
/// Cancellation is honored at every bridge call boundary; abandoned runs
/// leave the cache in a consistent (just incomplete) state because each
/// model is committed atomically.
/// </summary>
public sealed class Indexer
{
    private readonly BridgeClient _bridge;
    private readonly BridgePool _pool;
    private readonly IndexWriter _writer;
    private readonly ILogger<Indexer> _logger;
    private readonly IReadOnlyList<string> _labelLanguages;
    private readonly string _packagesDir;

    public Indexer(BridgeClient bridge, BridgePool pool, IndexWriter writer, ILogger<Indexer> logger, IndexerOptions? options = null)
    {
        _bridge = bridge;
        _pool = pool;
        _writer = writer;
        _logger = logger;
        _labelLanguages = options?.LabelLanguages ?? new[] { "en-US" };
        _packagesDir = options?.PackagesLocalDirectory ?? string.Empty;
    }

    public async Task<IndexRunSummary> RunPhase1Async(
        IProgress<IndexProgressEvent>? progress,
        CancellationToken ct,
        IReadOnlyCollection<string>? modelsFilter = null,
        bool pruneDeleted = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(new IndexProgressEvent("starting", "", "", 0, 0, false, "phase 1: inventory"));

        // ---- 1. discover models ----------------------------------------
        var allModels = await _bridge.ListModelsAsync(ct).ConfigureAwait(false);
        var models = allModels;
        if (modelsFilter != null && modelsFilter.Count > 0)
        {
            // NOCASE so the filter mirrors X++'s case-insensitive name semantics.
            var allowed = new HashSet<string>(modelsFilter, StringComparer.OrdinalIgnoreCase);
            models = allModels.Where(m => allowed.Contains(m.Name)).ToList();
        }
        _logger.LogInformation("Indexer discovered {All} models, indexing {Selected}", allModels.Count, models.Count);

        await _writer.EnqueueAsync(conn => UpsertModels(conn, models), ct).ConfigureAwait(false);
        progress?.Report(new IndexProgressEvent("models", "", "", models.Count, 0, false, $"{models.Count} models upserted"));

        // ---- 2. discover types -----------------------------------------
        var types = await _bridge.ListKnownTypesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Indexer will enumerate {Count} AxTypes per model", types.Count);

        // ---- 3. walk objects -------------------------------------------
        // Lever 5: fan out across models. Each (model, axType) listObjects
        // call is independent and the bridge pool has N workers — running
        // models serially leaves N-1 workers idle the whole phase. We
        // process up to pool-size models concurrently; the inner type
        // loop stays serial inside each worker so any one model's writer
        // batch lands atomically (one UpsertObjects per model, same as
        // before). Counters are Interlocked; progress events arrive in
        // completion order, not enumeration order, which is fine for a
        // streaming UI.
        // Drive parallelism to the pool's MAX, not its current size: a rebuild
        // is the burst the dynamic pool exists for, so fan out to Max and let
        // the scaler ramp the live worker count up to meet it (it starts at Min).
        long totalObjects = 0;
        long totalPruned = 0;
        var phase1Parallelism = Math.Max(1, _pool.Max);
        var phase1Opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = phase1Parallelism,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(models, phase1Opts, async (model, workerCt) =>
        {
            var modelObjects = new List<(string AxType, string Name, string Source)>();
            var succeededTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var axType in types)
            {
                workerCt.ThrowIfCancellationRequested();
                try
                {
                    var entries = await _bridge.ListObjectsAsync(model.Name, axType, workerCt).ConfigureAwait(false);
                    foreach (var e in entries) modelObjects.Add((axType, e.Name, e.Source));
                    // Enumeration succeeded (even if it returned nothing) — this
                    // (model, type) is authoritative and safe to prune against.
                    succeededTypes.Add(axType);
                }
                catch (BridgeRpcException ex)
                {
                    // A type this model doesn't support, or a metadata
                    // reader that can't enumerate it. Log and move on; we
                    // don't want a single bad (model, type) pair to abort
                    // the whole phase. NOT added to succeededTypes, so a
                    // transient enumeration failure can never prune that
                    // type's objects.
                    _logger.LogWarning("listObjects({Model}, {Type}) failed: {Code} {Msg}",
                        model.Name, axType, ex.Code, ex.Message);
                }
            }

            if (modelObjects.Count > 0)
            {
                await _writer.EnqueueAsync(conn => UpsertObjects(conn, model.Name, modelObjects), workerCt).ConfigureAwait(false);
                Interlocked.Add(ref totalObjects, modelObjects.Count);
            }

            // Reconcile deletions: within each successfully-enumerated (model,
            // type), remove index rows whose name the bridge no longer returns
            // (an object deleted since the last index — e.g. a feature flight a
            // platform update retired). Authoritative: driven by ListObjects,
            // not by disk-file absence. Gated on pruneDeleted so only the
            // startup reconcile prunes; a plain sweep never does.
            if (pruneDeleted && succeededTypes.Count > 0)
            {
                var namesByType = modelObjects
                    .GroupBy(o => o.AxType, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(o => o.Name).ToArray(), StringComparer.Ordinal);
                var removed = await _writer.EnqueueAsync(
                    conn => PruneDeletedObjects(conn, model.Name, namesByType, succeededTypes), workerCt)
                    .ConfigureAwait(false);
                if (removed > 0)
                {
                    Interlocked.Add(ref totalPruned, removed);
                    _logger.LogInformation("Reconcile pruned {N} deleted object(s) from {Model}", removed, model.Name);
                }
            }

            progress?.Report(new IndexProgressEvent(
                "objects", model.Name, "", Interlocked.Read(ref totalObjects), 0, false,
                $"{model.Name}: {modelObjects.Count} objects ({Interlocked.Read(ref totalObjects)} total)"));
        }).ConfigureAwait(false);

        // ---- 4. finalize index_state -----------------------------------
        await _writer.EnqueueAsync(conn => UpdateIndexState(conn), ct).ConfigureAwait(false);

        sw.Stop();
        var summary = new IndexRunSummary(models.Count, totalObjects, 0, 0, sw.Elapsed);
        progress?.Report(new IndexProgressEvent(
            "phase1-complete", "", "", totalObjects, 0, false,
            $"phase 1 done: {totalObjects} objects across {models.Count} models in {sw.Elapsed.TotalSeconds:F1}s"));

        _logger.LogInformation("Indexer phase 1 complete: {Models} models, {Objects} objects, {Pruned} pruned in {Seconds}s",
            models.Count, totalObjects, totalPruned, sw.Elapsed.TotalSeconds);
        return summary;
    }

    /// <summary>
    /// Delete index rows for a model that the bridge no longer enumerates.
    /// Scoped to the types we successfully listed this pass (a failed
    /// enumeration leaves that type untouched). For each such type, the current
    /// name set is passed as JSON and anything in the index NOT in it is removed
    /// — cascading to methods/refs/labels/embeddings via the schema's ON DELETE
    /// CASCADE. An empty name set for a successfully-enumerated type correctly
    /// prunes every row of that type for the model (they're genuinely gone).
    /// Returns the number of object rows removed.
    /// </summary>
    private static int PruneDeletedObjects(
        SqliteConnection conn, string model,
        IReadOnlyDictionary<string, string[]> namesByType, IReadOnlyCollection<string> succeededTypes)
    {
        using var tx = conn.BeginTransaction();
        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText =
            "DELETE FROM objects WHERE model = $m AND ax_type = $t " +
            "AND name NOT IN (SELECT value FROM json_each($names));";
        var pM = del.Parameters.Add("$m",     SqliteType.Text);
        var pT = del.Parameters.Add("$t",     SqliteType.Text);
        var pN = del.Parameters.Add("$names", SqliteType.Text);
        pM.Value = model;

        var removed = 0;
        foreach (var axType in succeededTypes)
        {
            var names = namesByType.TryGetValue(axType, out var arr) ? arr : Array.Empty<string>();
            pT.Value = axType;
            pN.Value = System.Text.Json.JsonSerializer.Serialize(names);
            removed += del.ExecuteNonQuery();
        }
        tx.Commit();
        return removed;
    }

    /// <summary>
    /// Startup reconcile (Layer A): for every indexed object, compare its
    /// on-disk content file to what we last indexed and, when it genuinely
    /// changed, reset last_phase2_at to 0 so the incremental Phase 2 re-reads
    /// it. This is what catches an MS platform update (LCS, while we were down)
    /// or a TFS GET LATEST that mutated already-indexed objects — the plain
    /// incremental sweep only picks up never-visited rows.
    ///
    /// Signal: file mtime is a cheap gate (stat only), confirmed by a SHA-256
    /// of the file bytes stored in objects.content_hash. Only files whose mtime
    /// moved past their last_phase2_at are read+hashed, so a quiet startup does
    /// almost no work. A file that moved but hashes identically (a re-extract)
    /// is NOT re-read — we just refresh its hash/marker. Objects whose model
    /// isn't on disk (binary/runtime-only) or whose file we can't locate are
    /// left untouched and counted, never thrashed.
    /// </summary>
    public async Task InvalidateChangedObjectsAsync(IProgress<IndexProgressEvent>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_packagesDir) || !Directory.Exists(_packagesDir))
        {
            _logger.LogInformation("Reconcile skipped: PackagesLocalDirectory '{Dir}' not available", _packagesDir);
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(new IndexProgressEvent("reconcile-starting", "", "", 0, 0, false,
            "reconcile: detecting changed objects on disk"));

        // Snapshot the inventory up front so we stat/hash files outside the
        // writer lock, then apply invalidations in batches.
        var rows = await _writer.EnqueueAsync(conn =>
        {
            var list = new List<ReconcileRow>();
            using var cmd = conn.CreateCommand();
            // Only disk-backed objects can be reconciled against a file.
            // Runtime-only objects (source='runtime', compiled provider) have no
            // XML on disk; they change only with their binary module's version,
            // which disk hashing can't observe, so we exclude them here rather
            // than count them all as "missing".
            cmd.CommandText =
                "SELECT id, model, ax_type, name, last_phase2_at, content_hash " +
                "FROM objects WHERE source = 'disk';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ReconcileRow(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5)));
            }
            return list;
        }, ct).ConfigureAwait(false);

        var knownModels = new HashSet<string>(rows.Select(r => r.Model), StringComparer.OrdinalIgnoreCase);
        var roots = DiskReconciler.BuildModelRoots(_packagesDir, knownModels);
        _logger.LogInformation("Reconcile: {Objects} objects, {Models} models, {Mapped} model roots resolved on disk",
            rows.Count, knownModels.Count, roots.Count);

        long hashed = 0, changed = 0, touchedIdentical = 0, unresolved = 0, missing = 0;
        var updates = new List<(long Id, long Phase2, string Hash)>();

        async Task FlushAsync()
        {
            if (updates.Count == 0) return;
            var batch = updates.ToArray();
            updates.Clear();
            await _writer.EnqueueAsync(conn =>
            {
                using var tx = conn.BeginTransaction();
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE objects SET last_phase2_at = $p2, content_hash = $h WHERE id = $id;";
                var pP2 = upd.Parameters.Add("$p2", SqliteType.Integer);
                var pH  = upd.Parameters.Add("$h",  SqliteType.Text);
                var pId = upd.Parameters.Add("$id", SqliteType.Integer);
                foreach (var (id, p2, hash) in batch)
                {
                    pP2.Value = p2; pH.Value = hash; pId.Value = id;
                    upd.ExecuteNonQuery();
                }
                tx.Commit();
                return true;
            }, ct).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            var path = DiskReconciler.ContentFilePath(roots, r.Model, r.AxType, r.Name);
            if (path == null) { unresolved++; continue; }          // binary/runtime-only model

            FileInfo fi;
            try { fi = new FileInfo(path); if (!fi.Exists) { missing++; continue; } }
            catch { missing++; continue; }

            var mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            if (mtime <= r.LastPhase2At) continue;                 // untouched since last index — stat only

            string hash;
            try { hash = DiskReconciler.HashFile(path); hashed++; }
            catch { missing++; continue; }

            if (r.LastPhase2At != 0
                && !string.IsNullOrEmpty(r.ContentHash)
                && string.Equals(hash, r.ContentHash, StringComparison.Ordinal))
            {
                // Touched but identical (a re-extract). Don't re-read; just
                // refresh the marker so we stop re-checking it every startup.
                // The last_phase2_at != 0 guard is essential: an object flagged
                // by a PRIOR (interrupted) reconcile has content_hash set to the
                // new file hash but its methods not yet re-read (last_phase2_at
                // still 0). Without the guard we'd see hash == content_hash and
                // wrongly un-flag it, stranding stale content. A not-yet-
                // processed object always falls through to the changed branch
                // below and stays flagged until Phase 2 actually re-reads it.
                touchedIdentical++;
                updates.Add((r.Id, now, hash));
            }
            else
            {
                // Real change (or no baseline hash yet). Invalidate so the
                // incremental Phase 2 re-reads it; record the new baseline.
                changed++;
                updates.Add((r.Id, 0, hash));
            }
            if (updates.Count >= 5000) await FlushAsync().ConfigureAwait(false);
        }
        await FlushAsync().ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "Reconcile done in {Sec:F1}s: {Changed} changed, {Same} touched-identical, {Hashed} hashed, " +
            "{Unresolved} off-disk (binary model), {Missing} file-gone (likely deleted; pruned by the deletion pass)",
            sw.Elapsed.TotalSeconds, changed, touchedIdentical, hashed, unresolved, missing);
        progress?.Report(new IndexProgressEvent("reconcile-complete", "", "", changed, rows.Count, false,
            $"reconcile: {changed} changed objects flagged for re-index ({touchedIdentical} touched-identical skipped)"));
    }

    private readonly record struct ReconcileRow(
        long Id, string Model, string AxType, string Name, long LastPhase2At, string ContentHash);

    /// <summary>
    /// Phase 2: for each object already in the inventory, fetch its methods
    /// and structural references from the bridge and persist them. Triggers
    /// keep methods_fts in sync automatically.
    ///
    /// The walk is bounded by <paramref name="maxObjectsPhase2"/> when &gt; 0
    /// so smoke tests and partial rebuilds don't have to grind through the
    /// full 165k+ object corpus. Per-object writes are atomic via a
    /// per-object transaction (DELETE existing children + INSERT new) so a
    /// cancelled run leaves each object's data consistent.
    /// </summary>
    public async Task<IndexRunSummary> RunPhase2Async(
        IProgress<IndexProgressEvent>? progress,
        CancellationToken ct,
        IReadOnlyCollection<string>? modelsFilter = null,
        int maxObjectsPhase2 = 0,
        bool incremental = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(new IndexProgressEvent("phase2-starting", "", "", 0, 0, false,
            $"phase 2: methods + refs{(incremental ? " (incremental)" : "")}"));

        // ---- 1. enumerate target objects from the cache ----------------
        var targets = await _writer.EnqueueAsync(conn => LoadPhase2Targets(conn, modelsFilter, maxObjectsPhase2, incremental), ct).ConfigureAwait(false);
        _logger.LogInformation("Phase 2 will process {Count} objects (cap={Cap}, incremental={Inc})",
            targets.Count, maxObjectsPhase2, incremental);

        // Lever 4c: detect a fresh insert. If no methods exist at all yet,
        // every per-object DELETE we'd issue is a no-op and we can skip
        // them. Cheap query (SELECT count(*)>0 short-circuits with the
        // first row in the methods table).
        var freshInsert = await _writer.EnqueueAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM methods LIMIT 1);";
            return Convert.ToInt64(cmd.ExecuteScalar()) == 0;
        }, ct).ConfigureAwait(false);
        if (freshInsert) _logger.LogInformation("Phase 2 starting in fresh-insert mode (no DELETEs)");

        // Lever 6: drop FTS triggers during fresh bulk load. Each method
        // INSERT fires the methods_ai trigger which inserts into
        // methods_fts, and FTS5 maintenance per-row dominates commit cost
        // on this workload (~120ms/commit observed before this change).
        // We bulk-load methods with triggers disabled, then rebuild the
        // FTS index in one pass via the external-content table's own
        // 'rebuild' command. For non-fresh runs we keep triggers so
        // mutations stay in sync.
        if (freshInsert)
        {
            await _writer.EnqueueAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                // Lever 7: relax durability + suspend WAL autocheckpoint
                // during the bulk load. synchronous=OFF skips fsyncs
                // entirely on the writer's commits; wal_autocheckpoint=0
                // prevents SQLite from blocking a commit to flush WAL to
                // the main DB mid-load. Both get restored after the
                // bulk load finishes. Safe because a crash mid-rebuild
                // is recoverable by re-running the rebuild — the cache
                // is regenerated content, not user data.
                //
                // Lever 6: drop FTS triggers during fresh bulk load so
                // each method INSERT doesn't fire methods_ai. We rebuild
                // the FTS index in one pass at the end.
                cmd.CommandText = @"
                    PRAGMA synchronous = OFF;
                    PRAGMA wal_autocheckpoint = 0;
                    DROP TRIGGER IF EXISTS methods_ai;
                    DROP TRIGGER IF EXISTS methods_ad;
                    DROP TRIGGER IF EXISTS methods_au;
                    DROP TRIGGER IF EXISTS labels_ai;
                    DROP TRIGGER IF EXISTS labels_ad;
                    DROP TRIGGER IF EXISTS labels_au;
                ";
                cmd.ExecuteNonQuery();
            }, ct).ConfigureAwait(false);
            _logger.LogInformation("Phase 2: FTS triggers dropped, sync=OFF, autocheckpoint=0 for bulk load");
        }

        long processed   = 0;
        long methodsSeen = 0;
        long refsSeen    = 0;
        long labelsSeen  = 0;
        long failed      = 0;
        var stride = Math.Max(1, targets.Count / 25); // ~25 progress events per run

        // Bridge workers fan out in parallel; results land in a channel
        // that a single drainer task batches into one writer transaction
        // per ~50 objects.
        //
        // Why: lever 2 measured only ~17% gain from parallelising bridge
        // calls. The reason is the writer — one fsync per per-object
        // commit at ~5ms × 20k = ~100s of serial work no amount of
        // bridge parallelism can compress. Batching 50 objects into one
        // transaction amortises that to one fsync per ~50 objects, so
        // the writer cost drops from O(N) commits to O(N/batch).
        //
        // The channel is bounded — if the writer falls behind the
        // batcher applies backpressure on the bridge workers rather
        // than queueing unbounded result objects in memory. Capacity
        // is comfortably more than one batch so workers don't stall
        // between flushes.
        const int BatchSize = 200;
        var resultChannel = Channel.CreateBounded<Phase2Result>(new BoundedChannelOptions(BatchSize * 4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var drainer = Task.Run(async () =>
        {
            var batch = new List<Phase2Result>(BatchSize);
            await foreach (var r in resultChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                batch.Add(r);
                if (batch.Count >= BatchSize)
                {
                    var toFlush = batch;
                    batch = new List<Phase2Result>(BatchSize);
                    await _writer.EnqueueAsync(conn => UpsertObjectChildrenBatch(conn, toFlush, freshInsert), ct).ConfigureAwait(false);
                }
            }
            if (batch.Count > 0)
            {
                await _writer.EnqueueAsync(conn => UpsertObjectChildrenBatch(conn, batch, freshInsert), ct).ConfigureAwait(false);
            }
        }, ct);

        // Chunk targets so each parallel worker fetches N objects per
        // bridge RPC via getObjectsFull instead of one per RPC. This
        // amortises pipe/JSON overhead across the batch and keeps the
        // bridge's provider hot for runs of objects in the same model
        // (the targets list is ordered by model, ax_type, name).
        //
        // Bridge batch size and the writer batch size are decoupled —
        // bridge batches stream into the result channel one item at a
        // time so the drainer still flushes in writer-friendly chunks.
        const int BridgeBatchSize = 25;
        var chunks = new List<List<Phase2Target>>((targets.Count / BridgeBatchSize) + 1);
        for (int i = 0; i < targets.Count; i += BridgeBatchSize)
        {
            var slice = new List<Phase2Target>(BridgeBatchSize);
            for (int j = i; j < Math.Min(i + BridgeBatchSize, targets.Count); j++) slice.Add(targets[j]);
            chunks.Add(slice);
        }

        var parallelism = Math.Max(1, _pool.Max);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(chunks, parallelOptions, async (chunk, workerCt) =>
        {
            IReadOnlyList<BridgeObjectsFullItem> batch;
            try
            {
                var requests = chunk.Select(t => (t.Model, t.AxType, t.Name)).ToList();
                batch = await _bridge.GetObjectsFullAsync(requests, _labelLanguages, workerCt).ConfigureAwait(false);
            }
            catch (BridgeRpcException ex)
            {
                Interlocked.Add(ref failed, chunk.Count);
                _logger.LogWarning("Phase 2 bridge batch failed (chunk of {N}): {Code} {Msg}",
                    chunk.Count, ex.Code, ex.Message);
                Interlocked.Add(ref processed, chunk.Count);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Add(ref failed, chunk.Count);
                _logger.LogWarning(ex, "Phase 2 unexpected error on bridge batch of {N}", chunk.Count);
                Interlocked.Add(ref processed, chunk.Count);
                return;
            }

            // Items return in input order so we can zip 1:1 with the chunk.
            // If counts diverge we still match what we can and count the
            // rest as failures.
            var pairCount = Math.Min(chunk.Count, batch.Count);
            for (int i = 0; i < pairCount; i++)
            {
                var t = chunk[i];
                var item = batch[i];
                if (item.Error != null)
                {
                    Interlocked.Increment(ref failed);
                }
                else
                {
                    var methodsRaw = item.Methods ?? (IReadOnlyList<BridgeMethodInfo>)Array.Empty<BridgeMethodInfo>();
                    var refs       = item.References ?? (IReadOnlyList<BridgeReferenceEdge>)Array.Empty<BridgeReferenceEdge>();
                    var fieldRefs  = item.FieldReferences ?? (IReadOnlyList<BridgeFieldReferenceEdge>)Array.Empty<BridgeFieldReferenceEdge>();
                    var labelRefs  = item.LabelReferences ?? (IReadOnlyList<BridgeLabelReferenceEdge>)Array.Empty<BridgeLabelReferenceEdge>();
                    var labels     = item.Labels ?? (IReadOnlyList<BridgeLabelEntry>)Array.Empty<BridgeLabelEntry>();
                    var prepared   = PrepareMethodRows(methodsRaw);
                    await resultChannel.Writer.WriteAsync(new Phase2Result(t.Id, prepared, refs, fieldRefs, labelRefs, labels), workerCt).ConfigureAwait(false);
                    Interlocked.Add(ref methodsSeen, prepared.Count);
                    Interlocked.Add(ref refsSeen,    refs.Count + fieldRefs.Count + labelRefs.Count);
                    Interlocked.Add(ref labelsSeen,  labels.Count);
                }
                var n = Interlocked.Increment(ref processed);
                if (n % stride == 0 || n == targets.Count)
                {
                    progress?.Report(new IndexProgressEvent(
                        "objects-detail", t.Model, t.AxType, n, targets.Count, false,
                        $"phase 2: {n}/{targets.Count} objects ({Interlocked.Read(ref methodsSeen)} methods, {Interlocked.Read(ref refsSeen)} refs, {Interlocked.Read(ref labelsSeen)} labels, {Interlocked.Read(ref failed)} skipped)"));
                }
            }
            if (batch.Count < chunk.Count)
            {
                var missing = chunk.Count - batch.Count;
                Interlocked.Add(ref failed, missing);
                Interlocked.Add(ref processed, missing);
            }
        }).ConfigureAwait(false);

        // All producers are done; signal the drainer to flush its tail and exit.
        resultChannel.Writer.Complete();
        await drainer.ConfigureAwait(false);

        // Lever 6 (cont.): rebuild FTS in a single pass, then recreate
        // the triggers so future incremental mutations stay in sync.
        // 'rebuild' walks methods.id/source_code and reconstructs the
        // index; one fsync amortises across the whole rebuild instead
        // of ~60k per-row trigger invocations during INSERT.
        if (freshInsert)
        {
            progress?.Report(new IndexProgressEvent("fts-rebuild", "", "", processed, targets.Count, false,
                "rebuilding methods_fts"));
            await _writer.EnqueueAsync(conn =>
            {
                using var cmd = conn.CreateCommand();
                // Rebuild FTS, recreate triggers, then restore durability
                // and force a checkpoint so the WAL doesn't carry a giant
                // pending state into normal operation.
                cmd.CommandText = @"
                    INSERT INTO methods_fts(methods_fts) VALUES ('rebuild');
                    INSERT INTO labels_fts(labels_fts)   VALUES ('rebuild');
                    CREATE TRIGGER methods_ai AFTER INSERT ON methods BEGIN
                        INSERT INTO methods_fts(rowid, source_code) VALUES (new.id, new.source_code);
                    END;
                    CREATE TRIGGER methods_ad AFTER DELETE ON methods BEGIN
                        INSERT INTO methods_fts(methods_fts, rowid, source_code) VALUES ('delete', old.id, old.source_code);
                    END;
                    CREATE TRIGGER methods_au AFTER UPDATE ON methods BEGIN
                        INSERT INTO methods_fts(methods_fts, rowid, source_code) VALUES ('delete', old.id, old.source_code);
                        INSERT INTO methods_fts(rowid, source_code)              VALUES (new.id, new.source_code);
                    END;
                    CREATE TRIGGER labels_ai AFTER INSERT ON labels BEGIN
                        INSERT INTO labels_fts(rowid, value) VALUES (new.id, new.value);
                    END;
                    CREATE TRIGGER labels_ad AFTER DELETE ON labels BEGIN
                        INSERT INTO labels_fts(labels_fts, rowid, value) VALUES ('delete', old.id, old.value);
                    END;
                    CREATE TRIGGER labels_au AFTER UPDATE ON labels BEGIN
                        INSERT INTO labels_fts(labels_fts, rowid, value) VALUES ('delete', old.id, old.value);
                        INSERT INTO labels_fts(rowid, value)             VALUES (new.id, new.value);
                    END;
                    PRAGMA synchronous = NORMAL;
                    PRAGMA wal_autocheckpoint = 1000;
                    PRAGMA wal_checkpoint(TRUNCATE);
                ";
                cmd.ExecuteNonQuery();
            }, ct).ConfigureAwait(false);
            _logger.LogInformation("Phase 2: FTS rebuilt, triggers restored, durability restored, WAL checkpointed");
        }

        await _writer.EnqueueAsync(conn => UpdateIndexState(conn), ct).ConfigureAwait(false);

        sw.Stop();
        progress?.Report(new IndexProgressEvent(
            "complete", "", "", processed, targets.Count, true,
            $"phase 2 done: {methodsSeen} methods + {refsSeen} refs + {labelsSeen} labels across {processed} objects in {sw.Elapsed.TotalSeconds:F1}s ({failed} skipped)"));

        _logger.LogInformation("Phase 2 complete: {Processed} objects, {Methods} methods, {Refs} refs, {Labels} labels in {Seconds}s ({Failed} failed)",
            processed, methodsSeen, refsSeen, labelsSeen, sw.Elapsed.TotalSeconds, failed);
        return new IndexRunSummary(0, processed, methodsSeen, refsSeen, sw.Elapsed);
    }

    // -------------------------------------------------------------------
    // Write helpers — run on the IndexWriter thread, hold the write
    // connection exclusively, do all their inserts in one transaction so
    // a partial failure leaves the model atomically un-updated rather
    // than half-applied.
    // -------------------------------------------------------------------

    private static void UpsertModels(SqliteConnection conn, IReadOnlyList<BridgeModel> models)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO models
                (name, display_name, publisher, version, layer, is_custom, is_binary, dependencies_json, last_indexed)
            VALUES
                ($name, $display, $publisher, $version, $layer, $isCustom, $isBinary, $deps, $lastIndexed)
            ON CONFLICT(name) DO UPDATE SET
                display_name      = excluded.display_name,
                publisher         = excluded.publisher,
                version           = excluded.version,
                layer             = excluded.layer,
                is_custom         = excluded.is_custom,
                is_binary         = excluded.is_binary,
                dependencies_json = excluded.dependencies_json,
                last_indexed      = excluded.last_indexed;
        ";
        var pName    = cmd.Parameters.Add("$name",        Microsoft.Data.Sqlite.SqliteType.Text);
        var pDisplay = cmd.Parameters.Add("$display",     Microsoft.Data.Sqlite.SqliteType.Text);
        var pPub     = cmd.Parameters.Add("$publisher",   Microsoft.Data.Sqlite.SqliteType.Text);
        var pVer     = cmd.Parameters.Add("$version",     Microsoft.Data.Sqlite.SqliteType.Text);
        var pLayer   = cmd.Parameters.Add("$layer",       Microsoft.Data.Sqlite.SqliteType.Text);
        var pCustom  = cmd.Parameters.Add("$isCustom",    Microsoft.Data.Sqlite.SqliteType.Integer);
        var pBinary  = cmd.Parameters.Add("$isBinary",    Microsoft.Data.Sqlite.SqliteType.Integer);
        var pDeps    = cmd.Parameters.Add("$deps",        Microsoft.Data.Sqlite.SqliteType.Text);
        var pIndexed = cmd.Parameters.Add("$lastIndexed", Microsoft.Data.Sqlite.SqliteType.Integer);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var m in models)
        {
            pName.Value    = m.Name;
            pDisplay.Value = (object?)m.DisplayName ?? DBNull.Value;
            pPub.Value     = (object?)m.Publisher ?? DBNull.Value;
            pVer.Value     = (object?)m.Version ?? DBNull.Value;
            pLayer.Value   = (object?)m.Layer ?? DBNull.Value;
            pCustom.Value  = m.IsCustom ? 1 : 0;
            pBinary.Value  = m.IsBinary ? 1 : 0;
            pDeps.Value    = JsonSerializer.Serialize(m.Dependencies ?? Array.Empty<string>());
            pIndexed.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // Source tag used when the bridge response doesn't carry one (write-through
    // path before runtime support, or legacy callers). Default to disk —
    // runtime objects always arrive with an explicit "runtime" tag.
    private const string DefaultSource = "disk";

    private static void UpsertObjects(SqliteConnection conn, string model, List<(string AxType, string Name, string Source)> objects)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO objects
                (name, ax_type, model, file_path, last_modified, last_indexed, content_hash, source)
            VALUES
                ($name, $axType, $model, '', 0, $lastIndexed, '', $source)
            ON CONFLICT(name, ax_type, model) DO UPDATE SET
                last_indexed = excluded.last_indexed,
                source       = excluded.source;
        ";
        var pName    = cmd.Parameters.Add("$name",        Microsoft.Data.Sqlite.SqliteType.Text);
        var pType    = cmd.Parameters.Add("$axType",      Microsoft.Data.Sqlite.SqliteType.Text);
        var pModel   = cmd.Parameters.Add("$model",       Microsoft.Data.Sqlite.SqliteType.Text);
        var pIndexed = cmd.Parameters.Add("$lastIndexed", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pSource  = cmd.Parameters.Add("$source",      Microsoft.Data.Sqlite.SqliteType.Text);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        pModel.Value   = model;
        pIndexed.Value = now;
        foreach (var (axType, name, source) in objects)
        {
            pName.Value   = name;
            pType.Value   = axType;
            pSource.Value = string.IsNullOrEmpty(source) ? DefaultSource : source;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Write-through re-index of a single object after a domain mutation.
    /// Cheaper than a full sweep: one bridge call + one writer transaction.
    /// Idempotent. If the object isn't yet in the index (newly created),
    /// inserts the row first; if it's gone (deleted via bridge), deletes
    /// the row.
    /// </summary>
    /// <summary>
    /// Remove an object's row from the index (cascades to its methods /
    /// references / FTS via the schema's ON DELETE CASCADE + triggers). Used
    /// both when the bridge reports an object gone during a refresh, and
    /// directly by the delete path — which already knows the object was
    /// removed, so it skips the bridge re-read (whose metadata provider could
    /// still have the just-deleted object cached) and deletes the row outright.
    /// Idempotent: a no-match DELETE is a no-op.
    /// </summary>
    public async Task RemoveObjectAsync(string model, string axType, string name, CancellationToken ct)
    {
        await _writer.EnqueueAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM objects WHERE name=$n AND ax_type=$t AND model=$m";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$t", axType);
            cmd.Parameters.AddWithValue("$m", model);
            cmd.ExecuteNonQuery();
        }, ct).ConfigureAwait(false);
    }

    public async Task RefreshSingleObjectAsync(string model, string axType, string name, CancellationToken ct)
    {
        // 1. Pull the object's full projection from the bridge first so we
        // know its source tag. Surfaced from the same response that carries
        // methods + refs — the bridge already loaded the object once.
        BridgeObjectFull full;
        try
        {
            full = await _bridge.GetObjectFullAsync(model, axType, name, ct).ConfigureAwait(false);
        }
        // -32001 == JsonRpcErrorCodes.ObjectNotFound (bridge-side)
        catch (BridgeRpcException ex) when (ex.Code == -32001)
        {
            // The bridge says the object is gone. Remove it from the index.
            await RemoveObjectAsync(model, axType, name, ct).ConfigureAwait(false);
            return;
        }

        // 2. Ensure the object row exists, tagged with the source we just
        // resolved (disk or runtime).
        await _writer.EnqueueAsync(conn =>
        {
            var single = new List<(string AxType, string Name, string Source)> { (axType, name, full.Source) };
            UpsertObjects(conn, model, single);
        }, ct).ConfigureAwait(false);

        // 3. Resolve object_id and apply a single-row batch through the
        // existing UpsertObjectChildrenBatch path.
        await _writer.EnqueueAsync(conn =>
        {
            long objectId;
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT id FROM objects WHERE name=$n AND ax_type=$t AND model=$m";
                sel.Parameters.AddWithValue("$n", name);
                sel.Parameters.AddWithValue("$t", axType);
                sel.Parameters.AddWithValue("$m", model);
                var raw = sel.ExecuteScalar();
                if (raw == null || raw is DBNull) return;
                objectId = Convert.ToInt64(raw);
            }

            var prepared = PrepareMethodRows(full.Methods);
            var fieldRefs = full.FieldReferences ?? (IReadOnlyList<BridgeFieldReferenceEdge>)Array.Empty<BridgeFieldReferenceEdge>();
            var labelRefs = full.LabelReferences ?? (IReadOnlyList<BridgeLabelReferenceEdge>)Array.Empty<BridgeLabelReferenceEdge>();
            var batch = new[]
            {
                new Phase2Result(objectId, prepared, full.References, fieldRefs, labelRefs, Array.Empty<BridgeLabelEntry>())
            };
            // freshInsert=false: methods are upserted in place (ids preserved,
            // unchanged bodies keep their embeddings), vanished methods pruned;
            // refs / field_refs / label_refs are cleared and reinserted for
            // this object_id.
            UpsertObjectChildrenBatch(conn, batch, freshInsert: false);
        }, ct).ConfigureAwait(false);

        _logger.LogDebug("Write-through refreshed {AxType}:{Name} in {Model}", axType, name, model);
    }

    private static IReadOnlyList<Phase2Target> LoadPhase2Targets(SqliteConnection conn, IReadOnlyCollection<string>? modelsFilter, int cap, bool incremental)
    {
        var list = new List<Phase2Target>();
        using var cmd = conn.CreateCommand();

        var sql = "SELECT o.id, o.name, o.ax_type, o.model FROM objects o";
        var clauses = new List<string>();

        if (incremental)
        {
            // Skip objects Phase 2 has already visited. Schema v4
            // introduced objects.last_phase2_at specifically to fix the
            // legitimately-zero-method case (label files, security
            // privileges, resources, tiles, menus) which the older
            // "methods row exists" predicate kept re-fetching every sweep.
            //
            // Once mtime detection lands on Phase 1, this can also fold
            // in `OR o.last_phase2_at < o.last_modified` to pick up files
            // changed on disk since the last visit.
            clauses.Add("o.last_phase2_at = 0");
        }

        var hasFilter = modelsFilter != null && modelsFilter.Count > 0;
        if (hasFilter)
        {
            // Build a parameterised IN clause - parameter count == filter count.
            var paramNames = modelsFilter!.Select((_, i) => $"$m{i}").ToArray();
            clauses.Add($"o.model IN ({string.Join(",", paramNames)})");
            var idx = 0;
            foreach (var m in modelsFilter!)
            {
                cmd.Parameters.AddWithValue(paramNames[idx++], m);
            }
        }

        if (clauses.Count > 0) sql += " WHERE " + string.Join(" AND ", clauses);
        sql += " ORDER BY o.model, o.ax_type, o.name";
        if (cap > 0) sql += $" LIMIT {cap}";
        cmd.CommandText = sql;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Phase2Target(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                AxType: reader.GetString(2),
                Model: reader.GetString(3)));
        }
        return list;
    }

    /// <summary>
    /// Applies many objects' methods + refs in one transaction. Reuses
    /// parameterised commands across the whole batch so we pay
    /// parser/plan cost once. When <paramref name="freshInsert"/> is true
    /// the per-row DELETEs are skipped (rows can't exist yet).
    /// </summary>
    private static IReadOnlyList<PreparedMethodRow> PrepareMethodRows(IReadOnlyList<BridgeMethodInfo> raw)
    {
        if (raw.Count == 0) return Array.Empty<PreparedMethodRow>();
        var list = new List<PreparedMethodRow>(raw.Count);
        foreach (var m in raw)
        {
            if (string.IsNullOrEmpty(m.Name)) continue;
            var src = m.Source ?? string.Empty;
            list.Add(new PreparedMethodRow(
                Name:        m.Name,
                Signature:   m.Signature,
                IsStatic:    m.IsStatic,
                AccessLevel: m.AccessLevel,
                ReturnType:  m.ReturnType,
                Source:      src,
                SourceHash:  Sha256(src),
                LineCount:   src.Length == 0 ? 0 : src.Count(c => c == '\n') + 1));
        }
        return list;
    }

    private static void UpsertObjectChildrenBatch(SqliteConnection conn, IReadOnlyList<Phase2Result> batch, bool freshInsert)
    {
        if (batch.Count == 0) return;

        using var tx = conn.BeginTransaction();

        // Lever 4c: on a fresh insert we know the target rows can't exist,
        // so we can skip the DELETEs entirely. Each skipped DELETE is one
        // less statement to parse + one less B-tree probe per row, plus the
        // WAL doesn't have to log a no-op tombstone. Small but free.
        // Layer B: methods are UPSERTed by (object_id, name) rather than
        // deleted-and-reinserted, so a surviving method keeps its row id.
        // Keeping the id keeps its method_embedding_meta / method_vec rows
        // valid, so an unchanged body is never re-embedded on a re-index (the
        // embedder only re-embeds when chunk_text_hash <> source_hash). We no
        // longer wipe every method up front; instead we prune just the ones
        // that vanished from this object's new projection.
        using var delRemovedMethods = conn.CreateCommand();
        delRemovedMethods.Transaction = tx;
        delRemovedMethods.CommandText =
            "DELETE FROM methods WHERE object_id = $id " +
            "AND name NOT IN (SELECT value FROM json_each($names));";
        var drmId    = delRemovedMethods.Parameters.Add("$id",    SqliteType.Integer);
        var drmNames = delRemovedMethods.Parameters.Add("$names", SqliteType.Text);

        using var delRefs = conn.CreateCommand();
        delRefs.Transaction = tx;
        delRefs.CommandText = "DELETE FROM refs WHERE source_object_id = $id;";
        var drId = delRefs.Parameters.Add("$id", SqliteType.Integer);

        using var delFieldRefs = conn.CreateCommand();
        delFieldRefs.Transaction = tx;
        delFieldRefs.CommandText = "DELETE FROM field_refs WHERE source_object_id = $id;";
        var dfrId = delFieldRefs.Parameters.Add("$id", SqliteType.Integer);

        using var delLabelRefs = conn.CreateCommand();
        delLabelRefs.Transaction = tx;
        delLabelRefs.CommandText = "DELETE FROM label_refs WHERE source_object_id = $id;";
        var dlrId = delLabelRefs.Parameters.Add("$id", SqliteType.Integer);

        // Labels are embedded (label_vec), so like methods they're upserted by
        // their natural key (label_file_id, key, language) to preserve row ids
        // and keep unchanged values from re-embedding. We prune only the labels
        // that vanished from the file's new projection. The key set is passed
        // lowercased ("key  language") to mirror the NOCASE UNIQUE index.
        using var delRemovedLabels = conn.CreateCommand();
        delRemovedLabels.Transaction = tx;
        delRemovedLabels.CommandText =
            "DELETE FROM labels WHERE label_file_id = $id " +
            "AND (lower(key) || char(1) || lower(language)) NOT IN (SELECT value FROM json_each($keys));";
        var dlrmId   = delRemovedLabels.Parameters.Add("$id",   SqliteType.Integer);
        var dlrmKeys = delRemovedLabels.Parameters.Add("$keys", SqliteType.Text);

        // Per-object Phase 2 marker — see Schema/004-phase2-marker.sql.
        using var markProcessed = conn.CreateCommand();
        markProcessed.Transaction = tx;
        markProcessed.CommandText = "UPDATE objects SET last_phase2_at = $ts WHERE id = $id;";
        var mpId = markProcessed.Parameters.Add("$id", SqliteType.Integer);
        var mpTs = markProcessed.Parameters.Add("$ts", SqliteType.Integer);
        mpTs.Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var insMethod = conn.CreateCommand();
        insMethod.Transaction = tx;
        insMethod.CommandText = @"
            INSERT INTO methods
                (object_id, name, signature, is_static, access_level, return_type,
                 source_code, source_hash, line_count, parameters_json)
            VALUES
                ($oid, $name, $sig, $isStatic, $access, $rtype,
                 $src, $hash, $lc, NULL)
            ON CONFLICT(object_id, name) DO UPDATE SET
                signature    = excluded.signature,
                is_static    = excluded.is_static,
                access_level = excluded.access_level,
                return_type  = excluded.return_type,
                source_code  = excluded.source_code,
                source_hash  = excluded.source_hash,
                line_count   = excluded.line_count
            WHERE methods.source_hash <> excluded.source_hash;
        ";
        var mOid    = insMethod.Parameters.Add("$oid",      SqliteType.Integer);
        var mName   = insMethod.Parameters.Add("$name",     SqliteType.Text);
        var mSig    = insMethod.Parameters.Add("$sig",      SqliteType.Text);
        var mStatic = insMethod.Parameters.Add("$isStatic", SqliteType.Integer);
        var mAcc    = insMethod.Parameters.Add("$access",   SqliteType.Text);
        var mRType  = insMethod.Parameters.Add("$rtype",    SqliteType.Text);
        var mSrc    = insMethod.Parameters.Add("$src",      SqliteType.Text);
        var mHash   = insMethod.Parameters.Add("$hash",     SqliteType.Text);
        var mLc     = insMethod.Parameters.Add("$lc",       SqliteType.Integer);

        using var insRef = conn.CreateCommand();
        insRef.Transaction = tx;
        insRef.CommandText = @"
            INSERT INTO refs
                (source_object_id, target_object_name, target_object_type, reference_kind, context)
            VALUES
                ($oid, $tname, $ttype, $kind, $ctx);
        ";
        var rOid   = insRef.Parameters.Add("$oid",   SqliteType.Integer);
        var rTName = insRef.Parameters.Add("$tname", SqliteType.Text);
        var rTType = insRef.Parameters.Add("$ttype", SqliteType.Text);
        var rKind  = insRef.Parameters.Add("$kind",  SqliteType.Text);
        var rCtx   = insRef.Parameters.Add("$ctx",   SqliteType.Text);

        using var insFieldRef = conn.CreateCommand();
        insFieldRef.Transaction = tx;
        insFieldRef.CommandText = @"
            INSERT INTO field_refs
                (source_object_id, source_member, target_table_name, target_field_name, reference_kind, context)
            VALUES
                ($oid, $member, $ttable, $tfield, $kind, $ctx);
        ";
        var frOid    = insFieldRef.Parameters.Add("$oid",    SqliteType.Integer);
        var frMember = insFieldRef.Parameters.Add("$member", SqliteType.Text);
        var frTTable = insFieldRef.Parameters.Add("$ttable", SqliteType.Text);
        var frTField = insFieldRef.Parameters.Add("$tfield", SqliteType.Text);
        var frKind   = insFieldRef.Parameters.Add("$kind",   SqliteType.Text);
        var frCtx    = insFieldRef.Parameters.Add("$ctx",    SqliteType.Text);

        using var insLabelRef = conn.CreateCommand();
        insLabelRef.Transaction = tx;
        insLabelRef.CommandText = @"
            INSERT INTO label_refs
                (source_object_id, source_member, label_file, label_key, reference_kind, context)
            VALUES
                ($oid, $member, $lfile, $lkey, $kind, $ctx);
        ";
        var lrOid    = insLabelRef.Parameters.Add("$oid",    SqliteType.Integer);
        var lrMember = insLabelRef.Parameters.Add("$member", SqliteType.Text);
        var lrFile   = insLabelRef.Parameters.Add("$lfile",  SqliteType.Text);
        var lrKey    = insLabelRef.Parameters.Add("$lkey",   SqliteType.Text);
        var lrKind   = insLabelRef.Parameters.Add("$kind",   SqliteType.Text);
        var lrCtx    = insLabelRef.Parameters.Add("$ctx",    SqliteType.Text);

        using var insLabel = conn.CreateCommand();
        insLabel.Transaction = tx;
        insLabel.CommandText = @"
            INSERT INTO labels (label_file_id, key, value, language, description, value_hash)
            VALUES ($fid, $key, $val, $lang, $desc, $vhash)
            ON CONFLICT(label_file_id, key, language) DO UPDATE SET
                value       = excluded.value,
                description = excluded.description,
                value_hash  = excluded.value_hash
            WHERE labels.value_hash <> excluded.value_hash;
        ";
        var lFid   = insLabel.Parameters.Add("$fid",   SqliteType.Integer);
        var lKey   = insLabel.Parameters.Add("$key",   SqliteType.Text);
        var lVal   = insLabel.Parameters.Add("$val",   SqliteType.Text);
        var lLang  = insLabel.Parameters.Add("$lang",  SqliteType.Text);
        var lDesc  = insLabel.Parameters.Add("$desc",  SqliteType.Text);
        var lVHash = insLabel.Parameters.Add("$vhash", SqliteType.Text);

        foreach (var item in batch)
        {
            if (!freshInsert)
            {
                // Methods and labels are embedded, so they're upserted in place
                // and pruned after their loops (delRemovedMethods /
                // delRemovedLabels) to preserve ids and keep their embeddings.
                // Refs / field-refs / label-refs aren't embedded, so
                // delete-and-reinsert is harmless for them and stays.
                drId.Value = item.ObjectId;
                delRefs.ExecuteNonQuery();
                dfrId.Value = item.ObjectId;
                delFieldRefs.ExecuteNonQuery();
                dlrId.Value = item.ObjectId;
                delLabelRefs.ExecuteNonQuery();
            }

            mOid.Value = item.ObjectId;
            foreach (var m in item.Methods)
            {
                mName.Value   = m.Name;
                mSig.Value    = (object?)m.Signature ?? DBNull.Value;
                mStatic.Value = m.IsStatic ? 1 : 0;
                mAcc.Value    = (object?)m.AccessLevel ?? DBNull.Value;
                mRType.Value  = (object?)m.ReturnType ?? DBNull.Value;
                mSrc.Value    = m.Source;
                mHash.Value   = m.SourceHash;
                mLc.Value     = m.LineCount;
                try
                {
                    // Upsert: inserts a new method, updates a changed one in
                    // place (id preserved), and is a true no-op for an
                    // unchanged body (the DO UPDATE ... WHERE guard skips it,
                    // so no trigger fires and the embedding stays valid).
                    insMethod.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    // Belt-and-suspenders: the (object_id, name) conflict is
                    // handled by the upsert, so this should not fire; kept in
                    // case some other constraint ever trips on a bad projection.
                }
            }

            // Prune methods that disappeared from this object's new projection
            // (renamed or removed). Surviving methods were upserted in place
            // above and keep their ids/embeddings. Skipped on a fresh insert
            // where no prior rows exist. An empty method set serializes to
            // "[]", which correctly deletes all of the object's old methods.
            if (!freshInsert)
            {
                drmId.Value    = item.ObjectId;
                drmNames.Value = System.Text.Json.JsonSerializer.Serialize(
                    item.Methods.Select(m => m.Name).ToArray());
                delRemovedMethods.ExecuteNonQuery();
            }

            rOid.Value = item.ObjectId;
            foreach (var r in item.References)
            {
                if (string.IsNullOrEmpty(r.TargetName)) continue;
                rTName.Value = r.TargetName;
                rTType.Value = (object?)r.TargetType ?? DBNull.Value;
                rKind.Value  = r.Kind ?? "unknown";
                rCtx.Value   = (object?)r.Context ?? DBNull.Value;
                insRef.ExecuteNonQuery();
            }

            frOid.Value = item.ObjectId;
            foreach (var fr in item.FieldReferences)
            {
                if (string.IsNullOrEmpty(fr.TargetTable) || string.IsNullOrEmpty(fr.TargetField)) continue;
                frMember.Value = (object?)fr.SourceMember ?? DBNull.Value;
                frTTable.Value = fr.TargetTable;
                frTField.Value = fr.TargetField;
                frKind.Value   = fr.Kind ?? "unknown";
                frCtx.Value    = (object?)fr.Context ?? DBNull.Value;
                insFieldRef.ExecuteNonQuery();
            }

            lrOid.Value = item.ObjectId;
            foreach (var lr in item.LabelReferences)
            {
                if (string.IsNullOrEmpty(lr.LabelKey)) continue;
                lrMember.Value = (object?)lr.SourceMember ?? DBNull.Value;
                lrFile.Value   = lr.LabelFile ?? string.Empty;
                lrKey.Value    = lr.LabelKey;
                lrKind.Value   = lr.Kind ?? "unknown";
                lrCtx.Value    = (object?)lr.Context ?? DBNull.Value;
                insLabelRef.ExecuteNonQuery();
            }

            // Mark the object as visited by Phase 2, even when it had zero
            // children. This is what lets the incremental sweep correctly
            // skip legitimately-empty objects (label files with no readable
            // labels, security duties with no privilege grants, etc.) on
            // subsequent passes.
            mpId.Value = item.ObjectId;
            markProcessed.ExecuteNonQuery();

            // Labels: upsert in place (id preserved so the label's embedding
            // survives; the DO UPDATE ... WHERE value_hash guard makes an
            // unchanged value a no-op), then prune vanished keys. Runs for
            // every object — for a non-label object item.Labels is empty, so the
            // upsert loop does nothing and the prune (keyed on label_file_id,
            // index-backed) matches no rows.
            lFid.Value = item.ObjectId;
            var labelKeys = new List<string>(item.Labels.Count);
            foreach (var lab in item.Labels)
            {
                if (string.IsNullOrEmpty(lab.Key)) continue;
                var val  = lab.Value ?? string.Empty;
                var lang = lab.Language ?? "en-US";
                lKey.Value   = lab.Key;
                lVal.Value   = val;
                lLang.Value  = lang;
                lDesc.Value  = (object?)lab.Description ?? DBNull.Value;
                lVHash.Value = Sha256(val);
                labelKeys.Add(lab.Key.ToLowerInvariant() + (char)1 + lang.ToLowerInvariant());
                try
                {
                    insLabel.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    // Duplicate (label_file, key, language) within one
                    // projection. Rare; the bridge may surface the same key
                    // twice. The upsert handled the first; skip the dupe.
                }
            }

            if (!freshInsert)
            {
                dlrmId.Value   = item.ObjectId;
                dlrmKeys.Value = System.Text.Json.JsonSerializer.Serialize(labelKeys);
                delRemovedLabels.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static string Sha256(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Refresh the index_state summary counts. Called at the end of Phase 1 and
    /// Phase 2, so the ~1.4s the embeddable_count scan costs is noise against
    /// the run — and it keeps GetStatus O(1), which it's documented to be.
    ///
    /// embeddable_count is the denominator the embedder can actually reach:
    /// methods/labels with non-empty content, mirroring the drain predicates'
    /// length(trim(...)) > 0. Reporting method_count + label_count instead left
    /// a fully-drained index stuck at ~98% (runtime-source objects carry empty
    /// source_code by design and can never be embedded), which reads as stalled.
    ///
    /// embedding_count is deliberately NOT written here: the Embedder owns it
    /// and computes it correctly (completed rows across BOTH method and label
    /// meta, filtered to the active model_version). This used to overwrite it
    /// with a method-only COUNT, so after every sweep the status under-reported
    /// embeddings until the embedder's next batch corrected it.
    /// </summary>
    private static void UpdateIndexState(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE index_state SET
                last_full_scan_at = strftime('%s','now'),
                object_count      = (SELECT COUNT(*) FROM objects),
                method_count      = (SELECT COUNT(*) FROM methods),
                label_count       = (SELECT COUNT(*) FROM labels),
                embeddable_count  =
                    (SELECT COUNT(*) FROM methods WHERE length(trim(source_code)) > 0)
                  + (SELECT COUNT(*) FROM labels  WHERE length(trim(value))       > 0)
            WHERE id = 1;
        ";
        cmd.ExecuteNonQuery();
    }
}

internal sealed record Phase2Target(long Id, string Name, string AxType, string Model);

internal sealed record Phase2Result(
    long ObjectId,
    IReadOnlyList<PreparedMethodRow> Methods,
    IReadOnlyList<BridgeReferenceEdge> References,
    IReadOnlyList<BridgeFieldReferenceEdge> FieldReferences,
    IReadOnlyList<BridgeLabelReferenceEdge> LabelReferences,
    IReadOnlyList<BridgeLabelEntry> Labels);

/// <summary>
/// Method row with SHA256 + line_count precomputed off the writer thread.
/// Lever 4d: hashing 60k strings is non-trivial CPU and we want it
/// happening on the parallel bridge workers, not on the single writer.
/// </summary>
internal sealed record PreparedMethodRow(
    string Name,
    string? Signature,
    bool IsStatic,
    string? AccessLevel,
    string? ReturnType,
    string Source,
    string SourceHash,
    int LineCount);

public sealed class IndexerOptions
{
    /// <summary>
    /// Languages to extract from AxLabelFile entries during phase 2.
    /// Defaults to en-US only. Configurable via XppService:LabelLanguages
    /// in appsettings (comma-separated).
    /// </summary>
    public IReadOnlyList<string> LabelLanguages { get; init; } = new[] { "en-US" };

    /// <summary>
    /// D365 PackagesLocalDirectory root. Used by the startup reconcile to
    /// locate and hash each object's on-disk content file for change
    /// detection. Empty disables disk-based reconcile (the sweep still runs,
    /// but only picks up never-visited objects).
    /// </summary>
    public string PackagesLocalDirectory { get; init; } = string.Empty;
}

public sealed record IndexProgressEvent(
    string Phase,
    string CurrentModel,
    string CurrentAxType,
    long ObjectsSeen,
    long ObjectsTotalEstimate,
    bool Done,
    string Message);

public sealed record IndexRunSummary(
    long Models,
    long Objects,
    long Methods,
    long References,
    TimeSpan Duration);
