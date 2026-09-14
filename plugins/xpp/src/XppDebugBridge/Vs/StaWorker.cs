using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace XppDebugBridge.Vs
{
    /// <summary>
    /// A single STA thread that owns every call into Visual Studio's COM
    /// automation model.
    ///
    /// DTE is an STA server. Calling it from arbitrary thread-pool threads
    /// (which the JSON-RPC loop uses) means cross-apartment marshalling on
    /// every call and, worse, a busy VS answering RPC_E_CALL_REJECTED with no
    /// message filter in place to retry. Funnelling everything through one STA
    /// thread with an <see cref="IOleMessageFilter"/> registered is the
    /// documented way to drive VS from another process.
    ///
    /// The flip side of one thread: one call that never returns blocks every
    /// call behind it. So the worker knows when it is WEDGED (an item has been
    /// running past its deadline) and fails later calls fast with a message
    /// that names the escape hatch, instead of letting them pile up behind the
    /// stuck one until the gRPC deadlines trip -- which is how a paused AOS
    /// once sat frozen with "detach" queued uselessly behind "set breakpoint".
    /// </summary>
    internal sealed class StaWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private volatile bool _disposed;
        private long _currentStartedTicks;      // Environment.TickCount64-ish via DateTime; 0 = idle
        private int _currentDeadlineMs;
        private string _currentName = string.Empty;

        public StaWorker()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "vs-sta" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>True while an item has been running longer than its own deadline.</summary>
        public bool IsWedged
        {
            get
            {
                var started = Interlocked.Read(ref _currentStartedTicks);
                if (started == 0) return false;
                return (DateTime.UtcNow.Ticks - started) / TimeSpan.TicksPerMillisecond > _currentDeadlineMs;
            }
        }

        public string WedgedOn => _currentName;

        private void Loop()
        {
            MessageFilter.Register();
            try
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    try { work(); } catch { /* surfaced through Invoke */ }
                }
            }
            finally { MessageFilter.Revoke(); }
        }

        /// <summary>
        /// Run <paramref name="func"/> on the STA thread and return its result.
        /// Throws <see cref="VsWedgedException"/> immediately if a previous call
        /// is stuck past its deadline, and <see cref="TimeoutException"/> if this
        /// call does not finish within <paramref name="timeoutMs"/> (the work
        /// keeps running on the STA thread; it cannot be aborted).
        /// </summary>
        public T Invoke<T>(Func<T> func, int timeoutMs = 30_000, string name = "vs-call")
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StaWorker));
            if (IsWedged)
                throw new VsWedgedException($"Visual Studio automation is not responding (stuck in '{_currentName}'). Use force detach to release the target.");

            T result = default!;
            Exception? error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                _queue.Add(() =>
                {
                    Interlocked.Exchange(ref _currentStartedTicks, DateTime.UtcNow.Ticks);
                    _currentDeadlineMs = timeoutMs; _currentName = name;
                    try { result = Retry(func); }
                    catch (Exception ex) { error = ex; }
                    finally
                    {
                        Interlocked.Exchange(ref _currentStartedTicks, 0);
                        done.Set();
                    }
                });
                if (!done.Wait(timeoutMs))
                    throw new TimeoutException($"Visual Studio automation call '{name}' did not complete within {timeoutMs / 1000}s");
            }
            if (error != null) throw new VsCallException(error);
            return result;
        }

        public void Invoke(Action action, int timeoutMs = 30_000, string name = "vs-call")
            => Invoke(() => { action(); return true; }, timeoutMs, name);

        private static T Retry<T>(Func<T> func)
        {
            const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
            const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
            for (var i = 0; ; i++)
            {
                try { return func(); }
                catch (COMException ex) when ((ex.HResult == RPC_E_CALL_REJECTED || ex.HResult == RPC_E_SERVERCALL_RETRYLATER) && i < 40)
                {
                    Thread.Sleep(250);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            _thread.Join(3000);
        }
    }

    internal sealed class VsCallException : Exception
    {
        public VsCallException(Exception inner) : base(inner.Message, inner) { }
    }

    internal sealed class VsWedgedException : Exception
    {
        public VsWedgedException(string message) : base(message) { }
    }
}
