using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Xpp.Service.Embeddings;

/// <summary>
/// Local in-process embedding generator for Qwen3-Embedding-0.6B (int8 ONNX),
/// behind the Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput,
/// TEmbedding}"/> seam so the backend stays swappable.
///
/// Recipe (verified empirically — related strings ~0.79 cosine vs ~0.29 for
/// unrelated):
///   - Tokenize with a GPT-2-family byte-level BPE (CodeGenTokenizer) built from
///     vocab.json + the special tokens merged in from tokenizer.json.
///   - Append the EOS token (&lt;|endoftext|&gt;, 151643) and LEFT-pad each
///     batch (so the last position is always the real EOS).
///   - The export is a decoder-with-past graph: feed input_ids / attention_mask
///     / position_ids (= max(0, cumsum(mask)-1) for RoPE under left padding) and
///     56 empty past_key_values tensors; read last_hidden_state.
///   - LAST-token pooling (Qwen3-Embedding is a causal-LM embedder), Matryoshka
///     truncate 1024 -&gt; Dim, L2-normalize.
///
/// Lazy-initialized: the ONNX session + tokenizer load on first use, after the
/// model has been self-downloaded (gated by <see cref="ModelStatus"/>). Throws
/// if called before the model is ready.
/// </summary>
public sealed class QwenEmbeddingGenerator : IEmbeddingProvider
{
    private const long EosTokenId = 151643;     // <|endoftext|>
    private const int HiddenSize = 1024;

    private readonly EmbeddingOptions _options;
    private readonly EmbeddingPaths _paths;
    private readonly ModelStatus _status;
    private readonly ILogger<QwenEmbeddingGenerator> _logger;
    private readonly object _initLock = new();
    private readonly SemaphoreSlim _runLock = new(1, 1); // serialize ORT runs (one heavy session)

    private InferenceSession? _session;
    private CodeGenTokenizer? _tokenizer;
    private string[] _pastInputNames = Array.Empty<string>();
    // We only ever read the hidden states; naming the single output we want lets
    // ORT skip allocating/returning the 56 present_key_values tensors (~GBs of
    // throwaway buffers per batch on CPU).
    private static readonly string[] WantedOutputs = { "last_hidden_state" };

    public QwenEmbeddingGenerator(
        EmbeddingOptions options, EmbeddingPaths paths, ModelStatus status,
        ILogger<QwenEmbeddingGenerator> logger)
    {
        _options = options;
        _paths = paths;
        _status = status;
        _logger = logger;
    }

    public int Dim => _options.Dim;
    public bool IsReady => _status.IsReady;

    /// <summary>Format a user query with the instruction prefix Qwen3-Embedding
    /// expects (documents are embedded raw).</summary>
    public string FormatQuery(string query) => string.Format(_options.QueryInstruction, query);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var texts = values as IList<string> ?? values.ToList();

        // Tokenize everything up front, then process in length-sorted batches.
        // Sequences are LEFT-padded to the batch's longest member, and the model
        // runs over every padded position — so batching a 512-token method with
        // fifteen 30-token ones wastes ~16x the compute on padding. Sorting by
        // length first makes each batch near-uniform, cutting padding to almost
        // nothing. Results are scattered back to the caller's original order so
        // the IEmbeddingGenerator contract (output[i] <-> input[i]) holds.
        var seqs = new long[texts.Count][];
        for (var i = 0; i < texts.Count; i++) seqs[i] = Tokenize(texts[i]);

        var order = new int[texts.Count];
        for (var i = 0; i < texts.Count; i++) order[i] = i;
        Array.Sort(order, (a, b) => seqs[a].Length.CompareTo(seqs[b].Length));

        var vectors = new float[texts.Count][];
        for (var start = 0; start < order.Length; start += _options.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(_options.BatchSize, order.Length - start);
            var batchSeqs = new long[count][];
            for (var j = 0; j < count; j++) batchSeqs[j] = seqs[order[start + j]];

            await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var batchVecs = RunBatch(batchSeqs);
                for (var j = 0; j < count; j++) vectors[order[start + j]] = batchVecs[j];
            }
            finally { _runLock.Release(); }
        }

        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var v in vectors) result.Add(new Embedding<float>(v));
        return result;
    }

    /// <summary>Encode one text to token ids, truncated to MaxTokens-1 with the
    /// EOS token appended (Qwen3-Embedding pools the final EOS position).</summary>
    private long[] Tokenize(string text)
    {
        var ids = _tokenizer!.EncodeToIds(text);
        var len = Math.Min(ids.Count, _options.MaxTokens - 1);
        var arr = new long[len + 1];
        for (var i = 0; i < len; i++) arr[i] = ids[i];
        arr[len] = EosTokenId;
        return arr;
    }

    // ---- inference -----------------------------------------------------
    private IReadOnlyList<float[]> RunBatch(IReadOnlyList<long[]> seqs)
    {
        var maxLen = 1;
        foreach (var s in seqs) maxLen = Math.Max(maxLen, s.Length);

        var b = seqs.Count;
        var inputIds = new long[b * maxLen];
        var mask = new long[b * maxLen];
        var pos = new long[b * maxLen];
        for (var r = 0; r < b; r++)
        {
            var s = seqs[r];
            var pad = maxLen - s.Length;            // LEFT padding
            long running = 0;
            for (var c = 0; c < maxLen; c++)
            {
                var idx = r * maxLen + c;
                if (c < pad)
                {
                    inputIds[idx] = EosTokenId;     // pad == <|endoftext|>
                    mask[idx] = 0;
                    pos[idx] = 0;
                }
                else
                {
                    inputIds[idx] = s[c - pad];
                    mask[idx] = 1;
                    pos[idx] = running;             // 0,1,2,... over real tokens
                    running++;
                }
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, new[] { b, maxLen })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { b, maxLen })),
            NamedOnnxValue.CreateFromTensor("position_ids", new DenseTensor<long>(pos, new[] { b, maxLen })),
        };
        foreach (var pn in _pastInputNames)
            inputs.Add(NamedOnnxValue.CreateFromTensor(pn, new DenseTensor<float>(Array.Empty<float>(), new[] { b, 8, 0, 128 })));

        using var results = _session!.Run(inputs, WantedOutputs);
        var hidden = results.First(r => r.Name == "last_hidden_state").AsTensor<float>(); // [b, maxLen, 1024]

        var outRows = new List<float[]>(b);
        for (var r = 0; r < b; r++)
        {
            // last-token pooling: last position is the real EOS (left-padded).
            var vec = new float[_options.Dim];
            double norm = 0;
            for (var d = 0; d < _options.Dim; d++)
            {
                var x = hidden[r, maxLen - 1, d];
                vec[d] = x;
                norm += (double)x * x;
            }
            norm = Math.Sqrt(norm);
            if (norm > 0) for (var d = 0; d < _options.Dim; d++) vec[d] = (float)(vec[d] / norm);
            outRows.Add(vec);
        }
        return outRows;
    }

    // ---- lazy init -----------------------------------------------------
    private void EnsureInitialized()
    {
        if (_session != null) return;
        if (!_status.IsReady)
            throw new InvalidOperationException("Embedding model is not ready yet (still downloading or disabled).");

        lock (_initLock)
        {
            if (_session != null) return;

            var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
            // Threads are the main CPU-inference lever, but MORE IS NOT BETTER:
            // ONNX Runtime's default IntraOpNumThreads is the PHYSICAL core count,
            // which is optimal. Forcing it to the logical count (e.g. cores-1 on a
            // hyperthreaded box) oversubscribes the physical cores and thrashes —
            // it can stall outright. So when IntraOpThreads <= 0 (the default) we
            // leave ORT's auto value untouched and only override on an explicit,
            // deliberate config. InterOp stays at 1 (a transformer forward is one
            // sequential op chain; intra-op parallelism is what matters).
            if (_options.IntraOpThreads > 0) so.IntraOpNumThreads = _options.IntraOpThreads;
            so.InterOpNumThreads = 1;
            so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            var session = new InferenceSession(_paths.ModelPath, so);
            _logger.LogInformation(
                "ONNX session ready: intraOpThreads={Threads} ({Cores} logical cores)",
                _options.IntraOpThreads > 0 ? _options.IntraOpThreads.ToString() : "ORT-auto", Environment.ProcessorCount);
            _pastInputNames = session.InputMetadata.Keys.Where(k => k.StartsWith("past")).ToArray();

            _tokenizer = BuildTokenizer();
            _session = session; // publish last
            _logger.LogInformation("Embedding generator ready ({Dim}-d, {Past} past inputs)", _options.Dim, _pastInputNames.Length);
        }
    }

    /// <summary>vocab.json holds the base BPE vocab; the special tokens
    /// (&lt;|endoftext|&gt; etc., ids 151643+) live in tokenizer.json's
    /// added_tokens. CodeGenTokenizer requires its unknown/eos token to be in
    /// the vocab, so merge them before constructing.</summary>
    private CodeGenTokenizer BuildTokenizer()
    {
        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_paths.VocabPath))
                    ?? throw new InvalidOperationException("vocab.json failed to parse.");
        using (var tj = JsonDocument.Parse(File.ReadAllText(_paths.TokenizerJsonPath)))
        {
            if (tj.RootElement.TryGetProperty("added_tokens", out var added))
                foreach (var a in added.EnumerateArray())
                    vocab[a.GetProperty("content").GetString()!] = a.GetProperty("id").GetInt32();
        }
        var mergedVocab = Path.Combine(_paths.ModelDir, "vocab_merged.json");
        File.WriteAllText(mergedVocab, JsonSerializer.Serialize(vocab));

        using var v = File.OpenRead(mergedVocab);
        using var m = File.OpenRead(_paths.MergesPath);
        return CodeGenTokenizer.Create(v, m, addPrefixSpace: false, addBeginOfSentence: false, addEndOfSentence: false);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _runLock.Dispose();
    }
}
