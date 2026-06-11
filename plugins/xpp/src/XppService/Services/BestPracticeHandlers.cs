using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Grpc.Core;
using Xpp.Service.Bridge;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Services;

/// <summary>
/// Best Practice check handler. Wraps the F&O server-side xppbp.exe — the
/// same tool the AOS uses (and that VS Build invokes under the hood).
///
/// Why xppbp.exe instead of the BestPracticeFramework DLL API:
///   xppbp.exe is a stable CLI, takes module+model+element-filters, and
///   writes structured XML diagnostics. Reverse-engineering the DLL entry
///   point (IAgentWrapperService.RunBestPracticeChecks) means matching
///   internal descriptor / output-dir / refs construction that VS does in
///   process. The CLI hides all of that behind documented arguments.
///
/// We invoke xppbp once per request with every element filter the caller
/// provided. Process startup dominates the cost (~5-6 seconds), so
/// batching elements into a single invocation is a strict perf win.
/// </summary>
public sealed partial class PingGrpcService
{
    // Token vocabulary discovered by sending an invalid token to xppbp.exe;
    // it lists every supported type in its error output. Keep this
    // case-insensitive — we compare with the casing on our AxType side.
    private static readonly Dictionary<string, string> AxTypeToBpToken = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AxClass"] = "Class",
        ["AxTable"] = "Table",
        ["AxTableExtension"] = "TableExtension",
        ["AxForm"] = "Form",
        ["AxFormExtension"] = "FormExtension",
        ["AxView"] = "View",
        ["AxEnum"] = "Enum",
        ["AxEdt"] = "ExtendedDataType",
        ["AxMenu"] = "Menu",
        ["AxMenuExtension"] = "MenuExtension",
        ["AxMenuItemDisplay"] = "MenuItemDisplay",
        ["AxMenuItemAction"] = "MenuItemAction",
        ["AxMenuItemOutput"] = "MenuItemOutput",
        ["AxConfigurationKey"] = "ConfigurationKey",
        ["AxLicenseCode"] = "LicenseCode",
        ["AxMacro"] = "Macro",
        ["AxMacroDictionary"] = "Macro",
        ["AxMap"] = "Map",
        ["AxQuery"] = "Query",
        ["AxService"] = "Service",
        ["AxServiceGroup"] = "ServiceGroup",
        ["AxSecurityPrivilege"] = "SecurityPrivilege",
        ["AxSecurityDuty"] = "SecurityDuty",
        ["AxSecurityRole"] = "SecurityRole",
        ["AxInfoPart"] = "Infopart",
        ["AxReport"] = "Report",
        ["AxAggregateDimension"] = "AggregateDimension",
        ["AxAggregateMeasurement"] = "AggregateMeasurement",
        ["AxDataEntityView"] = "DataEntityView",
        ["AxAggregateDataEntity"] = "AggregateDataEntity",
        ["AxCompositeDataEntityView"] = "CompositeDataEntityView",
    };

    public override async Task RunBestPracticeChecks(
        BpCheckRequest request,
        IServerStreamWriter<BpDiagnostic> responseStream,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Module))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "module is required"));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "model is required"));
        if (request.Elements == null || request.Elements.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "elements must contain at least one filter"));

        // Resolve xppbp.exe from <PackagesLocalDirectory>/bin/xppbp.exe.
        var packagesRoot = _bridgeOptions.PackagesLocalDirectory;
        if (string.IsNullOrWhiteSpace(packagesRoot))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "D365:PackagesLocalDirectory is not configured; cannot locate xppbp.exe"));
        var xppbp = Path.Combine(packagesRoot!, "bin", "xppbp.exe");
        if (!File.Exists(xppbp))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"xppbp.exe not found at {xppbp}"));

        var args = BuildArgs(packagesRoot!, request, out var xmlOutPath);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = xppbp,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)
                ?? throw new RpcException(new Status(StatusCode.Internal, "xppbp.exe could not be started"));

            // Read both streams so the process doesn't block on a full pipe
            // buffer. We don't actually use stdout/stderr beyond logging —
            // the structured payload is the XML output file.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (!File.Exists(xmlOutPath))
            {
                _logger.LogError("xppbp.exe exited with code {Code} but no XML output was written. stdout={Stdout} stderr={Stderr}",
                    process.ExitCode, stdout, stderr);
                throw new RpcException(new Status(StatusCode.Internal,
                    $"xppbp.exe produced no XML output (exit code {process.ExitCode}). stderr: {stderr.TrimEnd()}"));
            }

            XDocument doc;
            using (var fs = File.OpenRead(xmlOutPath))
            {
                doc = await XDocument.LoadAsync(fs, LoadOptions.None, context.CancellationToken).ConfigureAwait(false);
            }

            foreach (var el in doc.Descendants("Diagnostic"))
            {
                var diag = ParseDiagnostic(el);
                if (diag != null)
                    await responseStream.WriteAsync(diag, context.CancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try { if (File.Exists(xmlOutPath)) File.Delete(xmlOutPath); } catch { /* best-effort */ }
        }
    }

    private static List<string> BuildArgs(string packagesRoot, BpCheckRequest request, out string xmlOutPath)
    {
        var args = new List<string>
        {
            $"-metadata={packagesRoot}",
            $"-packagesRoot={packagesRoot}",
            $"-module={request.Module}",
            $"-model={request.Model}"
        };

        var supported = 0;
        foreach (var el in request.Elements)
        {
            if (!AxTypeToBpToken.TryGetValue(el.AxType, out var token))
            {
                // xppbp.exe only knows a fixed token set (class/table/form/...).
                // Project scope can include AOT types xppbp can't check (e.g.
                // AxCompositeDataEntityView); silently skip them rather than
                // fail the whole batch.
                continue;
            }
            if (string.IsNullOrWhiteSpace(el.Name))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    $"element name is required (use '*' for all of type)"));
            args.Add($"{token}:{el.Name}");
            supported++;
        }
        if (supported == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "none of the supplied elements are checkable by xppbp.exe"));

        // -rules= takes a semicolon-separated list. Push the filter down to
        // xppbp so it skips evaluator work for non-listed rules and emits
        // a smaller XML payload.
        if (request.OnlyRules != null && request.OnlyRules.Count > 0)
            args.Add($"-rules={string.Join(';', request.OnlyRules)}");

        // -TreatWarningsAsErrors= takes comma-separated monikers.
        if (request.EscalateRules != null && request.EscalateRules.Count > 0)
            args.Add($"-TreatWarningsAsErrors={string.Join(',', request.EscalateRules)}");

        xmlOutPath = Path.Combine(Path.GetTempPath(),
            $"xpp-bp-{Guid.NewGuid():N}.xml");
        args.Add($"-x={xmlOutPath}");
        return args;
    }

    private static BpDiagnostic? ParseDiagnostic(XElement el)
    {
        return new BpDiagnostic
        {
            DiagnosticType = (string?)el.Element("DiagnosticType") ?? string.Empty,
            Severity       = (string?)el.Element("Severity")       ?? string.Empty,
            Path           = (string?)el.Element("Path")           ?? string.Empty,
            ElementType    = (string?)el.Element("ElementType")    ?? string.Empty,
            Moniker        = (string?)el.Element("Moniker")        ?? string.Empty,
            Message        = (string?)el.Element("Message")        ?? string.Empty,
            Line           = ParseInt(el.Element("Line")),
            Column         = ParseInt(el.Element("Column")),
            EndLine        = ParseInt(el.Element("EndLine")),
            EndColumn      = ParseInt(el.Element("EndColumn")),
        };
    }

    private static int ParseInt(XElement? el)
    {
        if (el == null) return 0;
        return int.TryParse(el.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
