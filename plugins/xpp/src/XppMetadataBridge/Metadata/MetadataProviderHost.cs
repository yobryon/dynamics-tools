using System;
using System.Collections.Generic;
using Microsoft.Dynamics.AX.Metadata.Storage;
using Microsoft.Dynamics.AX.Metadata.Storage.Runtime;
using Microsoft.Dynamics.AX.Metadata.Providers;
using XppMetadataBridge.Config;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// Identifies which underlying metadata provider produced a result.
    /// Threaded back to callers so read responses can carry a `source`
    /// field — important for binary modules where Runtime is the only
    /// provider that can satisfy a read, and X++ source isn't available.
    ///
    /// Priority order for fallback reads: Custom -> Standard -> Runtime.
    /// Disk wins where it can see the object (it has source); Runtime is
    /// the fallback for binary-only modules.
    /// </summary>
    internal enum ProviderSource
    {
        Custom,
        Standard,
        Runtime,
    }

    /// <summary>
    /// Lazy host for the three metadata providers (standard packages,
    /// custom metadata workspace, and the runtime/compiled-DLL view).
    /// The first metadata-touching RPC pays for the initialization; ping
    /// and other non-metadata calls never trigger it.
    ///
    /// Why three providers:
    ///  - <b>Standard</b> is a DiskProvider rooted at PackagesLocalDirectory.
    ///    Sees on-disk XML for every module that ships with source.
    ///  - <b>Custom</b> is a DiskProvider rooted at CustomMetadataPath (often
    ///    the same path on Tier 1 VMs). It's the writable workspace; write
    ///    operations always target this provider.
    ///  - <b>Runtime</b> is a RuntimeProvider rooted at the same packages
    ///    directory. Reads from the compiled DLLs in <c>bin\</c>, so it can
    ///    see modules that ship without on-disk XML (binary-only modules).
    ///    X++ source bodies come back empty; metadata shape is complete.
    ///
    /// Why lazy: opening any provider walks the PackagesLocalDirectory and
    /// can take a couple of seconds on a real Tier 1 VM. Doing that during
    /// bridge process startup would race the service's 5-second readiness
    /// probe, and would also block a healthy ping behind an unhealthy
    /// metadata config. Better to fail at the per-call boundary.
    ///
    /// Thread-safety: lazy construction is guarded by a per-host lock.
    /// Once initialized the providers are read-only references that
    /// Microsoft's library treats as thread-safe for read operations
    /// (which is all we do).
    /// </summary>
    internal sealed class MetadataProviderHost
    {
        private readonly BridgeConfig _config;
        private readonly object _gate = new object();

        private IMetadataProvider? _standard;
        private IMetadataProvider? _custom;
        private IMetadataProvider? _runtime;
        private bool _sameAsStandard;
        private bool _initialized;

        public MetadataProviderHost(BridgeConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// The bridge configuration this host was constructed with. Handlers
        /// that need raw filesystem paths (PackagesLocalDirectory,
        /// CustomMetadataPath) access them through here rather than threading
        /// BridgeConfig separately through every constructor.
        /// </summary>
        public BridgeConfig Config => _config;

        /// <summary>
        /// The provider rooted at PackagesLocalDirectory (the read-mostly
        /// Microsoft + ISV metadata, on-disk XML).
        /// </summary>
        public IMetadataProvider Standard => Initialize()._standard!;

        /// <summary>
        /// The provider rooted at CustomMetadataPath (writable custom
        /// metadata workspace). On Tier 1 VMs this is often the same path
        /// as Standard; <see cref="CustomDistinctFromStandard"/> tells you.
        /// </summary>
        public IMetadataProvider Custom => Initialize()._custom!;

        /// <summary>
        /// The runtime provider — sees binary-only modules (no on-disk XML)
        /// by reading compiled metadata from the DLLs in <c>bin\</c>. Read
        /// surface only; X++ source bodies come back empty.
        /// </summary>
        public IMetadataProvider Runtime => Initialize()._runtime!;

        /// <summary>
        /// True when the custom path resolves to a different directory than
        /// the standard one. Lets callers enumerate both paths without
        /// risking double-counting on Tier 1 setups.
        /// </summary>
        public bool CustomDistinctFromStandard => Initialize()._sameAsStandard == false;

        /// <summary>
        /// Enumerate providers in read-priority order: Custom (if distinct),
        /// then Standard, then Runtime. Callers that want disk-first / runtime-
        /// fallback semantics iterate this and stop on the first hit. Yields
        /// the provider plus the source tag so callers can record which one
        /// satisfied the read.
        /// </summary>
        public IEnumerable<(IMetadataProvider provider, ProviderSource source)> ReadOrder()
        {
            Initialize();
            if (_sameAsStandard)
            {
                yield return (_standard!, ProviderSource.Standard);
            }
            else
            {
                yield return (_custom!, ProviderSource.Custom);
                yield return (_standard!, ProviderSource.Standard);
            }
            yield return (_runtime!, ProviderSource.Runtime);
        }

        /// <summary>
        /// Enumerate the disk-backed providers (Custom + Standard, deduped),
        /// excluding Runtime. Used by the write path and by enumeration calls
        /// that want only the on-disk universe.
        /// </summary>
        public IEnumerable<IMetadataProvider> DiskProviders()
        {
            Initialize();
            yield return _standard!;
            if (!_sameAsStandard) yield return _custom!;
        }

        private MetadataProviderHost Initialize()
        {
            if (_initialized) return this;
            lock (_gate)
            {
                if (_initialized) return this;

                if (!_config.IsMetadataConfigured)
                {
                    throw new JsonRpcException(
                        JsonRpcErrorCodes.MetadataUnavailable,
                        "Metadata paths not configured. Bridge was started without --packages and --custom arguments.");
                }

                try
                {
                    var factory = new MetadataProviderFactory();
                    _standard = factory.CreateDiskProvider(_config.PackagesLocalDirectory);

                    var samePath = string.Equals(
                        System.IO.Path.GetFullPath(_config.PackagesLocalDirectory).TrimEnd('\\'),
                        System.IO.Path.GetFullPath(_config.CustomMetadataPath).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase);

                    if (samePath)
                    {
                        _custom = _standard;
                        _sameAsStandard = true;
                    }
                    else
                    {
                        _custom = factory.CreateDiskProvider(_config.CustomMetadataPath);
                        _sameAsStandard = false;
                    }

                    // Runtime provider reads compiled metadata from the
                    // DLLs alongside PackagesLocalDirectory\bin. Initialized
                    // here so the per-call helper can fall through to it on
                    // disk misses without a second lazy boundary.
                    var runtimeCfg = new RuntimeProviderConfiguration(_config.PackagesLocalDirectory);
                    _runtime = factory.CreateRuntimeProvider(runtimeCfg);

                    _initialized = true;
                    Console.Error.WriteLine(
                        $"[bridge] metadata providers ready (standard={_config.PackagesLocalDirectory}, same={_sameAsStandard}, runtime=on)");
                    return this;
                }
                catch (Exception ex) when (!(ex is JsonRpcException))
                {
                    throw new JsonRpcException(
                        JsonRpcErrorCodes.MetadataUnavailable,
                        $"Failed to initialize metadata providers: {ex.Message}",
                        new { detail = ex.ToString() });
                }
            }
        }
    }
}
