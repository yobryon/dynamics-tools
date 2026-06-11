using System.Diagnostics;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Mcp.Grpc;

/// <summary>
/// Owns the gRPC channel and client used to talk to the local XppService.
/// Registered as a singleton; reused across every MCP tool invocation in
/// this stub process so we open one HTTP/2 connection over the named pipe
/// and multiplex requests through it.
///
/// Auto-start behavior: when the first gRPC call attempts to connect to
/// the named pipe and that pipe doesn't exist, we spawn the service as a
/// detached child process and poll for the pipe to appear. This makes the
/// out-of-box experience "the agent works on first launch" without the
/// user having to remember to start the service first. The spawned
/// service is NOT a child of this process; it lives until the box reboots
/// or the user explicitly stops it, so subsequent MCP sessions connect
/// to it directly without re-spawning.
/// </summary>
public sealed class XppServiceConnection : IDisposable
{
    private readonly GrpcChannel _channel;
    public XppService.XppServiceClient Client { get; }

    public XppServiceConnection(McpOptions options, ILogger<XppServiceConnection> logger)
    {
        var factory = new NamedPipeConnectionFactory(options, logger);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = factory.ConnectAsync,
        };

        _channel = GrpcChannel.ForAddress(
            $"http://{options.PipeName}",
            new GrpcChannelOptions
            {
                HttpHandler = handler
            });

        Client = new XppService.XppServiceClient(_channel);
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>
/// HttpMessageHandler ConnectCallback that opens a NamedPipeClientStream
/// against the configured pipe name. Handles the auto-start retry loop
/// when the pipe isn't there on the first try.
/// </summary>
internal sealed class NamedPipeConnectionFactory
{
    private readonly McpOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public NamedPipeConnectionFactory(McpOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _, CancellationToken ct)
    {
        // First attempt: try once with a short connect timeout. Pipe might
        // already be there because the service is already running (other
        // agent sessions; previous boot left it up).
        try
        {
            return await DialOnceAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            if (!_options.AutoStart)
            {
                _logger.LogError("Could not connect to pipe '{Pipe}' and auto-start is disabled", _options.PipeName);
                throw;
            }
        }

        // Slow path: spawn the service, then poll for the pipe.
        //
        // We DO NOT cache "we already tried to spawn" across calls. The MCP
        // server outlives any single service instance: dev rebuilds, manual
        // kills, crashes, OS reboots all leave the MCP process running and
        // expecting transparent recovery on the next tool call. A cached
        // "first attempt was successful" flag would lock us out of recovery
        // in exactly those cases.
        //
        // Concurrent callers are serialized by _startGate (so they don't
        // all spawn simultaneously). Wasted spawns under contention are
        // bounded: the XppService's global Windows mutex makes any
        // duplicate-spawn process exit immediately with code 75. Cost is
        // a few hundred ms in the absolute-worst-case storm of failed
        // reconnects, which we're already paying in the polling phase
        // anyway.
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Cheap re-check: a peer caller may have already brought the
            // service up while we were queued. If so, skip the spawn.
            try
            {
                using var probe = await DialOnceAsync(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false);
                // Service is up; no spawn needed. Drop this probe stream and
                // let the polling phase below open a fresh one for gRPC.
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                TryStartService();
            }
        }
        finally
        {
            _startGate.Release();
        }

        // Poll for up to ~15 seconds. Service startup measures ~3-5s in
        // practice (mutex + bridge spawn + bridge ping + schema check); the
        // safety margin handles slower disks / cold caches.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await DialOnceAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                lastError = ex;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        _logger.LogError(lastError, "Service did not open pipe '{Pipe}' within 15s of auto-start", _options.PipeName);
        throw lastError ?? new TimeoutException($"timed out waiting for pipe '{_options.PipeName}'");
    }

    private async ValueTask<Stream> DialOnceAsync(TimeSpan connectTimeout, CancellationToken ct)
    {
        var stream = new System.IO.Pipes.NamedPipeClientStream(
            serverName: ".",
            pipeName: _options.PipeName,
            direction: System.IO.Pipes.PipeDirection.InOut,
            options: System.IO.Pipes.PipeOptions.Asynchronous,
            impersonationLevel: System.Security.Principal.TokenImpersonationLevel.Anonymous);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(connectTimeout);
        try
        {
            await stream.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            return stream;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout fired - rethrow as TimeoutException so the
            // outer loop can decide whether to retry.
            stream.Dispose();
            throw new TimeoutException($"connect to pipe '{_options.PipeName}' timed out after {connectTimeout.TotalMilliseconds:F0}ms");
        }
    }

    private bool TryStartService()
    {
        var exePath = ResolveServiceExe(out var searchTrace);
        if (exePath == null)
        {
            _logger.LogError(
                "Auto-start is enabled but could not locate XppService.exe.\nSearched:\n{Trace}\nSet --service-exe <path> or XPP_SERVICE_EXE env var, or put XppService.exe next to XppService.Mcp.exe.",
                string.Join("\n", searchTrace.Select(p => "  - " + p)));
            return false;
        }

        try
        {
            // Detached spawn: no input/output redirection, no UseShellExecute.
            // The service inherits no handles from us so its lifetime is
            // independent. It logs to its own configured sinks (stderr,
            // captured by whatever started it - in our case nothing,
            // which is fine because the bridge logs to the same place).
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                _logger.LogError("Process.Start returned null for {Exe}", exePath);
                return false;
            }
            _logger.LogInformation("Started XppService.exe (pid {Pid}) from {Path}", proc.Id, exePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start XppService.exe at {Path}", exePath);
            return false;
        }
    }

    /// <summary>
    /// Locate XppService.exe with the following priority:
    ///   1. --service-exe argument / McpOptions.ServiceExePath (explicit)
    ///   2. XPP_SERVICE_EXE environment variable
    ///   3. Sibling: same directory as the MCP exe (typical installed layout
    ///      when the service is published alongside the MCP)
    ///   4. Dev-tree walk: from MCP exe's directory, walk up looking for
    ///      src/XppService/bin/{Release,Debug}/net9.0-windows/XppService.exe
    ///      Release first since that's our shipping config.
    /// Returns null if nothing resolves to an existing file.
    /// <paramref name="searchTrace"/> lists every candidate path we checked,
    /// for diagnostic purposes — useful when the resolver fails and we need
    /// to tell the user where we looked.
    /// </summary>
    private string? ResolveServiceExe(out IReadOnlyList<string> searchTrace)
    {
        var trace = new List<string>();

        if (!string.IsNullOrEmpty(_options.ServiceExePath))
        {
            trace.Add($"explicit ServiceExePath -> {_options.ServiceExePath}");
            if (File.Exists(_options.ServiceExePath))
            {
                searchTrace = trace;
                return _options.ServiceExePath;
            }
        }

        var envPath = Environment.GetEnvironmentVariable("XPP_SERVICE_EXE");
        if (!string.IsNullOrEmpty(envPath))
        {
            trace.Add($"env XPP_SERVICE_EXE -> {envPath}");
            if (File.Exists(envPath))
            {
                searchTrace = trace;
                return envPath;
            }
        }

        var mcpDir = AppContext.BaseDirectory;

        var sibling = Path.Combine(mcpDir, "XppService.exe");
        trace.Add($"sibling -> {sibling}");
        if (File.Exists(sibling))
        {
            searchTrace = trace;
            return sibling;
        }

        // Dev-tree walk: starting from mcpDir, walk up looking for the
        // canonical project layout. Release first (our shipping config).
        // Stops at the drive root.
        var dir = new DirectoryInfo(mcpDir);
        while (dir != null)
        {
            foreach (var config in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "XppService", "bin", config, "net10.0-windows", "XppService.exe");
                trace.Add($"walk -> {candidate}");
                if (File.Exists(candidate))
                {
                    searchTrace = trace;
                    return candidate;
                }
            }
            dir = dir.Parent;
        }

        searchTrace = trace;
        return null;
    }
}

public sealed class McpOptions
{
    /// <summary>
    /// Name of the XppService named pipe to dial. Defaults match the
    /// service's appsettings.json so the common case requires no config.
    /// </summary>
    public string PipeName { get; init; } = "xpp-service-v2";

    /// <summary>
    /// When true (default), the MCP server will auto-spawn XppService.exe
    /// if the configured pipe doesn't have a listener. Set false to fail
    /// fast when you want to debug a service that's supposed to already
    /// be running.
    /// </summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>
    /// Explicit path to XppService.exe. Overrides discovery heuristics.
    /// Empty/null means "resolve via env var, sibling, or dev-tree walk."
    /// </summary>
    public string? ServiceExePath { get; init; }
}
