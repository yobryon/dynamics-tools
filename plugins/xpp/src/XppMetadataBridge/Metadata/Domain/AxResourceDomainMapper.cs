using Newtonsoft.Json.Linq;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>AxResource — tiny manifest (FileName + RelativeUri + TypeOfResource).
    /// The file content copy is handled elsewhere; this maps the manifest only.</summary>
    internal sealed class AxResourceDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxResource";
        protected override string AccessorProperty => "Resources";

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxResource");
            MetaclassMap.SetName(ax, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxResource name is required."));
            Apply(ax, json);
            return ax;
        }

        protected override object ApplyPatch(object current, JObject patch) { Apply(current, patch); return current; }

        private static void Apply(object ax, JObject json)
        {
            MetaclassJson.Assign(ax, "FileName", json["fileName"]);
            MetaclassJson.Assign(ax, "RelativeUriInModelStore", json["relativeUriInModelStore"]);
            MetaclassJson.Assign(ax, "TypeOfResource", json["typeOfResource"]);
        }

        protected override JObject ReadToJson(object ax)
        {
            // Domain shape's FileName/RelativeUri/TypeOfResource are non-nullable
            // (always serialized by the MCP), so emit them unconditionally.
            var t = ax.GetType();
            return new JObject
            {
                ["name"] = MetaclassMap.GetName(ax),
                ["fileName"] = (string?)t.GetProperty("FileName")?.GetValue(ax) ?? string.Empty,
                ["relativeUriInModelStore"] = (string?)t.GetProperty("RelativeUriInModelStore")?.GetValue(ax) ?? string.Empty,
                ["typeOfResource"] = MetaclassJson.ToCamel(t.GetProperty("TypeOfResource")?.GetValue(ax)?.ToString() ?? "XmlDoc"),
            };
        }
    }
}
