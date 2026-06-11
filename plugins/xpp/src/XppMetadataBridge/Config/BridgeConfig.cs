using System;
using System.Collections.Generic;

namespace XppMetadataBridge.Config
{
    /// <summary>
    /// Per-invocation configuration for the bridge process. Populated once
    /// at startup from command-line arguments passed by the service (or
    /// from sensible defaults when launched manually for debugging).
    ///
    /// Kept as a tiny immutable record so it can be passed into handlers
    /// at construction time without an IoC container — net48 + a 10-handler
    /// surface doesn't earn the ceremony.
    /// </summary>
    internal sealed class BridgeConfig
    {
        /// <summary>
        /// Path to the standard D365 packages directory
        /// (e.g. "J:\AosService\PackagesLocalDirectory"). Required for any
        /// metadata-touching RPC; empty disables those handlers.
        /// </summary>
        public string PackagesLocalDirectory { get; }

        /// <summary>
        /// Path to the writable custom-metadata workspace. On classic
        /// Tier 1 VMs this is the same as PackagesLocalDirectory.
        /// </summary>
        public string CustomMetadataPath { get; }

        public BridgeConfig(string packagesLocalDirectory, string customMetadataPath)
        {
            PackagesLocalDirectory = packagesLocalDirectory ?? string.Empty;
            CustomMetadataPath = customMetadataPath ?? string.Empty;
        }

        /// <summary>
        /// True iff both paths are configured; metadata RPCs check this and
        /// return a typed error if false rather than throwing later.
        /// </summary>
        public bool IsMetadataConfigured =>
            !string.IsNullOrWhiteSpace(PackagesLocalDirectory) &&
            !string.IsNullOrWhiteSpace(CustomMetadataPath);

        /// <summary>
        /// Parse from --key=value command-line arguments. Unknown args are
        /// ignored (forward compatibility). Missing required values produce
        /// an empty path; callers decide how to react.
        /// </summary>
        public static BridgeConfig FromArgs(string[] args)
        {
            string? packages = null;
            string? custom = null;

            foreach (var a in args)
            {
                if (a == null) continue;
                if (a.StartsWith("--packages=", StringComparison.OrdinalIgnoreCase))
                {
                    packages = a.Substring("--packages=".Length).Trim('"');
                }
                else if (a.StartsWith("--custom=", StringComparison.OrdinalIgnoreCase))
                {
                    custom = a.Substring("--custom=".Length).Trim('"');
                }
            }

            return new BridgeConfig(packages ?? string.Empty, custom ?? string.Empty);
        }
    }
}
