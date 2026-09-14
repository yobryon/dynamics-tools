using System;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppDebugBridge.Debug;
using XppMetadataBridge.Rpc;

namespace XppDebugBridge.Handlers
{
    /// <summary>
    /// JSON-RPC surface of the debug bridge. Thin: validate params, call the
    /// session, return a plain object. All the debugger semantics live in
    /// <see cref="DebugSession"/>. Handlers run concurrently (see
    /// ConcurrentJsonRpcServer); the session serializes what must be.
    /// </summary>
    internal static class Elevation
    {
        public static bool IsElevated()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal sealed class PingHandler : IRpcHandler
    {
        public string Method => "ping";
        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Optional(@params);
            return Task.FromResult<object?>(new
            {
                echo = Params.OptionalString(p, "echo") ?? string.Empty,
                serverTime = DateTime.UtcNow.ToString("O"),
                bridgeVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                elevated = Elevation.IsElevated(),
                devenv = Vs.DevenvLocator.Find(),
            });
        }
    }

    internal abstract class SessionHandler : IRpcHandler
    {
        protected readonly DebugSession Session;
        protected SessionHandler(DebugSession session) { Session = session; }
        public abstract string Method { get; }
        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
            => Task.Run(() => Handle(Params.Optional(@params)), ct);
        protected abstract object? Handle(JObject p);
    }

    internal sealed class AttachHandler : SessionHandler
    {
        public AttachHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.attach";
        protected override object? Handle(JObject p)
        {
            if (!Elevation.IsElevated())
                throw new JsonRpcException(JsonRpcErrorCodes.InternalError,
                    "the plugin service is not running elevated, so Visual Studio cannot attach to the AOS (NETWORK SERVICE). " +
                    "Start Claude Code as administrator, then run 'dt service restart' so the service (and this bridge) inherit the elevated token.");
            var target = Params.OptionalString(p, "target") ?? "aos";
            var max = Params.OptionalInt(p, "maxPauseSeconds", 0);
            if (max > 0) Session.MaxPauseSeconds = Math.Max(10, Math.Min(1800, max));
            return Session.Attach(target);
        }
    }

    internal sealed class DetachHandler : SessionHandler
    {
        public DetachHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.detach";
        protected override object? Handle(JObject p) => Session.Detach(Params.OptionalBool(p, "force", false));
    }

    internal sealed class SetBreakpointHandler : SessionHandler
    {
        public SetBreakpointHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.setBreakpoint";
        protected override object? Handle(JObject p)
            => Session.SetBreakpoint(
                Params.RequireString(p, "axType"),
                Params.RequireString(p, "name"),
                Params.RequireString(p, "method"),
                Params.RequireString(p, "model"),
                Params.RequireString(p, "xmlPath"),
                Params.OptionalInt(p, "offset", 1),
                Params.OptionalString(p, "condition"));
    }

    internal sealed class ListBreakpointsHandler : SessionHandler
    {
        public ListBreakpointsHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.listBreakpoints";
        protected override object? Handle(JObject p) => Session.ListBreakpoints();
    }

    internal sealed class ClearBreakpointsHandler : SessionHandler
    {
        public ClearBreakpointsHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.clearBreakpoints";
        protected override object? Handle(JObject p) => Session.ClearBreakpoints();
    }

    internal sealed class WaitHandler : SessionHandler
    {
        public WaitHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.wait";
        protected override object? Handle(JObject p)
            => Session.Wait(Params.OptionalInt(p, "timeoutSec", 60), Params.OptionalStrings(p, "watch"));
    }

    internal sealed class ContinueHandler : SessionHandler
    {
        public ContinueHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.continue";
        protected override object? Handle(JObject p) => Session.Continue();
    }

    internal sealed class StepHandler : SessionHandler
    {
        public StepHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.step";
        protected override object? Handle(JObject p)
            => Session.Step(Params.OptionalString(p, "kind") ?? "over", Params.OptionalStrings(p, "watch"));
    }

    internal sealed class EvalHandler : SessionHandler
    {
        public EvalHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.eval";
        protected override object? Handle(JObject p)
            => Session.Eval(Params.RequireString(p, "expression"), Params.OptionalBool(p, "expand", false));
    }

    internal sealed class StatusHandler : SessionHandler
    {
        public StatusHandler(DebugSession s) : base(s) { }
        public override string Method => "debug.status";
        protected override object? Handle(JObject p)
        {
            var st = Session.Status();
            return new { status = st, elevated = Elevation.IsElevated() };
        }
    }
}
