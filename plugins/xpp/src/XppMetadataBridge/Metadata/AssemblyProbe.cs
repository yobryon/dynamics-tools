using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// Resolves the Microsoft.Dynamics.AX.Metadata.* DLLs on demand at runtime
    /// by hooking AppDomain.AssemblyResolve.
    ///
    /// We reference those DLLs at compile time with HintPath + Private=false
    /// (so we don't redistribute Microsoft binaries into our bin folder). That
    /// gives a clean build, but at runtime the assembly probe path doesn't
    /// include wherever those DLLs actually live; this class tells .NET where
    /// to look.
    ///
    /// Primary source is the deployed platform's own copy under
    /// <c>&lt;PackagesLocalDirectory&gt;\bin</c>. That path is stable — it does
    /// NOT change across D365 SDK updates — and it's the very same metadata the
    /// disk provider reads, so compile-time and runtime resolve against one
    /// source of truth. The VS2022 development extension folder (whose name is
    /// randomized and is re-created on every platform update) is kept only as a
    /// fallback: for a box where the packages path isn't configured, or on the
    /// off chance a transitive dependency lives only under the extension.
    ///
    /// Best-effort: if no source is found, failures are logged to stderr and
    /// the metadata RPCs later report MetadataUnavailable. Non-metadata RPCs
    /// (ping) remain unaffected.
    /// </summary>
    internal static class AssemblyProbe
    {
        private static readonly List<string> _probeRoots = new List<string>();
        private static bool _hooked;

        /// <summary>Probe roots in resolution order: the packages bin first
        /// (when configured), the VS extension folder (if present) last.</summary>
        public static IReadOnlyList<string> ProbeRoots => _probeRoots;

        /// <param name="packagesLocalDirectory">The D365 PackagesLocalDirectory
        /// the service passed via --packages=. Its <c>\bin</c> subfolder holds
        /// the platform's metadata DLLs. Empty when the bridge is launched
        /// manually without args, in which case only the extension fallback
        /// applies.</param>
        public static void HookResolve(string? packagesLocalDirectory)
        {
            if (_hooked) return;
            _hooked = true;

            // Primary: the deployed platform's metadata DLLs. Stable across SDK
            // updates; the same copy the disk provider reads.
            if (!string.IsNullOrWhiteSpace(packagesLocalDirectory))
            {
                var binPath = Path.Combine(packagesLocalDirectory, "bin");
                if (File.Exists(Path.Combine(binPath, "Microsoft.Dynamics.AX.Metadata.dll")))
                {
                    _probeRoots.Add(binPath);
                }
            }

            // Fallback: the VS2022 development extension. Randomized folder
            // name; only reached if the packages bin above didn't supply a DLL.
            var extPath = FindD365ExtensionPath();
            if (extPath != null) _probeRoots.Add(extPath);

            if (_probeRoots.Count == 0)
            {
                Console.Error.WriteLine("[bridge] no D365 metadata source found (neither packages bin nor VS extension); metadata RPCs will be unavailable.");
                return;
            }

            Console.Error.WriteLine($"[bridge] D365 metadata probe roots: {string.Join(" ; ", _probeRoots)}");

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                // args.Name is "Microsoft.Dynamics.AX.Metadata, Version=7.0.0.0, ..."
                // We extract the simple name and look for it as a DLL in each
                // probe root, in order. Subsequent loads of the same assembly
                // come from .NET's loaded-assemblies cache; this resolver only
                // fires for names .NET hasn't already resolved.
                var name = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(name)) return null;

                foreach (var root in _probeRoots)
                {
                    var dllPath = Path.Combine(root, name + ".dll");
                    if (!File.Exists(dllPath)) continue;
                    try
                    {
                        return Assembly.LoadFrom(dllPath);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[bridge] failed to load {dllPath}: {ex.Message}");
                        // fall through to the next probe root
                    }
                }
                return null;
            };
        }

        private static string? FindD365ExtensionPath()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(pf)) return null;

            var skus = new[] { "Enterprise", "Professional", "Community", "BuildTools" };
            foreach (var sku in skus)
            {
                var extRoot = Path.Combine(pf, "Microsoft Visual Studio", "2022", sku, "Common7", "IDE", "Extensions");
                if (!Directory.Exists(extRoot)) continue;

                foreach (var dir in Directory.EnumerateDirectories(extRoot))
                {
                    if (File.Exists(Path.Combine(dir, "Microsoft.Dynamics.AX.Metadata.dll")))
                    {
                        return dir;
                    }
                }
            }
            return null;
        }
    }
}
