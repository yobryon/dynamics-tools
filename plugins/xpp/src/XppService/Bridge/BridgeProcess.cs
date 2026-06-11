using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Xpp.Service.Bridge;

/// <summary>
/// Owns the XppMetadataBridge child process and exposes a request/response
/// API over its stdio JSON-RPC stream.
///
/// Concurrency model:
///   - A single writer task drains a Channel of outgoing requests, writing
///     one JSON line at a time to the bridge's stdin. We serialize writes
///     this way (rather than locking around Console.Out) so callers don't
///     block each other; they post and continue.
///   - A single reader task consumes the bridge's stdout line by line and
///     resolves pending TaskCompletionSources by id. Order doesn't matter:
///     correlation is purely by id.
///   - Multiple callers can issue InvokeAsync concurrently and get their
///     answers back as the bridge produces them. Even though the bridge
///     itself processes requests sequentially today, this client is
///     correct for any future bridge that pipelines.
///
/// Lifecycle: Start() spawns the bridge and primes the IO tasks. DisposeAsync
/// closes stdin (signals the bridge to exit), drains the IO tasks, and
/// disposes the process handle. The service should call DisposeAsync on
/// shutdown to avoid orphaning the child.
/// </summary>
public sealed class BridgeProcess : IAsyncDisposable
{
    private readonly ILogger<BridgeProcess> _logger;
    private readonly BridgeOptions _options;

    private Process? _process;
    private Channel<string>? _outgoing;
    private Task? _writerTask;
    private Task? _readerTask;
    private Task? _stderrTask;

    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonRpcResponseEnvelope>> _pending = new();
    private long _nextId;

    // Load tracking for the dynamic pool. _inFlight is the number of requests
    // currently awaiting a response on this worker; because the bridge processes
    // requests strictly sequentially, anything beyond the first is queued behind
    // it. _lastActiveTicks is when this worker last finished a request (used to
    // retire workers idle past the pool's idle timeout).
    private int _inFlight;
    private long _lastActiveTicks = Environment.TickCount64;

    /// <summary>Requests currently awaiting a response on this worker. 0 = idle.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Ambient monotonic timestamp (Environment.TickCount64) of the last
    /// completed request; seeded at construction so a never-used worker still ages.</summary>
    public long LastActiveTicks => Volatile.Read(ref _lastActiveTicks);

    /// <summary>Stable id for logging which worker scaled in/out.</summary>
    public int WorkerId { get; init; }

    private readonly CancellationTokenSource _shutdownCts = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public BridgeProcess(ILogger<BridgeProcess> logger, BridgeOptions options)
    {
        _logger = logger;
        _options = options;
    }

    /// <summary>Whether the underlying process is running.</summary>
    public bool IsAlive => _process is { HasExited: false };

    public Task StartAsync(CancellationToken ct)
    {
        if (_process != null) throw new InvalidOperationException("Bridge already started.");

        var psi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath) ?? Environment.CurrentDirectory
        };

        // Pass D365 metadata paths to the bridge as command-line arguments
        // so its lazy MetadataProviderHost can open the providers when the
        // first metadata-touching RPC arrives. Empty values are fine — the
        // bridge will return a typed MetadataUnavailable error in that
        // case, leaving ping and other non-metadata RPCs working.
        if (!string.IsNullOrEmpty(_options.PackagesLocalDirectory))
        {
            psi.ArgumentList.Add($"--packages={_options.PackagesLocalDirectory}");
        }
        if (!string.IsNullOrEmpty(_options.CustomMetadataPath))
        {
            psi.ArgumentList.Add($"--custom={_options.CustomMetadataPath}");
        }

        _logger.LogInformation("Spawning bridge: {Path}", _options.ExecutablePath);
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start bridge at {_options.ExecutablePath}");

        // Unbounded channel: callers should never block enqueuing a request.
        // Backpressure, if we ever need it, comes from the bridge being slow
        // (pending TCSes accumulate, callers await), not from this channel.
        _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _writerTask = Task.Run(() => WriterLoop(_shutdownCts.Token));
        _readerTask = Task.Run(() => ReaderLoop(_shutdownCts.Token));
        _stderrTask = Task.Run(() => StderrLoop(_shutdownCts.Token));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Issue a JSON-RPC request and await the response. Throws
    /// <see cref="BridgeRpcException"/> on a typed bridge error, or
    /// <see cref="InvalidOperationException"/> if the bridge has died.
    /// </summary>
    public async Task<JsonNode?> InvokeAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        if (_outgoing == null) throw new InvalidOperationException("Bridge not started.");
        if (_process is null or { HasExited: true }) throw new InvalidOperationException("Bridge process has exited.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonRpcResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        Interlocked.Increment(ref _inFlight);

        try
        {
            var request = new JsonRpcRequest
            {
                Method = method,
                Params = @params,
                Id = id
            };
            var json = JsonSerializer.Serialize(request, JsonOptions);

            try
            {
                await _outgoing.Writer.WriteAsync(json, ct).ConfigureAwait(false);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }

            // Tie the per-call cancellation to the TCS so a hung handler doesn't
            // leak a pending entry. The reader will still remove the entry if a
            // response arrives later; this race is benign.
            using var reg = ct.Register(() =>
            {
                if (_pending.TryRemove(id, out var pending))
                    pending.TrySetCanceled(ct);
            });

            var response = await tcs.Task.ConfigureAwait(false);

            if (response.Error != null)
            {
                throw new BridgeRpcException(response.Error.Code, response.Error.Message, response.Error.Data);
            }

            return response.Result;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            Volatile.Write(ref _lastActiveTicks, Environment.TickCount64);
        }
    }

    private async Task WriterLoop(CancellationToken ct)
    {
        try
        {
            while (await _outgoing!.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_outgoing.Reader.TryRead(out var line))
                {
                    await _process!.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
                    await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* expected during shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bridge writer loop failed");
            FailAllPending(ex);
        }
    }

    private async Task ReaderLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _process!.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break; // bridge closed stdout
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonRpcResponseEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<JsonRpcResponseEnvelope>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Bridge emitted unparseable response: {Line}", line);
                    continue;
                }

                if (envelope == null) continue;
                if (envelope.Id == null)
                {
                    // Spec allows id=null on parse errors. With nothing to
                    // correlate to we can only log and move on; no caller
                    // is waiting for this.
                    _logger.LogWarning("Bridge response with null id: code={Code} message={Message}",
                        envelope.Error?.Code, envelope.Error?.Message);
                    continue;
                }

                if (envelope.Id.GetValueKind() != JsonValueKind.Number)
                {
                    _logger.LogWarning("Bridge response with non-numeric id: {Id}", envelope.Id);
                    continue;
                }

                var id = envelope.Id.GetValue<long>();
                if (_pending.TryRemove(id, out var pending))
                {
                    pending.TrySetResult(envelope);
                }
                else
                {
                    _logger.LogWarning("Bridge response for unknown id {Id}", id);
                }
            }
        }
        catch (OperationCanceledException) { /* expected during shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bridge reader loop failed");
            FailAllPending(ex);
        }
        finally
        {
            // If the bridge died with pending requests, surface that rather
            // than letting callers wait forever.
            FailAllPending(new InvalidOperationException("Bridge process exited"));
        }
    }

    private async Task StderrLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _process!.StandardError.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                _logger.LogInformation("bridge: {Line}", line);
            }
        }
        catch (OperationCanceledException) { /* expected during shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bridge stderr loop failed");
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        _outgoing?.Writer.TryComplete();

        try { _process?.StandardInput.Close(); } catch { /* already closed */ }

        var ioTasks = new[] { _writerTask, _readerTask, _stderrTask }
            .Where(t => t != null)
            .Select(t => t!)
            .ToArray();
        if (ioTasks.Length > 0)
        {
            try { await Task.WhenAll(ioTasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* best-effort drain */ }
        }

        if (_process is { HasExited: false })
        {
            try { _process.WaitForExit(2000); } catch { /* ignored */ }
            if (!_process.HasExited)
            {
                _logger.LogWarning("Bridge did not exit after stdin close; killing");
                try { _process.Kill(true); } catch { /* race */ }
            }
        }

        _process?.Dispose();
        _shutdownCts.Dispose();
    }
}

public sealed class BridgeOptions
{
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// Absolute path to the D365 PackagesLocalDirectory. Passed to the
    /// bridge so it can open the standard metadata provider on first use.
    /// Empty/null is allowed (metadata RPCs will fail at the bridge with
    /// MetadataUnavailable, but ping still works).
    /// </summary>
    public string PackagesLocalDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Absolute path to the writable custom-metadata workspace. On Tier 1
    /// VMs this typically equals PackagesLocalDirectory.
    /// </summary>
    public string CustomMetadataPath { get; init; } = string.Empty;

    /// <summary>
    /// Floor on the worker count. The pool always keeps at least this many
    /// warm so a user query never pays bridge cold-start (~1s + provider init).
    /// Default 2.
    /// </summary>
    public int Min { get; init; } = 2;

    /// <summary>
    /// Ceiling on the worker count. The pool scales up to this under load
    /// (e.g. a full-corpus rebuild). Each worker is ~200MB resident, so the
    /// default tracks the box: max(2, ProcessorCount - 1).
    /// </summary>
    public int Max { get; init; } = 4;

    /// <summary>
    /// How long a worker may sit idle (zero in-flight) before the scaler
    /// retires it back toward <see cref="Min"/>. Default 60s.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
