using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Status tools. The indexer's lifecycle is managed by the service —
/// there is intentionally no agent-facing "rebuild index" tool. Bootstrap
/// runs automatically on first launch, incremental sweeps fire on a lazy
/// 5-minute cadence triggered by search RPCs, and write-through keeps the
/// index in sync after domain mutations.
///
/// For disaster recovery (corrupt DB, schema-version skew the migration
/// path can't bridge), run <c>./tools/dev.ps1 -Action rebuild-index</c>
/// from the plugin directory — that nukes the cache file and lets the
/// next service launch rebuild from scratch.
/// </summary>
[McpServerToolType]
public sealed class StatusTools
{
    private readonly XppServiceConnection _conn;

    public StatusTools(XppServiceConnection conn)
    {
        _conn = conn;
    }

    [McpServerTool(Name = "xpp_status"), Description(
        "Report the XppService's current state: bridge alive, index lifecycle " +
        "phase, counts, sweep cadence. Cheap and side-effect-free. Useful " +
        "at the start of any session that needs search. The indexState " +
        "field tells you whether to trust search results: 'ready' = fully " +
        "populated and idle; 'sweeping' = an incremental refresh is running " +
        "in the background (search still works, results may be slightly " +
        "stale); 'warming' = the first-time bootstrap is in flight (search " +
        "returns partial results — warn the user); 'uninitialized' = no " +
        "data yet (bootstrap will kick automatically). The embeddingState " +
        "field reports the semantic-search subsystem: 'ready' = vector " +
        "search available; 'downloading'/'absent' = the model is still being " +
        "self-acquired (xpp_search_semantic hybrid falls back to full-text " +
        "meanwhile); 'disabled'/'error'/'unavailable' = semantic search off. " +
        "embeddingCount/embeddingTotal track the background embedding pass. " +
        "codeSearchReady reports whether the method-body full-text index is " +
        "populated: when false, xpp_search_code returns 0 for EVERY query " +
        "regardless of matches (index not ready) — treat a zero as untrusted " +
        "and fall back to xpp_find_references / xpp_search_pattern.")]
    public async Task<string> Status(CancellationToken ct = default)
    {
        var s = await _conn.Client.GetStatusAsync(new StatusRequest(), cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            bridgeHealthy    = s.BridgeHealthy,
            indexState       = s.IndexState,
            indexReady       = s.IndexReady,
            sweepInProgress  = s.SweepInProgress,
            objectCount      = s.ObjectCount,
            methodCount      = s.MethodCount,
            codeSearchReady  = s.CodeSearchReady,
            referenceCount   = s.ReferenceCount,
            labelCount       = s.LabelCount,
            embeddingState   = s.EmbeddingState,
            embeddingCount   = s.EmbeddingCount,
            embeddingTotal   = s.EmbeddingTotal,
            lastSweepAt      = s.LastSweepAt,
            lastFullScanAt   = s.LastFullScanAt,
            lastIndexUpdate  = s.LastIndexUpdate,
        });
    }

    [McpServerTool(Name = "xpp_list_modules"), Description(
        "Enumerate the modules visible to the indexer with publisher, " +
        "version, layer, and binary/source flags. Use this to disambiguate " +
        "where an object lives (e.g. \"is sunECommIntegration a binary " +
        "module?\") and to understand which modules ship without X++ " +
        "source. binary=true means the module ships compiled-only — " +
        "metadata is fully visible (forms, tables, classes, etc. all " +
        "indexable and searchable), but X++ method bodies are unavailable " +
        "and write tools cannot mutate objects in that module. Use " +
        "extensions in your own model to customize binary-module objects.")]
    public async Task<string> ListModules(
        [Description("Optional substring filter on module name (case-insensitive). Empty returns all.")] string? nameFilter = null,
        [Description("When true, return only binary-only modules. Default false.")] bool binaryOnly = false,
        CancellationToken ct = default)
    {
        var req = new ListModulesRequest
        {
            NameFilter = nameFilter ?? string.Empty,
            BinaryOnly = binaryOnly,
        };
        var modules = new List<object>();
        var binaryCount = 0;
        var customCount = 0;
        using var call = _conn.Client.ListModules(req);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var m = call.ResponseStream.Current;
            if (m.IsBinary) binaryCount++;
            if (m.IsCustom) customCount++;
            modules.Add(new
            {
                name         = m.Name,
                displayName  = m.DisplayName,
                publisher    = m.Publisher,
                version      = m.Version,
                layer        = m.Layer,
                isCustom     = m.IsCustom,
                isBinary     = m.IsBinary,
                dependencies = m.Dependencies.ToArray(),
            });
        }
        return JsonSerializer.Serialize(new
        {
            count       = modules.Count,
            binaryCount,
            customCount,
            modules,
        });
    }
}
