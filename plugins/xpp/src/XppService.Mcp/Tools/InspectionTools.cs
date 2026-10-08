using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Inspection tools. Two-step pattern matched to the agent's natural workflow:
///   xpp_get_object_methods    "what methods does this object have?"
///   xpp_get_method_source     "show me this specific method"
///
/// Both pass through to the service's cache-first/bridge-fallback handlers,
/// so the agent can read source for objects that haven't been phase-2 indexed
/// yet without manual recovery steps.
/// </summary>
[McpServerToolType]
public sealed class InspectionTools
{
    private readonly XppServiceConnection _conn;

    public InspectionTools(XppServiceConnection conn)
    {
        _conn = conn;
    }

    [McpServerTool(Name = "xpp_get_object_methods"), Description(
        "List every method on a D365 X++ object as a lightweight summary " +
        "(no source bodies). Returns name, signature, isStatic, accessLevel, " +
        "returnType, lineCount per method. Cache-first - if the indexer " +
        "has seen this object the response is instant; otherwise the bridge " +
        "is asked live. Pair with xpp_get_method_source to read the body " +
        "of any one method.")]
    public async Task<string> GetObjectMethods(
        [Description("Object name (e.g. 'CustTable').")] string name,
        [Description("D365 type (AxClass, AxTable, AxForm, ...).")] string axType,
        [Description("Model the object lives in (e.g. 'Foundation', 'ApplicationSuite'). Optional: resolved from the index when omitted; required only when the same name exists in more than one model.")] string? model = null,
        CancellationToken ct = default)
    {
        try
        {
            var (resolvedModel, modelError) = await ResolveModelAsync(name, axType, model, ct).ConfigureAwait(false);
            if (modelError != null) return modelError;
            var summaries = new List<object>();
            using var call = _conn.Client.GetObjectMethods(new ObjectRef
            {
                Name = name,
                AxType = axType,
                Model = resolvedModel
            });

            while (await call.ResponseStream.MoveNext(ct))
            {
                var m = call.ResponseStream.Current;
                summaries.Add(new
                {
                    name = m.Name,
                    signature = m.Signature,
                    isStatic = m.IsStatic,
                    accessLevel = m.AccessLevel,
                    returnType = m.ReturnType,
                    lineCount = m.LineCount
                });
            }

            return JsonSerializer.Serialize(new { count = summaries.Count, methods = summaries });
        }
        catch (Exception ex) { return ToolError.From("xpp_get_object_methods", ex); }
    }

    [McpServerTool(Name = "xpp_get_method_source"), Description(
        "Return the X++ source code for a specific method on an object. " +
        "Cache-first with bridge fallback. The 'fromCache' flag in the " +
        "response indicates which path served the call (useful for " +
        "diagnostics; semantically the source is the same either way).")]
    public async Task<string> GetMethodSource(
        [Description("Object name.")] string name,
        [Description("D365 type (AxClass, AxTable, ...).")] string axType,
        [Description("Method name (case-insensitive).")] string methodName,
        [Description("Model name. Optional: resolved from the index when omitted; required only when the same name exists in more than one model.")] string? model = null,
        CancellationToken ct = default)
    {
        try
        {
            var (resolvedModel, modelError) = await ResolveModelAsync(name, axType, model, ct).ConfigureAwait(false);
            if (modelError != null) return modelError;
            var result = await _conn.Client.GetMethodSourceAsync(new GetMethodSourceRequest
            {
                Object = new ObjectRef { Name = name, AxType = axType, Model = resolvedModel },
                MethodName = methodName
            }, cancellationToken: ct);

            return JsonSerializer.Serialize(new
            {
                name = result.Name,
                signature = result.Signature,
                isStatic = result.IsStatic,
                accessLevel = result.AccessLevel,
                returnType = result.ReturnType,
                fromCache = result.FromCache,
                source = result.SourceCode
            });
        }
        catch (global::Grpc.Core.RpcException rex) when (rex.StatusCode == global::Grpc.Core.StatusCode.NotFound)
        {
            // Return a structured "not found" payload instead of an exception
            // so the agent can branch on the data rather than catching errors.
            return JsonSerializer.Serialize(new { error = "not_found", message = rex.Status.Detail });
        }
        catch (Exception ex)
        {
            // Anything else (non-NotFound RpcException, bridge/internal failure)
            // would otherwise escape to the SDK's contentless envelope. Surface it.
            return ToolError.From("xpp_get_method_source", ex);
        }
    }

    /// <summary>
    /// The index knows which model an object lives in; asking the caller for
    /// it was a required argument that, when omitted, died inside the SDK's
    /// binding as a contentless error. Resolve it here; insist only when the
    /// name is genuinely ambiguous across models.
    /// </summary>
    private async Task<(string Model, string? Error)> ResolveModelAsync(string name, string axType, string? model, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(model)) return (model, null);
        var models = new List<string>();
        using var call = _conn.Client.FindObject(new FindObjectRequest { Name = name, AxType = axType });
        while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
        {
            var r = call.ResponseStream.Current.Ref;
            if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.AxType, axType, StringComparison.OrdinalIgnoreCase)
                && !models.Contains(r.Model, StringComparer.OrdinalIgnoreCase))
                models.Add(r.Model);
        }
        if (models.Count == 1) return (models[0], null);
        if (models.Count == 0)
            return (string.Empty, JsonSerializer.Serialize(new
            {
                error = "not_found",
                message = $"{axType} '{name}' is not in the index. Check the name with xpp_find_object (the axType must be the on-disk kind, e.g. AxClass not Class).",
            }));
        return (string.Empty, JsonSerializer.Serialize(new
        {
            error = "ambiguous_model",
            models,
            message = $"{axType} '{name}' exists in {models.Count} models; pass model to pick one.",
        }));
    }
}
