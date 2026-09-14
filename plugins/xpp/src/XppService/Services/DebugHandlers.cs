using System.Text.Json.Nodes;
using Grpc.Core;
using Xpp.Service.Bridge;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Indexing;

namespace Xpp.Service.Services;

/// <summary>
/// Live X++ debugging RPCs. Thin translation layer: each RPC becomes one
/// JSON-RPC call on the debug bridge (which owns the hidden Visual Studio
/// and the debugger session) and its JSON answer is mapped onto the typed
/// proto messages. The one piece of real logic here is resolving an AOT
/// element to its on-disk XML + model from the index, which the bridge
/// needs to generate the .xpp document a breakpoint binds against.
/// </summary>
public sealed partial class PingGrpcService
{
    public override async Task<DebugAttachResponse> DebugAttach(DebugAttachRequest request, ServerCallContext context)
    {
        var target = string.IsNullOrWhiteSpace(request.Target) ? "aos" : request.Target;
        var p = new JsonObject { ["target"] = target };
        if (request.MaxPauseSeconds > 0) p["maxPauseSeconds"] = request.MaxPauseSeconds;
        var r = await DebugCallAsync("debug.attach", p, context.CancellationToken).ConfigureAwait(false);
        _debugBridge.OwnerClientId = request.ClientId ?? string.Empty;
        _debugBridge.VsPid = I(r, "vsPid");
        return new DebugAttachResponse
        {
            Attached = B(r, "attached"),
            Target = S(r, "target"),
            Pid = I(r, "pid"),
            ProcessName = S(r, "processName"),
            Engine = S(r, "engine"),
            ElapsedMs = L(r, "elapsedMs"),
        };
    }

    public override async Task<DebugDetachResponse> DebugDetach(DebugEmpty request, ServerCallContext context)
    {
        var r = await DebugCallAsync("debug.detach", new JsonObject(), context.CancellationToken).ConfigureAwait(false);
        _debugBridge.OwnerClientId = string.Empty;
        return new DebugDetachResponse
        {
            WasAttached = r?["detachedFrom"] != null,
            Target = S(r, "detachedFrom"),
            Pid = I(r, "pid"),
            ResumedBeforeDetach = B(r, "resumedBeforeDetach"),
            How = S(r, "how"),
            TargetTerminated = B(r, "targetTerminated"),
            Note = S(r, "note"),
        };
    }

    /// <summary>
    /// Must work when nothing else does. Ask the bridge for a forced detach
    /// (it kills its hidden VS without touching automation) with a short
    /// deadline; if the bridge does not answer, kill the bridge and its VS
    /// from here. Never waits behind a stuck request.
    /// </summary>
    public override async Task<DebugDetachResponse> DebugForceDetach(DebugEmpty request, ServerCallContext context)
    {
        if (!_debugBridge.IsRunning)
            return new DebugDetachResponse { WasAttached = false, How = "bridge not running" };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var r = await _debugBridge.InvokeAsync("debug.detach", new JsonObject { ["force"] = true }, cts.Token).ConfigureAwait(false);
            _debugBridge.OwnerClientId = string.Empty;
            return new DebugDetachResponse { WasAttached = r?["detachedFrom"] != null, Target = S(r, "detachedFrom"), Pid = I(r, "pid"), How = S(r, "how", "killed-vs (forced)"), TargetTerminated = B(r, "targetTerminated"), Note = S(r, "note") };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Debug bridge did not answer a forced detach ({Reason}); killing bridge + its VS", ex.Message);
            var how = await _debugBridge.KillAsync().ConfigureAwait(false);
            return new DebugDetachResponse { WasAttached = true, How = how, TargetTerminated = how == "killed-bridge",
                Note = how == "killed-bridge" ? "the debug bridge did not answer, so the service killed it and its hidden Visual Studio. The target process died with its debugger and its host (IIS / the batch service) restarts it; sessions on an AOS worker are lost." : "the debug bridge did not answer and was killed; its Visual Studio was already gone" };
        }
    }

    public override async Task<DebugBreakpoint> DebugSetBreakpoint(DebugSetBreakpointRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.AxType) || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Method))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ax_type, name and method are required"));

        // Resolve the element to its model from the index, then derive the
        // on-disk XML the same way the reconciler does (the index does not
        // store paths; the packages layout is uniform, so it doesn't need to).
        var candidates = new List<(string Model, string Source)>();
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT model, COALESCE(source, 'disk') FROM objects
                WHERE name = $name AND ax_type = $axType AND ($model = '' OR model = $model)
                ORDER BY model";
            cmd.Parameters.AddWithValue("$name", request.Name);
            cmd.Parameters.AddWithValue("$axType", request.AxType);
            cmd.Parameters.AddWithValue("$model", request.Model ?? string.Empty);
            using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
                candidates.Add((reader.GetString(0), reader.GetString(1)));
        }
        if (candidates.Count == 0)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"{request.AxType} '{request.Name}' is not in the index (is the name right? try xpp_find_object)"));

        var roots = DiskReconciler.BuildModelRoots(_bridgeOptions.PackagesLocalDirectory, candidates.Select(c => c.Model));
        string? model = null, filePath = null;
        foreach (var c in candidates)
        {
            var path = DiskReconciler.ContentFilePath(roots, c.Model, request.AxType, request.Name);
            if (path != null && File.Exists(path)) { model = c.Model; filePath = path; break; }
        }
        if (model == null || filePath == null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"{request.AxType} '{request.Name}' has no on-disk XML (models: {string.Join(", ", candidates.Select(c => c.Model))}) -- runtime-only elements cannot be source-debugged"));
        if (candidates.Count > 1 && string.IsNullOrEmpty(request.Model))
            _logger.LogInformation("Debug breakpoint on {Type} {Name}: several models ({Models}); using {Model}", request.AxType, request.Name, string.Join(", ", candidates.Select(c => c.Model)), model);

        var p = new JsonObject
        {
            ["axType"] = request.AxType,
            ["name"] = request.Name,
            ["method"] = request.Method,
            ["model"] = model,
            ["xmlPath"] = filePath,
            ["offset"] = request.Offset > 0 ? request.Offset : 1,
        };
        if (!string.IsNullOrWhiteSpace(request.Condition)) p["condition"] = request.Condition;
        var r = await DebugCallAsync("debug.setBreakpoint", p, context.CancellationToken).ConfigureAwait(false);
        return MapBreakpoint(r);
    }

    public override async Task<DebugBreakpointList> DebugListBreakpoints(DebugEmpty request, ServerCallContext context)
    {
        var r = await DebugCallAsync("debug.listBreakpoints", new JsonObject(), context.CancellationToken).ConfigureAwait(false);
        var list = new DebugBreakpointList();
        if (r is JsonArray arr) foreach (var n in arr) list.Breakpoints.Add(MapBreakpoint(n));
        return list;
    }

    public override async Task<DebugClearBreakpointsResponse> DebugClearBreakpoints(DebugEmpty request, ServerCallContext context)
    {
        var r = await DebugCallAsync("debug.clearBreakpoints", new JsonObject(), context.CancellationToken).ConfigureAwait(false);
        return new DebugClearBreakpointsResponse { Cleared = I(r, "cleared") };
    }

    public override async Task<DebugCapture> DebugWait(DebugWaitRequest request, ServerCallContext context)
    {
        var p = new JsonObject { ["timeoutSec"] = request.TimeoutSec > 0 ? request.TimeoutSec : 60, ["watch"] = new JsonArray(request.Watch.Select(w => (JsonNode?)w).ToArray()) };
        var r = await DebugCallAsync("debug.wait", p, context.CancellationToken).ConfigureAwait(false);
        return MapCapture(r);
    }

    public override async Task<DebugContinueResponse> DebugContinue(DebugEmpty request, ServerCallContext context)
    {
        var r = await DebugCallAsync("debug.continue", new JsonObject(), context.CancellationToken).ConfigureAwait(false);
        return new DebugContinueResponse { Resumed = B(r, "resumed"), WasPaused = B(r, "wasPaused") };
    }

    public override async Task<DebugCapture> DebugStep(DebugStepRequest request, ServerCallContext context)
    {
        var p = new JsonObject { ["kind"] = string.IsNullOrWhiteSpace(request.Kind) ? "over" : request.Kind, ["watch"] = new JsonArray(request.Watch.Select(w => (JsonNode?)w).ToArray()) };
        var r = await DebugCallAsync("debug.step", p, context.CancellationToken).ConfigureAwait(false);
        return MapCapture(r);
    }

    public override async Task<DebugEvalResponse> DebugEval(DebugEvalRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Expression))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "expression is required"));
        var p = new JsonObject { ["expression"] = request.Expression, ["expand"] = request.Expand };
        var r = await DebugCallAsync("debug.eval", p, context.CancellationToken).ConfigureAwait(false);
        var rsp = new DebugEvalResponse { Expression = S(r, "expression"), Valid = B(r, "valid"), Type = S(r, "type"), Value = S(r, "value") };
        if (r?["members"] is JsonArray members) foreach (var m in members) rsp.Members.Add(MapValue(m));
        return rsp;
    }

    public override async Task<DebugStatusResponse> DebugStatus(DebugEmpty request, ServerCallContext context)
    {
        // Status must not START the bridge (that spawns a VS): answer
        // "detached, bridge not running" cheaply when it is down.
        var probe = _debugBridge.Probe();
        if (!_debugBridge.IsRunning)
            return new DebugStatusResponse { Attached = false, Mode = "detached", BridgeRunning = false, Elevated = OperatingSystem.IsWindows() && IsElevated(), BridgeAvailable = probe.Available, BridgeExe = probe.Path };

        JsonNode? r;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            r = await DebugCallAsync("debug.status", new JsonObject(), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            return new DebugStatusResponse { Attached = true, Mode = "unknown (bridge not responding)", BridgeRunning = true, AutomationWedged = true, Elevated = IsElevated(), BridgeAvailable = probe.Available, BridgeExe = probe.Path, ClientId = _debugBridge.OwnerClientId, Watchdog = "the bridge did not answer within 15s; use xpp_debug_detach force=true to release the target" };
        }
        var st = r?["status"];
        if (I(st, "vsPid") > 0) _debugBridge.VsPid = I(st, "vsPid");
        return new DebugStatusResponse
        {
            Attached = B(st, "attached"),
            Target = S(st, "target"),
            Pid = I(st, "pid"),
            ProcessName = S(st, "processName"),
            Mode = S(st, "mode"),
            Paused = B(st, "paused"),
            PausedSeconds = I(st, "pausedSeconds"),
            MaxPauseSeconds = I(st, "maxPauseSeconds"),
            Breakpoints = I(st, "breakpoints"),
            Elevated = B(r, "elevated"),
            ClientId = _debugBridge.OwnerClientId,
            AutoResumed = !string.IsNullOrEmpty(S(st, "watchdog")),
            BridgeRunning = true,
            BridgeAvailable = probe.Available,
            BridgeExe = probe.Path,
            AutomationWedged = B(st, "automationWedged"),
            Watchdog = S(st, "watchdog"),
        };
    }

    // ---- plumbing ----------------------------------------------------------------

    private async Task<JsonNode?> DebugCallAsync(string method, JsonNode? p, CancellationToken ct)
    {
        try
        {
            return await _debugBridge.InvokeAsync(method, p, ct).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            // The bridge's messages are written for the agent; pass them through.
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static DebugBreakpoint MapBreakpoint(JsonNode? n) => new()
    {
        Id = I(n, "id"), AxType = S(n, "axType"), Name = S(n, "name"), Method = S(n, "method"), File = S(n, "file"),
        RequestedLine = I(n, "requestedLine"), Bound = B(n, "bound"), BoundLine = I(n, "boundLine"),
        SourceLine = S(n, "sourceLine"), Condition = S(n, "condition"), Note = S(n, "note"),
    };

    private static DebugFrame MapFrame(JsonNode? n) => new()
    {
        Index = I(n, "index"), Function = S(n, "function"), Language = S(n, "language"), Xpp = B(n, "xpp"),
        File = S(n, "file"), Line = I(n, "line"), SourceLine = S(n, "sourceLine"),
    };

    private static DebugValue MapValue(JsonNode? n) => new()
    {
        Name = S(n, "name", S(n, "expression")), Type = S(n, "type"), Value = S(n, "value"),
        Valid = n?["valid"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : true, Error = S(n, "error"),
    };

    private static DebugCapture MapCapture(JsonNode? r)
    {
        var c = new DebugCapture
        {
            Hit = B(r, "hit"), TimedOut = B(r, "timedOut"), Paused = B(r, "paused"), ThreadId = I(r, "threadId"),
            ThreadName = S(r, "threadName"), Note = S(r, "note"), AutoResumed = !string.IsNullOrEmpty(S(r, "watchdog")), MaxPauseSeconds = I(r, "maxPauseSeconds"),
        };
        if (r?["breakpoint"] is JsonObject bp) c.Breakpoint = new DebugHit { File = S(bp, "file"), Line = I(bp, "line"), Function = S(bp, "function") };
        if (r?["location"] is JsonObject loc) c.Location = MapFrame(loc);
        if (r?["this"] is JsonObject th) c.This = new DebugValue { Name = "this", Type = S(th, "type"), Value = S(th, "value"), Valid = true };
        if (r?["arguments"] is JsonArray args) foreach (var a in args) c.Arguments.Add(MapValue(a));
        if (r?["locals"] is JsonArray locals) foreach (var l in locals) c.Locals.Add(MapValue(l));
        if (r?["watches"] is JsonArray watches) foreach (var w in watches) c.Watches.Add(MapValue(w));
        if (r?["stack"] is JsonArray stack) foreach (var f in stack) c.Stack.Add(MapFrame(f));
        return c;
    }

    private static string S(JsonNode? n, string key, string fallback = "") => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    private static int I(JsonNode? n, string key) => n?[key] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : (v.TryGetValue<long>(out var l) ? (int)l : 0)) : 0;
    private static long L(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
    private static bool B(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
