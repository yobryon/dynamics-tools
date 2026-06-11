// XppService.Mcp - MCP server frontend to the local XppService.
//
// Speaks Model Context Protocol over stdio with the calling agent
// (Claude Code, Claude Desktop, etc.). Translates each MCP tool call into
// a gRPC RPC against the XppService over its well-known named pipe.
// Stateless and tiny - the service holds the index, the bridge holds the
// metadata, this layer just adapts protocols.
//
// One process per agent session; multiple agent sessions share the one
// XppService instance running on the box.
//
// Usage (typical .mcp.json configuration):
//   {
//     "mcpServers": {
//       "dynamics-xpp": {
//         "command": "C:\\path\\to\\XppService.Mcp.exe",
//         "args": ["--pipe", "xpp-service-v2"]
//       }
//     }
//   }
//
// Args:
//   --pipe <name>           Named pipe to dial. Default "xpp-service-v2".
//   --service-exe <path>    Explicit XppService.exe path. Overrides discovery.
//   --no-auto-start         Don't auto-spawn the service if the pipe is dead.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Xpp.Service.Mcp.Grpc;

// Parse --pipe out of args so the MCP server can be wired into agents that
// need to talk to a non-default pipe (multi-service setups, tests).
var options = ParseArgs(args);

var builder = Host.CreateApplicationBuilder(args);

// Critical: route ALL logs to stderr. The MCP stdio protocol uses stdout
// exclusively for JSON-RPC frames; any stray write to stdout corrupts the
// protocol stream and the calling agent throws a JSON parse error. This was
// the single most common v1 footgun and the .NET version blocks it at the
// logging configuration layer rather than relying on individual call sites.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o =>
{
    o.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Dependency injection: the connection is shared across every tool call so
// we hold one HTTP/2 channel open over the named pipe for the lifetime of
// this MCP process.
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<XppServiceConnection>();
builder.Services.AddSingleton<Xpp.Service.Mcp.Project.ProjectContext>();

// Eager-connect to XppService in the background so the indexer's
// first-time warm-up overlaps with the agent's startup chatter rather
// than blocking the first tool call. Fire-and-forget — see the class
// doc comment for why we don't await it inside StartAsync.
builder.Services.AddHostedService<Xpp.Service.Mcp.Grpc.EagerConnectionPrimer>();

// MCP server bootstrap. WithToolsFromAssembly discovers every
// [McpServerToolType] class in this assembly and registers each
// [McpServerTool] method as an addressable tool.
// WithResourcesFromAssembly does the same for [McpServerResourceType]
// classes - this is how the xpp://schema/{type} family gets advertised on
// the resources/list and resources/templates/list endpoints.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
return;

// --- arg parsing -----------------------------------------------------------
static McpOptions ParseArgs(string[] args)
{
    var pipe = "xpp-service-v2";
    string? serviceExe = null;
    var autoStart = true;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--pipe" when i + 1 < args.Length:
                pipe = args[++i];
                break;
            case "--service-exe" when i + 1 < args.Length:
                serviceExe = args[++i];
                break;
            case "--no-auto-start":
                autoStart = false;
                break;
        }
    }

    return new McpOptions
    {
        PipeName = pipe,
        AutoStart = autoStart,
        ServiceExePath = serviceExe
    };
}
