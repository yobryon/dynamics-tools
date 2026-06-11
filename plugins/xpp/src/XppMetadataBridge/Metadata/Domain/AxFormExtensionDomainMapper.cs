using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// AxFormExtension — the deepest extension. Reuses the base form's proven
    /// control / data-source / part build+emit (AxFormDomainMapper.*) for the
    /// heavy polymorphic trees, and the shared extension base for the two
    /// modification primitives. Collections:
    ///   - ControlModifications / DataSourceModifications: AxExtensionModification
    ///     { Name, Parent, PropertyModifications, Tags } — base helpers.
    ///   - Controls: AxFormExtensionControl { Name (wrapper), FormControl
    ///     (full AxFormControl tree), Parent, PositionType, PreviousSibling, Tags }.
    ///   - DataSourceReferences: AxFormExtensionDataSourceReference { Name, Parent,
    ///     PositionType, PreviousSibling, Tags, FormDataSourceReferenced (a
    ///     Referenced-kind AxFormDataSource) }.
    ///   - DataSources: polymorphic AxFormDataSource family (added data sources).
    ///   - Parts: AxFormExtensionPartReference { Name (wrapper), PositionType,
    ///     PreviousSibling, Tags, FormPartReference (AxFormPartReference) }.
    ///   - PropertyModifications + scalars (ConfigurationKey/IsObsolete/Tags/
    ///     Visibility): base BuildCommon/EmitCommon.
    ///
    /// Per-item method bodies (control / data-source source split that the base
    /// form mapper externalizes) are carried inside the item JSON under private
    /// "controlSource" / "dsSource" arrays so the round-trip stays lossless.
    /// </summary>
    internal sealed class AxFormExtensionDomainMapper : AxExtensionDomainMapperBase
    {
        public override string AxType => "AxFormExtension";
        protected override string AccessorProperty => "FormExtensions";
        protected override string MetaTypeName => "AxFormExtension";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "controlModifications", "controls", "dataSourceModifications",
            "dataSourceReferences", "dataSources", "dataSourceSource", "parts",
            "propertyModifications", "advanced",
        };

        protected override (string Prop, string Key, EmitAs Kind)[] ExtraScalars { get; } =
            new[] { ("ConfigurationKey", "configurationKey", EmitAs.Raw) };

        protected override void BuildTypeSpecific(object ax, JObject json)
        {
            BuildExtensionMods(ax, "ControlModifications", json["controlModifications"] as JArray);
            BuildExtensionMods(ax, "DataSourceModifications", json["dataSourceModifications"] as JArray);
            BuildControls(ax, json["controls"] as JArray);
            BuildDataSourceRefs(ax, json["dataSourceReferences"] as JArray);
            BuildDataSources(ax, json["dataSources"] as JArray, json["dataSourceSource"] as JArray);
            BuildParts(ax, json["parts"] as JArray);
        }

        protected override void EmitTypeSpecific(JObject jo, object ax)
        {
            Put(jo, "controlModifications", EmitExtensionMods(ax, "ControlModifications"));
            Put(jo, "dataSourceModifications", EmitExtensionMods(ax, "DataSourceModifications"));
            Put(jo, "controls", EmitControls(ax));
            Put(jo, "dataSourceReferences", EmitDataSourceRefs(ax));
            EmitDataSources(jo, ax);
            Put(jo, "parts", EmitParts(ax));
        }

        // ---- Controls (AxFormExtensionControl wrappers) -------------------
        private static void BuildControls(object ax, JArray? arr)
        {
            if (arr == null || Prop(ax, "Controls") is not IList list) return;
            ClearAllowDup(list);
            foreach (var cj in arr.OfType<JObject>())
            {
                var ee = MetaclassMap.Instantiate("AxFormExtensionControl");
                MetaclassMap.SetName(ee, (string?)cj["name"] ?? string.Empty);
                MetaclassJson.Assign(ee, "Parent", cj["parent"]);
                MetaclassJson.Assign(ee, "PositionType", cj["positionType"]);
                MetaclassJson.Assign(ee, "PreviousSibling", cj["previousSibling"]);
                MetaclassJson.Assign(ee, "Tags", cj["tags"]);
                if (cj["formControl"] is JObject fcj)
                {
                    var ctlSource = AxFormDomainMapper.IndexByName(cj["controlSource"] as JArray);
                    var ctl = AxFormDomainMapper.BuildControl(fcj, ctlSource);
                    ee.GetType().GetProperty("FormControl")?.SetValue(ee, ctl);
                }
                list.Add(ee);
            }
        }

        private static JArray EmitControls(object ax)
        {
            var arr = new JArray();
            if (Prop(ax, "Controls") is not IEnumerable en) return arr;
            foreach (var ee in en)
            {
                var r = MetaclassMap.Reference(ee.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(ee) };
                EmitStr(o, ee, "Parent", "parent");
                MetaclassJson.EmitDefaulted(o, ee, r, "PositionType", "positionType", EmitAs.EnumCamel);
                EmitStr(o, ee, "PreviousSibling", "previousSibling");
                EmitStr(o, ee, "Tags", "tags");
                if (Prop(ee, "FormControl") is AxFormControl fc)
                {
                    var ctlSrc = new JArray();
                    o["formControl"] = AxFormDomainMapper.EmitControl(fc, ctlSrc);
                    if (ctlSrc.Count > 0) o["controlSource"] = ctlSrc;
                }
                arr.Add(o);
            }
            return arr;
        }

        // ---- DataSourceReferences (AxFormExtensionDataSourceReference) ----
        private static void BuildDataSourceRefs(object ax, JArray? arr)
        {
            if (arr == null || Prop(ax, "DataSourceReferences") is not IList list) return;
            ClearAllowDup(list);
            foreach (var rj in arr.OfType<JObject>())
            {
                var dr = MetaclassMap.Instantiate("AxFormExtensionDataSourceReference");
                MetaclassMap.SetName(dr, (string?)rj["name"] ?? string.Empty);
                MetaclassJson.Assign(dr, "Parent", rj["parent"]);
                MetaclassJson.Assign(dr, "PositionType", rj["positionType"]);
                MetaclassJson.Assign(dr, "PreviousSibling", rj["previousSibling"]);
                MetaclassJson.Assign(dr, "Tags", rj["tags"]);
                if (rj["formDataSourceReferenced"] is JObject fdj)
                {
                    var inner = AxFormDomainMapper.BuildDataSource(fdj, AxFormDomainMapper.IndexByName(rj["dsSource"] as JArray));
                    dr.GetType().GetProperty("FormDataSourceReferenced")?.SetValue(dr, inner);
                }
                list.Add(dr);
            }
        }

        private static JArray EmitDataSourceRefs(object ax)
        {
            var arr = new JArray();
            if (Prop(ax, "DataSourceReferences") is not IEnumerable en) return arr;
            foreach (var dr in en)
            {
                var r = MetaclassMap.Reference(dr.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(dr) };
                EmitStr(o, dr, "Parent", "parent");
                MetaclassJson.EmitDefaulted(o, dr, r, "PositionType", "positionType", EmitAs.EnumCamel);
                EmitStr(o, dr, "PreviousSibling", "previousSibling");
                EmitStr(o, dr, "Tags", "tags");
                if (Prop(dr, "FormDataSourceReferenced") is object fd)
                {
                    var s = new JArray();
                    o["formDataSourceReferenced"] = AxFormDomainMapper.EmitDataSource(fd, s);
                    if (s.Count > 0) o["dsSource"] = s;
                }
                arr.Add(o);
            }
            return arr;
        }

        // ---- DataSources (added; polymorphic AxFormDataSource) ------------
        private static void BuildDataSources(object ax, JArray? arr, JArray? dsSource)
        {
            if (arr == null || Prop(ax, "DataSources") is not IList list) return;
            ClearAllowDup(list);
            var src = AxFormDomainMapper.IndexByName(dsSource);
            foreach (var dj in arr.OfType<JObject>())
                list.Add(AxFormDomainMapper.BuildDataSource(dj, src));
        }

        private static void EmitDataSources(JObject jo, object ax)
        {
            if (Prop(ax, "DataSources") is not IEnumerable en) return;
            var arr = new JArray();
            var dsSrc = new JArray();
            foreach (var ds in en) arr.Add(AxFormDomainMapper.EmitDataSource(ds, dsSrc));
            Put(jo, "dataSources", arr);
            if (dsSrc.Count > 0) jo["dataSourceSource"] = dsSrc;
        }

        // ---- Parts (AxFormExtensionPartReference wrappers) ----------------
        private static void BuildParts(object ax, JArray? arr)
        {
            if (arr == null || Prop(ax, "Parts") is not IList list) return;
            ClearAllowDup(list);
            foreach (var pj in arr.OfType<JObject>())
            {
                var pr = MetaclassMap.Instantiate("AxFormExtensionPartReference");
                MetaclassMap.SetName(pr, (string?)pj["wrapperName"] ?? (string?)pj["name"] ?? string.Empty);
                MetaclassJson.Assign(pr, "PositionType", pj["positionType"]);
                MetaclassJson.Assign(pr, "PreviousSibling", pj["previousSibling"]);
                MetaclassJson.Assign(pr, "Tags", pj["wrapperTags"]);
                var inner = AxFormDomainMapper.BuildPart(pj);
                pr.GetType().GetProperty("FormPartReference")?.SetValue(pr, inner);
                list.Add(pr);
            }
        }

        private static JArray EmitParts(object ax)
        {
            var arr = new JArray();
            if (Prop(ax, "Parts") is not IEnumerable en) return arr;
            foreach (var pr in en)
            {
                var r = MetaclassMap.Reference(pr.GetType());
                JObject o = Prop(pr, "FormPartReference") is object inner
                    ? AxFormDomainMapper.EmitPart(inner)
                    : new JObject();
                o["wrapperName"] = MetaclassMap.GetName(pr);
                MetaclassJson.EmitDefaulted(o, pr, r, "PositionType", "positionType", EmitAs.EnumCamel);
                EmitStr(o, pr, "PreviousSibling", "previousSibling");
                var wt = pr.GetType().GetProperty("Tags")?.GetValue(pr) as string;
                if (!string.IsNullOrEmpty(wt)) o["wrapperTags"] = wt;
                arr.Add(o);
            }
            return arr;
        }
    }
}
