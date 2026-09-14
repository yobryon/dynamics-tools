using System;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace XppDebugBridge.Debug
{
    /// <summary>
    /// Turns the agent-facing target name into a pid.
    ///   aos   -> the IIS worker for the AOSService application pool
    ///   batch -> the Dynamics batch host (Batch.exe)
    ///   1234  -> that pid, verbatim
    /// </summary>
    internal static class TargetResolver
    {
        public sealed class Target
        {
            public string Kind = string.Empty;
            public int Pid;
            public string ProcessName = string.Empty;
        }

        public static Target Resolve(string target)
        {
            if (int.TryParse(target, out var pid))
            {
                var p = Process.GetProcessById(pid);
                return new Target { Kind = "pid", Pid = pid, ProcessName = p.ProcessName };
            }

            switch (target.Trim().ToLowerInvariant())
            {
                case "aos":
                case "w3wp":
                {
                    // Several w3wp can run (FinancialReporting, RetailServer...);
                    // pick the one IIS started for the AOSService pool.
                    foreach (var p in Process.GetProcessesByName("w3wp"))
                    {
                        var cmd = CommandLine(p.Id) ?? string.Empty;
                        if (cmd.IndexOf("AOSService", StringComparison.OrdinalIgnoreCase) >= 0)
                            return new Target { Kind = "aos", Pid = p.Id, ProcessName = "w3wp" };
                    }
                    var any = Process.GetProcessesByName("w3wp").FirstOrDefault();
                    if (any != null) return new Target { Kind = "aos", Pid = any.Id, ProcessName = "w3wp" };
                    throw new InvalidOperationException("No w3wp.exe is running -- is the AOS started (IIS site AOSService)?");
                }
                case "batch":
                {
                    var p = Process.GetProcessesByName("Batch").FirstOrDefault()
                        ?? throw new InvalidOperationException("Batch.exe is not running -- is the DynamicsAxBatch service started?");
                    return new Target { Kind = "batch", Pid = p.Id, ProcessName = "Batch" };
                }
                default:
                    throw new ArgumentException($"unknown target '{target}': use aos, batch, or a pid");
            }
        }

        private static string? CommandLine(int pid)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                foreach (ManagementObject o in searcher.Get()) return o["CommandLine"]?.ToString();
            }
            catch { }
            return null;
        }
    }
}
