using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Compile tool. Delegates to the service's devenv.com /Build shell-out, so
/// the agent gets the same Build experience the user does in VS: metadata
/// validation -> X++ compile (via xppcAgent) -> Best Practice check ->
/// CopyReferences -> app pool recycle.
///
/// We surface a summary-first response in the same spirit as xpp_bp_check:
/// timing per step, pass/fail, full errors, and Warning/Informational
/// grouped by moniker so the agent isn't drowned. verbosity="full" returns
/// every diagnostic.
/// </summary>
[McpServerToolType]
public sealed class CompileTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public CompileTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_compile"), Description(
        "Build the active dynamics-xpp project. Drives devenv.com /Build on " +
        "the slnPath configured in .dynamics-xpp/config.json, replicating " +
        "the VS Build pipeline exactly (metadata validation -> X++ compile " +
        "via xppcAgent -> BP check -> CopyReferences -> app pool recycle). " +
        "rebuild=true forces /Rebuild instead of /Build — slower but " +
        "guarantees fresh diagnostics output even when nothing changed. " +
        "verbosity=\"default\" groups errors AND warnings by moniker (count " +
        "+ a few concrete samples each) — a failing build can carry hundreds " +
        "of errors; verbosity=\"full\" returns every diagnostic with full " +
        "location detail. errorCount is always the true total. " +
        "Honors the project's bestPractices.suppress list (BP diagnostics " +
        "matching it land in the suppressed bucket). " +
        "upToDate=true means devenv reported 'succeeded or up-to-date' — " +
        "the build pipeline still RAN every step (metadata + xppcAgent + " +
        "BPC), so per-step timings remain non-zero even on a no-op build; " +
        "upToDate just tells you no source artifacts changed. Cold-start " +
        "tax is ~14s devenv startup + xppcAgent load; subsequent build " +
        "steps run in seconds. Requires a configured project with a valid " +
        "slnPath (see dynamics-xpp:xpp-project).")]
    public async Task<string> CompileProject(
        [Description("When true, run /Rebuild instead of /Build. Forces fresh diagnostics.")] bool rebuild = false,
        [Description("\"default\" | \"full\". Default summarises non-error diagnostics; full returns every diagnostic.")] string? verbosity = null,
        [Description("Optional. When set, toggles the rnrproj's DBSyncInBuild property BEFORE building (true=enable, false=disable), then leaves it set. The database sync still runs only as a product of a SUCCESSFUL (re)build per that property — there is no standalone sync. Pair with rebuild=true to materialize a schema change. Omit to leave the project's setting untouched.")] bool? syncDb = null,
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
        var verbosityNormalized = (verbosity ?? "default").Trim().ToLowerInvariant();
        if (verbosityNormalized is not ("default" or "full"))
            throw new InvalidOperationException($"unknown verbosity '{verbosity}' (use \"default\" or \"full\")");

        // Optional DBSyncInBuild toggle: set the rnrproj property before the
        // build so a sync rides along on a successful (re)build. We never sync
        // standalone — the build's own pipeline does it, only on success.
        string? dbSyncToggle = null;
        var dbSyncWarnings = new List<string>();
        if (syncDb.HasValue)
        {
            try
            {
                var r = await _project.SetDbSyncInBuildAsync(syncDb.Value, ct).ConfigureAwait(false);
                dbSyncToggle = $"{r.Previous} -> {r.Current}";
                dbSyncWarnings.AddRange(r.Warnings);
            }
            catch (Exception ex) { dbSyncWarnings.Add($"dbSync toggle failed: {ex.Message}"); }
        }

        CompileResponse rsp;
        try
        {
            rsp = await _conn.Client.CompileAsync(new CompileRequest
            {
                SlnPath = resolved.SlnPath,
                RnrprojPath = resolved.RnprojPath,
                Module = resolved.Module,
                Rebuild = rebuild,
                Configuration = "Debug|Any CPU"
            }, cancellationToken: ct);
        }
        catch (RpcException rx)
        {
            return JsonSerializer.Serialize(new
            {
                error = rx.Status.StatusCode.ToString(),
                message = rx.Status.Detail
            });
        }

        // Always echo the effective DBSyncInBuild setting so the agent can
        // reason about whether a sync occurred: a sync ran iff this is True
        // AND success is true (a rebuild does the work; an up-to-date no-op
        // build does not). There is deliberately no standalone sync handle.
        var dbSync = new
        {
            inBuild = _project.ReadDbSyncInBuildEffective(),
            toggledThisCall = dbSyncToggle,
            note = "Database sync is performed by the build only when DBSyncInBuild is True AND the build succeeds; a failed or up-to-date build performs no sync.",
            warnings = dbSyncWarnings.Count > 0 ? dbSyncWarnings.ToArray() : null,
        };

        var suppress = new HashSet<string>(resolved.BpSuppress, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ShapeResponse(rsp, verbosityNormalized == "full", suppress, dbSync));
    }

    private static object ShapeResponse(CompileResponse rsp, bool fullDetail, HashSet<string> suppress, object dbSync)
    {
        var errorDiags = new List<BpDiagnostic>();
        var warnings = new List<BpDiagnostic>();
        var informational = new List<BpDiagnostic>();
        var suppressed = new List<BpDiagnostic>();

        foreach (var d in rsp.Diagnostics)
        {
            if (suppress.Contains(d.Moniker)) { suppressed.Add(d); continue; }
            switch (d.Severity)
            {
                case "Error":
                case "Fatal":
                    errorDiags.Add(d);
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

        // When the service surfaced raw devenv output (last-resort fallback
        // for failures with no parseable diagnostics), pass it through so
        // the agent can read the unparsed text. Empty strings serialize to
        // nothing meaningful — only attach when there's content.
        object? rawOutput = null;
        if (!string.IsNullOrEmpty(rsp.RawStdout) || !string.IsNullOrEmpty(rsp.RawStderr))
        {
            rawOutput = new
            {
                hint = "devenv reported failure but produced no parseable diagnostics. The raw output is included so you can extract error lines manually.",
                stdout = rsp.RawStdout,
                stderr = rsp.RawStderr,
            };
        }

        // Pass relevant_skills through verbatim. Empty array means no
        // diagnostic matched a skill-linkage rule.
        var relevantSkills = rsp.RelevantSkills?.ToArray() ?? Array.Empty<string>();

        return new
        {
            success = rsp.Success,
            upToDate = rsp.UpToDate,
            summary = rsp.SummaryLine,
            timing = new
            {
                metadataValidationMs = rsp.Timing?.MetadataValidationMs ?? 0,
                xppCompileMs = rsp.Timing?.XppCompileMs ?? 0,
                bpCheckMs = rsp.Timing?.BpCheckMs ?? 0,
                appPoolRecycleMs = rsp.Timing?.AppPoolRecycleMs ?? 0,
                elapsedMs = rsp.ElapsedMs
            },
            appPoolRecycled = rsp.AppPoolRecycled,
            dbSync,
            // Errors are the actionable bucket. In default verbosity we group
            // them by moniker (count + a few concrete samples) — a real build
            // can carry hundreds of errors, and dumping every one in full blows
            // the response past the MCP token limit. verbosity="full" returns
            // every error with full location detail.
            errorCount = errorDiags.Count,
            errors = fullDetail
                ? (object)new { total = errorDiags.Count, diagnostics = errorDiags.Select(ProjectDiag).ToArray() }
                : GroupErrors(errorDiags),
            warnings = fullDetail
                ? (object)new { total = warnings.Count, diagnostics = warnings.Select(ProjectDiag).ToArray() }
                : Group(warnings),
            informational = fullDetail
                ? (object)new { total = informational.Count, diagnostics = informational.Select(ProjectDiag).ToArray() }
                : Group(informational),
            suppressed = Group(suppressed),
            rawOutput,
            relevantSkills,
            verbosity = fullDetail ? "full" : "default"
        };
    }

    // Group errors by moniker for the default view: count + up to 5 concrete
    // samples (message/path/line/element) per moniker so the agent can triage
    // by category and still have real locations to jump to, without the full
    // 500-line dump. Full detail comes from verbosity="full".
    private static object GroupErrors(List<BpDiagnostic> diags)
    {
        var byMon = diags
            .GroupBy(d => d.Moniker)
            .Select(g => new
            {
                moniker = g.Key,
                count = g.Count(),
                samples = g.Take(5).Select(d => new
                {
                    message = d.Message,
                    path = d.Path,
                    line = d.Line,
                    element = string.IsNullOrEmpty(d.ElementType) ? null : d.ElementType,
                }).ToArray()
            })
            .OrderByDescending(x => x.count)
            .ToArray();
        return new { total = diags.Count, byMoniker = byMon };
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

    private static string? ElementName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        const string prefix = "dynamics://";
        var s = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
        var parts = s.Split('/');
        return parts.Length >= 2 ? parts[1] : null;
    }
}
