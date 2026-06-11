using Microsoft.Extensions.AI;

namespace Xpp.Service.Embeddings;

/// <summary>
/// Null-object embedding backend registered when no credentials/model are
/// configured. Keeps the DI graph satisfiable (the gRPC service and embedder
/// always depend on <see cref="IEmbeddingProvider"/>) while reporting
/// not-ready, so the embedder idles and semantic search degrades to FTS. Never
/// produces vectors.
/// </summary>
public sealed class DisabledEmbeddingProvider : IEmbeddingProvider
{
    private readonly int _dim;
    public DisabledEmbeddingProvider(int dim) => _dim = dim;

    public bool IsReady => false;
    public int Dim => _dim;
    public string FormatQuery(string query) => query;

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Embedding subsystem is disabled (no backend configured).");

    public void Dispose() { }
}
