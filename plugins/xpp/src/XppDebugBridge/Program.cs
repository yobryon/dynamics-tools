using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XppDebugBridge.Debug;
using XppDebugBridge.Handlers;
using XppDebugBridge.Vs;
using XppMetadataBridge.Rpc;

namespace XppDebugBridge
{
    /// <summary>
    /// Entry point for the debug bridge: a net48 child of XppService that hosts
    /// a hidden Visual Studio and drives its managed debugger against the AOS
    /// worker or the batch host. JSON-RPC over stdio, one request per line;
    /// stderr carries diagnostics.
    ///
    /// Lifecycle: spawned on the first debug RPC, lives until the service
    /// closes stdin. On the way out it resumes and detaches whatever it was
    /// attached to and quits its VS -- an orphaned attached debugger is the
    /// one outcome this process must never leave behind.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;

            var packages = string.Empty;
            foreach (var a in args)
            {
                if (a.StartsWith("--packages=", StringComparison.OrdinalIgnoreCase)) packages = a.Substring("--packages=".Length).Trim('"');
            }
            if (string.IsNullOrWhiteSpace(packages))
                Console.Error.WriteLine("[debug-bridge] warning: --packages not supplied; source generation will fail");

            Action<string> log = m => Console.Error.WriteLine("[debug-bridge] " + m);
            log($"starting; elevated={Elevation.IsElevated()} packages={packages}");

            using var sta = new StaWorker();
            using var vs = new VsHost(sta, log);
            using var session = new DebugSession(sta, vs, packages, log);

            var handlers = new IRpcHandler[]
            {
                new PingHandler(),
                new AttachHandler(session),
                new DetachHandler(session),
                new SetBreakpointHandler(session),
                new ListBreakpointsHandler(session),
                new ClearBreakpointsHandler(session),
                new WaitHandler(session),
                new ContinueHandler(session),
                new StepHandler(session),
                new EvalHandler(session),
                new StatusHandler(session),
            };

            var server = new JsonRpcServer(Console.In, Console.Out, handlers);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            try
            {
                await server.RunAsync(cts.Token).ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex)
            {
                log("fatal: " + ex);
                return 1;
            }
            finally
            {
                // using-disposal order: session (resume + detach) -> vs (quit) -> sta.
                log("shutting down");
            }
        }
    }
}
