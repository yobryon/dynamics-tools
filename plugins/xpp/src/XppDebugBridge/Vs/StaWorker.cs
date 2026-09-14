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
    /// documented way to drive VS from another process; it is also what made
    /// the experiments stop flaking.
    /// </summary>
    internal sealed class StaWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private volatile bool _disposed;

        public StaWorker()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "vs-sta" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Loop()
        {
            // Registered per-thread; must happen on the STA thread itself.
            MessageFilter.Register();
            try
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    try { work(); } catch { /* surfaced through the TCS in Invoke */ }
                }
            }
            finally
            {
                MessageFilter.Revoke();
            }
        }

        /// <summary>Run <paramref name="func"/> on the STA thread and return its result (exceptions propagate).</summary>
        public T Invoke<T>(Func<T> func, int timeoutMs = 120_000)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StaWorker));
            T result = default!;
            Exception? error = null;
            using (var done = new ManualResetEventSlim(false))
            {
                _queue.Add(() =>
                {
                    try { result = Retry(func); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });
                if (!done.Wait(timeoutMs))
                    throw new TimeoutException($"VS automation call did not complete within {timeoutMs / 1000}s");
            }
            if (error != null) throw new VsCallException(error);
            return result;
        }

        public void Invoke(Action action, int timeoutMs = 120_000) => Invoke(() => { action(); return true; }, timeoutMs);

        /// <summary>
        /// Belt and braces on top of the message filter: retry the two
        /// "server is busy" HRESULTs a few times before giving up.
        /// </summary>
        private static T Retry<T>(Func<T> func)
        {
            const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
            const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
            for (var i = 0; ; i++)
            {
                try { return func(); }
                catch (COMException ex) when ((ex.HResult == RPC_E_CALL_REJECTED || ex.HResult == RPC_E_SERVERCALL_RETRYLATER) && i < 80)
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
            if (!_thread.Join(5000)) { /* background thread; process exit reaps it */ }
        }
    }

    /// <summary>Wraps the real exception so callers see the STA-side failure with its message intact.</summary>
    internal sealed class VsCallException : Exception
    {
        public VsCallException(Exception inner) : base(inner.Message, inner) { }
    }
}
