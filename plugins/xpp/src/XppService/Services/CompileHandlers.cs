using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Grpc.Core;
using Xpp.Service.Bridge;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Services;

/// <summary>
/// Compile handler. Drives devenv.com /Build on the project's .sln so the
/// agent gets the same pipeline the user would running Build inside VS:
/// metadata validation -> xppcAgent -> xppbp -> CopyReferences -> app pool
/// recycle.
///
/// Why devenv (not msbuild): the MSBuild custom task that drives xppcAgent
/// lives inside the VS Dynamics extension dir. Standalone msbuild can't
/// find it (the UsingTask references the assembly by strong name and the
/// extension dir isn't on the probe path). devenv.com loads the extension
/// properly and IS the canonical command-line build entry.
///
/// devenv.com cold-start costs ~14s; the actual build is the same xppcAgent
/// VS uses live, so once it's loaded compilation runs in seconds. We pay
/// the startup tax per-invocation today; attaching to a running VS via
/// DTE/COM is a future optimization.
/// </summary>
public sealed partial class PingGrpcService
{
    // VS-Build stdout has a stable shape we parse to surface timings and
    // pass/fail without re-reading the on-disk log. Examples:
    //   "Build step: X++ compilation completed (3263 ms). Time: ..."
    //   "========== Build: 1 succeeded or up-to-date, 0 failed, 0 skipped =========="
    //   "Application pool recycling completed (2120 ms). Time: ..."
    private static readonly Regex StepTimingRegex = new(
        @"Build step:\s+(?<step>[^\s].*?)\s+completed\s+\((?<ms>\d+)\s*ms\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SummaryRegex = new(
        @"=+\s+(?<line>(?:Build|Rebuild(?:\s+All)?):\s+\d+\s+succeeded[^=]*?\d+\s+failed[^=]*)=+",
        RegexOptions.Compiled);
    private static readonly Regex SucceededCountRegex = new(
        @"(?<succ>\d+)\s+succeeded(?:\s+or\s+up-to-date)?\s*,\s*(?<fail>\d+)\s+failed",
        RegexOptions.Compiled);
    private static readonly Regex UpToDateRegex = new(
        @"succeeded\s+or\s+up-to-date",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AppPoolRecycleRegex = new(
        @"Application pool recycling completed\s+\((?<ms>\d+)\s*ms\)",
        RegexOptions.Compiled);

    // devenv's STDOUT is the authoritative, current diagnostic stream — it is
    // exactly what the VS Error List mirrors. The result XMLs are unreliable
    // for a command-line /Rebuild: the .err.xml files are frequently left
    // stale (not rewritten), and the .xml captures only warnings/info — so
    // metadata-validation errors AND BP-error-severity diagnostics surface
    // ONLY on stdout. These three regexes parse the D365 build-log line shape:
    //   <path>(<line>,<col>):  <Moniker>: <rest>
    // where <rest> is either "BP Rule: [Moniker]:Moniker: <msg>" (X++/BP),
    // "Path: [<element-path>]:<msg>" (metadata validation), or a bare message.
    // There is NO explicit error/warning token on these lines — severity is
    // inferred from the moniker + format (see ClassifyStdoutSeverity).
    private static readonly Regex StdoutDiagnosticRegex = new(
        @"^(?<path>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<moniker>[A-Za-z0-9_]+):\s*(?<rest>.*?)\s*$",
        RegexOptions.Compiled);
    private static readonly Regex BpRuleRestRegex = new(
        @"^BP Rule:\s*\[[^\]]*\]:[^:]*:\s*(?<msg>.*)$", RegexOptions.Compiled);
    private static readonly Regex MetaPathRestRegex = new(
        @"^Path:\s*\[(?<ep>[^\]]*)\]:\s*(?<msg>.*)$", RegexOptions.Compiled);
    // Compile-cascade errors have no (line,col)/moniker shape — they're a bare
    // sentence. The X++ compiler emits one per element that failed to generate
    // because something it depends on errored. VS counts each as an error.
    private static readonly Regex ElementNotFoundRegex = new(
        @"^Element '(?<name>[^']+)' of type '(?<type>[^']+)' was not found in the metadata\b.*$",
        RegexOptions.Compiled);

    public override async Task<CompileResponse> Compile(CompileRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SlnPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sln_path is required"));
        if (string.IsNullOrWhiteSpace(request.RnrprojPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "rnrproj_path is required"));
        if (string.IsNullOrWhiteSpace(request.Module))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "module is required"));
        if (!File.Exists(request.SlnPath))
            throw new RpcException(new Status(StatusCode.NotFound, $"sln not found: {request.SlnPath}"));
        if (!File.Exists(request.RnrprojPath))
            throw new RpcException(new Status(StatusCode.NotFound, $"rnrproj not found: {request.RnrprojPath}"));

        var devenv = LocateDevenv();
        if (devenv == null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "devenv.com not found. Install Visual Studio 2022 with the Dynamics 365 development extension."));

        var configuration = string.IsNullOrWhiteSpace(request.Configuration) ? "Debug|Any CPU" : request.Configuration;
        var target = request.Rebuild ? "/Rebuild" : "/Build";

        // Snapshot the build-result XMLs' mtimes so we only consume diagnostics
        // that the CURRENT build emitted. devenv skips the write path on a
        // no-op "up-to-date" invocation, leaving prior XMLs in place — we
        // must NOT mistake stale content for the current build's result.
        //
        // Four candidate result files cover the full diagnostic surface:
        //   BuildModelResult.xml      — metadata validation, full result
        //   BuildModelResult.err.xml  — metadata validation, errors only
        //                               (pattern violations land here)
        //   BuildProjectResult.xml    — X++ compilation, full result
        //   BuildProjectResult.err.xml — X++ compilation, errors only
        //
        // Earlier versions of this handler only read BuildProjectResult.xml,
        // which is why pattern-validation failures (FormPatternValidation
        // moniker) reported `errors: []` despite devenv exiting non-zero —
        // those diagnostics land in BuildModelResult.err.xml.
        var packagesRoot = _bridgeOptions.PackagesLocalDirectory ?? string.Empty;
        var resultXmlNames = new[]
        {
            "BuildModelResult.xml",
            "BuildModelResult.err.xml",
            "BuildProjectResult.xml",
            "BuildProjectResult.err.xml",
        };
        var resultXmlPaths = resultXmlNames
            .Select(n => Path.Combine(packagesRoot, request.Module, n))
            .ToArray();
        var priorMtimes = resultXmlPaths
            .Select(p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue)
            .ToArray();

        var psi = new ProcessStartInfo
        {
            FileName = devenv,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(request.SlnPath);
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(configuration);
        psi.ArgumentList.Add("/Project");
        psi.ArgumentList.Add(request.RnrprojPath);

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi)
            ?? throw new RpcException(new Status(StatusCode.Internal, "devenv.com could not be started"));

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        sw.Stop();

        var response = new CompileResponse
        {
            ElapsedMs = sw.ElapsedMilliseconds,
            Timing = new CompileTiming()
        };

        // Stdout parsing. devenv emits step timings and a summary line that
        // tell us pass/fail without touching the XML.
        foreach (Match m in StepTimingRegex.Matches(stdout))
        {
            if (!long.TryParse(m.Groups["ms"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                continue;
            var step = m.Groups["step"].Value.Trim();
            if (step.StartsWith("Metadata validation", StringComparison.OrdinalIgnoreCase))
                response.Timing.MetadataValidationMs = ms;
            else if (step.StartsWith("X++ compilation", StringComparison.OrdinalIgnoreCase))
                response.Timing.XppCompileMs = ms;
            else if (step.StartsWith("Best practice check", StringComparison.OrdinalIgnoreCase))
                response.Timing.BpCheckMs = ms;
        }

        var recycle = AppPoolRecycleRegex.Match(stdout);
        if (recycle.Success && long.TryParse(recycle.Groups["ms"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var recycleMs))
        {
            response.Timing.AppPoolRecycleMs = recycleMs;
            response.AppPoolRecycled = true;
        }

        var summary = SummaryRegex.Match(stdout);
        if (summary.Success)
        {
            response.SummaryLine = summary.Groups["line"].Value.Trim();
            var counts = SucceededCountRegex.Match(response.SummaryLine);
            if (counts.Success)
            {
                var succeeded = int.Parse(counts.Groups["succ"].Value, CultureInfo.InvariantCulture);
                var failed = int.Parse(counts.Groups["fail"].Value, CultureInfo.InvariantCulture);
                if (succeeded == 0 && failed == 0)
                {
                    // devenv engaged ZERO projects ("0 succeeded or up-to-date,
                    // 0 failed"). That is a silent no-op, NOT a pass — nothing was
                    // compiled or validated, so reporting success here hands the
                    // caller a false green (exactly what bit a brand-new project
                    // whose objects linked into undeclared rnrproj folders). An
                    // exit code of 0 is meaningless when nothing built.
                    response.Success = false;
                    response.UpToDate = false;
                    response.SummaryLine = response.SummaryLine +
                        " -- built 0 projects: devenv engaged NO project, so nothing was compiled or validated. " +
                        "This is NOT a pass. Check that slnPath/rnprojPath in .dynamics-xpp/config.json are " +
                        "absolute and the .sln actually references the project; a brand-new project also needs its " +
                        "rnrproj folder definitions (auto-managed on object add). An MCP restart may be required to " +
                        "pick up config/path changes, then retry.";
                }
                else
                {
                    response.Success = failed == 0 && process.ExitCode == 0;
                    response.UpToDate = UpToDateRegex.IsMatch(response.SummaryLine);
                }
            }
            else
            {
                response.Success = process.ExitCode == 0;
                response.UpToDate = UpToDateRegex.IsMatch(response.SummaryLine);
            }
        }
        else
        {
            // devenv may exit non-zero without ever printing a summary line
            // (e.g. it rejected the args). Treat absence of the summary as a
            // failure regardless of exit code.
            response.Success = false;
            response.SummaryLine = string.IsNullOrWhiteSpace(stderr)
                ? "devenv produced no build summary; check arguments and project layout."
                : stderr.Trim();
        }

        // Pick up diagnostics across all four result XMLs. The XML format is
        // identical across them (xppbp's diagnostic shape). Deduplicate by
        // (path, moniker, line) — the .err.xml is typically a subset of the
        // main .xml when both fire, so reading both would double-report
        // without dedup.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < resultXmlPaths.Length; i++)
        {
            var path = resultXmlPaths[i];
            if (!File.Exists(path)) continue;
            var mtime = File.GetLastWriteTimeUtc(path);
            // Skip stale files that weren't touched by this build. .err.xml
            // files are deleted/recreated only when there are errors — when
            // a build switches from "errored" to "clean," the prior .err.xml
            // stays on disk with old content. Stale-skip is the safety net.
            if (mtime == priorMtimes[i]) continue;
            try
            {
                using var fs = File.OpenRead(path);
                var doc = await XDocument.LoadAsync(fs, LoadOptions.None, context.CancellationToken).ConfigureAwait(false);
                foreach (var el in doc.Descendants("Diagnostic"))
                {
                    var diag = ParseDiagnostic(el);
                    if (diag == null) continue;
                    // Path-independent key: the XML uses dynamics:// paths while
                    // stdout uses file paths, so keying on path would defeat the
                    // cross-source dedup below. Moniker+location+message is the
                    // stable identity.
                    if (!seen.Add(DiagKey(diag))) continue;
                    response.Diagnostics.Add(diag);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse {Path}", path);
            }
        }

        // Merge the STDOUT diagnostics — the authoritative current set. When the
        // result XMLs were stale-skipped (the common failing-build case) this is
        // the ONLY source that fires; when an XML was fresh, dedup keeps us from
        // double-reporting the same diagnostic.
        foreach (var diag in ParseStdoutDiagnostics(stdout))
        {
            if (!seen.Add(DiagKey(diag))) continue;
            response.Diagnostics.Add(diag);
        }

        // Stale-diagnostics guard. An UP-TO-DATE build (devenv recompiled
        // nothing) that nonetheless surfaces ERROR diagnostics is incoherent:
        // those errors were re-printed from a PRIOR build's cache and do not
        // reflect the current on-disk source. This bit the edit->compile loop —
        // MCP writes that already fixed the errors, then an incremental /Build
        // reports them as still present (upToDate:true alongside contradictory
        // errors). Flag it loudly; rebuild=true is the only trustworthy path
        // after a write. Source-agnostic: catches the leak whether it arrived
        // via stdout or a touched-but-stale result XML.
        var errorDiagCount = response.Diagnostics.Count(d =>
            string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        if (response.UpToDate && errorDiagCount > 0)
        {
            response.SummaryLine = response.SummaryLine +
                $" -- WARNING: build was up-to-date (nothing recompiled) yet reports {errorDiagCount} " +
                "error(s); these are CACHED from a prior build and may NOT reflect the current source " +
                "(e.g. after MCP edits the errors already fixed). Re-run with rebuild=true for " +
                "authoritative diagnostics.";
        }

        // Last-resort surface: when success=false and we still have no
        // structured diagnostics, attach the raw devenv stdout/stderr so the
        // agent has SOMETHING to reason about rather than an empty
        // diagnostics array. Truncate to avoid swamping the response.
        if (!response.Success && response.Diagnostics.Count == 0)
        {
            response.RawStdout = TruncateForWire(stdout);
            response.RawStderr = TruncateForWire(stderr);
            _logger.LogError(
                "devenv reported failure but produced no parseable diagnostics. exit={Exit}; stdout/stderr surfaced to response (truncated). length(stdout)={OutLen} length(stderr)={ErrLen}",
                process.ExitCode, stdout.Length, stderr.Length);
        }

        // Skill linkage: when diagnostics include pattern violations or other
        // recognised cues, attach the relevant skill keys so the agent can
        // load the right docs at the moment they're maximally receptive.
        AddRelevantSkills(response);

        return response;
    }

    /// <summary>
    /// Map diagnostic content patterns onto skill keys the agent should
    /// consider loading. The rules here are deliberately conservative —
    /// we only add a skill key when there's strong textual evidence
    /// that the content of that skill would help.
    ///
    /// New rules are cheap to add: each is a simple regex-or-substring
    /// check over the diagnostic message + moniker.
    /// </summary>
    private static void AddRelevantSkills(CompileResponse response)
    {
        var skills = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in response.Diagnostics)
        {
            var msg = d.Message ?? string.Empty;
            var moniker = d.Moniker ?? string.Empty;

            // "per pattern 'X'" → form pattern conventions live in
            // form-subpatterns + the per-pattern skills.
            if (msg.Contains("per pattern ", StringComparison.OrdinalIgnoreCase) ||
                moniker.StartsWith("PatternControl", StringComparison.OrdinalIgnoreCase) ||
                d.DiagnosticType?.IndexOf("PatternValidation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                skills.Add("dynamics-xpp:xpp-form-subpatterns");
                skills.Add("dynamics-xpp:xpp-form");
            }

            // BP rules → the language skill carries the rule taxonomy.
            if (string.Equals(d.DiagnosticType, "BestPractices", StringComparison.OrdinalIgnoreCase))
            {
                skills.Add("dynamics-xpp:xpp-language");
            }
        }
        foreach (var s in skills.OrderBy(s => s, StringComparer.Ordinal))
            response.RelevantSkills.Add(s);
    }

    /// <summary>Dedup identity for a diagnostic. Includes path + elementType
    /// because metadata diagnostics share message and line (0,0) and differ
    /// ONLY by the element they fire on (e.g. one "Field type must be enum"
    /// per table relation) — dropping those would collapse N distinct errors
    /// into one.</summary>
    private static string DiagKey(BpDiagnostic d)
        => $"{d.Moniker}|{d.Path}|{d.Line}|{d.Column}|{d.Message}|{d.ElementType}";

    /// <summary>
    /// Parse diagnostics out of devenv's stdout build log. This is the
    /// authoritative current diagnostic surface (what the VS Error List
    /// reflects) and the only reliable one for a command-line /Rebuild, where
    /// the result XMLs are routinely stale or error-free even as the build
    /// fails. Recognises the X++/BP line shape and the metadata-validation
    /// line shape; severity is inferred (no explicit token on the lines).
    /// </summary>
    private static IEnumerable<BpDiagnostic> ParseStdoutDiagnostics(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) yield break;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            var m = StdoutDiagnosticRegex.Match(line);
            if (!m.Success)
            {
                // Compile-cascade "Element 'X' of type 'Y' was not found" lines
                // carry no (line,col)/moniker — match them separately. These ARE
                // errors (the element failed to build because a dependency did).
                var enf = ElementNotFoundRegex.Match(line);
                if (enf.Success)
                {
                    yield return new BpDiagnostic
                    {
                        DiagnosticType = "Generation",
                        Severity = "Error",
                        Path = string.Empty,
                        ElementType = $"{enf.Groups["type"].Value}/{enf.Groups["name"].Value}",
                        Moniker = "ElementNotFoundInMetadata",
                        Message = line.Trim(),
                        Line = 0,
                        Column = 0,
                    };
                }
                continue;
            }

            var moniker = m.Groups["moniker"].Value;
            var rest = m.Groups["rest"].Value;

            string diagnosticType;
            string message;
            string elementType = string.Empty;
            bool metadataFormat = false;

            var bp = BpRuleRestRegex.Match(rest);
            var meta = bp.Success ? Match.Empty : MetaPathRestRegex.Match(rest);
            if (bp.Success)
            {
                diagnosticType = "BestPractices";
                message = bp.Groups["msg"].Value;
            }
            else if (meta.Success)
            {
                diagnosticType = "MetadataProvider";
                metadataFormat = true;
                elementType = meta.Groups["ep"].Value;
                message = meta.Groups["msg"].Value;
            }
            else
            {
                diagnosticType = "Build";
                message = rest;
            }

            int.TryParse(m.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln);
            int.TryParse(m.Groups["col"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var col);

            yield return new BpDiagnostic
            {
                DiagnosticType = diagnosticType,
                Severity = ClassifyStdoutSeverity(moniker, metadataFormat),
                Path = m.Groups["path"].Value,
                ElementType = elementType,
                Moniker = moniker,
                Message = message,
                Line = ln,
                Column = col,
            };
        }
    }

    /// <summary>
    /// Infer severity for a stdout diagnostic. The build-log lines carry no
    /// explicit severity token, so we lean on the moniker and the line format.
    /// Conservative bias: when unsure we return "Warning" so the diagnostic
    /// stays VISIBLE (the MCP groups warnings) without crying a false error.
    /// Build pass/fail is decided independently by the summary line, so a
    /// mis-bucketed warning never hides the fact that the build failed.
    /// </summary>
    private static string ClassifyStdoutSeverity(string moniker, bool metadataFormat)
    {
        if (moniker.Equals("UnhandledException", StringComparison.OrdinalIgnoreCase) ||
            moniker.IndexOf("Fatal", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Fatal";
        // Best-Practice rules are NOT build errors in the VS Error List — even
        // the ones named BPError*. They're deviations (VS lists them as
        // warnings/messages; the build doesn't fail on them in this config).
        // Calibrated against VS: its error count excludes every BP* moniker.
        // They still surface — grouped in the warnings bucket — so they remain
        // a visible cleanup backlog without inflating the error count.
        if (moniker.StartsWith("BP", StringComparison.Ordinal)) return "Warning";
        // Metadata- and pattern-validation diagnostics (the "Path: [element]"
        // shape) are the genuine build-breakers VS shows as errors.
        if (metadataFormat) return "Error";
        // Other non-BP diagnostics whose moniker names an error (compiler /
        // generation failures). Unknown non-BP monikers default to Warning so
        // we don't cry false errors.
        if (moniker.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Error";
        return "Warning";
    }

    private const int MaxRawCapture = 64 * 1024;
    private static string TruncateForWire(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.Length <= MaxRawCapture) return s;
        return s.Substring(0, MaxRawCapture) +
            $"\n\n... [truncated; original length {s.Length} bytes]";
    }

    // VS canonical install layout is well-known; we probe the common paths
    // and fall back to vswhere when nothing matches. The exhaustive search
    // happens once per process — the result is cached for subsequent calls.
    private static string? _cachedDevenv;
    private static string? LocateDevenv()
    {
        if (_cachedDevenv != null && File.Exists(_cachedDevenv)) return _cachedDevenv;
        var candidates = new[]
        {
            @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.com",
            @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.com",
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.com",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\Common7\IDE\devenv.com",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\Common7\IDE\devenv.com",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\Common7\IDE\devenv.com",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) { _cachedDevenv = c; return c; }
        }
        return TryVswhere();
    }

    private static string? TryVswhere()
    {
        var vswhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";
        if (!File.Exists(vswhere)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vswhere,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("-latest");
            psi.ArgumentList.Add("-property");
            psi.ArgumentList.Add("productPath");
            using var p = Process.Start(psi);
            if (p == null) return null;
            var line = p.StandardOutput.ReadToEnd()?.Trim();
            p.WaitForExit(5000);
            if (string.IsNullOrEmpty(line)) return null;
            var devenvCom = Path.ChangeExtension(line, "com");
            if (File.Exists(devenvCom)) { _cachedDevenv = devenvCom; return devenvCom; }
        }
        catch { /* swallow — caller will surface a friendly error */ }
        return null;
    }
}
