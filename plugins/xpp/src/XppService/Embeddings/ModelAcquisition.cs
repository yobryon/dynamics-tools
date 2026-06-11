using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Xpp.Service.Embeddings;

/// <summary>Lifecycle state of the self-acquired embedding model.</summary>
public enum ModelState { Disabled, Absent, Downloading, Ready, Error }

/// <summary>
/// Shared, thread-safe view of model-acquisition progress so the status RPC
/// (and the embedder, which must wait for Ready) can observe it without
/// referencing the hosted service directly.
/// </summary>
public sealed class ModelStatus
{
    private volatile ModelStateSnapshot _snap = new(ModelState.Absent, "", 0, 0, null);
    public ModelStateSnapshot Current => _snap;
    public void Set(ModelStateSnapshot s) => _snap = s;
    public bool IsReady => _snap.State == ModelState.Ready;
}

public sealed record ModelStateSnapshot(
    ModelState State, string CurrentFile, long DownloadedBytes, long TotalBytes, string? Error);

/// <summary>
/// Self-manages the embedding model the same way the indexer self-manages the
/// cache: on startup, if the model isn't already present (verified by the
/// .complete marker matching the configured version), it downloads the ONNX
/// model + tokenizer files from HuggingFace in the background into
/// %LOCALAPPDATA%\dynamics-xpp\models\&lt;version&gt;\. Service start is NOT
/// blocked — search works (FTS) while the model warms; semantic features come
/// online when state reaches Ready.
///
/// Downloads stream to a .partial sibling and atomically rename on success, so
/// an interrupted run re-pulls cleanly. The .complete marker (containing the
/// model version) is written last; ModelReady gates on it.
/// </summary>
public sealed class ModelAcquisition : BackgroundService
{
    private readonly EmbeddingOptions _options;
    private readonly EmbeddingPaths _paths;
    private readonly ModelStatus _status;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ModelAcquisition> _logger;

    public ModelAcquisition(
        EmbeddingOptions options, EmbeddingPaths paths, ModelStatus status,
        IHttpClientFactory httpFactory, ILogger<ModelAcquisition> logger)
    {
        _options = options;
        _paths = paths;
        _status = status;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _status.Set(new(ModelState.Disabled, "", 0, 0, null));
            _logger.LogInformation("Embedding subsystem disabled (Embedding:Enabled=false)");
            return;
        }

        if (_paths.ModelReady)
        {
            _status.Set(new(ModelState.Ready, "", 0, 0, null));
            _logger.LogInformation("Embedding model already present at {Dir}", _paths.ModelDir);
            return;
        }

        try
        {
            await DownloadAllAsync(ct).ConfigureAwait(false);
            File.WriteAllText(_paths.CompleteMarkerPath, _options.ModelVersion);
            _status.Set(new(ModelState.Ready, "", 0, 0, null));
            _logger.LogInformation("Embedding model ready at {Dir}", _paths.ModelDir);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _status.Set(new(ModelState.Error, "", 0, 0, ex.Message));
            _logger.LogError(ex, "Embedding model download failed; semantic search stays disabled (FTS unaffected)");
        }
    }

    private async Task DownloadAllAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.ModelDir);

        // (repoPath, localPath). Tokenizer/config live at the repo root; the
        // model lives under onnx/. Names are flattened locally.
        var files = new List<(string Repo, string Local)>
        {
            (_options.ModelFile, _paths.ModelPath),
            ("tokenizer.json", _paths.TokenizerJsonPath),
            ("tokenizer_config.json", _paths.TokenizerConfigPath),
            ("config.json", Path.Combine(_paths.ModelDir, "config.json")),
            ("special_tokens_map.json", _paths.SpecialTokensPath),
            ("vocab.json", _paths.VocabPath),
            ("merges.txt", _paths.MergesPath),
        };
        if (_options.ModelDataFile is { Length: > 0 } dataFile)
            files.Add((dataFile, _paths.ModelDataPath!));

        _status.Set(new(ModelState.Downloading, "", 0, 0, null));
        using var http = _httpFactory.CreateClient("hf");

        foreach (var (repo, local) in files)
        {
            if (File.Exists(local)) continue; // resume: keep already-pulled files
            var url = $"{_options.HfBaseUrl}/{_options.HfRepo}/resolve/{_options.HfRevision}/{repo}";
            await DownloadOneAsync(http, url, local, ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadOneAsync(HttpClient http, string url, string local, CancellationToken ct)
    {
        var name = Path.GetFileName(local);
        var partial = local + ".partial";
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Downloading {Name} <- {Url}", name, url);

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            long lastLog = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                _status.Set(new(ModelState.Downloading, name, done, total, null));
                if (done - lastLog >= (50L << 20)) // log every 50 MB
                {
                    lastLog = done;
                    _logger.LogInformation("  {Name}: {Done:N0}/{Total:N0} MB",
                        name, done >> 20, total >> 20);
                }
            }
        }

        if (File.Exists(local)) File.Delete(local);
        File.Move(partial, local);
        _logger.LogInformation("Downloaded {Name} ({Mb:N0} MB) in {Sec:N0}s",
            name, new FileInfo(local).Length >> 20, sw.Elapsed.TotalSeconds);
    }
}
