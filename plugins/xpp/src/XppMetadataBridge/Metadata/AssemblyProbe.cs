using System;
using System.IO;
using System.Reflection;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// Locates the VS2022 D365 development extension at runtime and hooks
    /// AppDomain.AssemblyResolve so the Microsoft.Dynamics.AX.Metadata.*
    /// DLLs load on demand from that folder.
    ///
    /// We reference those DLLs at compile time with HintPath + Private=false
    /// (so we don't redistribute Microsoft binaries into our bin folder).
    /// That gets us a clean build, but at runtime the assembly probe path
    /// doesn't include the VS extension directory; we have to tell .NET
    /// where to look. This class does that.
    ///
    /// Detection logic mirrors tools/dev.ps1's setup action: scan the four
    /// known VS2022 SKU folders for an Extensions/* subfolder that contains
    /// Microsoft.Dynamics.AX.Metadata.dll, take the first hit. This works
    /// because the D365 dev tools install into a single such folder per VS
    /// instance.
    ///
    /// Best-effort: failures are logged to stderr and the metadata RPCs
    /// will later report MetadataUnavailable. Non-metadata RPCs (ping)
    /// remain unaffected.
    /// </summary>
    internal static class AssemblyProbe
    {
        private static string? _extensionPath;
        private static bool _hooked;

        public static string? ExtensionPath => _extensionPath;

        public static void HookResolve()
        {
            if (_hooked) return;
            _hooked = true;

            _extensionPath = FindD365ExtensionPath();
            if (_extensionPath == null)
            {
                Console.Error.WriteLine("[bridge] D365 VS2022 extension not found; metadata RPCs will be unavailable.");
                return;
            }

            Console.Error.WriteLine($"[bridge] D365 extension at: {_extensionPath}");

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                // args.Name is "Microsoft.Dynamics.AX.Metadata, Version=7.0.0.0, ..."
                // We extract the simple name and look for it as a DLL in the
                // extension folder. Subsequent loads of the same assembly
                // come from .NET's loaded-assemblies cache; this resolver
                // only fires for unresolved names.
                var name = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(name)) return null;

                var dllPath = Path.Combine(_extensionPath, name + ".dll");
                if (File.Exists(dllPath))
                {
                    try
                    {
                        return Assembly.LoadFrom(dllPath);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[bridge] failed to load {dllPath}: {ex.Message}");
                        return null;
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
