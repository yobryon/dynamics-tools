using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Best Practice check tool. Wraps the F&amp;O server-side xppbp.exe via the
/// RunBestPracticeChecks gRPC RPC.
///
/// Scope resolution lives in the MCP since the .dynamics-xpp project +
/// changeset state lives here:
///   scope = "changeset" — every (axType, name) the MCP has touched
///   scope = "project"   — every &lt;Content&gt; in the active .rnrproj
///   scope = "explicit"  — caller-supplied list
///
/// Output is summary-by-default so an agent in the middle of a forward
/// build isn't drowned in 200k of doc-style warnings. Errors come through
/// in full; Warning / Informational collapse to per-moniker counts. The
/// agent (or the user, on a polish pass) opts in to detail via
/// verbosity="full" or by passing specific monikers to drill into.
///
/// .dynamics-xpp/config.json carries the persistent policy:
///   bestPractices.suppress  — silenced rules (counted under "suppressed")
///   bestPractices.escalate  — Warning -> Error promotions
/// </summary>
[McpServerToolType]
public sealed class BestPracticeTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public BestPracticeTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_bp_check"), Description(
        "Run F&O Best Practice checks against AOT objects. Wraps the same " +
        "xppbp.exe the AOS uses; diagnostics are identical to what VS Build " +
        "surfaces. scope=\"changeset\" (default) checks the .dynamics-xpp " +
        "changeset; scope=\"project\" checks every object in the active " +
        ".rnrproj; scope=\"explicit\" requires the objects array. " +
        "verbosity=\"default\" (default) returns errors in full but collapses " +
        "warnings/informational to per-moniker counts so an agent isn't " +
        "drowned. verbosity=\"full\" returns every diagnostic with message " +
        "and location — use on polish passes or tech-debt sweeps. Pass " +
        "monikers=[...] to drill into specific rules at full detail; xppbp " +
        "then runs ONLY those rules (faster) and the project's suppress " +
        "list is bypassed. Project policy (suppress / escalate moniker " +
        "lists) lives under bestPractices in .dynamics-xpp/config.json. " +
        "See plugins/xpp/docs/bp-rules-reference.md for the 184-rule roster. " +
        "Requires a .dynamics-xpp project.")]
    public async Task<string> BpCheck(
        [Description("\"changeset\" | \"project\" | \"explicit\". Defaults to changeset.")] string? scope = null,
        [Description("Required when scope=explicit. Array of {axType, name} objects.")] BpCheckElement[]? objects = null,
        [Description("\"default\" | \"full\". Default summarises non-error diagnostics; full returns every diagnostic.")] string? verbosity = null,
        [Description("Optional moniker list (e.g. [\"BPLocalVariableNotUsed\"]). xppbp runs ONLY these rules; suppress list is bypassed.")] string[]? monikers = null,
        CancellationToken ct = default)
    {
        ResolvedConfig? resolved;
        try { resolved = _project.Resolve(); }
        catch (ProjectConfigException pcx)
        {
            return JsonSerializer.Serialize(new
            {
                configured = false,
                error = "project_config_invalid",
                message = pcx.Message,
                hint = "Load the dynamics-xpp:xpp-project skill for the .dynamics-xpp/config.json shape."
            });
        }
        if (resolved == null)
        {
            return JsonSerializer.Serialize(new
            {
                configured = false,
                cwd = Environment.CurrentDirectory,
                message = "No .dynamics-xpp/config.json in the current directory. Load the dynamics-xpp:xpp-project skill and walk the user through first-time setup.",
                skill = "dynamics-xpp:xpp-project"
            });
        }

        var scopeNormalized = (scope ?? "changeset").Trim().ToLowerInvariant();
        var verbosityNormalized = (verbosity ?? "default").Trim().ToLowerInvariant();
        if (verbosityNormalized is not ("default" or "full"))
            throw new InvalidOperationException($"unknown verbosity '{verbosity}' (use \"default\" or \"full\")");

        var elements = scopeNormalized switch
        {
            "changeset" => ResolveChangesetScope(),
            "project" => ResolveProjectScope(),
            "explicit" => ResolveExplicitScope(objects),
            _ => throw new InvalidOperationException($"unknown scope '{scope}'")
        };

        if (elements.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                scope = scopeNormalized,
                module = resolved.Module,
                model = resolved.Model,
                elementsChecked = 0,
                errors = Array.Empty<object>(),
                warnings = new { total = 0, byMoniker = Array.Empty<object>() },
                informational = new { total = 0, byMoniker = Array.Empty<object>() },
                suppressed = new { total = 0, byMoniker = Array.Empty<object>() },
                verbosity = verbosityNormalized,
                message = scopeNormalized switch
                {
                    "changeset" => "Changeset is empty; nothing to check. Touch an object (create/update) or use scope=\"explicit\".",
                    "project" => "Project has no Content entries.",
                    _ => "No elements supplied."
                }
            });
        }

        var req = new BpCheckRequest
        {
            Module = resolved.Module,
            Model = resolved.Model
        };
        req.Elements.AddRange(elements);

        // monikers=[...] drives xppbp's -rules= filter (perf win + smaller XML)
        // and bypasses the project's suppress list since the caller is
        // explicitly asking for those rules.
        var drilling = monikers != null && monikers.Length > 0;
        if (drilling)
            req.OnlyRules.AddRange(monikers!);

        if (resolved.BpEscalate.Count > 0)
            req.EscalateRules.AddRange(resolved.BpEscalate);

        var suppress = drilling
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(resolved.BpSuppress, StringComparer.Ordinal);

        var diagnostics = new List<BpDiagnostic>();
        try
        {
            using var call = _conn.Client.RunBestPracticeChecks(req, cancellationToken: ct);
            await foreach (var d in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
                diagnostics.Add(d);
        }
        catch (RpcException rx)
        {
            return JsonSerializer.Serialize(new
            {
                scope = scopeNormalized,
                module = resolved.Module,
                model = resolved.Model,
                error = rx.Status.StatusCode.ToString(),
                message = rx.Status.Detail
            });
        }

        return JsonSerializer.Serialize(ShapeResponse(
            scopeNormalized, resolved, elements.Count, diagnostics,
            verbosityNormalized == "full" || drilling, suppress));
    }

    private static object ShapeResponse(
        string scope, ResolvedConfig resolved, int elementsChecked,
        List<BpDiagnostic> diagnostics, bool fullDetail, HashSet<string> suppress)
    {
        var errors = new List<object>();
        var warnings = new List<BpDiagnostic>();
        var informational = new List<BpDiagnostic>();
        var suppressed = new List<BpDiagnostic>();

        foreach (var d in diagnostics)
        {
            if (suppress.Contains(d.Moniker)) { suppressed.Add(d); continue; }
            switch (d.Severity)
            {
                case "Error":
                case "Fatal":
                    errors.Add(ProjectDiag(d));
                    break;
                case "Informational":
                case "Info":
                    informational.Add(d);
                    break;
                default:
                    warnings.Add(d);
                    break;
            }
        }

        return new
        {
            scope,
            module = resolved.Module,
            model = resolved.Model,
            elementsChecked,
            errors,
            warnings = fullDetail
                ? (object)new { total = warnings.Count, diagnostics = warnings.Select(ProjectDiag).ToArray() }
                : Group(warnings),
            informational = fullDetail
                ? (object)new { total = informational.Count, diagnostics = informational.Select(ProjectDiag).ToArray() }
                : Group(informational),
            suppressed = Group(suppressed),
            verbosity = fullDetail ? "full" : "default"
        };
    }

    private static object Group(List<BpDiagnostic> diags)
    {
        var byMon = diags
            .GroupBy(d => d.Moniker)
            .Select(g => new
            {
                moniker = g.Key,
                count = g.Count(),
                elements = g.Select(d => ElementName(d.Path)).Where(n => n != null).Distinct().Count(),
                sampleMessage = g.First().Message
            })
            .OrderByDescending(x => x.count)
            .ToArray();
        return new { total = diags.Count, byMoniker = byMon };
    }

    private static object ProjectDiag(BpDiagnostic d) => new
    {
        severity = d.Severity,
        moniker = d.Moniker,
        message = d.Message,
        path = d.Path,
        line = d.Line,
        column = d.Column,
        endLine = d.EndLine,
        endColumn = d.EndColumn,
        elementType = d.ElementType,
        diagnosticType = d.DiagnosticType
    };

    // Extract the element name segment from dynamics://Class/Foo or
    // dynamics://Table/Foo/Method/bar so we can count distinct affected
    // elements per moniker in summaries.
    private static string? ElementName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        const string prefix = "dynamics://";
        var s = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
        var parts = s.Split('/');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private List<BpElementFilter> ResolveChangesetScope()
    {
        var changeset = _project.ReadChangeset();
        return changeset.Objects
            .Select(o => new BpElementFilter { AxType = o.AxType, Name = o.Name })
            .ToList();
    }

    private List<BpElementFilter> ResolveProjectScope()
    {
        return _project.ListRnprojObjects()
            .Select(o => new BpElementFilter { AxType = o.AxType, Name = o.Name })
            .ToList();
    }

    private static List<BpElementFilter> ResolveExplicitScope(BpCheckElement[]? objects)
    {
        if (objects == null || objects.Length == 0)
            throw new InvalidOperationException("scope=explicit requires a non-empty objects array");
        return objects
            .Where(o => !string.IsNullOrWhiteSpace(o.AxType) && !string.IsNullOrWhiteSpace(o.Name))
            .Select(o => new BpElementFilter { AxType = o.AxType!, Name = o.Name! })
            .ToList();
    }

    public sealed class BpCheckElement
    {
        [JsonPropertyName("axType")]
        public string? AxType { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
