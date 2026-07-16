using System.Linq;
using System.Text.Json.Nodes;
using Grpc.Core;
using Xpp.Service.Bridge;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;

namespace Xpp.Service.Services;

/// <summary>
/// Domain-object RPCs. Three generic calls (CreateDomainObject /
/// PatchDomainObject / GetDomainObject) handle every supported AxType.
///
/// All domain authoring now goes through the bridge's metaclass mappers:
/// the bridge takes the typed domain JSON, constructs an MS metaclass
/// instance directly, and lets MS's own provider serialize it. No
/// service-side XML emission — that route (and its per-type
/// <c>IDomainMapper</c> implementations) was retired once every type had a
/// bridge-side mapper, eliminating the symmetric parser/emitter loss,
/// element-ordering, and default-elision bug classes by construction.
///
/// Unknown ax_types are rejected by the bridge's domain mapper registry;
/// the error surfaces here as an InvalidArgument via <see cref="MapBridgeError"/>.
/// </summary>
public sealed partial class PingGrpcService
{
    public override async Task<WriteObjectResponse> CreateDomainObject(
        CreateDomainObjectRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "model is required"));
        if (string.IsNullOrWhiteSpace(request.DomainJson))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "domain_json is required"));

        BridgeDomainWriteResult writeResult;
        try
        {
            writeResult = await _bridgeClient.CreateDomainObjectViaMetaclassAsync(
                request.AxType, request.Model, request.DomainJson, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex) { throw MapBridgeError(ex); }

        var name = writeResult.Name;
        await _lifecycle.EnqueueWriteThroughAsync(request.Model, request.AxType, name, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new WriteObjectResponse { AxType = request.AxType, Model = request.Model, Name = name };
        ApplyConformance(writeResult.Conformance, response);
        await ApplyDriftDetectionAsync(request.AxType, name, request.DomainJson, response, context.CancellationToken)
            .ConfigureAwait(false);
        return response;
    }

    public override async Task<WriteObjectResponse> PatchDomainObject(
        PatchDomainObjectRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "model is required"));
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));
        if (string.IsNullOrWhiteSpace(request.PatchJson))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "patch_json is required"));

        BridgeDomainWriteResult writeResult;
        try
        {
            writeResult = await _bridgeClient.PatchDomainObjectViaMetaclassAsync(
                request.AxType, request.Model, request.Name, request.PatchJson, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex) { throw MapBridgeError(ex); }

        var name = writeResult.Name;
        await _lifecycle.EnqueueWriteThroughAsync(request.Model, request.AxType, name, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new WriteObjectResponse { AxType = request.AxType, Model = request.Model, Name = name };
        ApplyConformance(writeResult.Conformance, response);
        await ApplyDriftDetectionAsync(request.AxType, name, request.PatchJson, response, context.CancellationToken)
            .ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// After a successful typed write, fetch the on-disk object back through
    /// the bridge's metaclass serializer-then-deserializer and diff against
    /// the original request. Any drift entries are attached to
    /// <paramref name="response"/>.
    ///
    /// Drift detection is best-effort: a failure here (bridge transient,
    /// round-trip exception) is logged but never escalated. The write
    /// already succeeded; we don't want a noisy detection failure to mask
    /// that.
    /// </summary>
    private async Task ApplyDriftDetectionAsync(
        string axType,
        string name,
        string originalJson,
        WriteObjectResponse response,
        CancellationToken ct)
    {
        try
        {
            var roundTrippedJson = await _bridgeClient.GetDomainObjectViaMetaclassAsync(
                axType, name, ct, includeDefaults: true).ConfigureAwait(false);
            var drift = DriftDetector.Detect(originalJson, roundTrippedJson);
            foreach (var d in drift)
            {
                response.Drift.Add(new DriftWarning
                {
                    RequestPath = d.RequestPath,
                    RequestValue = d.RequestValue,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Drift detection failed for {AxType} '{Name}' (write succeeded; drift list will be empty)",
                axType, name);
        }
    }

    /// <summary>
    /// Map the bridge's raw conformance JSON onto the proto
    /// <see cref="PatternConformance"/> and attach it to the response. No-op
    /// when the bridge returned nothing (non-form write, or a form with no
    /// declared pattern).
    /// </summary>
    private static void ApplyConformance(JsonNode? conformance, WriteObjectResponse response)
    {
        if (conformance is not JsonObject o) return;

        var pc = new PatternConformance
        {
            Pattern = o["pattern"]?.GetValue<string>() ?? "",
            Version = o["version"]?.GetValue<string>() ?? "",
            Ok = o["ok"]?.GetValue<bool>() ?? false,
            Note = o["note"]?.GetValue<string>() ?? "",
            // The bridge emits versionActive only when the declared version is
            // retired; absent means active (the common case).
            VersionActive = o["versionActive"]?.GetValue<bool>() ?? true,
            VersionNote = o["versionNote"]?.GetValue<string>() ?? "",
        };

        if (o["activeVersions"] is JsonArray activeVersions)
            foreach (var av in activeVersions)
                if (av?.GetValue<string>() is { Length: > 0 } v) pc.ActiveVersions.Add(v);

        if (o["missing"] is JsonArray missing)
            foreach (var m in missing.OfType<JsonObject>())
                pc.Missing.Add(new PatternMissing
                {
                    Path = m["path"]?.GetValue<string>() ?? "",
                    Expected = m["expected"]?.GetValue<string>() ?? "",
                });

        if (o["overrides"] is JsonArray overrides)
            foreach (var ov in overrides.OfType<JsonObject>())
                pc.Overrides.Add(new PatternOverride
                {
                    Path = ov["path"]?.GetValue<string>() ?? "",
                    Control = ov["control"]?.GetValue<string>() ?? "",
                    Property = ov["property"]?.GetValue<string>() ?? "",
                    Requested = ov["requested"]?.GetValue<string>() ?? "",
                    PatternValue = ov["patternValue"]?.GetValue<string>() ?? "",
                });

        if (o["mismatches"] is JsonArray mismatches)
            foreach (var mm in mismatches.OfType<JsonObject>())
                pc.Mismatches.Add(new PatternMismatch
                {
                    Path = mm["path"]?.GetValue<string>() ?? "",
                    Control = mm["control"]?.GetValue<string>() ?? "",
                    Property = mm["property"]?.GetValue<string>() ?? "",
                    Actual = mm["actual"]?.GetValue<string>() ?? "",
                    PatternValue = mm["patternValue"]?.GetValue<string>() ?? "",
                    Op = mm["op"]?.GetValue<string>() ?? "",
                });

        response.PatternConformance = pc;
    }

    public override async Task<GetDomainObjectResponse> GetDomainObject(
        GetDomainObjectRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));

        string domainJson;
        try
        {
            domainJson = await _bridgeClient.GetDomainObjectViaMetaclassAsync(
                request.AxType, request.Name, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex) { throw MapBridgeError(ex); }

        var response = new GetDomainObjectResponse
        {
            AxType = request.AxType,
            Name = request.Name,
            DomainJson = domainJson,
        };

        var wantsNav = request.Outline || !string.IsNullOrWhiteSpace(request.AtPath);
        if (!wantsNav) return response;  // whole-object read — unchanged passthrough

        // Path-addressable navigation: walk the bridge JSON service-side so the
        // wire ships only the outline / subtree (saves payload AND agent tokens).
        // The bridge returns the inner domain object directly (the MCP layer is
        // what wraps it as {axType,name,domain,...}), so parse it as-is.
        var inner = JsonNode.Parse(domainJson);
        if (inner == null)
            throw new RpcException(new Status(StatusCode.Internal, "domain JSON did not parse"));

        var atPath = request.AtPath ?? "";
        var rooted = DomainSkeleton.Resolve(inner, atPath);
        if (rooted == null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"path '{atPath}' does not resolve in {request.AxType} '{request.Name}'. " +
                DomainSkeleton.ResolveHint(inner, atPath)));

        response.AtPath = atPath;
        if (request.Outline)
        {
            var rootName = LastSegment(atPath) ?? request.Name;
            var skeleton = DomainSkeleton.BuildOutline(rooted, atPath, rootName, request.Depth);
            skeleton["axType"] = request.AxType;
            skeleton["object"] = request.Name;
            response.DomainJson = skeleton.ToJsonString();
            response.IsOutline = true;
        }
        else
        {
            // Full subtree at a path (zoom).
            response.DomainJson = rooted.ToJsonString();
        }
        return response;
    }

    private static string? LastSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return null;
        var segs = path!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length > 0 ? segs[^1] : null;
    }

    public override async Task<WriteObjectResponse> PatchDomainObjectByPath(
        PatchByPathRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "model is required"));
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));
        if (string.IsNullOrWhiteSpace(request.AtPath))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "at_path is required"));

        // 1. Read current on-disk state as the bridge's domain JSON.
        string currentJson;
        try
        {
            currentJson = await _bridgeClient.GetDomainObjectViaMetaclassAsync(
                request.AxType, request.Name, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex) { throw MapBridgeError(ex); }

        var root = JsonNode.Parse(currentJson)
            ?? throw new RpcException(new Status(StatusCode.Internal, "domain JSON did not parse"));

        // 2. Splice the value at the path. Bad addressing / shape -> InvalidArgument.
        JsonNode? value = null;
        if (!string.IsNullOrWhiteSpace(request.ValueJson))
        {
            try { value = JsonNode.Parse(request.ValueJson); }
            catch (Exception ex) { throw new RpcException(new Status(StatusCode.InvalidArgument, $"value_json is not valid JSON: {ex.Message}")); }

            // Some clients marshal an untyped value param as a JSON STRING that
            // itself holds the object's JSON. Unwrap one level when the string
            // clearly contains a JSON object/array (never touches plain scalars).
            if (value is JsonValue jv && jv.TryGetValue<string>(out var inner))
            {
                var t = inner.TrimStart();
                if (t.StartsWith("{") || t.StartsWith("["))
                {
                    try { value = JsonNode.Parse(inner); } catch { /* leave as the string */ }
                }
            }
        }

        DomainSkeleton.SpliceResult splice;
        try { splice = DomainSkeleton.ApplyOp(root, request.AtPath, request.Op, value); }
        catch (DomainSkeleton.SpliceException ex)
        { throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)); }

        // Dry run: return the spliced subtree for confirmation; write nothing.
        // splice.Preview is a live reference to the edited node (not a re-resolve
        // by path), so it's correct even when the op renamed the member's key.
        if (request.DryRun)
        {
            return new WriteObjectResponse
            {
                AxType = request.AxType, Model = request.Model, Name = request.Name,
                PreviewJson = splice.Preview?.ToJsonString() ?? "null",
            };
        }

        // 3. Send only the changed top-level branch through the existing metaclass
        //    patch. Patch merge replaces a non-null collection wholesale and applies
        //    nested-object properties, and the branch is the full current value with
        //    the edit applied — so the object becomes exactly the spliced tree.
        var branchPatch = new JsonObject { [splice.TopSegment] = root[splice.TopSegment]?.DeepClone() };
        var patchJson = branchPatch.ToJsonString();

        BridgeDomainWriteResult writeResult;
        try
        {
            writeResult = await _bridgeClient.PatchDomainObjectViaMetaclassAsync(
                request.AxType, request.Model, request.Name, patchJson, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex) { throw MapBridgeError(ex); }

        var name = writeResult.Name;
        await _lifecycle.EnqueueWriteThroughAsync(request.Model, request.AxType, name, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new WriteObjectResponse { AxType = request.AxType, Model = request.Model, Name = name };
        ApplyConformance(writeResult.Conformance, response);
        await ApplyDriftDetectionAsync(request.AxType, name, patchJson, response, context.CancellationToken)
            .ConfigureAwait(false);

        // Drop false-positive drift: a key the edit INTRODUCED that didn't
        // round-trip is the node type not carrying that property (e.g. `name`
        // on a FormDataSourceField, which is keyed by dataField) — not a mapper
        // regression. Only keys the node already had count as real drift.
        if (splice.AddedKeys.Count > 0 && response.Drift.Count > 0)
        {
            var added = new HashSet<string>(splice.AddedKeys, StringComparer.OrdinalIgnoreCase);
            var kept = response.Drift.Where(d => !added.Contains(LeafProperty(d.RequestPath))).ToList();
            response.Drift.Clear();
            response.Drift.AddRange(kept);
        }
        return response;
    }

    /// <summary>Leaf property name of a drift path like
    /// "dataSources[0].fields[7].name" -> "name" (drops array indexers).</summary>
    private static string LeafProperty(string requestPath)
    {
        var seg = requestPath.Split('.').LastOrDefault() ?? requestPath;
        var bracket = seg.IndexOf('[');
        return bracket >= 0 ? seg[..bracket] : seg;
    }

    public override async Task<EvictObjectResponse> EvictObjectFromIndex(
        EvictObjectRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "model and name are required"));
        await _lifecycle.RemoveObjectAsync(request.Model, request.AxType, request.Name, context.CancellationToken)
            .ConfigureAwait(false);
        return new EvictObjectResponse { Evicted = true };
    }

    public override async Task<FindInObjectResponse> FindInObject(
        FindInObjectRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));

        string domainJson;
        try
        {
            domainJson = await _bridgeClient.GetDomainObjectViaMetaclassAsync(
                request.AxType, request.Name, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex) { throw MapBridgeError(ex); }

        // The bridge returns the inner domain object directly (the MCP layer is
        // what wraps it as {axType,name,domain,...}), so parse it as-is.
        var inner = JsonNode.Parse(domainJson);
        if (inner == null)
            throw new RpcException(new Status(StatusCode.Internal, "domain JSON did not parse"));

        var matches = DomainSkeleton.Find(inner, new DomainSkeleton.FindFilter(
            request.Query, request.Kind, request.DataSource, request.DataField));

        return new FindInObjectResponse
        {
            AxType = request.AxType,
            Name = request.Name,
            MatchesJson = matches.ToJsonString(),
        };
    }
}
