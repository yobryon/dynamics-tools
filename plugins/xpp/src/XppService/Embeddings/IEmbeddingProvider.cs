using Microsoft.Extensions.AI;

namespace Xpp.Service.Embeddings;

/// <summary>
/// The service-internal embedding seam. Extends the standard Microsoft.Extensions.AI
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> with the few extras the
/// embedder and search handlers need, so the backend (Azure OpenAI, local ONNX,
/// or a disabled stub) is swappable without touching callers.
/// </summary>
public interface IEmbeddingProvider : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>True when embeddings can actually be produced right now (creds
    /// present / model loaded). When false the embedder idles and semantic
    /// search reports unavailable.</summary>
    bool IsReady { get; }

    /// <summary>Stored vector width — must match the vec0 table dimension.</summary>
    int Dim { get; }

    /// <summary>Apply any backend-specific query framing before embedding a
    /// search query. Asymmetric models (e.g. Qwen3-Embedding) prepend an
    /// instruction; symmetric ones (text-embedding-3) return the query as-is.
    /// Documents are always embedded raw.</summary>
    string FormatQuery(string query);
}
