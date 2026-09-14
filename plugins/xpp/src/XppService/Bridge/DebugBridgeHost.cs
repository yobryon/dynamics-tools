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

    /// <summary>Where the bridge exe resolves to (or where it was expected), without starting anything.</summary>
    public (bool Available, string Path) Probe()
    {
        var exe = DebugBridgeExeResolver.Resolve(out _);
        return (exe != null, exe ?? DebugBridgeExeResolver.ExpectedPath());
    }

    /// <summary>
    /// Pid of the hidden Visual Studio the bridge reported (attach / status
    /// responses carry it). Kept here so the service can kill it even when the
    /// bridge itself has stopped answering. Only ever the pid the bridge
    /// started -- never the user's own VS.
    /// </summary>
    public int VsPid { get; set; }

    /// <summary>
    /// The release that needs nothing to cooperate: kill the bridge's hidden
    /// Visual Studio (killing the debugger detaches it and the target runs on),
    /// then the bridge. Used when the bridge itself stops answering.
    /// </summary>
    public async Task<string> KillAsync()
    {
        var bridge = _bridge; _bridge = null; OwnerClientId = string.Empty;
        var killedVs = false;
        var vsPid = VsPid; VsPid = 0;
        if (vsPid > 0)
        {
            try
            {
                using var vs = System.Diagnostics.Process.GetProcessById(vsPid);
                if (string.Equals(vs.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Killing hidden devenv {Pid} (the debug bridge's VS)", vsPid);
                    vs.Kill(true); killedVs = true;
                }
            }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex) { _logger.LogWarning("Could not kill devenv {Pid}: {Reason}", vsPid, ex.Message); }
        }
        if (bridge != null)
        {
            try { await bridge.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        return killedVs ? "killed-bridge" : "killed-bridge (VS already gone or unknown)";
    }

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

            // One working command beats a list of misses: name the csproj in
            // THIS tree and the build that works on a stock D365 box (dotnet
            // build; msbuild/VS lack the v4.8 targeting pack there). Normally
            // `dotnet run` of the MCP builds the bridge, so reaching this means
            // a hand-assembled layout or a stale launch.
            var exe = DebugBridgeExeResolver.Resolve(out var trace)
                ?? throw new InvalidOperationException(
                    "XppDebugBridge.exe is not built in this plugin tree. Build it with:\n" +
                    $"  dotnet build \"{DebugBridgeExeResolver.ExpectedCsproj()}\" -c Release\n" +
                    "(use dotnet build, not msbuild: stock D365 dev boxes lack the .NET Framework 4.8 targeting pack that msbuild needs; the SDK supplies it). " +
                    "It is normally built automatically when the MCP server starts; if it is missing right after an update, restart the session. " +
                    "Searched: " + string.Join("; ", trace.Take(4)) + (trace.Count > 4 ? $" (+{trace.Count - 4} more)" : ""));

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

    /// <summary>The source tree this service was built from (walk up to the dir holding src\).</summary>
    private static string? TreeRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "XppDebugBridge"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static string ExpectedCsproj()
    {
        var root = TreeRoot();
        return root == null ? @"src\XppDebugBridge\XppDebugBridge.csproj" : Path.Combine(root, "src", "XppDebugBridge", "XppDebugBridge.csproj");
    }

    public static string ExpectedPath()
    {
        var root = TreeRoot();
        return root == null ? @"src\XppDebugBridge\bin\Release\net48\XppDebugBridge.exe" : Path.Combine(root, "src", "XppDebugBridge", "bin", "Release", "net48", "XppDebugBridge.exe");
    }
}
