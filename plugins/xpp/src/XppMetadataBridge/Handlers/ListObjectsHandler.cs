using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Dynamics.AX.Metadata.Providers;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// listObjects — enumerate object names of a given AxType within a given
    /// model. Required params: model, axType. Returns a parallel pair of
    /// arrays: <c>names</c> (the union across disk + runtime providers) and
    /// <c>sources</c> (one entry per name, "disk" or "runtime"). Disk wins
    /// where both providers see the object; runtime fills the gap for
    /// binary-only modules.
    ///
    /// We require both filters so the response stays bounded. The standard
    /// ApplicationSuite package alone has tens of thousands of objects;
    /// returning everything in one shot would be wasteful both on the wire
    /// and in the service's memory. The indexer iterates (model x axType)
    /// pairs externally to walk the whole AOT.
    /// </summary>
    internal sealed class ListObjectsHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public ListObjectsHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "listObjects";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Require(@params);
            var model = Params.RequireString(p, "model");
            var axType = Params.RequireString(p, "axType");

            // Disk first: union the standard + (distinct) custom names.
            // Each name lands in the set tagged "disk".
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in _providers.DiskProviders())
            {
                EnumerateInto(provider, axType, model, sources, SourceWire.Disk);
            }

            // Runtime fills the gap. Names already on disk keep their disk
            // tag (we only Add when missing).
            EnumerateInto(_providers.Runtime, axType, model, sources, SourceWire.Runtime,
                onlyIfMissing: true);

            // Stable ordered output. Parallel arrays so the consumer can zip
            // them without a per-row JSON object.
            var names = sources.Keys
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var srcArr = names.Select(n => sources[n]).ToArray();

            return Task.FromResult<object?>(new
            {
                model,
                axType,
                names,
                sources = srcArr,
                count = names.Length
            });
        }

        private static void EnumerateInto(
            IMetadataProvider provider,
            string axType,
            string model,
            Dictionary<string, string> sink,
            string sourceTag,
            bool onlyIfMissing = false)
        {
            var prop = TypeMap.ResolveProperty(provider, axType);
            if (prop == null)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InvalidParams,
                    $"Unknown axType '{axType}'. No matching IMetadataReader on the provider.");
            }

            var reader = prop.GetValue(provider);
            if (reader == null) return;

            // IMetadataReader<T>.ListObjectsForModel(string) -> IEnumerable<string>.
            // Resolve dynamically because the property's declared type may be
            // an interface, and concrete implementations sometimes override
            // the method in subclasses.
            var listMethod = reader.GetType().GetMethod(
                "ListObjectsForModel",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);

            if (listMethod == null)
            {
                // Some providers don't expose ListObjectsForModel for every
                // type. Treat as "no objects from this provider" rather than
                // a fatal error — the other provider may still satisfy.
                return;
            }

            object? listResult;
            try { listResult = listMethod.Invoke(reader, new object[] { model }); }
            catch { return; }

            if (listResult is not System.Collections.IEnumerable enumerable) return;

            foreach (var item in enumerable)
            {
                if (item is not string s || string.IsNullOrEmpty(s)) continue;
                if (onlyIfMissing)
                {
                    if (!sink.ContainsKey(s)) sink[s] = sourceTag;
                }
                else
                {
                    sink[s] = sourceTag;
                }
            }
        }
    }
}
