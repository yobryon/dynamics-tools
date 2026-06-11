using System.ClientModel.Primitives;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI.Embeddings;

namespace Xpp.Service.Embeddings;

/// <summary>
/// Embedding backend that calls an Azure OpenAI embeddings deployment
/// (text-embedding-3-small by default) over HTTPS. This is the active backend
/// whenever Azure credentials are configured — cloud inference is orders of
/// magnitude faster than the local CPU ONNX path for batch embedding.
///
/// Sits behind <see cref="IEmbeddingProvider"/> like every backend. The model
/// is symmetric, so <see cref="FormatQuery"/> is the identity (no instruction
/// prefix). The vector width is requested via the Matryoshka <c>dimensions</c>
/// parameter so it matches the vec0 table without a separate truncation step.
/// </summary>
public sealed class AzureOpenAIEmbeddingGenerator : IEmbeddingProvider
{
    private readonly EmbeddingClient _client;
    private readonly OpenAI.Embeddings.EmbeddingGenerationOptions _requestOptions;
    private readonly int _dim;
    private readonly int _maxInputChars;
    private readonly ILogger<AzureOpenAIEmbeddingGenerator> _logger;

    public AzureOpenAIEmbeddingGenerator(
        string endpoint, string apiKey, string deployment, int dim, int maxInputChars,
        ILogger<AzureOpenAIEmbeddingGenerator> logger)
    {
        // Saturating the deployment's TPM quota inevitably brushes the per-minute
        // rate bucket, returning HTTP 429 with a Retry-After. The default retry
        // policy honors Retry-After and backs off exponentially; we just raise the
        // attempt count so a transient 429 is absorbed transparently instead of
        // bubbling up and aborting a drain page.
        var options = new AzureOpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(maxRetries: 8)
        };
        var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey), options);
        _client = azure.GetEmbeddingClient(deployment);
        _dim = dim;
        _maxInputChars = maxInputChars;
        _requestOptions = new OpenAI.Embeddings.EmbeddingGenerationOptions { Dimensions = dim };
        _logger = logger;
        _logger.LogInformation(
            "Azure OpenAI embeddings: deployment={Deployment} dim={Dim} endpoint={Endpoint}",
            deployment, dim, endpoint);
    }

    public bool IsReady => true;        // configured at construction
    public int Dim => _dim;

    // text-embedding-3 is a symmetric model: queries and documents are embedded
    // the same way, no instruction prefix.
    public string FormatQuery(string query) => query;

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values as IList<string> ?? values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>();
        if (inputs.Count == 0) return result;

        // Guard the per-input token ceiling (and cost): truncate by characters.
        // tokens <= chars always, so a char cap keeps every input safely under
        // the model's 8k-token limit. Most method bodies are far shorter.
        var prepared = new string[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var t = inputs[i];
            prepared[i] = t.Length > _maxInputChars ? t.Substring(0, _maxInputChars) : t;
        }

        var response = await _client.GenerateEmbeddingsAsync(prepared, _requestOptions, cancellationToken)
            .ConfigureAwait(false);

        // The collection comes back in request order.
        foreach (var e in response.Value)
            result.Add(new Embedding<float>(e.ToFloats().ToArray()));

        return result;
    }

    public void Dispose() { /* the underlying HTTP client is managed by the SDK */ }
}
