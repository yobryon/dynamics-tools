using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Search tools exposed to MCP clients. Each method maps 1:1 to a gRPC
/// streaming RPC on XppService, with the stream drained into a structured
/// JSON payload the agent can directly read.
///
/// We return JSON-as-string rather than richer MCP content types for two
/// reasons:
///   (1) the SDK's auto-schema generation works on plain return types,
///       and JSON-as-string composes well with the agent's existing
///       habit of consuming structured tool output;
///   (2) keeping every tool's contract trivial makes it easy to evolve
///       (we add a field to the JSON without touching tool signatures).
///
/// Tool naming follows snake_case (the convention v1 used and that the
/// agent ecosystem expects). All names are prefixed with "xpp_" so they
/// don't collide with other MCP tools the agent may have loaded.
/// </summary>
[McpServerToolType]
public sealed class SearchTools
{
    private readonly XppServiceConnection _conn;

    public SearchTools(XppServiceConnection conn)
    {
        _conn = conn;
    }

    [McpServerTool(Name = "xpp_find_object"), Description(
        "Find a D365 X++ object by its exact name. Case-insensitive. " +
        "Returns matches as a JSON array of {name, axType, model} entries. " +
        "Use this when you know the object's name; for wildcard / partial " +
        "matches, use xpp_search_pattern instead.")]
    public async Task<string> FindObject(
        [Description("Exact object name (case-insensitive)")] string name,
        [Description("Optional type filter (AxClass, AxTable, AxForm, AxEnum, AxEdt, AxView, AxQuery, ...). Leave empty for all types.")] string? axType = null,
        [Description("Optional model name filter. Leave empty for all models.")] string? model = null,
        CancellationToken ct = default)
    {
        var request = new FindObjectRequest
        {
            Name = name,
            AxType = axType ?? string.Empty,
            Model = model ?? string.Empty
        };

        var hits = new List<object>();
        using var call = _conn.Client.FindObject(request);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var m = call.ResponseStream.Current;
            var src = string.IsNullOrEmpty(m.Ref.Source) ? "disk" : m.Ref.Source;
            hits.Add(new
            {
                name = m.Ref.Name,
                axType = m.Ref.AxType,
                model = m.Ref.Model,
                source = src,
                binaryModule = src == "runtime",
            });
        }

        return JsonSerializer.Serialize(new { count = hits.Count, results = hits });
    }

    [McpServerTool(Name = "xpp_search_pattern"), Description(
        "Wildcard search over D365 object names. Use * for any characters " +
        "and ? for single character. Empty pattern (or '*') returns every " +
        "object subject to the type / model filters - useful for browsing " +
        "a model. Results are streamed and capped by 'limit'.")]
    public async Task<string> SearchPattern(
        [Description("Wildcard pattern: 'Cust*', '*Table', '*Order*', etc. Use '*' to browse.")] string pattern,
        [Description("Optional type filter (AxClass, AxTable, ...). Leave empty for all.")] string? axType = null,
        [Description("Optional model filter. Leave empty for all models.")] string? model = null,
        [Description("Maximum results to return. Default 100. Pass 0 for no cap (use carefully on broad patterns).")] int limit = 100,
        CancellationToken ct = default)
    {
        var request = new PatternRequest
        {
            Pattern = pattern,
            AxType = axType ?? string.Empty,
            Model = model ?? string.Empty,
            Limit = limit
        };

        var hits = new List<object>();
        using var call = _conn.Client.SearchByPattern(request);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var m = call.ResponseStream.Current;
            var src = string.IsNullOrEmpty(m.Ref.Source) ? "disk" : m.Ref.Source;
            hits.Add(new
            {
                name = m.Ref.Name,
                axType = m.Ref.AxType,
                model = m.Ref.Model,
                source = src,
                binaryModule = src == "runtime",
            });
        }

        return JsonSerializer.Serialize(new { count = hits.Count, results = hits });
    }

    [McpServerTool(Name = "xpp_search_code"), Description(
        "Full-text search over indexed X++ method bodies using SQLite FTS5. " +
        "Supports phrase queries (use quotes), boolean operators (AND, OR, " +
        "NOT), prefix matching ('foo*'), and proximity (NEAR). Returns " +
        "matching methods with FTS5-highlighted snippets (matches wrapped " +
        "in <mark>…</mark>).")]
    public async Task<string> SearchCode(
        [Description("FTS5 query. Examples: 'validateWrite', '\"select forUpdate\"', 'tax AND withholding', 'cust*'")] string query,
        [Description("Maximum results. Default 50.")] int limit = 50,
        CancellationToken ct = default)
    {
        var request = new CodeSearchRequest { Query = query, Limit = limit };

        var hits = new List<object>();
        using var call = _conn.Client.SearchCode(request);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var h = call.ResponseStream.Current;
            hits.Add(new
            {
                name = h.Object.Name,
                axType = h.Object.AxType,
                model = h.Object.Model,
                methodName = h.MethodName,
                snippet = h.Snippet
            });
        }

        return JsonSerializer.Serialize(new { count = hits.Count, results = hits });
    }

    [McpServerTool(Name = "xpp_search_semantic"), Description(
        "Semantic (meaning-based) search over indexed X++ method bodies or " +
        "label values, backed by local in-process embeddings. Unlike " +
        "xpp_search_code (which matches literal tokens), this finds " +
        "conceptually-related code even when the wording differs — e.g. " +
        "'reverse a posted invoice' surfaces cancellation/credit-note logic " +
        "that shares no keywords. Default mode 'hybrid' fuses semantic + " +
        "full-text rankings for the best of both; 'semantic' is pure vector " +
        "similarity. Returns ranked hits (best first) with a content excerpt " +
        "and score. Note: requires the embedding model, which the service " +
        "self-downloads on first run — check xpp_status's embeddingState; " +
        "until it's 'ready', hybrid falls back to full-text and pure-semantic " +
        "is unavailable.")]
    public async Task<string> SearchSemantic(
        [Description("Natural-language or code-fragment query describing what you're looking for.")] string query,
        [Description("What to search: 'method' (X++ source bodies, default) or 'label' (label text).")] string kind = "method",
        [Description("'hybrid' (semantic + full-text fused, default) or 'semantic' (pure vector similarity).")] string mode = "hybrid",
        [Description("Maximum results. Default 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        var request = new SemanticSearchRequest
        {
            Query = query,
            Kind = kind ?? "method",
            Mode = mode ?? "hybrid",
            Limit = limit
        };

        var hits = new List<object>();
        using var call = _conn.Client.SearchSemantic(request);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var h = call.ResponseStream.Current;
            hits.Add(new
            {
                kind = h.Kind,
                name = h.Object?.Name,
                axType = h.Object?.AxType,
                model = h.Object?.Model,
                methodName = string.IsNullOrEmpty(h.MethodName) ? null : h.MethodName,
                labelKey = string.IsNullOrEmpty(h.LabelKey) ? null : h.LabelKey,
                excerpt = h.Text,
                score = Math.Round(h.Score, 4),
                distance = Math.Round(h.Distance, 4),
            });
        }

        return JsonSerializer.Serialize(new { count = hits.Count, results = hits });
    }

    [McpServerTool(Name = "xpp_find_references"), Description(
        "Find what references a given object — or a specific field on a " +
        "table — or a label. Three modes:\n" +
        " (1) Object-level (default): pass targetName (+ optional " +
        "     targetType). Returns the declared structural edges from the " +
        "     metadata graph (extends / implements / datasource / table " +
        "     relation / menu item target / privilege entry point / " +
        "     extension target / [ExtensionOf] / ...) plus, when " +
        "     includeSourceMentions=true, methods whose source code " +
        "     mentions the target as a token.\n" +
        " (2) Field-level: pass targetName=<TableName> AND " +
        "     targetField=<FieldName>. Returns the structural usages of " +
        "     that field across forms (DataSource overrides + bound " +
        "     controls), queries (Ranges / OrderBy / GroupBy / Having / " +
        "     Fields), data entities (Mapped fields), and table-relation " +
        "     constraints. The sourceMember on each hit identifies the " +
        "     specific control / range / constraint doing the referencing.\n" +
        " (3) Label reverse-lookup: pass targetLabel (e.g. '@SYS:123', " +
        "     'SYS:123', '@SomeLocalLabel', or 'SomeLocalLabel'). Returns " +
        "     AOT objects whose properties reference the label (table / " +
        "     edt / enum-value / menu-item / form-control labels, etc.). " +
        "     The context on each hit identifies the source property " +
        "     (Label / HelpText / Caption / etc.).")]
    public async Task<string> FindReferences(
        [Description("Target object name (or target table name when targetField is set). Required for object-level and field-level lookup; ignored when targetLabel is given.")] string? targetName = null,
        [Description("Optional target type filter (AxTable, AxClass, ...) for object-level lookup. Ignored when targetField or targetLabel is set.")] string? targetType = null,
        [Description("Optional target field name. When set, switches to field-level lookup against the field_refs table (Tier B). targetName is then interpreted as the backing table name.")] string? targetField = null,
        [Description("Optional target label (e.g. '@SYS:123' or 'SYS:123'). When set, switches to label reverse-lookup against the label_refs table (Tier C). targetName, targetType, and targetField are ignored.")] string? targetLabel = null,
        [Description("Also include methods whose source code mentions the target as a token. Object-level mode only; ignored for field-level / label lookup.")] bool includeSourceMentions = false,
        [Description("Maximum results. Default 200.")] int limit = 200,
        CancellationToken ct = default)
    {
        var request = new ReferenceQuery
        {
            TargetName = targetName ?? string.Empty,
            TargetType = targetType ?? string.Empty,
            TargetField = targetField ?? string.Empty,
            TargetLabel = targetLabel ?? string.Empty,
            IncludeSourceMentions = includeSourceMentions,
            Limit = limit
        };

        var hits = new List<object>();
        using var call = _conn.Client.FindReferences(request);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var h = call.ResponseStream.Current;
            hits.Add(new
            {
                source = new { name = h.Source.Name, axType = h.Source.AxType, model = h.Source.Model },
                kind = h.Kind,
                context = h.Context,
                sourceMember = h.SourceMember,
            });
        }

        return JsonSerializer.Serialize(new { count = hits.Count, results = hits });
    }
}
