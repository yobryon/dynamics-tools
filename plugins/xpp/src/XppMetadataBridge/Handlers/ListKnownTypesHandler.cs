using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// listKnownTypes - return the AxType names the metadata provider knows
    /// how to enumerate. Used by the service indexer so the (model x axType)
    /// walking loop stays in lockstep with whatever the provider actually
    /// supports, rather than relying on a hand-curated constant that drifts
    /// when Microsoft adds new types.
    /// </summary>
    internal sealed class ListKnownTypesHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public ListKnownTypesHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "listKnownTypes";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            // The standard and custom providers are both DiskProviders with
            // the same reader surface, so it's sufficient to enumerate
            // either. We use the standard handle - it's always present.
            var types = TypeMap.For(_providers.Standard).Keys
                .OrderBy(t => t, System.StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.FromResult<object?>(new
            {
                types,
                count = types.Length
            });
        }
    }
}
