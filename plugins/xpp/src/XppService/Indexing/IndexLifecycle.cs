using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Xpp.Service.Embeddings;
using Xpp.Service.Storage;

namespace Xpp.Service.Indexing;

/// <summary>
/// Service-managed indexing lifecycle. Encapsulates three triggers so the
/// agent never has to reach for a rebuild tool:
///
///   1. <b>Bootstrap on startup</b> — when the object table is empty (fresh
///      install, post-DR), kick a phase-1 + phase-2 walk in the background.
///   2. <b>Lazy sweep on search</b> — search RPCs call
///      <see cref="MaybeTriggerSweepAsync"/>; if no sweep has run in
///      <c>sweepInterval</c>, a fresh incremental sweep fires (single-flight).
///   3. <b>Write-through on mutation</b> — domain handlers call
///      <see cref="EnqueueWriteThroughAsync"/>; a drainer task re-indexes
///      that single object so the next search reflects the change.
///
/// Single-flight enforced by an in-memory semaphore. The cross-process
/// guard is the existing global mutex around the whole XppService
/// process; there is at most one service per machine.
/// </summary>
public sealed class IndexLifecycle : IDisposable
{
    private readonly Indexer _indexer;
    private readonly IndexDatabase _db;
    private readonly EmbeddingWorkSignal _embedSignal;
    private readonly ILogger<IndexLifecycle> _logger;

    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private readonly Channel<WriteThroughItem> _writeThrough = Channel.CreateUnbounded<WriteThroughItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Sweep cadence: trigger an incremental sweep on a search RPC only if
    // the last one finished more than this long ago. Tuned to keep up with
    // typical authoring cadence without spinning needlessly when the user
    // is idle.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private DateTime _lastSweepCompletedAt = DateTime.MinValue;
    private DateTime _lastFullScanCompletedAt = DateTime.MinValue;
    private volatile bool _sweepInProgress;
    private volatile bool _bootstrapRunning;

    public IndexLifecycle(Indexer indexer, IndexDatabase db, EmbeddingWorkSignal embedSignal, ILogger<IndexLifecycle> logger)
    {
        _indexer = indexer;
        _db = db;
        _embedSignal = embedSignal;
        _logger = logger;
        LoadCadenceFromDb();
        SeedFirstSweepIfPopulated();
    }

    /// <summary>
    /// If the database is already populated (an earlier service version
    /// ran Phase 1+2 successfully) but our in-memory cadence shows we've
    /// never recorded a sweep, treat "now" as the last sweep time. This
    /// avoids a redundant full-corpus sweep firing on the very first
    /// search RPC after the lifecycle layer is deployed.
    ///
    /// Effect: subsequent sweeps wait the full <see cref="SweepInterval"/>
    /// before firing. The actual freshness guarantee is unchanged — the
    /// existing index is still trusted as-is until the sweep window
    /// elapses, same as for any other "we ran a sweep N minutes ago"
    /// state.
    /// </summary>
    private void SeedFirstSweepIfPopulated()
    {
        if (_lastSweepCompletedAt != DateTime.MinValue) return;
        if (CountObjects() == 0) return;
        _lastSweepCompletedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "Index already populated but no recorded sweep cadence; seeding lastSweepCompletedAt=now");
    }

    public bool SweepInProgress => _sweepInProgress;
    public DateTime LastSweepCompletedAt => _lastSweepCompletedAt;
    public DateTime LastFullScanCompletedAt => _lastFullScanCompletedAt;

    /// <summary>
    /// Returns the lifecycle phase string surfaced via the status RPC.
    ///   "uninitialized" — empty DB, bootstrap not yet started.
    ///   "warming"       — bootstrap in flight.
    ///   "sweeping"      — incremental sweep in flight.
    ///   "ready"         — populated, no sweep running.
    /// </summary>
    public string DescribePhase(long objectCount)
    {
        if (_bootstrapRunning) return "warming";
        if (_sweepInProgress) return "sweeping";
        if (objectCount == 0) return "uninitialized";
        return "ready";
    }

    /// <summary>
    /// Kicked on service startup. Always starts the write-through drainer
    /// (needed regardless of index state). Then:
    ///
    ///  - Empty index → run a bootstrap sweep right now.
    ///  - Populated but stale index (last sweep older than the regular
    ///    sweep interval) → run an incremental sweep right now, so the
    ///    indexer makes progress while the user is still getting their
    ///    bearings instead of waiting for the first search RPC.
    ///  - Populated and fresh index → no-op. The next search RPC's
    ///    <see cref="MaybeTriggerSweep"/> will fire when the window
    ///    elapses, same as the steady-state cadence.
    ///
    /// Combined with the MCP layer's eager-prime Ping at session start,
    /// this means binary-module additions (and any other indexer-relevant
    /// changes since the last shutdown) are picked up in the background
    /// well before the agent reaches for them.
    /// </summary>
    public void EnsureBootstrap(CancellationToken serviceShutdown)
    {
        // Write-through drainer is unconditional — it serves the
        // domain-mutation refresh path regardless of where the index
        // is in its lifecycle.
        StartWriteThroughDrainer(serviceShutdown);

        var objectCount = CountObjects();
        if (objectCount == 0)
        {
            _logger.LogInformation("Empty index detected — queuing bootstrap walk in background");
            _bootstrapRunning = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSweepAsync(forceFull: false, serviceShutdown).ConfigureAwait(false);
                    _logger.LogInformation("Bootstrap walk complete");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Bootstrap walk failed");
                }
                finally
                {
                    _bootstrapRunning = false;
                }
            }, serviceShutdown);
            return;
        }

        // Populated index. Decide whether to nudge a sweep now or wait
        // for the search-triggered cadence.
        var sinceLast = DateTime.UtcNow - _lastSweepCompletedAt;
        if (sinceLast < SweepInterval)
        {
            _logger.LogInformation(
                "Index populated and fresh (last sweep {Mins:F1}m ago); deferring to RPC-triggered cadence",
                sinceLast.TotalMinutes);
            return;
        }

        _logger.LogInformation(
            "Index populated but stale (last sweep {Mins:F1}m ago) — queuing startup reconcile sweep",
            sinceLast.TotalMinutes);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunSweepAsync(forceFull: false, serviceShutdown, reconcile: true).ConfigureAwait(false);
                _logger.LogInformation("Startup reconcile sweep complete");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup refresh sweep failed");
            }
        }, serviceShutdown);
    }

    /// <summary>
    /// Called from search RPC handlers. Idempotent: if a sweep is already
    /// running or the last one finished within <see cref="SweepInterval"/>,
    /// this is a no-op. Otherwise enqueues an incremental sweep in the
    /// background (fire-and-forget) and returns immediately so the caller's
    /// search isn't delayed.
    /// </summary>
    public void MaybeTriggerSweep(CancellationToken serviceShutdown)
    {
        if (_sweepInProgress) return;
        if (_bootstrapRunning) return;
        if (DateTime.UtcNow - _lastSweepCompletedAt < SweepInterval) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunSweepAsync(forceFull: false, serviceShutdown).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background sweep failed");
            }
        }, serviceShutdown);
    }

    /// <summary>
    /// Enqueue a single object for write-through re-indexing. Drained by
    /// the background worker started in <see cref="EnsureBootstrap"/>.
    /// Fire-and-forget for the caller; freshness is not synchronous.
    /// </summary>
    public ValueTask EnqueueWriteThroughAsync(string model, string axType, string name, CancellationToken ct)
    {
        return _writeThrough.Writer.WriteAsync(new WriteThroughItem(model, axType, name), ct);
    }

    /// <summary>
    /// Synchronously evict an object from the index (delete path). Unlike the
    /// write-through queue this awaits the removal so a follow-up find/search
    /// won't still see the just-deleted object. Cascades to methods/refs/FTS.
    /// </summary>
    public Task RemoveObjectAsync(string model, string axType, string name, CancellationToken ct)
    {
        return _indexer.RemoveObjectAsync(model, axType, name, ct);
    }

    // ---- internals ------------------------------------------------------

    /// <summary>
    /// Run a phase-1 + phase-2 walk. Single-flight via _sweepGate. When
    /// <paramref name="forceFull"/> is false (the normal path), phase 2
    /// runs in incremental mode — objects with up-to-date methods are
    /// skipped.
    /// </summary>
    private async Task RunSweepAsync(bool forceFull, CancellationToken ct, bool reconcile = false)
    {
        if (!await _sweepGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // Another sweep is already running. Skip silently.
            return;
        }
        _sweepInProgress = true;
        try
        {
            // Phase 1 inventories models + objects (inserts new, cheap).
            // When reconcile is set, an on-disk change pass then resets
            // last_phase2_at=0 for objects whose content file actually changed
            // since we indexed it (an SDK update / GET LATEST while we were
            // down) — without it the incremental Phase 2 only picks up
            // never-visited rows. Phase 2 (incremental=!forceFull) then
            // re-reads exactly the invalidated + new set.
            var progress = new SinkReporter();
            await _indexer.RunPhase1Async(progress, ct, Array.Empty<string>(), pruneDeleted: reconcile).ConfigureAwait(false);
            if (reconcile)
            {
                await _indexer.InvalidateChangedObjectsAsync(progress, ct).ConfigureAwait(false);
            }
            await _indexer.RunPhase2Async(progress, ct, Array.Empty<string>(),
                maxObjectsPhase2: 0,
                incremental: !forceFull).ConfigureAwait(false);

            _lastSweepCompletedAt = DateTime.UtcNow;
            if (forceFull) _lastFullScanCompletedAt = DateTime.UtcNow;
            PersistCadence();
            _logger.LogInformation(
                "Sweep complete (forceFull={Force}); lastSweepAt={At:o}",
                forceFull, _lastSweepCompletedAt);
        }
        finally
        {
            _sweepInProgress = false;
            _sweepGate.Release();
            // A sweep (bootstrap, startup-refresh, or steady-state) may have
            // landed new or changed methods/labels. Wake the embedder so the
            // vector index converges on the fresh content without waiting for
            // its poll backstop. No-op when embeddings are disabled.
            _embedSignal.Nudge();
        }
    }

    private void StartWriteThroughDrainer(CancellationToken serviceShutdown)
    {
        _ = Task.Run(async () =>
        {
            await foreach (var item in _writeThrough.Reader.ReadAllAsync(serviceShutdown).ConfigureAwait(false))
            {
                try
                {
                    await _indexer.RefreshSingleObjectAsync(item.Model, item.AxType, item.Name, serviceShutdown)
                        .ConfigureAwait(false);
                    // The refreshed object's methods/labels were re-inserted with
                    // fresh ids; nudge the embedder to vectorize them promptly so
                    // the change is searchable semantically right after a write.
                    _embedSignal.Nudge();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Write-through re-index failed for {AxType}:{Name} in {Model}",
                        item.AxType, item.Name, item.Model);
                }
            }
        }, serviceShutdown);
    }

    private long CountObjects()
    {
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM objects";
            var raw = cmd.ExecuteScalar();
            return raw is long l ? l : Convert.ToInt64(raw ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to count objects; assuming empty");
            return 0;
        }
    }

    private void LoadCadenceFromDb()
    {
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_incremental_at, last_full_scan_at FROM index_state WHERE id=1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    _lastSweepCompletedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).UtcDateTime;
                }
                if (!reader.IsDBNull(1))
                {
                    _lastFullScanCompletedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).UtcDateTime;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load index_state cadence; treating as never-swept");
        }
    }

    private void PersistCadence()
    {
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE index_state
                SET last_incremental_at = $incr,
                    last_full_scan_at   = COALESCE($full, last_full_scan_at)
                WHERE id = 1";
            cmd.Parameters.AddWithValue("$incr", new DateTimeOffset(_lastSweepCompletedAt).ToUnixTimeSeconds());
            cmd.Parameters.Add(new SqliteParameter("$full",
                _lastFullScanCompletedAt > DateTime.MinValue
                    ? (object)new DateTimeOffset(_lastFullScanCompletedAt).ToUnixTimeSeconds()
                    : DBNull.Value));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist index_state cadence");
        }
    }

    public void Dispose()
    {
        _writeThrough.Writer.TryComplete();
        _sweepGate.Dispose();
    }

    private readonly record struct WriteThroughItem(string Model, string AxType, string Name);

    /// <summary>
    /// IProgress sink that drops events. Search-triggered sweeps don't
    /// have anyone to stream progress to; the bootstrap path also runs
    /// silent. The status RPC surfaces what callers actually need.
    /// </summary>
    private sealed class SinkReporter : IProgress<IndexProgressEvent>
    {
        public void Report(IndexProgressEvent value) { /* drop */ }
    }
}
