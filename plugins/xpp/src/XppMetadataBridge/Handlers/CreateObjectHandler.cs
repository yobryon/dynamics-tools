using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// createObject - write a new AOT object from caller-supplied XML.
    ///
    /// Params:
    ///   axType: "AxClass" / "AxTable" / ... (must match the root element)
    ///   model:  the target model's logical Name. Manifest lookup decides
    ///           the physical write location.
    ///   xml:    the full AOT XML for the object.
    ///
    /// Semantics: matches Microsoft's own AddCodeContentToActiveProject -
    /// fails if an object with that Name already exists. To overwrite, use
    /// updateObject. To migrate an existing object to a different model,
    /// delete-then-create.
    /// </summary>
    internal sealed class CreateObjectHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public CreateObjectHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "createObject";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Require(@params);
            var axType = Params.RequireString(p, "axType");
            var model = Params.RequireString(p, "model");
            var xml = Params.RequireString(p, "xml");

            var accessor = WriteOperations.ResolveAccessor(_providers, axType);
            var metadataObject = WriteOperations.DeserializeXml(accessor.Accessor, xml, accessor.EntityType);
            var saveInfo = WriteOperations.ResolveModel(_providers, model);

            // Surface the deserialized Name to the response so the caller can
            // verify what was actually written (XML and the user's intent
            // don't always agree).
            var nameProp = accessor.EntityType.GetProperty("Name");
            var resolvedName = nameProp?.GetValue(metadataObject) as string;

            // Round-trip drop detection (advisory) — see UpdateObjectHandler.
            // Catches content the caller's XML carried that MS's FromFile
            // dropped on deserialize; never blocks the write.
            var drops = WriteOperations.DetectDroppedProperties(xml, metadataObject);
            JArray? dropped = null;
            if (drops.Count > 0)
            {
                dropped = new JArray();
                foreach (var d in drops)
                    dropped.Add(new JObject { ["path"] = d.Path, ["value"] = d.Value });
            }

            WriteOperations.Invoke(accessor, WriteOperations.WriteKind.Create, metadataObject, saveInfo);

            var response = new JObject
            {
                ["axType"] = axType,
                ["model"] = model,
                ["name"] = resolvedName,
                ["created"] = true
            };
            if (dropped != null) response["droppedProperties"] = dropped;
            return Task.FromResult<object?>(response);
        }
    }
}
