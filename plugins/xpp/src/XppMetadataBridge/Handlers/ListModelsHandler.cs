using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// listModels — enumerate every model visible to the bridge's metadata
    /// providers (Standard, Custom, and Runtime) and return its descriptive
    /// metadata. Deduplicates by model name. Adds an <c>isBinary</c> flag
    /// per model: true when the model is only visible via the Runtime
    /// provider (no on-disk XML), false when at least one disk provider
    /// sees it.
    ///
    /// Response shape (under JSON-RPC `result`):
    ///   {
    ///     models: [
    ///       {
    ///         name, displayName, publisher, version, layer,
    ///         isCustom, isBinary, dependencies: [...]
    ///       }, ...
    ///     ],
    ///     summary: { total, standard, custom, binary }
    ///   }
    ///
    /// "isCustom" is determined by publisher: anything whose Publisher does
    /// not start with "Microsoft" is treated as custom. This is more
    /// reliable than going by layer (customers can place Microsoft-named
    /// models in the USR layer via the dev tools) or by which path the
    /// model was discovered under (Tier 1 VMs only have one path).
    /// </summary>
    internal sealed class ListModelsHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public ListModelsHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "listModels";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            // Two-pass walk: disk first so isBinary flips to false for any
            // model the disk providers can see; runtime second to add the
            // binary-only models. Track per-name source so the summary
            // counters land correctly.
            var byName = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var provider in _providers.DiskProviders())
            {
                Enumerate(provider, byName, isFromRuntime: false);
            }
            Enumerate(_providers.Runtime, byName, isFromRuntime: true);

            var models = new List<object>(byName.Count);
            var standardCount = 0;
            var customCount = 0;
            var binaryCount = 0;

            foreach (var entry in byName.Values)
            {
                if (entry.IsBinary) binaryCount++;
                if (entry.IsCustom) customCount++; else standardCount++;

                models.Add(new
                {
                    name = entry.Name,
                    displayName = entry.DisplayName,
                    publisher = entry.Publisher,
                    version = entry.Version,
                    layer = entry.Layer,
                    isCustom = entry.IsCustom,
                    isBinary = entry.IsBinary,
                    dependencies = entry.Dependencies
                });
            }

            return Task.FromResult<object?>(new
            {
                models,
                summary = new
                {
                    total = models.Count,
                    standard = standardCount,
                    custom = customCount,
                    binary = binaryCount
                }
            });
        }

        private void Enumerate(
            Microsoft.Dynamics.AX.Metadata.Providers.IMetadataProvider provider,
            Dictionary<string, ModelEntry> sink,
            bool isFromRuntime)
        {
            var names = provider.ModelManifest.ListModels();
            if (names == null) return;

            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;

                // If we've already recorded this model from disk, just flip
                // the IsBinary flag off (disk wins) and move on. We never
                // overwrite a disk entry with the runtime one — the data
                // is identical, but disk has fidelity guarantees.
                if (sink.TryGetValue(name, out var existing))
                {
                    if (!isFromRuntime) existing.IsBinary = false;
                    continue;
                }

                try
                {
                    var info = provider.ModelManifest.Read(name);
                    if (info == null)
                    {
                        // Couldn't materialize details but the name is real.
                        // Record a minimal entry so callers see the model
                        // exists; mark as binary if it only came from runtime.
                        sink[name] = new ModelEntry
                        {
                            Name = name,
                            DisplayName = name,
                            Publisher = string.Empty,
                            Version = string.Empty,
                            Layer = string.Empty,
                            IsCustom = true,
                            IsBinary = isFromRuntime,
                            Dependencies = Array.Empty<string>()
                        };
                        continue;
                    }

                    var publisher = info.Publisher ?? string.Empty;
                    var isCustom = !publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
                    var version = $"{info.VersionMajor}.{info.VersionMinor}.{info.VersionBuild}.{info.VersionRevision}";

                    var deps = new List<string>();
                    try
                    {
                        if (info.ModuleReferences != null)
                        {
                            foreach (var m in info.ModuleReferences)
                            {
                                if (!string.IsNullOrEmpty(m)) deps.Add(m);
                            }
                        }
                    }
                    catch
                    {
                        // Older or differently-shaped ModelInfo can throw on
                        // this accessor on some F&O builds. Best-effort.
                    }

                    sink[name] = new ModelEntry
                    {
                        Name = info.Name,
                        DisplayName = info.DisplayName ?? info.Name,
                        Publisher = publisher,
                        Version = version,
                        Layer = info.Layer.ToString(),
                        IsCustom = isCustom,
                        IsBinary = isFromRuntime,
                        Dependencies = deps.ToArray()
                    };
                }
                catch (Exception ex)
                {
                    // Record the failure but keep going — one broken manifest
                    // shouldn't poison the whole list.
                    sink[name] = new ModelEntry
                    {
                        Name = name,
                        DisplayName = name,
                        Publisher = string.Empty,
                        Version = string.Empty,
                        Layer = string.Empty,
                        IsCustom = true,
                        IsBinary = isFromRuntime,
                        Dependencies = Array.Empty<string>(),
                        Error = ex.Message
                    };
                }
            }
        }

        private sealed class ModelEntry
        {
            public string Name { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Publisher { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string Layer { get; set; } = string.Empty;
            public bool IsCustom { get; set; }
            public bool IsBinary { get; set; }
            public IReadOnlyList<string> Dependencies { get; set; } = Array.Empty<string>();
            public string? Error { get; set; }
        }
    }
}
