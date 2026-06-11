using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// getStructuralReferences - return the outgoing edges from one D365
    /// object to the other objects it explicitly references in its metadata
    /// (base classes, implemented interfaces, form datasources, table
    /// relations, EDT extends, etc.).
    ///
    /// Source-code-level mentions are NOT returned here; those are recovered
    /// at search time via FTS over the methods table. This RPC is about the
    /// declared graph, not the textual graph.
    ///
    /// Kept around alongside getObjectFull for callers (MCP tools, future
    /// inspection RPCs) that need refs without the cost of source-code
    /// extraction.
    /// </summary>
    internal sealed class GetStructuralReferencesHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public GetStructuralReferencesHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "getStructuralReferences";

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

            var edges = ObjectProjection.ProjectReferences(hit.Value, axType);
            return Task.FromResult<object?>(new
            {
                model,
                axType,
                name,
                references = edges,
                count = edges.Count,
                source = SourceWire.From(hit.Source),
                binaryModule = hit.Source == ProviderSource.Runtime
            });
        }
    }
}
