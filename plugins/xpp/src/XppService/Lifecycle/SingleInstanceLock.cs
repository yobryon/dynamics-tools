using System.Runtime.Versioning;

namespace Xpp.Service.Lifecycle;

/// <summary>
/// Cross-process single-instance gate backed by a named Windows mutex.
///
/// The v2 architecture requires exactly one XppService running per machine
/// so the on-disk SQLite cache (later: in-memory embedding model) has a
/// single owner. Multiple Claude / agent / CLI clients connect to the one
/// service via its well-known named pipe; spawning a second service would
/// race on the cache and corrupt state.
///
/// We use the "Global\" prefix so the mutex is visible across user sessions
/// (Remote Desktop, scheduled tasks running as a different account, etc.).
/// If you really want one-per-user instead of one-per-machine, drop the
/// prefix.
///
/// Acquisition is non-blocking: if another instance has the lock, this
/// throws SingleInstanceAlreadyRunningException and the caller exits
/// cleanly. We intentionally don't wait — a Claude session shouldn't
/// hang on startup because some stale service somewhere isn't shutting
/// down.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;

    public SingleInstanceLock(string name)
    {
        // Mutex name length cap is 260 chars; ours is well under.
        var fullName = name.StartsWith(@"Global\", StringComparison.Ordinal)
            ? name
            : $@"Global\{name}";

        _mutex = new Mutex(initiallyOwned: false, fullName, out _);

        try
        {
            // WaitOne(0) is a non-blocking try-acquire. Returns false if
            // another process owns it. AbandonedMutexException means the
            // previous owner crashed without releasing — we treat that as
            // "now mine" (we got the handle) and keep going.
            _owned = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
        }

        if (!_owned)
        {
            throw new SingleInstanceAlreadyRunningException(
                $"Another instance is already running (mutex {fullName} is held).");
        }
    }

    public void Dispose()
    {
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { /* released racily */ }
            _owned = false;
        }
        _mutex.Dispose();
    }
}

public sealed class SingleInstanceAlreadyRunningException : Exception
{
    public SingleInstanceAlreadyRunningException(string message) : base(message) { }
}
