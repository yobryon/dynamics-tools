using System;
using System.Runtime.InteropServices;

namespace XppDebugBridge.Vs
{
    /// <summary>
    /// The standard OLE message filter for automating Visual Studio from
    /// another process. When VS is busy it rejects incoming COM calls; without
    /// a filter that surfaces as RPC_E_CALL_REJECTED / "Call was rejected by
    /// callee". With one, COM asks us what to do and we say "retry shortly".
    /// Must be registered on the STA thread that makes the calls.
    /// </summary>
    internal sealed class MessageFilter : IOleMessageFilter
    {
        private const int SERVERCALL_ISHANDLED = 0;
        private const int SERVERCALL_RETRYLATER = 2;
        private const int PENDINGMSG_WAITDEFPROCESS = 2;

        public static void Register()
        {
            CoRegisterMessageFilter(new MessageFilter(), out _);
        }

        public static void Revoke()
        {
            CoRegisterMessageFilter(null, out _);
        }

        int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
            => SERVERCALL_ISHANDLED;

        int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            // Keep retrying for up to ~2 minutes of accumulated waiting; VS can
            // legitimately be busy that long while attaching to a large process.
            if (dwRejectType == SERVERCALL_RETRYLATER && dwTickCount < 120_000)
                return 200; // retry after 200ms
            return -1;      // cancel
        }

        int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
            => PENDINGMSG_WAITDEFPROCESS;

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);
    }

    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }
}
