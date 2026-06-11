using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>AxMenu — navigation menu with a recursive, polymorphic element
    /// tree (MenuItem / MenuReference / Separator / SubMenu / Tile).</summary>
    internal sealed class AxMenuDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxMenu";
        protected override string AccessorProperty => "Menus";

        private static readonly (string Key, string Prop, EmitAs Kind)[] MenuScalars =
        {
            ("label","Label",EmitAs.Raw), ("configurationKey","ConfigurationKey",EmitAs.Raw),
            ("countryRegionCodes","CountryRegionCodes",EmitAs.Raw), ("featureClass","FeatureClass",EmitAs.Raw),
            ("isObsolete","IsObsolete",EmitAs.Bool), ("tags","Tags",EmitAs.Raw),
            ("parameters","Parameters",EmitAs.Raw), ("setCompany","SetCompany",EmitAs.Bool),
            ("shortCut","ShortCut",EmitAs.Raw),
        };
        private static readonly (string Key, string Prop)[] ImageFields =
        {
            ("normalImage","NormalImage"), ("disabledImage","DisabledImage"),
            ("imageLocation","ImageLocation"), ("disabledImageLocation","DisabledImageLocation"),
        };

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxMenu");
            MetaclassMap.SetName(ax, (string?)json["name"] ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "name required"));
            ApplyMenu(ax, json);
            return ax;
        }
        protected override object ApplyPatch(object current, JObject patch) { ApplyMenu(current, patch); return current; }

        private static void ApplyMenu(object ax, JObject json)
        {
            foreach (var (key, prop, _) in MenuScalars) MetaclassJson.Assign(ax, prop, json[key]);
            ApplyImage(ax, json["image"] as JObject);
            if (json["menuItemTarget"] is JObject mt)
            {
                MetaclassJson.Assign(ax, "MenuItemName", mt["menuItemName"]);
                MetaclassJson.Assign(ax, "MenuItemType", mt["menuItemType"]);
            }
            if (json["advanced"] is JObject adv) MetaclassJson.Assign(ax, "Visibility", adv["visibility"]);
            BuildElements(ax, json["elements"] as JArray);
        }

        protected override JObject ReadToJson(object ax)
        {
            var r = MetaclassMap.Reference(ax.GetType());
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            foreach (var (key, prop, kind) in MenuScalars)
                MetaclassJson.EmitDefaulted(jo, ax, r, prop, key, kind);
            var img = EmitImage(ax); if (img.Count > 0) jo["image"] = img;
            var mt = new JObject();
            MetaclassJson.EmitDefaulted(mt, ax, r, "MenuItemName", "menuItemName", EmitAs.Raw);
            MetaclassJson.EmitDefaulted(mt, ax, r, "MenuItemType", "menuItemType", EmitAs.EnumCamel);
            if (mt.Count > 0) jo["menuItemTarget"] = mt;
            var vis = MetaclassJson.ReadEnumCamel(ax, "Visibility");
            if (vis != null && vis != "public") jo["advanced"] = new JObject { ["visibility"] = vis };
            var els = EmitElements(ax); if (els.Count > 0) jo["elements"] = els;
            return jo;
        }

        private static void ApplyImage(object target, JObject? img)
        {
            if (img == null) return;
            foreach (var (key, prop) in ImageFields) MetaclassJson.Assign(target, prop, img[key]);
        }
        private static JObject EmitImage(object source)
        {
            var r = MetaclassMap.Reference(source.GetType());
            var o = new JObject();
            foreach (var (key, prop) in ImageFields)
                MetaclassJson.EmitDefaulted(o, source, r, prop, key, EmitAs.Raw);
            return o;
        }

        // ---- elements (polymorphic by Kind) -------------------------------
        private static void BuildElements(object parent, JArray? arr)
        {
            if (arr == null) return;   // null = leave the tree untouched (merge-patch)
            var coll = parent.GetType().GetProperty("Elements")?.GetValue(parent);
            if (coll == null) return;
            // REPLACE, don't append. On a patch the existing tree is already on the
            // metaclass; without clearing, the submitted tree would be concatenated,
            // producing duplicate element names and a menu that writes but won't read.
            // No-op on create / nested submenus (they start empty). We do NOT call
            // AllowDuplicates here: menu elements are keyed by Name and must be
            // unique, so the collection's guard rejects a duplicate-named tree at
            // write time instead of letting a structurally-invalid menu through.
            coll.GetType().GetMethod("Clear", System.Type.EmptyTypes)?.Invoke(coll, null);
            var add = coll.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
            foreach (var ej in arr.OfType<JObject>())
                MetaclassMap.AddTo(add, coll, BuildMenuElement(ej));
        }

        /// <summary>Build one polymorphic AxMenuElement* from its JObject.
        /// internal: reused by AxMenuExtensionDomainMapper, whose Elements wrap
        /// a single MenuElement of the same shape.</summary>
        internal static object BuildMenuElement(JObject ej)
        {
            var kind = (string?)ej["kind"] ?? "Separator";
            var el = MetaclassMap.Instantiate("AxMenuElement" + MetaclassMap.Pascal(kind));
            MetaclassMap.SetName(el, (string?)ej["name"] ?? string.Empty);
            MetaclassJson.Assign(el, "Visible", ej["visible"]);
            MetaclassJson.Assign(el, "Tags", ej["tags"]);
            // Per-kind fields — Assign no-ops the ones absent on the subtype.
            MetaclassJson.Assign(el, "MenuItemName", ej["menuItemName"]);
            MetaclassJson.Assign(el, "MenuItemType", ej["menuItemType"]);
            MetaclassJson.Assign(el, "DisplayInContentArea", ej["displayInContentArea"]);
            MetaclassJson.Assign(el, "Parameters", ej["parameters"]);
            MetaclassJson.Assign(el, "ShortCut", ej["shortCut"]);
            MetaclassJson.Assign(el, "ShowParentModule", ej["showParentModule"]);
            MetaclassJson.Assign(el, "MenuName", ej["menuName"]);
            MetaclassJson.Assign(el, "Tile", ej["tile"]);
            // SubMenu extras.
            MetaclassJson.Assign(el, "Label", ej["label"]);
            MetaclassJson.Assign(el, "ConfigurationKey", ej["configurationKey"]);
            MetaclassJson.Assign(el, "CountryRegionCodes", ej["countryRegionCodes"]);
            MetaclassJson.Assign(el, "FeatureClass", ej["featureClass"]);
            MetaclassJson.Assign(el, "SetCompany", ej["setCompany"]);
            ApplyImage(el, ej["image"] as JObject);
            if (kind.Equals("SubMenu", System.StringComparison.OrdinalIgnoreCase))
                BuildElements(el, ej["elements"] as JArray);
            return el;
        }

        private static JArray EmitElements(object parent)
        {
            var arr = new JArray();
            if (parent.GetType().GetProperty("Elements")?.GetValue(parent) is not IEnumerable en) return arr;
            foreach (var el in en) arr.Add(EmitMenuElement(el));
            return arr;
        }

        /// <summary>Emit one polymorphic AxMenuElement* to a JObject. internal:
        /// reused by AxMenuExtensionDomainMapper.</summary>
        internal static JObject EmitMenuElement(object el)
        {
            {
                var kind = el.GetType().Name.Substring("AxMenuElement".Length); // MenuItem/MenuReference/Separator/SubMenu/Tile
                var r = MetaclassMap.Reference(el.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(el), ["kind"] = kind };
                MetaclassJson.EmitDefaulted(o, el, r, "Visible", "visible", EmitAs.Bool);
                MetaclassJson.EmitDefaulted(o, el, r, "Tags", "tags", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "MenuItemName", "menuItemName", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "MenuItemType", "menuItemType", EmitAs.EnumCamel);
                MetaclassJson.EmitDefaulted(o, el, r, "DisplayInContentArea", "displayInContentArea", EmitAs.Bool);
                MetaclassJson.EmitDefaulted(o, el, r, "Parameters", "parameters", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "ShortCut", "shortCut", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "ShowParentModule", "showParentModule", EmitAs.Bool);
                MetaclassJson.EmitDefaulted(o, el, r, "MenuName", "menuName", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "Tile", "tile", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "Label", "label", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "ConfigurationKey", "configurationKey", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "CountryRegionCodes", "countryRegionCodes", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "FeatureClass", "featureClass", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, el, r, "SetCompany", "setCompany", EmitAs.Bool);
                if (kind == "SubMenu")
                {
                    var img = EmitImage(el); if (img.Count > 0) o["image"] = img;
                    var nested = EmitElements(el); if (nested.Count > 0) o["elements"] = nested;
                }
                return o;
            }
        }
    }
}
