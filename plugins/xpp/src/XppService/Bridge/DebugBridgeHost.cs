using System.Text.Json.Nodes;

namespace Xpp.Service.Bridge;

/// <summary>
/// Owns the (single) XppDebugBridge child process.
///
/// The debug bridge hosts a hidden Visual Studio and one debugger session, so
/// it is a box-wide resource just like the AOS it attaches to: exactly one,
/// shared by every agent session, started lazily on the first debug RPC and
/// kept alive so re-attaching is cheap. Closing its stdin (service shutdown)
/// makes it resume + detach + quit VS on the way out.
///
/// Reuses <see cref="BridgeProcess"/> -- same JSON-RPC-over-stdio plumbing
/// as the metadata bridge, different executable.
/// </summary>
public sealed class DebugBridgeHost : IAsyncDisposable
{
    private readonly ILogger<DebugBridgeHost> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BridgeOptions _metadataOptions;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private BridgeProcess? _bridge;

    /// <summary>Opaque id of the session that attached, for DebugStatus.</summary>
    public string OwnerClientId { get; set; } = string.Empty;

    public DebugBridgeHost(ILogger<DebugBridgeHost> logger, ILoggerFactory loggerFactory, BridgeOptions metadataOptions)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _metadataOptions = metadataOptions;
    }

    public bool IsRunning => _bridge is { IsAlive: true };

    /// <summary>Issue a JSON-RPC call to the debug bridge, starting it if needed.</summary>
    public async Task<JsonNode?> InvokeAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        var bridge = await EnsureStartedAsync(ct).ConfigureAwait(false);
        return await bridge.InvokeAsync(method, @params, ct).ConfigureAwait(false);
    }

    private async Task<BridgeProcess> EnsureStartedAsync(CancellationToken ct)
    {
        if (_bridge is { IsAlive: true }) return _bridge;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bridge is { IsAlive: true }) return _bridge;
            if (_bridge != null)
            {
                _logger.LogWarning("Debug bridge had exited; restarting it");
                try { await _bridge.DisposeAsync().ConfigureAwait(false); } catch { }
                _bridge = null;
            }

            var exe = DebugBridgeExeResolver.Resolve(out var trace)
                ?? throw new InvalidOperationException(
                    "XppDebugBridge.exe could not be located. Searched:\n" +
                    string.Join("\n", trace.Select(p => "  - " + p)) +
                    "\nBuild the solution (dt setup) or set XPP_DEBUG_BRIDGE_EXE.");

            var options = new BridgeOptions
            {
                ExecutablePath = exe,
                PackagesLocalDirectory = _metadataOptions.PackagesLocalDirectory,
                CustomMetadataPath = string.Empty,
                Min = 1,
                Max = 1,
                IdleTimeout = TimeSpan.FromDays(365),
            };
            var bridge = new BridgeProcess(_loggerFactory.CreateLogger<BridgeProcess>(), options) { WorkerId = 900 };
            await bridge.StartAsync(ct).ConfigureAwait(false);

            // Prove it answers before handing it out.
            var ping = await bridge.InvokeAsync("ping", new JsonObject { ["echo"] = "debug-host" }, ct).ConfigureAwait(false);
            _logger.LogInformation("Debug bridge started: {Ping}", ping?.ToJsonString());
            _bridge = bridge;
            return bridge;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_bridge != null)
        {
            _logger.LogInformation("Stopping debug bridge (it detaches and quits its VS on the way out)");
            try { await _bridge.DisposeAsync().ConfigureAwait(false); } catch { }
            _bridge = null;
        }
    }
}

/// <summary>Disposes the debug bridge with the host so an attached debugger never outlives the service.</summary>
public sealed class DebugBridgeLifecycle : IHostedService
{
    private readonly DebugBridgeHost _host;
    public DebugBridgeLifecycle(DebugBridgeHost host) { _host = host; }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken) => await _host.DisposeAsync().ConfigureAwait(false);
}

/// <summary>
/// Find XppDebugBridge.exe with the same priority as the metadata bridge:
/// env override, sibling, then the dev-tree walk (Release before Debug).
/// </summary>
public static class DebugBridgeExeResolver
{
    public static string? Resolve(out IReadOnlyList<string> searchTrace)
    {
        var trace = new List<string>();

        var env = Environment.GetEnvironmentVariable("XPP_DEBUG_BRIDGE_EXE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            trace.Add($"env XPP_DEBUG_BRIDGE_EXE -> {env}");
            if (File.Exists(env)) { searchTrace = trace; return env; }
        }

        var svcDir = AppContext.BaseDirectory;
        var sibling = Path.Combine(svcDir, "XppDebugBridge.exe");
        trace.Add($"sibling -> {sibling}");
        if (File.Exists(sibling)) { searchTrace = trace; return sibling; }

        var dir = new DirectoryInfo(svcDir);
        while (dir != null)
        {
            foreach (var config in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "XppDebugBridge", "bin", config, "net48", "XppDebugBridge.exe");
                trace.Add($"walk -> {candidate}");
                if (File.Exists(candidate)) { searchTrace = trace; return candidate; }
            }
            dir = dir.Parent;
        }

        searchTrace = trace;
        return null;
    }
}
