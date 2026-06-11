namespace Xpp.Service.Embeddings;

/// <summary>
/// Configuration for the local semantic-search embedding subsystem. Bound from
/// IConfiguration ("Embedding:*") with the user-global config.json overlay, so
/// the model variant / dimension / source URLs can be overridden per box
/// without rebuilding. Defaults target Qwen3-Embedding-0.6B (int8 ONNX) at a
/// Matryoshka-truncated 512 dimensions.
///
/// The runtime artifacts (the ONNX model + tokenizer, and the sqlite-vec native
/// extension) are NOT shipped — they're large / native-per-arch. The service
/// self-acquires them on first need into the data dir (see ModelAcquisition),
/// mirroring how it self-manages the index. See docs/semantic-search-design.md.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>Azure OpenAI embedding deployment name. The model behind it is
    /// expected to be text-embedding-3-small (or compatible). Override via
    /// config "Embedding:Deployment" or env AZURE_OPENAI_EMBEDDING_DEPLOYMENT.
    /// Endpoint + key are resolved from the environment (never defaulted in
    /// code) — see Program.cs.</summary>
    public string Deployment { get; init; } = "text-embedding-3-small";

    /// <summary>Per-input character cap for the cloud backend. Since token count
    /// never exceeds character count, this keeps every input safely under the
    /// model's ~8k token limit and bounds cost; method bodies are almost all
    /// well below it.</summary>
    public int MaxInputChars { get; init; } = 8000;

    /// <summary>Master switch. When false the whole subsystem stays dormant:
    /// no model download (ModelAcquisition no-ops), no inference (Embedder
    /// no-ops), no sqlite-vec load (IndexDatabase.VecEnabled is false), and
    /// xpp_search_semantic reports "disabled" / hybrid falls back to FTS. All
    /// the code stays in place; flip this back to true (or set Embedding:Enabled
    /// in config) to re-enable.
    ///
    /// Resolved at startup: turned on automatically when an Azure OpenAI
    /// embedding backend is configured (creds in the environment), off when
    /// nothing is configured. The local CPU ONNX path stays off by default —
    /// it proved far too slow to embed the ~1.3M-item corpus on this hardware.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>HuggingFace repo id hosting the ONNX export + tokenizer.</summary>
    public string HfRepo { get; init; } = "onnx-community/Qwen3-Embedding-0.6B-ONNX";

    /// <summary>HF revision/branch to pin (reproducible downloads).</summary>
    public string HfRevision { get; init; } = "main";

    /// <summary>ONNX model file under the repo's onnx/ folder. Default the
    /// self-contained int8 variant (614 MB). Alternatives: model_q4f16.onnx
    /// (567 MB, smaller), model.onnx (+ model.onnx_data, fp32, best quality).</summary>
    public string ModelFile { get; init; } = "onnx/model_quantized.onnx";

    /// <summary>Optional external-weights sidecar (only fp32/fp16 variants have
    /// one). Null for the self-contained quantized variants.</summary>
    public string? ModelDataFile { get; init; }

    /// <summary>Stable id stamped on every embedding_meta row. The embedder's
    /// pending-work predicate keys off it, so changing it re-embeds the whole
    /// corpus — which is exactly what we want when the backend/model changes
    /// (vectors from different models aren't comparable). Set at startup to an
    /// Azure-specific id when the cloud backend is active.</summary>
    public string ModelVersion { get; set; } = "qwen3-embedding-0.6b-int8-d512";

    /// <summary>Native embedding width before truncation (Qwen3-0.6B = 1024).</summary>
    public int NativeDim { get; init; } = 1024;

    /// <summary>Stored vector width (Matryoshka truncation of NativeDim). 512 is
    /// the quality/size sweet spot (~2.6 GB for the full corpus).</summary>
    public int Dim { get; init; } = 512;

    /// <summary>Max input tokens per text before truncation.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>Texts per ONNX forward pass.</summary>
    public int BatchSize { get; init; } = 16;

    /// <summary>Rows the embedder pulls per pending-work query (one drain page).
    /// A page is split into RequestSize-sized chunks embedded concurrently, so
    /// keep this comfortably &gt;= RequestSize * EmbedConcurrency to keep every
    /// in-flight request busy.</summary>
    public int EmbedReadBatch { get; init; } = 2048;

    /// <summary>Inputs per embedding request (one HTTP call to the cloud
    /// backend). The cloud API accepts large arrays; 256 keeps each request's
    /// token total well under per-request caps while amortizing round-trips.</summary>
    public int RequestSize { get; init; } = 256;

    /// <summary>How many embedding requests to run concurrently within a drain
    /// page. Throughput is ultimately TPM-quota-bound, and a single 256-input
    /// request already pushes most of a ~1M-TPM quota, so a low value (just
    /// enough to hide inter-request latency) is optimal — higher only produces
    /// 429 churn at the same ceiling. Raise this only with a correspondingly
    /// larger deployment quota.</summary>
    public int EmbedConcurrency { get; init; } = 2;

    /// <summary>Backstop poll cadence (seconds). The embedder normally wakes on
    /// an explicit nudge after a sweep / write-through, but also re-checks for
    /// pending work this often so nothing is ever stranded even if a nudge is
    /// missed.</summary>
    public int EmbedPollSeconds { get; init; } = 30;

    /// <summary>Optional cooldown (ms) inserted between forward passes so the
    /// bulk-embedding pass yields CPU to search / the bridge. 0 = run flat out
    /// (the ONNX session is already intra-op bounded by IntraOpThreads).</summary>
    public int EmbedThrottleMs { get; init; } = 0;

    /// <summary>ONNX intra-op thread count. &lt;= 0 (the default) leaves ORT's
    /// own auto value, which is the PHYSICAL core count — the right choice for
    /// CPU inference. Setting this above the physical core count (e.g. to the
    /// logical/hyperthread count) oversubscribes and thrashes, making embedding
    /// slower, so only override with a deliberate, measured value — e.g. to a
    /// small number to keep the background pass out of heavy concurrent bridge
    /// work (compiles, large authoring runs).</summary>
    public int IntraOpThreads { get; init; } = 0;

    /// <summary>The instruction prefix Qwen3-Embedding expects on QUERY text
    /// (documents are embedded raw). "{0}" is the user query.</summary>
    public string QueryInstruction { get; init; } =
        "Instruct: Given a code-search query, retrieve relevant X++ source and metadata.\nQuery: {0}";

    /// <summary>Base URL for HF resolve downloads (override for a mirror).</summary>
    public string HfBaseUrl { get; init; } = "https://huggingface.co";
}
