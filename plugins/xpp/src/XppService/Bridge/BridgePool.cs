namespace Xpp.Service.Bridge;

/// <summary>
/// Owns a dynamically-sized set of <see cref="BridgeProcess"/> workers,
/// bounded by [Min, Max], and hands one out per request. Each worker is its
/// own net48 child process with its own provider cache, so the live worker
/// count is the unit of true bridge parallelism (the bridge processes its
/// stdin requests strictly sequentially, so a worker serves exactly one
/// request at a time).
///
/// Sizing is driven by <see cref="BridgePoolScaler"/>: the pool starts at
/// <see cref="Min"/> (kept warm so user queries never pay cold-start), grows
/// toward <see cref="Max"/> when every worker is busy (e.g. a full-corpus
/// rebuild), and retires workers that sit idle past the idle timeout. This
/// class owns the MECHANISM (snapshot, grow, shrink, reap); the scaler owns
/// the POLICY (when to call them).
///
/// Concurrency: <see cref="Acquire"/> is lock-free — it reads a
/// <c>volatile</c> array snapshot and picks the least-busy worker. Mutations
/// (grow/shrink/reap/start) take <c>_mutateLock</c> and publish a fresh
/// snapshot array; the scaler is the only mutator after startup, so there's
/// no write contention.
/// </summary>
public sealed class BridgePool : IAsyncDisposable
{
    private readonly Func<BridgeProcess> _workerFactory;
    private readonly ILogger<BridgePool> _logger;
    private readonly object _mutateLock = new();

    private volatile BridgeProcess[] _workers = Array.Empty<BridgeProcess>();
    private bool _disposed;

    public int Min { get; }
    public int Max { get; }
    public TimeSpan IdleTimeout { get; }

    public BridgePool(Func<BridgeProcess> workerFactory, BridgeOptions options, ILogger<BridgePool> logger)
    {
        _workerFactory = workerFactory;
        _logger = logger;
        Min = Math.Max(1, options.Min);
        Max = Math.Max(Min, options.Max);
        IdleTimeout = options.IdleTimeout > TimeSpan.Zero ? options.IdleTimeout : TimeSpan.FromSeconds(60);
    }

    /// <summary>Current live worker count.</summary>
    public int Size => _workers.Length;

    /// <summary>True when every live worker's process is alive.</summary>
    public bool AllAlive => _workers.All(w => w.IsAlive);

    /// <summary>Current snapshot of live workers (used by lifecycle + scaler).</summary>
    public IReadOnlyList<BridgeProcess> Workers => _workers;

    /// <summary>
    /// Least-busy worker selection. Reads the lock-free snapshot and returns
    /// the worker with the fewest in-flight requests (an idle worker, InFlight
    /// == 0, wins). Because bridges are sequential, this routes a new request
    /// to a free worker when one exists and load-balances otherwise.
    /// </summary>
    public BridgeProcess Acquire()
    {
        var workers = _workers;
        if (workers.Length == 0)
            throw new InvalidOperationException("BridgePool has no live workers.");

        var best = workers[0];
        var bestLoad = best.InFlight;
        for (var i = 1; i < workers.Length && bestLoad > 0; i++)
        {
            var load = workers[i].InFlight;
            if (load < bestLoad)
            {
                best = workers[i];
                bestLoad = load;
            }
        }
        return best;
    }

    /// <summary>Spawn the initial <see cref="Min"/> workers in parallel and
    /// publish them. Completes only when every worker process has started.</summary>
    public async Task StartAllAsync(CancellationToken ct)
    {
        var initial = Enumerable.Range(0, Min).Select(_ => NewWorker()).ToArray();
        await Task.WhenAll(initial.Select(w => w.StartAsync(ct))).ConfigureAwait(false);
        lock (_mutateLock) _workers = initial;
    }

    /// <summary>
    /// Add one worker (up to Max), starting and readiness-probing it before it
    /// joins the snapshot so it never receives traffic before its provider is
    /// initialized. Returns true if a worker was added.
    /// </summary>
    public async Task<bool> GrowAsync(CancellationToken ct)
    {
        if (_disposed || Size >= Max) return false;

        var worker = NewWorker();
        try
        {
            await worker.StartAsync(ct).ConfigureAwait(false);
            // Warm the provider so the first real request doesn't eat init cost.
            await worker.InvokeAsync("ping", new System.Text.Json.Nodes.JsonObject { ["echo"] = "scale-up-probe" }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bridge scale-up worker {Id} failed to start; discarding", worker.WorkerId);
            try { await worker.DisposeAsync().ConfigureAwait(false); } catch { }
            return false;
        }

        lock (_mutateLock)
        {
            if (_disposed || _workers.Length >= Max)
            {
                // Lost a race or disposed mid-grow — drop the spare.
                _ = worker.DisposeAsync();
                return false;
            }
            _workers = Append(_workers, worker);
            _logger.LogInformation("Bridge pool grew to {Count} workers (added {Id})", _workers.Length, worker.WorkerId);
            return true;
        }
    }

    /// <summary>
    /// Remove a specific worker from the snapshot (so no new request routes to
    /// it) and dispose it. Caller decides which worker (idle / dead).
    /// </summary>
    public async Task RetireAsync(BridgeProcess worker, string reason)
    {
        lock (_mutateLock)
        {
            if (!_workers.Contains(worker)) return;
            _workers = _workers.Where(w => !ReferenceEquals(w, worker)).ToArray();
            _logger.LogInformation("Bridge pool shrank to {Count} workers (retired {Id}: {Reason})",
                _workers.Length, worker.WorkerId, reason);
        }
        try { await worker.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
    }

    private BridgeProcess NewWorker() => _workerFactory();

    private static BridgeProcess[] Append(BridgeProcess[] arr, BridgeProcess w)
    {
        var next = new BridgeProcess[arr.Length + 1];
        Array.Copy(arr, next, arr.Length);
        next[arr.Length] = w;
        return next;
    }

    public async ValueTask DisposeAsync()
    {
        BridgeProcess[] workers;
        lock (_mutateLock)
        {
            _disposed = true;
            workers = _workers;
            _workers = Array.Empty<BridgeProcess>();
        }
        await Task.WhenAll(workers.Select(async w =>
        {
            try { await w.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort shutdown */ }
        })).ConfigureAwait(false);
    }
}
