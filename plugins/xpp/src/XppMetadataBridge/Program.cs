using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XppMetadataBridge.Config;
using XppMetadataBridge.Handlers;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge
{
    /// <summary>
    /// Entry point for the metadata bridge.
    ///
    /// The bridge is a net48 child process spawned by the modern .NET
    /// XppService. It reads JSON-RPC requests from stdin one line at a time,
    /// dispatches them to handlers, and writes JSON-RPC responses to stdout.
    /// Stderr is reserved for human-readable diagnostics so the service can
    /// capture and log them without polluting the protocol stream.
    ///
    /// This is the only process in the v2 stack that loads the
    /// Microsoft.Dynamics.AX.Metadata.* assemblies. Everything else reaches
    /// the D365 metadata through this narrow waist.
    ///
    /// Startup is intentionally minimal: build the handler list, hand off to
    /// the JSON-RPC server, run until EOF. Each handler is responsible for
    /// its own lazy initialization of D365 resources so a healthy ping
    /// doesn't pay the cost of loading 200MB of metadata DLLs.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            // Force UTF-8 on stdin/stdout. On Windows the default console
            // encoding is the system code page (often 1252), which will
            // mangle any non-ASCII characters that show up in object names
            // or X++ source strings the moment we start moving real data.
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;

            // Hook the D365 metadata DLL resolver BEFORE we touch any handler
            // that references Microsoft.Dynamics.AX.Metadata.*. The resolver
            // only fires on names .NET hasn't already loaded, so installing
            // it early means the first metadata-touching call gets it right.
            AssemblyProbe.HookResolve();

            // Parse --packages= and --custom= from the command line. The
            // service passes these when spawning us; if a developer launches
            // the bridge manually for debugging, ping still works without
            // them but metadata RPCs return MetadataUnavailable.
            var config = BridgeConfig.FromArgs(args);
            var providers = new MetadataProviderHost(config);
            var domainRegistry = new XppMetadataBridge.Metadata.Domain.DomainMapperRegistry();

            var handlers = new IRpcHandler[]
            {
                new PingHandler(),
                new ListModelsHandler(providers),
                new ListObjectsHandler(providers),
                new GetObjectMethodsHandler(providers),
                new GetStructuralReferencesHandler(providers),
                new GetObjectFullHandler(providers),
                new GetObjectsFullHandler(providers),
                new GetObjectXmlHandler(providers),
                new CreateObjectHandler(providers),
                new UpdateObjectHandler(providers),
                new ListKnownTypesHandler(providers),
                new LabelSearchHandler(providers),
                new LabelReadHandler(providers),
                new LabelAddHandler(providers),
                new LabelUpdateHandler(providers),
                new LabelDeleteHandler(providers),
                // Metaclass-routed domain handlers (tranche-by-tranche replacement
                // of the legacy createObject/XML flow).
                new CreateDomainObjectHandler(providers, domainRegistry),
                new PatchDomainObjectHandler(providers, domainRegistry),
                new GetDomainObjectHandler(providers, domainRegistry),
                new ListDomainMappedTypesHandler(domainRegistry),
            };

            var server = new JsonRpcServer(Console.In, Console.Out, handlers);

            // Graceful shutdown on Ctrl+C if the bridge is ever run
            // interactively for debugging. Under normal operation (spawned
            // by XppService) the parent closes stdin to signal shutdown,
            // which falls out naturally in the server's read loop.
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                try
                {
                    await server.RunAsync(cts.Token).ConfigureAwait(false);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[bridge] fatal: {ex}");
                    return 1;
                }
            }
        }
    }
}
