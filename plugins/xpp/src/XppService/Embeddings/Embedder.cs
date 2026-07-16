using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Xpp.Service.Storage;

namespace Xpp.Service.Embeddings;

/// <summary>
/// Continuously embeds indexed content (method bodies + label values) into the
/// sqlite-vec store, so semantic search stays current no matter how content
/// arrives.
///
/// Design intent — match the indexer's spirit. Content lands in the cache three
/// ways: a cold full rebuild (a flood of ~1.3M rows), periodic incremental
/// sweeps (a trickle as files change), and write-through after a domain
/// mutation (a single object). The embedder doesn't care which: it drains
/// <em>whatever lacks a current vector</em>. Because the indexer DELETE+reinserts
/// an object's methods/labels on every re-index, their ids churn and the
/// <c>*_embedding_meta</c> rows cascade away — so "row with no current meta" is
/// the single, uniform signal that captures brand-new content, changed content,
/// and content whose object was just touched. One predicate, every path.
///
/// The loop:
///   - parks on <see cref="EmbeddingWorkSignal"/> (nudged after every
///     sweep / write-through) with a timer backstop, so a flood wakes it
///     immediately and a missed nudge only delays — never strands — work;
///   - drains in pages, embedding each page through the shared singleton
///     generator and writing vectors + meta through the single-writer queue;
///   - is fully resumable: a crash/restart mid-flood just re-drains the rows
///     that never got a completed meta row;
///   - opportunistically GCs orphaned vectors once caught up (the churned ids
///     leave their old vec rows behind — vec0 has no FK cascade).
///
/// Throttled (bounded ONNX threads + optional inter-page cooldown) so the bulk
/// pass doesn't starve search or the bridge. Quietly inert when embeddings are
/// disabled, the model never arrives, or sqlite-vec is unavailable — FTS is
/// never affected.
/// </summary>
public sealed class Embedder : BackgroundService
{
    private readonly IndexDatabase _db;
    private readonly IndexWriter _writer;
    private readonly IEmbeddingProvider _generator;
    private readonly EmbeddingOptions _options;
    private readonly EmbeddingWorkSignal _signal;
    private readonly ILogger<Embedder> _logger;

    // True once we've published an accurate count for the current caught-up
    // state. Reset whenever we embed something, so the next catch-up republishes
    // exactly once instead of re-counting on every poll cycle.
    private bool _idleCountPublished;

    public Embedder(
        IndexDatabase db, IndexWriter writer, IEmbeddingProvider generator,
        EmbeddingOptions options, EmbeddingWorkSignal signal,
        ILogger<Embedder> logger)
    {
        _db = db;
        _writer = writer;
        _generator = generator;
        _options = options;
        _signal = signal;
        _logger = logger;
    }

    private enum Kind { Method, Label }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Embedder: disabled (Embedding:Enabled=false); not starting");
            return;
        }

        if (!await WaitForReadyAsync(ct).ConfigureAwait(false))
            return; // not-ready / vec-unavailable — logged inside

        _logger.LogInformation(
            "Embedder online ({Dim}-d, model {Version}); draining backlog", _generator.Dim, _options.ModelVersion);

        while (!ct.IsCancellationRequested)
        {
            int embedded;
            try
            {
                embedded = await DrainAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Systemic failure (e.g. ONNX session hiccup). Back off and
                // retry on the next cycle rather than hot-looping.
                _logger.LogError(ex, "Embedder drain failed; backing off");
                embedded = 0;
            }

            if (embedded > 0)
            {
                // Work happened; the count published per-page is already fresh,
                // but the next catch-up should publish a final one.
                _idleCountPublished = false;
            }
            else
            {
                // Caught up. Publish the count once here: DrainKindAsync only
                // refreshes it per embedded page, so with nothing pending it
                // would never run — leaving whatever a sweep's UpdateIndexState
                // last wrote (historically a method-only COUNT) standing as the
                // published total, which reads as "embeddings incomplete"
                // forever. Guarded so we don't re-count on every poll cycle.
                if (!_idleCountPublished)
                {
                    try
                    {
                        await UpdateEmbeddingStateAsync(ct).ConfigureAwait(false);
                        _idleCountPublished = true;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Embedding count publish failed (non-fatal)"); }
                }

                // Tidy orphaned vectors left by churned ids, then park until
                // something changes (or the backstop fires).
                try { await GcOrphansAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Embedding vector GC failed (non-fatal)"); }

                try { await _signal.WaitAsync(TimeSpan.FromSeconds(_options.EmbedPollSeconds), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("Embedder stopped");
    }

    /// <summary>
    /// Wait until the backend reports ready and sqlite-vec is live. sqlite-vec
    /// being unavailable is terminal (no vector storage possible); otherwise we
    /// poll until the provider is ready (instant for the cloud backend, gated on
    /// download for the local one). Returns true when ready to embed.
    /// </summary>
    private async Task<bool> WaitForReadyAsync(CancellationToken ct)
    {
        if (!_db.VecEnabled)
        {
            _logger.LogWarning("Embedder: sqlite-vec unavailable; vector storage disabled (FTS unaffected)");
            return false;
        }
        while (!ct.IsCancellationRequested)
        {
            if (_generator.IsReady) return true;
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<int> DrainAsync(CancellationToken ct)
    {
        var total = 0;
        total += await DrainKindAsync(Kind.Method, ct).ConfigureAwait(false);
        total += await DrainKindAsync(Kind.Label, ct).ConfigureAwait(false);
        if (total > 0)
            _logger.LogInformation("Embedder: embedded {Count} item(s) this pass", total);
        return total;
    }

    private async Task<int> DrainKindAsync(Kind kind, CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            // One read per loop yields DISTINCT pending rows (nothing is marked
            // until we write), so we can safely fan the page out into concurrent
            // requests without overlapping work.
            var page = ReadPending(kind, _options.EmbedReadBatch);
            if (page.Count == 0) break;

            var vectors = new float[page.Count][];
            var chunkStarts = new List<int>();
            for (var s = 0; s < page.Count; s += _options.RequestSize) chunkStarts.Add(s);

            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _options.EmbedConcurrency),
                CancellationToken = ct
            };
            // Documents are embedded raw (no query instruction prefix — that's
            // applied only to search queries at retrieval time).
            await Parallel.ForEachAsync(chunkStarts, parallel, async (start, c) =>
            {
                var count = Math.Min(_options.RequestSize, page.Count - start);
                var texts = new List<string>(count);
                for (var i = 0; i < count; i++) texts.Add(page[start + i].Text);

                var embeddings = await _generator.GenerateAsync(texts, cancellationToken: c).ConfigureAwait(false);
                var j = 0;
                foreach (var e in embeddings) vectors[start + j++] = e.Vector.ToArray();
            }).ConfigureAwait(false);

            await _writer.EnqueueAsync(conn => WriteBatch(conn, kind, page, vectors), ct).ConfigureAwait(false);
            total += page.Count;

            // Update the published count per page, not once at the end of the
            // (multi-hour) full drain — otherwise status shows 0 the whole time.
            await UpdateEmbeddingStateAsync(ct).ConfigureAwait(false);

            if (_options.EmbedThrottleMs > 0)
                await Task.Delay(_options.EmbedThrottleMs, ct).ConfigureAwait(false);

            if (page.Count < _options.EmbedReadBatch) break; // last partial page
        }
        return total;
    }

    /// <summary>
    /// Pull a page of rows that have no current vector. The LEFT JOIN anti-match
    /// (em.id IS NULL) catches never-embedded rows and — because re-indexing
    /// churns ids and cascades the meta away — also everything that changed. The
    /// extra hash guard on methods is a cheap belt-and-braces for the
    /// theoretical in-place update that preserves an id.
    /// </summary>
    private IReadOnlyList<PendingRow> ReadPending(Kind kind, int limit)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        if (kind == Kind.Method)
        {
            cmd.CommandText = @"
                SELECT m.id, m.source_code, m.source_hash
                FROM methods m
                LEFT JOIN method_embedding_meta em
                    ON em.method_id = m.id AND em.chunk_index = 0 AND em.model_version = $ver
                WHERE (em.id IS NULL OR em.chunk_text_hash <> m.source_hash)
                  AND length(trim(m.source_code)) > 0
                LIMIT $lim;";
        }
        else
        {
            // Hash-aware, transition-safe: re-embed a label with no vector yet,
            // OR one whose stored value_hash no longer matches the embedded
            // chunk_text_hash. The value_hash<>'' guard means rows predating the
            // value_hash column (backfilled lazily on their next re-index) keep
            // their existing valid vector until the value actually changes —
            // no mass re-embed on the schema upgrade. value_hash and
            // chunk_text_hash are the same SHA-256(value), so they compare
            // directly.
            cmd.CommandText = @"
                SELECT l.id, l.value
                FROM labels l
                LEFT JOIN label_embedding_meta em
                    ON em.label_id = l.id AND em.chunk_index = 0 AND em.model_version = $ver
                WHERE (em.id IS NULL OR (l.value_hash <> '' AND em.chunk_text_hash <> l.value_hash))
                  AND length(trim(l.value)) > 0
                LIMIT $lim;";
        }
        cmd.Parameters.AddWithValue("$ver", _options.ModelVersion);
        cmd.Parameters.AddWithValue("$lim", limit);

        var rows = new List<PendingRow>(limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var text = reader.GetString(1);
            // Methods carry a precomputed source hash; for labels we hash the
            // value so the meta row records exactly what we embedded.
            var hash = kind == Kind.Method ? reader.GetString(2) : Sha256(text);
            rows.Add(new PendingRow(id, text, hash));
        }
        return rows;
    }

    private void WriteBatch(SqliteConnection conn, Kind kind, IReadOnlyList<PendingRow> rows, IReadOnlyList<float[]> vectors)
    {
        var vecTable = kind == Kind.Method ? "method_vec" : "label_vec";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var tx = conn.BeginTransaction();

        // vec0 virtual tables don't support INSERT OR REPLACE; delete-then-insert
        // by rowid is the portable upsert. The delete is a no-op for the common
        // case (a fresh, never-seen id) and replaces the vector for an in-place
        // re-embed.
        using var delVec = conn.CreateCommand();
        delVec.Transaction = tx;
        delVec.CommandText = $"DELETE FROM {vecTable} WHERE rowid = $id;";
        var dvId = delVec.Parameters.Add("$id", SqliteType.Integer);

        using var insVec = conn.CreateCommand();
        insVec.Transaction = tx;
        insVec.CommandText = $"INSERT INTO {vecTable}(rowid, embedding) VALUES ($id, $emb);";
        var ivId = insVec.Parameters.Add("$id", SqliteType.Integer);
        var ivEmb = insVec.Parameters.Add("$emb", SqliteType.Blob);

        using var upMeta = conn.CreateCommand();
        upMeta.Transaction = tx;
        upMeta.CommandText = kind == Kind.Method
            ? @"INSERT INTO method_embedding_meta
                    (method_id, chunk_index, chunk_start_line, chunk_end_line,
                     model_version, chunk_text_hash, last_computed, status)
                VALUES ($id, 0, 0, 0, $ver, $hash, $ts, 'completed')
                ON CONFLICT(method_id, chunk_index, model_version) DO UPDATE SET
                    chunk_text_hash = excluded.chunk_text_hash,
                    last_computed   = excluded.last_computed,
                    status          = 'completed',
                    error_message   = NULL;"
            : @"INSERT INTO label_embedding_meta
                    (label_id, chunk_index, model_version, chunk_text_hash, last_computed, status)
                VALUES ($id, 0, $ver, $hash, $ts, 'completed')
                ON CONFLICT(label_id, chunk_index, model_version) DO UPDATE SET
                    chunk_text_hash = excluded.chunk_text_hash,
                    last_computed   = excluded.last_computed,
                    status          = 'completed',
                    error_message   = NULL;";
        var muId = upMeta.Parameters.Add("$id", SqliteType.Integer);
        var muVer = upMeta.Parameters.Add("$ver", SqliteType.Text);
        var muHash = upMeta.Parameters.Add("$hash", SqliteType.Text);
        var muTs = upMeta.Parameters.Add("$ts", SqliteType.Integer);
        muVer.Value = _options.ModelVersion;
        muTs.Value = now;

        for (var i = 0; i < rows.Count; i++)
        {
            var id = rows[i].Id;
            dvId.Value = id;
            delVec.ExecuteNonQuery();
            ivId.Value = id;
            ivEmb.Value = ToBlob(vectors[i]);
            insVec.ExecuteNonQuery();

            muId.Value = id;
            muHash.Value = rows[i].Hash;
            upMeta.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Remove vectors whose backing method/label no longer exists. Re-indexing
    /// churns ids (DELETE+reinsert), and vec0 has no foreign-key cascade, so the
    /// old id's vector lingers. Run only once we're caught up, so it costs
    /// nothing during a flood and runs at most once per change-burst.
    /// </summary>
    private async Task GcOrphansAsync(CancellationToken ct)
    {
        var removed = await _writer.EnqueueAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM method_vec WHERE rowid NOT IN (SELECT id FROM methods);";
            var m = cmd.ExecuteNonQuery();
            cmd.CommandText = "DELETE FROM label_vec WHERE rowid NOT IN (SELECT id FROM labels);";
            var l = cmd.ExecuteNonQuery();
            return m + l;
        }, ct).ConfigureAwait(false);

        if (removed > 0)
        {
            _logger.LogInformation("Embedder: GC removed {Count} orphaned vector(s)", removed);
            await UpdateEmbeddingStateAsync(ct).ConfigureAwait(false);
        }
    }

    private Task UpdateEmbeddingStateAsync(CancellationToken ct) => _writer.EnqueueAsync(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE index_state SET
                embedding_count = (
                    (SELECT COUNT(*) FROM method_embedding_meta WHERE status='completed' AND model_version=$ver)
                  + (SELECT COUNT(*) FROM label_embedding_meta  WHERE status='completed' AND model_version=$ver)),
                embedding_model_version = $ver
            WHERE id = 1;";
        cmd.Parameters.AddWithValue("$ver", _options.ModelVersion);
        cmd.ExecuteNonQuery();
    }, ct);

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        MemoryMarshal.AsBytes(vector.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    private static string Sha256(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    private readonly record struct PendingRow(long Id, string Text, string Hash);
}
