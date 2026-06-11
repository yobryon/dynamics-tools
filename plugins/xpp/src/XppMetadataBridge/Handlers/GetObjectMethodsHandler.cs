using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// getObjectMethods - load a D365 object and return every method on it,
    /// with the X++ source code. Used by the MCP inspection tools
    /// (xpp_get_object_methods) so the agent can list methods without
    /// fetching their structural-reference edges. Indexer prefers
    /// getObjectFull (which returns both in one call).
    ///
    /// Runtime-sourced reads still return method signatures (Name plus
    /// any reflected signature data) but the Source body is empty since
    /// binary modules ship no X++ source. The response's source field
    /// surfaces this to callers.
    /// </summary>
    internal sealed class GetObjectMethodsHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public GetObjectMethodsHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "getObjectMethods";

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
            return Task.FromResult<object?>(new
            {
                model,
                axType,
                name,
                methods,
                count = methods.Count,
                source = SourceWire.From(hit.Source),
                binaryModule = hit.Source == ProviderSource.Runtime
            });
        }
    }
}
