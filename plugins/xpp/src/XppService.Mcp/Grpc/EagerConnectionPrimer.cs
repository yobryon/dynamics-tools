using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Mcp.Grpc;

/// <summary>
/// Fires a single background Ping at the XppService shortly after MCP
/// startup so the service (and its bridge child) come up while the agent
/// is still wiring itself together, rather than waiting for the first
/// tool call. Critical because the indexer's first-time warm-up runs
/// transparently in the background — if the service doesn't start until
/// the agent reaches for <c>xpp_find_object</c>, all of that overlap is
/// lost and the very first search blocks behind the warm-up.
///
/// Deliberately fire-and-forget:
///  - The MCP host's <c>StartAsync</c> must return promptly. Claude Code
///    times out the MCP <c>initialize</c> handshake if it stalls, and
///    that timeout would be invisible to the user.
///  - Auto-start of the service can legitimately take 3-5s on a cold box
///    (mutex + bridge spawn + bridge ping + schema check). We let that
///    happen on a Task.Run continuation while StartAsync returns.
///  - Any failure is logged and swallowed. Subsequent real tool calls
///    will hit the same code path and either succeed or fail with the
///    user-visible error, which is the right place for diagnostics.
/// </summary>
internal sealed class EagerConnectionPrimer : IHostedService
{
    private readonly XppServiceConnection _conn;
    private readonly ILogger<EagerConnectionPrimer> _logger;

    public EagerConnectionPrimer(XppServiceConnection conn, ILogger<EagerConnectionPrimer> logger)
    {
        _conn = conn;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget. Not awaited intentionally — see the class
        // doc comment for why blocking StartAsync would break MCP.
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogDebug("Priming XppService connection in background");
                // Ping rides the full stack (MCP -> service -> bridge ->
                // back) so a successful response means the bridge is alive
                // too. Triggers the auto-spawn path in XppServiceConnection
                // when the pipe isn't there yet.
                var rsp = await _conn.Client.PingAsync(
                    new PingRequest { Echo = "mcp-primer" },
                    deadline: DateTime.UtcNow.AddSeconds(30),
                    cancellationToken: cancellationToken);

                // Version negotiation (observe-only for now; newest-wins takeover
                // hangs off this comparison in a later increment). The service's
                // plugin_version and our own are both stamped from plugin.json,
                // so a mismatch means a version-skewed service is running (e.g. an
                // older session's service that a newer session connected to).
                var mine = ServiceVersionInfo.PluginVersion;
                var running = rsp.PluginVersion;
                var cmp = ServiceVersionInfo.Compare(running, mine);
                if (string.IsNullOrEmpty(running))
                    _logger.LogInformation("XppService primed: {Composite} (pre-versioning service; mine={Mine})", rsp.ServiceVersion, mine);
                else if (cmp == 0)
                    _logger.LogInformation("XppService primed: {Composite} (version in sync: {Ver})", rsp.ServiceVersion, running);
                else if (cmp < 0)
                    _logger.LogWarning("XppService is OLDER than this MCP (service={Running}, mcp={Mine}). Newest-wins takeover not yet enabled — connected anyway.", running, mine);
                else
                    _logger.LogInformation("XppService is newer than this MCP (service={Running}, mcp={Mine}); connected as a compatible client.", running, mine);
            }
            catch (OperationCanceledException)
            {
                // MCP shutting down before the prime completed. Not an error.
            }
            catch (RpcException ex)
            {
                _logger.LogWarning(ex,
                    "Eager prime failed ({Code}): {Detail}. The first real tool call will retry.",
                    ex.Status.StatusCode, ex.Status.Detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Eager prime threw. The first real tool call will retry.");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
