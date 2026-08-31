using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Xpp.Service.Storage;

/// <summary>
/// Single-writer queue for cache mutations.
///
/// Anywhere in the service that needs to write to the index DB does so by
/// awaiting <see cref="EnqueueAsync"/> with a delegate that takes a
/// SqliteConnection. The delegate runs on the writer's dedicated thread,
/// holding a long-lived connection opened in ReadWriteCreate mode. The
/// channel serializes calls; SQLite never sees concurrent writers from
/// inside this process.
///
/// Why: SQLite supports only one writer at a time. In WAL mode readers and
/// writers don't block each other, but multiple writers contending on the
/// write lock would cause SQLITE_BUSY retries and complicate every call
/// site. Funneling everything through one task eliminates contention
/// entirely and means transaction boundaries are obvious in code.
///
/// Reads do NOT go through this queue. Each gRPC handler that reads opens
/// its own short-lived connection via IndexDatabase.Open(); WAL mode lets
/// those run concurrently with the writer.
/// </summary>
public sealed class IndexWriter : IHostedService, IAsyncDisposable
{
    private readonly IndexDatabase _db;
    private readonly ILogger<IndexWriter> _logger;
    private readonly Channel<WriteEnvelope> _channel;
    private Task? _loop;
    private readonly CancellationTokenSource _stopCts = new();
    private bool _stopped;
    private bool _disposed;

    public IndexWriter(IndexDatabase db, ILogger<IndexWriter> logger)
    {
        _db = db;
        _logger = logger;
        _channel = Channel.CreateUnbounded<WriteEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(() => RunLoopAsync(_stopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _channel.Writer.TryComplete();
        // Idempotent: we are both a hosted service and a disposable singleton,
        // so the host's stop and the DI container's disposal both land here.
        // Cancelling a disposed CTS throws, and an exception escaping shutdown
        // MASKS whatever actually went wrong — that's how a clean
        // "refusing to start" turned into an unhandled crash.
        if (!_stopped)
        {
            _stopped = true;
            try { _stopCts.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch { /* best-effort drain */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopCts.Dispose();
    }

    /// <summary>
    /// Queue a write operation and await its result. The delegate runs on the
    /// writer thread with exclusive access to the write connection.
    /// </summary>
    public async Task<T> EnqueueAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var envelope = new WriteEnvelope(conn =>
        {
            var result = work(conn);
            tcs.TrySetResult(result);
        }, tcs);

        await _channel.Writer.WriteAsync(envelope, ct).ConfigureAwait(false);
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var raw = await tcs.Task.ConfigureAwait(false);
        return (T)raw!;
    }

    public Task EnqueueAsync(Action<SqliteConnection> work, CancellationToken ct)
        => EnqueueAsync<object?>(conn => { work(conn); return null; }, ct);

    private async Task RunLoopAsync(CancellationToken ct)
    {
        SqliteConnection? conn = null;
        try
        {
            conn = _db.Open();
            _logger.LogInformation("Index writer loop started");

            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var envelope))
                {
                    try
                    {
                        envelope.Apply(conn);
                    }
                    catch (Exception ex)
                    {
                        envelope.Tcs.TrySetException(ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Index writer loop crashed");
        }
        finally
        {
            conn?.Dispose();
            _logger.LogInformation("Index writer loop exited");
        }
    }

    private sealed class WriteEnvelope
    {
        public WriteEnvelope(Action<SqliteConnection> apply, TaskCompletionSource<object?> tcs)
        {
            Apply = apply;
            Tcs = tcs;
        }
        public Action<SqliteConnection> Apply { get; }
        public TaskCompletionSource<object?> Tcs { get; }
    }
}
