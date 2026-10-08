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
        "location detail. errorCount is the raw total of Error-severity " +
        "diagnostics; it is split into buildErrors (fatal — these failed the " +
        "build) and validationDiagnostics (non-fatal metadata-validation " +
        "diagnostics that do NOT fail the build). When success=true these are " +
        "all validationDiagnostics — success:true with errorCount>0 is not a " +
        "contradiction. errorsFailedBuild mirrors !success. " +
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
        [Description("Default true. When a FAILED build's errors are all metadata-validation diagnostics (MethodMustBeStatic / MethodReturnTypeInvalid on a touched view's computed columns, DataMethodNotFoundOnDataSource on a form using it), run one more plain build automatically and return THAT: validation runs before the X++ compile, so a first build after such a change validates against stale objects and reports precise, wrong errors that a second build clears. The response says when this happened (orderingRetry). Set false to see the raw first pass.")] bool retryOrderingArtifacts = true,
        [Description("When true, start the build and return at once with the devenv pid; poll xpp_compile_status for progress and the final result. Use for long rebuilds (DB sync enabled) that would outlive the tool-call timeout.")] bool background = false,
        [Description("When true, recycle the AOSService app pool after a SUCCESSFUL build (unless the build already did). Needed before verifying METADATA-only changes in the browser: menu items, menus, security objects, tiles and labels are served from the AOS metadata cache until a recycle, so a stale cache can show a deleted menu item or hide a new one. X++ code changes do not need it. Costs the AOS a cold start (~1-2 min before the first page).")] bool recycleAppPool = false,
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
            return JsonSerializer.Serialize(new
            {
                error = "invalid_argument",
                argument = "verbosity",
                value = verbosity,
                validValues = new[] { "default", "full" },
                message = $"verbosity '{verbosity}' is not valid; use \"default\" or \"full\". No build was started.",
            });

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
                RecycleAppPool = recycleAppPool,
                RetryOrderingArtifacts = retryOrderingArtifacts,
                Background = background,
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

        if (rsp.BuildInProgress)
            return JsonSerializer.Serialize(new
            {
                buildInProgress = true,
                progress = ShapeProgress(rsp.Progress),
                message = "No build was started: one is already running on this box (one devenv build at a time). Poll xpp_compile_status; it returns the result when that build finishes.",
            });
        if (rsp.StartedInBackground)
            return JsonSerializer.Serialize(new
            {
                started = true,
                background = true,
                progress = ShapeProgress(rsp.Progress),
                message = "Build started. Poll xpp_compile_status (every 30-60 s) for lastStepCompleted / lastOutputAgeMs; when running=false its lastResult is this build's full result.",
            });

        var suppress = new HashSet<string>(resolved.BpSuppress, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ShapeResponse(rsp, verbosityNormalized == "full", suppress, dbSync));
    }

    [McpServerTool(Name = "xpp_compile_status"), Description(
        "Progress of the devenv build that is running on this box, or the last one that finished. Use it when an " +
        "xpp_compile call timed out client-side (the build keeps running; the result is NOT lost) or after " +
        "xpp_compile background=true. running=true: progress shows the devenv pid, elapsed time, the last build step " +
        "reported complete (Metadata validation / X++ compilation / Best practice check / Database synchronization), " +
        "the last output line and how long ago it was written, and whether the process is alive. A DB sync can be " +
        "silent for many minutes; a dead process with running=true means devenv was killed. running=false: lastResult " +
        "is the finished build's full result (same shape as xpp_compile).")]
    public async Task<string> CompileStatus(CancellationToken ct = default)
    {
        CompileStatusResponse st;
        try { st = await _conn.Client.CompileStatusAsync(new CompileStatusRequest(), cancellationToken: ct); }
        catch (RpcException rx) { return JsonSerializer.Serialize(new { error = rx.StatusCode.ToString(), message = rx.Status.Detail }); }

        object? last = null;
        if (st.LastResult != null && !string.IsNullOrEmpty(st.LastFinishedUtc))
        {
            var suppress = new HashSet<string>(StringComparer.Ordinal);
            try { var r = _project.Resolve(); if (r != null) suppress = new HashSet<string>(r.BpSuppress, StringComparer.Ordinal); } catch { }
            last = ShapeResponse(st.LastResult, false, suppress, new { note = "see xpp_compile for dbSync details" });
        }
        return JsonSerializer.Serialize(new
        {
            running = st.Running,
            progress = st.Running ? ShapeProgress(st.Progress) : null,
            lastFinishedUtc = string.IsNullOrEmpty(st.LastFinishedUtc) ? null : st.LastFinishedUtc,
            lastResult = last,
            hint = st.Running
                ? (st.Progress.ProcessAlive
                    ? (st.Progress.LastOutputAgeMs > 600_000
                        ? "devenv has written nothing for over 10 minutes. The pipeline is: Metadata validation, X++ compilation, Best practice check, then (with DBSyncInBuild) the database synchronization, which prints nothing until it completes and can take 20+ minutes on a full sync. Silence after 'Best practice check' is normally that sync; silence after an earlier step is suspicious. The operator can kill devenv.com (pid above) and rebuild."
                        : "build is progressing; poll again in 30-60 s. Silence after 'Best practice check' is normally the database synchronization.")
                    : "the devenv process is gone but no result was recorded: it was killed or crashed. Run xpp_compile again.")
                : (last == null ? "no build has run since the service started" : "no build is running; lastResult is the most recent finished build"),
        });
    }

    private static object ShapeProgress(BuildProgress p) => new
    {
        pid = p.Pid,
        startedUtc = p.StartedUtc,
        elapsedMs = p.ElapsedMs,
        lastStepCompleted = p.LastStepCompleted,
        lastOutputLine = p.LastOutputLine,
        lastOutputAgeMs = p.LastOutputAgeMs,
        processAlive = p.ProcessAlive,
        rebuild = p.Rebuild,
        slnPath = p.SlnPath,
    };

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

        // Two build-pipeline signals that look authoritative and are not.
        var hints = new List<string>();
        if (rsp.Success && errorDiags.Count > 0)
            hints.Add("Metadata validation runs BEFORE the X++ compile. A diagnostic against an object this build introduced " +
                      "(a menu item pointing at a brand-new class, a new entry point) can be validated against the NOT-YET-COMPILED class " +
                      "and report a precise, wrong defect (MethodMustBeStatic / InvalidMethodSignature on a correct main(Args)). " +
                      "Before changing code to satisfy such a diagnostic, run the build once more; if it clears, it was ordering, not your code.");
        if (!rsp.AppPoolRecycled)
            hints.Add("The app pool was NOT recycled. X++ changes are live now, but metadata-only changes (menu items, menus, " +
                      "security objects, tiles, labels) are served from the AOS metadata cache until a recycle: the browser can still show a " +
                      "deleted menu item or miss a new one. Pass recycleAppPool=true (or restart the AOSService app pool) before verifying those.");
        if (!string.IsNullOrEmpty(rsp.AppPoolRecycleError))
            hints.Add("App pool recycle was requested but failed: " + rsp.AppPoolRecycleError);
        if (rsp.OrderingRetry)
            hints.Add("orderingRetry: " + rsp.OrderingRetryNote);
        if (!rsp.Success && !rsp.OrderingRetry && errorDiags.Count > 0
            && errorDiags.All(d => string.Equals(d.DiagnosticType, "MetadataProvider", StringComparison.OrdinalIgnoreCase)))
            hints.Add("Every error is a metadata-validation diagnostic. Validation runs BEFORE the X++ compile, so errors on objects " +
                      "whose X++ changed in this build (computed columns of a touched view, a form using its display methods) may be " +
                      "ordering artifacts: run the build once more before changing code. retryOrderingArtifacts=true does that automatically.");

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
            // success=true alongside errorCount>0 is NOT a contradiction: the
            // build passed, so those Error-severity entries did not fail it —
            // they're non-fatal metadata-validation diagnostics (e.g.
            // DataMethodNotFoundOnDataSource on a custom control, which the
            // control resolves itself at runtime). Split the count by build
            // outcome so the two numbers can't be misread as one. buildErrors is
            // the actionable "your build failed" bucket; validationDiagnostics is
            // advisory. errorCount stays as the raw total for back-compat.
            buildErrors = rsp.Success ? 0 : errorDiags.Count,
            validationDiagnostics = rsp.Success ? errorDiags.Count : 0,
            errorsFailedBuild = !rsp.Success,
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
            hints = hints.Count > 0 ? hints.ToArray() : null,
            orderingRetry = rsp.OrderingRetry ? new { retried = true, note = rsp.OrderingRetryNote } : null,
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
