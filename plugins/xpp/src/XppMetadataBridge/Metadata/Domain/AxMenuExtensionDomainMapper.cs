using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// AxMenuExtension — inserts elements into a base menu's tree, plus element/
    /// property modifications. Each added element is an AxMenuExtensionElement
    /// wrapper { Parent, PositionType, PreviousSibling, MenuElement, Tags }
    /// whose MenuElement is the same polymorphic AxMenuElement* tree AxMenu
    /// builds — reused via AxMenuDomainMapper.BuildMenuElement/EmitMenuElement.
    /// </summary>
    internal sealed class AxMenuExtensionDomainMapper : AxExtensionDomainMapperBase
    {
        public override string AxType => "AxMenuExtension";
        protected override string AccessorProperty => "MenuExtensions";
        protected override string MetaTypeName => "AxMenuExtension";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "elements", "customizations", "menuElementModifications", "propertyModifications", "advanced",
        };

        protected override (string Prop, string Key, EmitAs Kind)[] ExtraScalars { get; } =
            new[] { ("ConfigurationKey", "configurationKey", EmitAs.Raw) };

        protected override void BuildTypeSpecific(object ax, JObject json)
        {
            if (json["elements"] is JArray els && Prop(ax, "Elements") is IList list)
            {
                ClearAllowDup(list);
                foreach (var ej in els.OfType<JObject>())
                {
                    var ee = MetaclassMap.Instantiate("AxMenuExtensionElement");
                    MetaclassJson.Assign(ee, "Parent", ej["parent"]);
                    MetaclassJson.Assign(ee, "PositionType", ej["positionType"]);
                    MetaclassJson.Assign(ee, "PreviousSibling", ej["previousSibling"]);
                    MetaclassJson.Assign(ee, "Tags", ej["tags"]);
                    if (ej["menuElement"] is JObject me)
                        ee.GetType().GetProperty("MenuElement")?.SetValue(ee, AxMenuDomainMapper.BuildMenuElement(me));
                    list.Add(ee);
                }
            }
            // Customizations: AxMenuCustomizationElement { Name, Visible(NoYes),
            // Tags } — hide/reorder existing base-menu items.
            if (json["customizations"] is JArray cz && Prop(ax, "Customizations") is IList czl)
            {
                ClearAllowDup(czl);
                foreach (var cj in cz.OfType<JObject>())
                {
                    var ce = MetaclassMap.Instantiate("AxMenuCustomizationElement");
                    MetaclassMap.SetName(ce, (string?)cj["name"] ?? string.Empty);
                    MetaclassJson.Assign(ce, "Visible", cj["visible"]);
                    MetaclassJson.Assign(ce, "Tags", cj["tags"]);
                    czl.Add(ce);
                }
            }
            BuildExtensionMods(ax, "MenuElementModifications", json["menuElementModifications"] as JArray);
        }

        protected override void EmitTypeSpecific(JObject jo, object ax)
        {
            var els = new JArray();
            if (Prop(ax, "Elements") is IEnumerable en)
                foreach (var ee in en)
                {
                    var r = MetaclassMap.Reference(ee.GetType());
                    var o = new JObject();
                    EmitStr(o, ee, "Parent", "parent");
                    MetaclassJson.EmitDefaulted(o, ee, r, "PositionType", "positionType", EmitAs.EnumCamel);
                    EmitStr(o, ee, "PreviousSibling", "previousSibling");
                    EmitStr(o, ee, "Tags", "tags");
                    if (ee.GetType().GetProperty("MenuElement")?.GetValue(ee) is object me)
                        o["menuElement"] = AxMenuDomainMapper.EmitMenuElement(me);
                    els.Add(o);
                }
            Put(jo, "elements", els);

            var cz = new JArray();
            if (Prop(ax, "Customizations") is IEnumerable cze)
                foreach (var ce in cze)
                {
                    var r = MetaclassMap.Reference(ce.GetType());
                    var o = new JObject { ["name"] = MetaclassMap.GetName(ce) };
                    MetaclassJson.EmitDefaulted(o, ce, r, "Visible", "visible", EmitAs.Bool);
                    EmitStr(o, ce, "Tags", "tags");
                    cz.Add(o);
                }
            Put(jo, "customizations", cz);
            Put(jo, "menuElementModifications", EmitExtensionMods(ax, "MenuElementModifications"));
        }
    }
}
