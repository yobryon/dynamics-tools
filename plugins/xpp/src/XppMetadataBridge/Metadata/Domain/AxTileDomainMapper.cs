using Newtonsoft.Json.Linq;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>AxTile — workspace tile (KPI / count / link). Flat scalar surface.</summary>
    internal sealed class AxTileDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxTile";
        protected override string AccessorProperty => "Tiles";

        private static readonly (string Key, string Prop, EmitAs Kind)[] Fields =
        {
            ("label","Label",EmitAs.Raw), ("helpText","HelpText",EmitAs.Raw),
            ("configurationKey","ConfigurationKey",EmitAs.Raw), ("countryRegionCodes","CountryRegionCodes",EmitAs.Raw),
            ("isObsolete","IsObsolete",EmitAs.Bool), ("type","Type",EmitAs.EnumCamel),
            ("size","Size",EmitAs.EnumCamel), ("tileDisplay","TileDisplay",EmitAs.EnumCamel),
            ("menuItemName","MenuItemName",EmitAs.Raw), ("menuItemType","MenuItemType",EmitAs.EnumCamel),
            ("formViewOption","FormViewOption",EmitAs.EnumCamel), ("openMode","OpenMode",EmitAs.EnumCamel),
            ("parameters","Parameters",EmitAs.Raw), ("copyCallerQuery","CopyCallerQuery",EmitAs.Bool),
            ("applyFilter","ApplyFilter",EmitAs.Bool), ("query","Query",EmitAs.Raw),
            ("kpi","KPI",EmitAs.Raw), ("refreshFrequency","RefreshFrequency",EmitAs.EnumCamel),
            ("allowUserCacheRefresh","AllowUserCacheRefresh",EmitAs.Bool), ("normalImage","NormalImage",EmitAs.Raw),
            ("imageLocation","ImageLocation",EmitAs.Raw), ("url","URL",EmitAs.Raw), ("tags","Tags",EmitAs.Raw),
        };

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxTile");
            MetaclassMap.SetName(ax, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxTile name is required."));
            Apply(ax, json);
            return ax;
        }

        protected override object ApplyPatch(object current, JObject patch) { Apply(current, patch); return current; }

        private static void Apply(object ax, JObject json)
        {
            foreach (var (key, prop, _) in Fields) MetaclassJson.Assign(ax, prop, json[key]);
            if (json["advanced"] is JObject adv) MetaclassJson.Assign(ax, "Visibility", adv["visibility"]);
        }

        protected override JObject ReadToJson(object ax)
        {
            var reference = MetaclassMap.Reference(ax.GetType());
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            foreach (var (key, prop, kind) in Fields)
                MetaclassJson.EmitDefaulted(jo, ax, reference, prop, key, kind);
            var vis = MetaclassJson.ReadEnumCamel(ax, "Visibility");
            if (vis != null && vis != "public") jo["advanced"] = new JObject { ["visibility"] = vis };
            return jo;
        }
    }
}
