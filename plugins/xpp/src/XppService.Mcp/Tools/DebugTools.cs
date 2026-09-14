using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Live X++ debugging against the F&amp;O processes on this dev box. The
/// service hosts a hidden Visual Studio and drives its managed debugger; these
/// tools are the agent's remote control. See the dynamics-xpp:xpp-debug skill
/// for the workflow and the safety rules -- a paused AOS freezes every client
/// session on the box, so hits are budgeted and auto-resumed.
/// </summary>
[McpServerToolType]
public sealed class DebugTools
{
    private readonly XppServiceConnection _conn;
    private static readonly string ClientId = $"mcp-{Environment.ProcessId}";

    public DebugTools(XppServiceConnection conn) { _conn = conn; }

    [McpServerTool(Name = "xpp_debug_attach"), Description(
        "Attach the X++ debugger to a running F&O process on this box: target=\"aos\" (the AOS worker, w3wp - what the web client " +
        "and OData run in) or \"batch\" (Batch.exe - what scheduled jobs run in). Starts a hidden Visual Studio the first time " +
        "(~10-20s) and attaches its managed debugger (~5-10s). Requires the plugin service to run ELEVATED (Claude Code started as " +
        "administrator); the error says so if not. One debug session per machine. maxPauseSeconds (default 300) is the watchdog: a " +
        "breakpoint hit left paused longer than this is resumed automatically so the AOS is never left frozen. Load " +
        "dynamics-xpp:xpp-debug before using these tools.")]
    public async Task<string> Attach(
        [Description("\"aos\" | \"batch\" | a pid. Default aos.")] string? target = null,
        [Description("Watchdog budget in seconds for a paused hit (10-1800). Default 300.")] int maxPauseSeconds = 0,
        CancellationToken ct = default)
    {
        try
        {
            var rsp = await _conn.Client.DebugAttachAsync(new DebugAttachRequest { Target = target ?? "aos", MaxPauseSeconds = maxPauseSeconds, ClientId = ClientId },
                deadline: DateTime.UtcNow.AddMinutes(5), cancellationToken: ct);
            return JsonSerializer.Serialize(new { rsp.Attached, target = rsp.Target, rsp.Pid, processName = rsp.ProcessName, engine = rsp.Engine, elapsedMs = rsp.ElapsedMs,
                next = "set breakpoints with xpp_debug_breakpoint, then xpp_debug_wait while you (or the user) trigger the code path" });
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_attach", ex); }
    }

    [McpServerTool(Name = "xpp_debug_detach"), Description(
        "Detach the debugger: resumes the target if paused, deletes every breakpoint, detaches. ALWAYS call this when you are " +
        "done debugging - an attached debugger with live breakpoints keeps pausing the AOS for whoever hits them.")]
    public async Task<string> Detach(CancellationToken ct = default)
    {
        try
        {
            var rsp = await _conn.Client.DebugDetachAsync(new DebugEmpty(), deadline: DateTime.UtcNow.AddMinutes(2), cancellationToken: ct);
            return JsonSerializer.Serialize(new { detached = true, wasAttached = rsp.WasAttached, target = rsp.Target, rsp.Pid, resumedBeforeDetach = rsp.ResumedBeforeDetach });
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_detach", ex); }
    }

    [McpServerTool(Name = "xpp_debug_breakpoint"), Description(
        "Set a breakpoint at the start of an X++ method (axType + name + method), e.g. AxClass PriceDisc findPrice or AxTable " +
        "SalesLine modifiedField. Supported element types: AxClass, AxTable, AxDataEntityView, AxView, AxQuery, AxMap (forms and " +
        "extensions are not yet - put the breakpoint in a class/table method on the call path). offset = lines below the method's " +
        "declaration line (default 1); VS moves the breakpoint to the nearest executable statement and boundLine/sourceLine tell " +
        "you where it landed. condition is an optional X++ expression (e.g. \"_salesLine.SalesId == 'SO-000123'\") so a hot method " +
        "only breaks for the case you care about. bound=false means the module isn't loaded in the target yet; it binds when it is.")]
    public async Task<string> SetBreakpoint(
        [Description("AOT type, e.g. AxClass, AxTable.")] string axType,
        [Description("Element name, e.g. PriceDisc.")] string name,
        [Description("Method name, e.g. findPrice.")] string method,
        [Description("Optional model to disambiguate a name that exists in several models.")] string? model = null,
        [Description("Lines below the declaration line. Default 1.")] int offset = 0,
        [Description("Optional X++ condition; the breakpoint only fires when it is true.")] string? condition = null,
        CancellationToken ct = default)
    {
        try
        {
            var rsp = await _conn.Client.DebugSetBreakpointAsync(new DebugSetBreakpointRequest { AxType = axType, Name = name, Method = method, Model = model ?? "", Offset = offset, Condition = condition ?? "" },
                deadline: DateTime.UtcNow.AddMinutes(3), cancellationToken: ct);
            return JsonSerializer.Serialize(Bp(rsp));
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_breakpoint", ex); }
    }

    [McpServerTool(Name = "xpp_debug_breakpoints"), Description("List the debugger's breakpoints with their bound state, or clear them all (clear=true).")]
    public async Task<string> Breakpoints([Description("true = delete every breakpoint.")] bool clear = false, CancellationToken ct = default)
    {
        try
        {
            if (clear)
            {
                var c = await _conn.Client.DebugClearBreakpointsAsync(new DebugEmpty(), deadline: DateTime.UtcNow.AddMinutes(1), cancellationToken: ct);
                return JsonSerializer.Serialize(new { cleared = c.Cleared });
            }
            var rsp = await _conn.Client.DebugListBreakpointsAsync(new DebugEmpty(), deadline: DateTime.UtcNow.AddMinutes(1), cancellationToken: ct);
            return JsonSerializer.Serialize(new { count = rsp.Breakpoints.Count, breakpoints = rsp.Breakpoints.Select(Bp) });
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_breakpoints", ex); }
    }

    [McpServerTool(Name = "xpp_debug_wait"), Description(
        "Block until a breakpoint hits (or timeoutSec passes, default 60, max 600). Trigger the code path while waiting - " +
        "e.g. the user (or the browser tools) performs the action in the F&O client; batch code runs on its schedule. On a hit " +
        "you get the X++ call stack with file:line and source text, `this`, the arguments and locals of the hit frame, and any " +
        "watch expressions evaluated there. The target is then PAUSED (every AOS session waits): inspect with xpp_debug_eval / " +
        "xpp_debug_step promptly and release it with xpp_debug_continue. The watchdog resumes it after maxPauseSeconds regardless.")]
    public async Task<string> Wait(
        [Description("Seconds to wait for a hit. Default 60.")] int timeoutSec = 0,
        [Description("X++ expressions to evaluate in the hit frame, e.g. [\"salesLine.SalesId\", \"this.parmItemId()\"].")] string[]? watch = null,
        CancellationToken ct = default)
    {
        var t = timeoutSec <= 0 ? 60 : Math.Min(600, timeoutSec);
        try
        {
            var req = new DebugWaitRequest { TimeoutSec = t };
            if (watch != null) req.Watch.AddRange(watch);
            var rsp = await _conn.Client.DebugWaitAsync(req, deadline: DateTime.UtcNow.AddSeconds(t + 120), cancellationToken: ct);
            return JsonSerializer.Serialize(Capture(rsp));
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_wait", ex); }
    }

    [McpServerTool(Name = "xpp_debug_continue"), Description("Resume the paused target (after a hit). Breakpoints stay armed; call xpp_debug_wait again for the next hit.")]
    public async Task<string> Continue(CancellationToken ct = default)
    {
        try
        {
            var rsp = await _conn.Client.DebugContinueAsync(new DebugEmpty(), deadline: DateTime.UtcNow.AddMinutes(1), cancellationToken: ct);
            return JsonSerializer.Serialize(new { resumed = rsp.Resumed, wasPaused = rsp.WasPaused });
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_continue", ex); }
    }

    [McpServerTool(Name = "xpp_debug_step"), Description(
        "Step while paused: kind=\"over\" (next statement), \"into\" (descend into the call), \"out\" (finish this method). " +
        "Returns the new location with locals and watches, like a hit. The target stays paused.")]
    public async Task<string> Step(
        [Description("over | into | out. Default over.")] string? kind = null,
        [Description("X++ expressions to evaluate after the step.")] string[]? watch = null,
        CancellationToken ct = default)
    {
        try
        {
            var req = new DebugStepRequest { Kind = kind ?? "over" };
            if (watch != null) req.Watch.AddRange(watch);
            var rsp = await _conn.Client.DebugStepAsync(req, deadline: DateTime.UtcNow.AddMinutes(1), cancellationToken: ct);
            return JsonSerializer.Serialize(Capture(rsp));
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_step", ex); }
    }

    [McpServerTool(Name = "xpp_debug_eval"), Description(
        "Evaluate an X++ expression in the current (paused) frame with the X++ expression evaluator: variables, fields " +
        "(salesLine.SalesPrice), method calls (this.parmItemId()), statics (CustTable::find('C1').Name). expand=true also " +
        "returns the value's members one level down - use it on a record buffer to see all its fields.")]
    public async Task<string> Eval(
        [Description("X++ expression.")] string expression,
        [Description("Also return the value's data members (one level).")] bool expand = false,
        CancellationToken ct = default)
    {
        try
        {
            var rsp = await _conn.Client.DebugEvalAsync(new DebugEvalRequest { Expression = expression, Expand = expand }, deadline: DateTime.UtcNow.AddMinutes(1), cancellationToken: ct);
            return JsonSerializer.Serialize(new { expression = rsp.Expression, valid = rsp.Valid, type = rsp.Type, value = rsp.Value, members = rsp.Members.Count > 0 ? rsp.Members.Select(V) : null });
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_eval", ex); }
    }

    [McpServerTool(Name = "xpp_debug_status"), Description("Debugger state: attached to what, paused or running, for how long, breakpoint count, whether the service is elevated, and which session owns the debugger.")]
    public async Task<string> Status(CancellationToken ct = default)
    {
        try
        {
            var s = await _conn.Client.DebugStatusAsync(new DebugEmpty(), deadline: DateTime.UtcNow.AddMinutes(1), cancellationToken: ct);
            return JsonSerializer.Serialize(new { s.Attached, target = s.Target, s.Pid, processName = s.ProcessName, mode = s.Mode, s.Paused, pausedSeconds = s.PausedSeconds, maxPauseSeconds = s.MaxPauseSeconds,
                s.Breakpoints, s.Elevated, ownerClientId = s.ClientId, isMine = s.ClientId == ClientId, autoResumed = s.AutoResumed, bridgeRunning = s.BridgeRunning });
        }
        catch (RpcException ex) { return ToolError.From("xpp_debug_status", ex); }
    }

    // ---- shaping ------------------------------------------------------------------

    private static object Bp(DebugBreakpoint b) => new
    {
        b.Id, axType = b.AxType, b.Name, b.Method, file = b.File, requestedLine = b.RequestedLine, b.Bound,
        boundLine = b.Bound ? b.BoundLine : (int?)null, sourceLine = b.SourceLine, condition = string.IsNullOrEmpty(b.Condition) ? null : b.Condition,
        note = string.IsNullOrEmpty(b.Note) ? null : b.Note,
    };

    private static object V(DebugValue v) => new { v.Name, v.Type, v.Value, valid = v.Valid, error = string.IsNullOrEmpty(v.Error) ? null : v.Error };

    private static object F(DebugFrame f) => new { f.Index, f.Function, language = f.Language, xpp = f.Xpp, file = string.IsNullOrEmpty(f.File) ? null : f.File, line = f.Line > 0 ? f.Line : (int?)null, sourceLine = string.IsNullOrEmpty(f.SourceLine) ? null : f.SourceLine };

    private static object Capture(DebugCapture c) => new
    {
        c.Hit, timedOut = c.TimedOut, c.Paused, threadId = c.ThreadId, threadName = c.ThreadName,
        breakpoint = c.Breakpoint == null ? null : new { file = c.Breakpoint.File, line = c.Breakpoint.Line, function = c.Breakpoint.Function },
        location = c.Location == null ? null : F(c.Location),
        @this = c.This == null ? null : new { type = c.This.Type, value = c.This.Value },
        arguments = c.Arguments.Select(V), locals = c.Locals.Select(V), watches = c.Watches.Select(V),
        stack = c.Stack.Select(F),
        autoResumed = c.AutoResumed, maxPauseSeconds = c.MaxPauseSeconds, note = string.IsNullOrEmpty(c.Note) ? null : c.Note,
    };
}
