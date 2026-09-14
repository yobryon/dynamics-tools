using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace XppDebugBridge.Vs
{
    /// <summary>
    /// Running Object Table lookups. Every Visual Studio instance registers
    /// its DTE as "!VisualStudio.DTE.&lt;version&gt;:&lt;pid&gt;", which is how we
    /// get hold of the automation object for the exact devenv we spawned
    /// (rather than whichever instance GetActiveObject happens to return --
    /// on a dev box that is usually the user's own VS).
    /// </summary>
    internal static class RotHelper
    {
        [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
        [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

        public static IEnumerable<string> Names()
        {
            GetRunningObjectTable(0, out var rot);
            rot.EnumRunning(out var e);
            e.Reset();
            var m = new IMoniker[1];
            while (e.Next(1, m, IntPtr.Zero) == 0)
            {
                CreateBindCtx(0, out var ctx);
                m[0].GetDisplayName(ctx, null, out var name);
                yield return name;
            }
        }

        /// <summary>The DTE for devenv <paramref name="pid"/>, any VS version, or null if not (yet) registered.</summary>
        public static object? FindDte(int pid)
        {
            var suffix = ":" + pid;
            GetRunningObjectTable(0, out var rot);
            rot.EnumRunning(out var e);
            e.Reset();
            var m = new IMoniker[1];
            while (e.Next(1, m, IntPtr.Zero) == 0)
            {
                CreateBindCtx(0, out var ctx);
                m[0].GetDisplayName(ctx, null, out var name);
                if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    rot.GetObject(m[0], out var obj);
                    return obj;
                }
            }
            return null;
        }
    }
}
