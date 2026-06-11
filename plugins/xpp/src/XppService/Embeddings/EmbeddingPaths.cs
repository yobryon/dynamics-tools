using Xpp.Service.Storage;

namespace Xpp.Service.Embeddings;

/// <summary>
/// Resolves where the self-acquired embedding artifacts live. Everything sits
/// under the same data dir as the index db (%LOCALAPPDATA%\dynamics-xpp by
/// default) so a user who clears the cache clears the model too, and the
/// "future v2 artifacts (embeddings, ...)" comment in Program.cs holds.
///
///   &lt;dataDir&gt;/models/&lt;modelVersion&gt;/   model.onnx + tokenizer files
///   &lt;dataDir&gt;/runtime/                   vec0.dll (sqlite-vec native)
/// </summary>
public sealed class EmbeddingPaths
{
    private readonly EmbeddingOptions _options;

    public EmbeddingPaths(IndexDatabaseOptions dbOptions, EmbeddingOptions options)
    {
        _options = options;
        DataDir = Path.GetDirectoryName(dbOptions.DatabasePath)
                  ?? AppContext.BaseDirectory;
        ModelDir = Path.Combine(DataDir, "models", options.ModelVersion);
        RuntimeDir = Path.Combine(DataDir, "runtime");
    }

    public string DataDir { get; }
    public string ModelDir { get; }
    public string RuntimeDir { get; }

    /// <summary>Local path the ONNX model lands at (flattened filename — the HF
    /// onnx/ prefix is stripped on download).</summary>
    public string ModelPath => Path.Combine(ModelDir, FlatName(_options.ModelFile));

    public string? ModelDataPath =>
        _options.ModelDataFile is { Length: > 0 } d ? Path.Combine(ModelDir, FlatName(d)) : null;

    public string TokenizerJsonPath => Path.Combine(ModelDir, "tokenizer.json");
    public string TokenizerConfigPath => Path.Combine(ModelDir, "tokenizer_config.json");
    public string SpecialTokensPath => Path.Combine(ModelDir, "special_tokens_map.json");
    public string VocabPath => Path.Combine(ModelDir, "vocab.json");
    public string MergesPath => Path.Combine(ModelDir, "merges.txt");

    /// <summary>Marker written after a fully-verified download; its contents are
    /// the model version, so a model change forces a re-pull.</summary>
    public string CompleteMarkerPath => Path.Combine(ModelDir, ".complete");

    /// <summary>Native sqlite-vec loadable extension, vendored in the repo and
    /// copied next to the build output (native\win-x64\vec0.dll). Loaded with
    /// the explicit entry point "sqlite3_vec_init" (the filename-derived default
    /// sqlite3_vec0_init is not exported). Verified v0.1.9.</summary>
    public string VecDllPath => Path.Combine(AppContext.BaseDirectory, "native", "win-x64", "vec0.dll");

    public bool VecReady => File.Exists(VecDllPath);

    public bool ModelReady =>
        File.Exists(CompleteMarkerPath)
        && File.ReadAllText(CompleteMarkerPath).Trim() == _options.ModelVersion
        && File.Exists(ModelPath)
        && File.Exists(TokenizerJsonPath);

    private static string FlatName(string repoPath) => Path.GetFileName(repoPath);
}
