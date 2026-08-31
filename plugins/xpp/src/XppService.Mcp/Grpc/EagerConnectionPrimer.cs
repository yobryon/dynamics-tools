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
    private readonly McpOptions _options;
    private readonly ILogger<EagerConnectionPrimer> _logger;

    public EagerConnectionPrimer(XppServiceConnection conn, McpOptions options, ILogger<EagerConnectionPrimer> logger)
    {
        _conn = conn;
        _options = options;
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

                // Newest-wins negotiation. The service's plugin_version and our
                // own are both stamped from plugin.json, so a mismatch means a
                // version-skewed service is running — typically a service left
                // behind by a session that started before the user updated the
                // plugin. An OLDER service gets asked to stand down so ours can
                // own the box; a NEWER one we simply use (the contract is
                // additive, so an older client is a valid client of it).
                var mine = ServiceVersionInfo.PluginVersion;
                var running = rsp.PluginVersion;
                var cmp = ServiceVersionInfo.Compare(running, mine);
                if (cmp == 0)
                {
                    _logger.LogInformation("XppService primed: {Composite} (version in sync: {Ver})", rsp.ServiceVersion, mine);
                }
                else if (cmp > 0)
                {
                    _logger.LogInformation("XppService is newer than this MCP (service={Running}, mcp={Mine}); connected as a compatible client.", running, mine);
                }
                else
                {
                    _logger.LogWarning("XppService is older than this MCP (service={Running}, mcp={Mine}); taking over.",
                        string.IsNullOrEmpty(running) ? "pre-versioning" : running, mine);
                    await TakeOverAsync(running, mine, cancellationToken).ConfigureAwait(false);
                }
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

    /// <summary>
    /// Stand the old service down, then re-Ping. The re-Ping is what actually
    /// brings our build up: the pipe is gone, so the connection factory takes
    /// its auto-spawn path and starts the service that sits next to THIS MCP
    /// build. We then confirm the version we ended up with rather than assuming
    /// the spawn produced what we expected.
    /// </summary>
    private async Task TakeOverAsync(string running, string mine, CancellationToken ct)
    {
        var clear = await ServiceTakeover.SupersedeAsync(
            _conn.Client, _options.PipeName, running, mine, _logger, ct).ConfigureAwait(false);

        if (!clear)
        {
            _logger.LogWarning(
                "Takeover did not complete; continuing against the running service. Tool calls will work, " +
                "but they run plugin {Running}, not {Mine}.",
                string.IsNullOrEmpty(running) ? "pre-versioning" : running, mine);
            return;
        }

        try
        {
            var rsp = await _conn.Client.PingAsync(
                new PingRequest { Echo = "mcp-takeover" },
                deadline: DateTime.UtcNow.AddSeconds(45),
                cancellationToken: ct);

            if (ServiceVersionInfo.Compare(rsp.PluginVersion, mine) == 0)
                _logger.LogInformation("Takeover complete: XppService now running plugin {Mine} (pid {Pid}).", mine, rsp.ProcessId);
            else
                _logger.LogWarning(
                    "Takeover restarted the service but it reports plugin {Running}, not {Mine}. " +
                    "Something else on this box is spawning a different build.",
                    string.IsNullOrEmpty(rsp.PluginVersion) ? "pre-versioning" : rsp.PluginVersion, mine);
        }
        catch (Exception ex)
        {
            // We stopped the old service and couldn't start ours. Say so
            // plainly — the next tool call retries the same spawn path, so
            // this is recoverable, but the user should see it.
            _logger.LogError(ex,
                "Stopped the older XppService but could not start plugin {Mine}. The next tool call will retry.", mine);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
