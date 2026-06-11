using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// getObjectFull - load an object ONCE and return both methods and
    /// structural references in a single response.
    ///
    /// Indexer fast-path: the indexer's phase-2 loop used to issue two
    /// separate RPCs (getObjectMethods + getStructuralReferences) per
    /// object, each requiring its own pipe round-trip and its own
    /// provider.Read() call inside the bridge. This handler halves the
    /// round-trip count and loads the object once, walking both
    /// projections off the cached instance.
    ///
    /// The two single-purpose handlers stay in place because the MCP
    /// inspection tools (xpp_get_object_methods / xpp_get_references)
    /// still want them, and external callers shouldn't be forced to
    /// pay for ref extraction when they only want methods.
    /// </summary>
    internal sealed class GetObjectFullHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public GetObjectFullHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "getObjectFull";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Require(@params);
            var model = Params.RequireString(p, "model");
            var axType = Params.RequireString(p, "axType");
            var name = Params.RequireString(p, "name");

            var hit = ObjectProjection.ReadObjectWithSource(_providers, axType, name)
                ?? throw new JsonRpcException(
                    JsonRpcErrorCodes.ObjectNotFound,
                    $"{axType}:{name} not found in any provider");

            var methods = ObjectProjection.ProjectMethods(hit.Value);
            var references = ObjectProjection.ProjectReferences(hit.Value, axType);
            var fieldReferences = ObjectProjection.ProjectFieldReferences(hit.Value, axType);
            var labelReferences = ObjectProjection.ProjectLabelReferences(hit.Value, axType);

            return Task.FromResult<object?>(new
            {
                model,
                axType,
                name,
                methods,
                references,
                fieldReferences,
                labelReferences,
                methodCount = methods.Count,
                referenceCount = references.Count,
                fieldReferenceCount = fieldReferences.Count,
                labelReferenceCount = labelReferences.Count,
                source = SourceWire.From(hit.Source),
                binaryModule = hit.Source == ProviderSource.Runtime
            });
        }
    }
}
