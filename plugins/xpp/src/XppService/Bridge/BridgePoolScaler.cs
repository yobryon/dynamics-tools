using Microsoft.Extensions.Hosting;

namespace Xpp.Service.Bridge;

/// <summary>
/// Background policy that sizes the <see cref="BridgePool"/> to the live
/// workload. Runs a short tick loop and, each tick:
///   1. Reaps dead workers (closes the old "a crashed worker stays dead" gap)
///      and tops the pool back up to Min.
///   2. Scales UP by one when every worker is busy (no idle worker) and we're
///      below Max — a full-corpus rebuild fires Max parallel reads, so the pool
///      ramps to Max within a few ticks and stays there for the burst.
///   3. Scales DOWN by one when a worker has been idle (zero in-flight) longer
///      than the pool's idle timeout and we're above Min — so steady-state
///      single-query usage settles back to Min (~200MB/worker reclaimed).
///
/// One step per tick keeps the response gradual (no thrash on a brief spike)
/// while still reaching Max in a few seconds under sustained load.
/// </summary>
public sealed class BridgePoolScaler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly BridgePool _pool;
    private readonly ILogger<BridgePoolScaler> _logger;

    public BridgePoolScaler(BridgePool pool, ILogger<BridgePoolScaler> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Bridge pool scaler tick failed"); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var workers = _pool.Workers;

        // 1. Reap dead workers first (don't count them toward capacity / load).
        foreach (var w in workers)
        {
            if (!w.IsAlive)
            {
                await _pool.RetireAsync(w, "process exited").ConfigureAwait(false);
            }
        }

        // 2. Top back up to Min (covers reaped workers and any deficit).
        while (_pool.Size < _pool.Min)
        {
            if (!await _pool.GrowAsync(ct).ConfigureAwait(false)) break;
        }

        workers = _pool.Workers;
        if (workers.Count == 0) return;

        // 3. Scale up: every worker busy and we have headroom -> add one.
        var anyIdle = workers.Any(w => w.InFlight == 0);
        if (!anyIdle && _pool.Size < _pool.Max)
        {
            await _pool.GrowAsync(ct).ConfigureAwait(false);
            return; // one step per tick
        }

        // 4. Scale down: a worker idle past the timeout and we're above Min.
        if (_pool.Size > _pool.Min)
        {
            var now = Environment.TickCount64;
            var idleMs = (long)_pool.IdleTimeout.TotalMilliseconds;
            var stale = workers.FirstOrDefault(w => w.InFlight == 0 && now - w.LastActiveTicks >= idleMs);
            if (stale != null)
                await _pool.RetireAsync(stale, $"idle > {_pool.IdleTimeout.TotalSeconds:0}s").ConfigureAwait(false);
        }
    }
}
