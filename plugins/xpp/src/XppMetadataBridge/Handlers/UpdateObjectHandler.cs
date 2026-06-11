using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// updateObject - overwrite an existing AOT object with caller-supplied
    /// XML. Mirrors Microsoft's UpdateMetadata: the object must already exist
    /// in the target model.
    ///
    /// The supplied XML must be the FULL object representation, not a patch.
    /// To make a small change, the canonical flow is:
    ///   1. getObjectXml -> get the current XML
    ///   2. edit locally
    ///   3. updateObject -> post the edited XML back
    ///
    /// For method-only edits, prefer the upcoming updateMethodSource RPC,
    /// which round-trips through the X++ parser without the caller having to
    /// regenerate the envelope.
    /// </summary>
    internal sealed class UpdateObjectHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public UpdateObjectHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "updateObject";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Require(@params);
            var axType = Params.RequireString(p, "axType");
            var model = Params.RequireString(p, "model");
            var xml = Params.RequireString(p, "xml");

            var accessor = WriteOperations.ResolveAccessor(_providers, axType);
            var metadataObject = WriteOperations.DeserializeXml(accessor.Accessor, xml, accessor.EntityType);
            var saveInfo = WriteOperations.ResolveModel(_providers, model);

            var nameProp = accessor.EntityType.GetProperty("Name");
            var resolvedName = nameProp?.GetValue(metadataObject) as string;

            WriteOperations.Invoke(accessor, WriteOperations.WriteKind.Update, metadataObject, saveInfo);

            return Task.FromResult<object?>(new
            {
                axType,
                model,
                name = resolvedName,
                updated = true
            });
        }
    }
}
