using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Grpc.Core;
using Xpp.Service.Bridge;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Embeddings;
using Xpp.Service.Indexing;
using Xpp.Service.Storage;

namespace Xpp.Service.Services;

/// <summary>
/// Implements the public Ping/GetStatus RPCs.
///
/// Ping is the wire-test for the whole stack: client gRPC -> service ->
/// bridge JSON-RPC -> bridge ping handler -> back. If this round-trips,
/// every layer is healthy. We deliberately call the bridge even though
/// we could fabricate the response locally; the point is to prove the
/// path.
///
/// GetStatus reports service-internal state (bridge alive? index ready?)
/// without bothering the bridge, so it's still useful when the bridge is
/// dead and you want to know that's why everything is failing.
/// </summary>
/// <summary>
/// IProgress&lt;T&gt; implementation that writes synchronously into a
/// ChannelWriter. Used by the gRPC handler so the "complete" event
/// emitted right before the indexer task returns can't race the
/// channel.Writer.TryComplete() in finally.
/// </summary>
file sealed class ChannelReporter : IProgress<IndexProgressEvent>
{
    private readonly ChannelWriter<IndexProgressEvent> _writer;
    public ChannelReporter(ChannelWriter<IndexProgressEvent> writer) => _writer = writer;
    public void Report(IndexProgressEvent value) => _writer.TryWrite(value);
}

public sealed partial class PingGrpcService : XppService.XppServiceBase
{
    private readonly BridgePool _bridgePool;
    private readonly BridgeClient _bridgeClient;
    private readonly BridgeOptions _bridgeOptions;
    private readonly IndexDatabase _db;
    private readonly Indexer _indexer;
    private readonly IndexLifecycle _lifecycle;
    private readonly IEmbeddingProvider _embeddings;
    private readonly EmbeddingOptions _embeddingOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PingGrpcService> _logger;

    public PingGrpcService(BridgePool bridgePool, BridgeClient bridgeClient, BridgeOptions bridgeOptions, IndexDatabase db, Indexer indexer, IndexLifecycle lifecycle, IEmbeddingProvider embeddings, EmbeddingOptions embeddingOptions, IHostApplicationLifetime lifetime, ILogger<PingGrpcService> logger)
    {
        _lifetime = lifetime;
        _bridgePool = bridgePool;
        _bridgeClient = bridgeClient;
        _bridgeOptions = bridgeOptions;
        _db = db;
        _indexer = indexer;
        _lifecycle = lifecycle;
        _embeddings = embeddings;
        _embeddingOptions = embeddingOptions;
        _logger = logger;
    }

    public override async Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        // Mirror the echo through the bridge so we exercise the full path.
        var paramsObj = new JsonObject { ["echo"] = request.Echo };

        JsonNode? bridgeResult;
        try
        {
            bridgeResult = await _bridgePool.Acquire().InvokeAsync("ping", paramsObj, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            _logger.LogError(ex, "Bridge ping failed: {Code} {Message}", ex.Code, ex.Message);
            throw new RpcException(new Status(StatusCode.Internal, $"bridge ping failed: {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            // Bridge isn't alive.
            throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
        }

        var bridgeEcho = bridgeResult?["echo"]?.GetValue<string>() ?? string.Empty;
        var bridgeVersion = bridgeResult?["bridgeVersion"]?.GetValue<string>() ?? "unknown";
        // AssemblyVersion is stamped from plugin.json via Directory.Build.props,
        // so ToString(3) is the plugin semver ("0.1.0") — the comparable value
        // the newest-wins negotiation keys on.
        var pluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        return new PingResponse
        {
            Echo = bridgeEcho,
            ServerTime = DateTime.UtcNow.ToString("O"),
            // Composite, human-readable, for diagnostics only — don't parse it.
            ServiceVersion = $"service={pluginVersion}; bridge={bridgeVersion}",
            // Bare, comparable semver — the version-negotiation field.
            PluginVersion = pluginVersion,
            ProcessId = Environment.ProcessId,
        };
    }

    /// <summary>
    /// Newest-wins takeover: a newer-build MCP asks this service to stand down
    /// so its own service can own the box. See
    /// docs/versioning-and-servicing-design.md.
    ///
    /// Ordering matters. We must RETURN before we stop, or the caller sees the
    /// pipe drop mid-call and can't distinguish "accepted and stopping" from
    /// "crashed". So we schedule StopApplication on a short delay and answer
    /// immediately; the host's own graceful-shutdown path then drains in-flight
    /// calls, disposes the bridge pool, checkpoints the DB, and releases the
    /// pipe + global mutex on the way out.
    ///
    /// Idempotent: <see cref="IHostApplicationLifetime.StopApplication"/> is a
    /// no-op once stopping has begun, and we gate on ApplicationStopping so a
    /// takeover storm (several new sessions starting at once) can't pile up.
    /// </summary>
    public override Task<ShutdownResponse> RequestShutdown(ShutdownRequest request, ServerCallContext context)
    {
        var pluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "(no reason given)" : request.Reason;

        if (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            _logger.LogInformation("RequestShutdown ignored (already stopping); reason: {Reason}", reason);
        }
        else
        {
            _logger.LogWarning(
                "Shutdown requested: {Reason}. This service (plugin {Version}, pid {Pid}) will stop so a newer build can take over.",
                reason, pluginVersion, Environment.ProcessId);

            _ = Task.Run(async () =>
            {
                // Give the response a moment to make it back over the pipe
                // before we start tearing the listener down.
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                _lifetime.StopApplication();
            });
        }

        return Task.FromResult(new ShutdownResponse
        {
            Accepted = true,
            ProcessId = Environment.ProcessId,
            PluginVersion = pluginVersion,
        });
    }

    public override async Task RebuildIndex(
        RebuildRequest request,
        IServerStreamWriter<IndexProgress> responseStream,
        ServerCallContext context)
    {
        // Bridge progress events into the gRPC stream. We use a bounded
        // channel as a buffer so the indexer doesn't block on slow client
        // reads, but a slow consumer can't blow our memory either: at the
        // capacity we shed old events (drop oldest) and continue. The
        // contract is "progress is advisory", so dropping intermediate
        // updates is fine.
        var channel = Channel.CreateBounded<IndexProgressEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        // Direct synchronous write into the channel. Using IProgress<T>
        // here lost the final "complete" event under load — Progress<T>
        // posts its callbacks to the thread pool, so a Report() issued
        // immediately before the indexer task returned could race the
        // channel.Writer.TryComplete() in the finally block. A direct
        // delegate runs before Report() returns, so the channel's state
        // is always consistent with what the indexer has emitted.
        var producer = new ChannelReporter(channel.Writer);

        var filter = request.ModelsFilter?.ToArray() ?? Array.Empty<string>();
        var maxPhase2 = request.MaxObjectsPhase2;
        var incremental = request.Incremental;
        var indexerTask = Task.Run(async () =>
        {
            try
            {
                await _indexer.RunPhase1Async(producer, context.CancellationToken, filter).ConfigureAwait(false);
                await _indexer.RunPhase2Async(producer, context.CancellationToken, filter, maxPhase2, incremental).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Log here so even if the stream consumer is gone (deadline
                // expired, client hung up) we still see the failure in
                // service logs.
                _logger.LogError(ex, "Indexer failed");
                throw;
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        await foreach (var evt in channel.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(new IndexProgress
            {
                Phase = evt.Phase ?? string.Empty,
                CurrentModel = evt.CurrentModel ?? string.Empty,
                CurrentAxType = evt.CurrentAxType ?? string.Empty,
                ObjectsSeen = evt.ObjectsSeen,
                ObjectsTotalEstimate = evt.ObjectsTotalEstimate,
                Done = evt.Done,
                Message = evt.Message ?? string.Empty
            }).ConfigureAwait(false);
        }

        // Surface any failure from the indexer as a gRPC error rather than
        // a silent stream completion.
        await indexerTask.ConfigureAwait(false);
    }

    public override async Task<StatusResponse> GetStatus(StatusRequest request, ServerCallContext context)
    {
        // index_state is a singleton row maintained by the writer; reading it
        // is O(1) and doesn't count anything. If the row hasn't seen its
        // first update yet, the counts are all zero and last_full_scan_at
        // is NULL.
        long objectCount = 0;
        long methodCount = 0;
        long refCount = 0;
        long labelCount = 0;
        long lastFullScan = 0;
        long embeddingCount = 0;
        long embeddableCount = 0;
        bool codeSearchReady = true;
        try
        {
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            // index_state carries the summary counts maintained by the writer.
            // refs aren't summarised there (cheap to COUNT directly). The last
            // probe asks whether the method-body FTS has any indexed document —
            // EXISTS short-circuits so it's O(1), not a full count.
            cmd.CommandText = @"
                SELECT
                    (SELECT object_count       FROM index_state WHERE id=1),
                    (SELECT method_count       FROM index_state WHERE id=1),
                    (SELECT COUNT(*)           FROM refs),
                    (SELECT label_count        FROM index_state WHERE id=1),
                    (SELECT last_full_scan_at  FROM index_state WHERE id=1),
                    (SELECT embedding_count    FROM index_state WHERE id=1),
                    (SELECT EXISTS(SELECT 1 FROM methods_fts_docsize LIMIT 1)),
                    (SELECT embeddable_count   FROM index_state WHERE id=1)";
            using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                objectCount  = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                methodCount  = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                refCount     = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                labelCount   = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                lastFullScan = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                embeddingCount = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                var ftsHasDocs = !reader.IsDBNull(6) && reader.GetInt64(6) != 0;
                embeddableCount = reader.IsDBNull(7) ? 0 : reader.GetInt64(7);
                // Ready when the FTS is populated, or when there's nothing to
                // index yet (no methods → an honest empty, not a broken index).
                codeSearchReady = ftsHasDocs || methodCount == 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetStatus could not read index_state");
        }

        // Embedding subsystem state: blend the model-acquisition phase with the
        // storage-side availability of sqlite-vec. embedding_total is the
        // denominator the background embedder is working toward.
        var embeddingState = DescribeEmbeddingState();
        // Only rows the embedder can actually reach: its drain predicates skip
        // empty content (length(trim(...)) > 0), and runtime-source objects
        // carry empty source_code by design — so method_count + label_count is a
        // denominator it can never hit, leaving a finished index reading ~98%
        // forever. embeddable_count is maintained by UpdateIndexState; fall back
        // to the old upper bound until the first sweep after the v7 migration
        // populates it (better a slightly-high denominator than a zero one).
        long embeddingTotal = embeddableCount > 0 ? embeddableCount : methodCount + labelCount;

        var lastSweep = _lifecycle.LastSweepCompletedAt;
        var lastFull  = _lifecycle.LastFullScanCompletedAt;
        return new StatusResponse
        {
            BridgeHealthy = _bridgePool.AllAlive,
            // index_ready: true when there's any populated state — even
            // mid-bootstrap, search returns partial results. Agents check
            // index_state for the precise phase.
            IndexReady = objectCount > 0,
            ObjectCount = objectCount,
            MethodCount = methodCount,
            ReferenceCount = refCount,
            LabelCount = labelCount,
            LastIndexUpdate = lastFullScan == 0
                ? string.Empty
                : DateTimeOffset.FromUnixTimeSeconds(lastFullScan).ToString("O"),
            IndexState = _lifecycle.DescribePhase(objectCount),
            SweepInProgress = _lifecycle.SweepInProgress,
            LastSweepAt = lastSweep == DateTime.MinValue ? string.Empty : lastSweep.ToString("O"),
            LastFullScanAt = lastFull == DateTime.MinValue ? string.Empty : lastFull.ToString("O"),
            EmbeddingState = embeddingState,
            EmbeddingCount = embeddingCount,
            EmbeddingTotal = embeddingTotal,
            CodeSearchReady = codeSearchReady,
        };
    }

    /// <summary>Maps backend readiness + sqlite-vec availability to the single
    /// status string the agent sees.</summary>
    private string DescribeEmbeddingState()
    {
        if (!_embeddingOptions.Enabled) return "disabled";
        if (!_db.VecEnabled) return "unavailable";   // sqlite-vec missing
        return _embeddings.IsReady ? "ready" : "warming";
    }

    public override async Task ListModules(
        ListModulesRequest request,
        IServerStreamWriter<ModuleInfo> responseStream,
        ServerCallContext context)
    {
        // Served straight from the cached models table — no bridge call.
        // The indexer keeps is_binary in sync via every phase-1 walk.
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name, display_name, publisher, version, layer,
                   is_custom, is_binary, dependencies_json
            FROM models
            WHERE ($filter   = '' OR name LIKE '%' || $filter || '%')
              AND ($binary   = 0 OR is_binary = 1)
            ORDER BY name COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$filter", request.NameFilter ?? string.Empty);
        cmd.Parameters.AddWithValue("$binary", request.BinaryOnly ? 1 : 0);

        using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
        {
            var depsJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);
            string[] deps;
            try { deps = System.Text.Json.JsonSerializer.Deserialize<string[]>(depsJson) ?? Array.Empty<string>(); }
            catch { deps = Array.Empty<string>(); }

            var info = new ModuleInfo
            {
                Name = reader.GetString(0),
                DisplayName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Publisher = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Version = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Layer = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                IsCustom = !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                IsBinary = !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
            };
            info.Dependencies.AddRange(deps);
            await responseStream.WriteAsync(info).ConfigureAwait(false);
        }
    }
}
