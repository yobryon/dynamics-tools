namespace Xpp.Service.Embeddings;

/// <summary>
/// One-bit wakeup between the indexing lifecycle (the producer of embeddable
/// content) and the <see cref="Embedder"/> (the consumer). Whenever a sweep,
/// bootstrap walk, or write-through refresh lands new/changed methods or labels,
/// the producer calls <see cref="Nudge"/>; the embedder, parked on
/// <see cref="WaitAsync"/>, wakes immediately and drains.
///
/// Deliberately edge-not-level: a flurry of nudges collapses to a single
/// pending wake (the semaphore caps at 1), because the embedder always drains
/// <em>all</em> outstanding work on each pass — it doesn't need a per-item
/// signal, just "something changed, look again." The embedder also polls on a
/// timer as a backstop, so a missed nudge only delays embedding, never strands
/// it. This keeps producer and consumer decoupled: the lifecycle depends on
/// this tiny signal, not on the whole embedding subsystem.
/// </summary>
public sealed class EmbeddingWorkSignal
{
    private readonly SemaphoreSlim _sem = new(0, 1);

    /// <summary>Wake the embedder. Cheap and non-blocking; coalesces.</summary>
    public void Nudge()
    {
        try { _sem.Release(); }
        catch (SemaphoreFullException) { /* a wake is already pending — fine */ }
    }

    /// <summary>Park until nudged or the timeout elapses. Returns true if woken
    /// by a nudge, false on timeout (the embedder treats both identically —
    /// re-drain either way).</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _sem.WaitAsync(timeout, ct);
}
