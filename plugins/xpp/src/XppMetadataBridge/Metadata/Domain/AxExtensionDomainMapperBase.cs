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
    /// Shared base for the extension sub-family — AxEnumExtension,
    /// AxEdtExtension, AxTableExtension, AxViewExtension,
    /// AxDataEntityViewExtension, AxMenuExtension, AxFormExtension. Every
    /// extension carries the same delta surface:
    ///   - scalars: Name, IsObsolete(NoYes), Tags, Visibility (+ a few
    ///     per-type extras like FormRef / ConfigurationKey)
    ///   - PropertyModifications: AxPropertyModification { Name, Value, Tags }
    ///     — changes to the target object's own properties
    ///   - *Modifications collections (Field / Relation / Value / DataSource /
    ///     Control / MenuElement): AxExtensionModification { Name,
    ///     PropertyModifications(coll), Tags } — "modify the named child"
    /// plus added-element collections (Fields / Indexes / EnumValues / ...)
    /// that reuse the base type's element shapes.
    ///
    /// This base owns the scalars + the two modification primitives. Subclasses
    /// provide StructuralKeys, optional ExtraScalars, and BuildTypeSpecific /
    /// EmitTypeSpecific for the added-element + *Modifications collections.
    /// </summary>
    internal abstract class AxExtensionDomainMapperBase : DomainBridgeMapperBase
    {
        /// <summary>The extension metaclass type name (e.g. "AxTableExtension").</summary>
        protected abstract string MetaTypeName { get; }

        /// <summary>Top-level JSON keys handled structurally (collections /
        /// nested blocks), excluded from the blind scalar assign.</summary>
        protected abstract ISet<string> StructuralKeys { get; }

        protected abstract void BuildTypeSpecific(object ax, JObject json);
        protected abstract void EmitTypeSpecific(JObject jo, object ax);

        /// <summary>Per-type scalar emit table beyond the common IsObsolete/Tags
        /// (e.g. table's FormRef, form/menu's ConfigurationKey). Default none.</summary>
        protected virtual (string Prop, string Key, EmitAs Kind)[] ExtraScalars
            => Array.Empty<(string, string, EmitAs)>();

        // ===================================================================
        protected override object BuildFromJson(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"{AxType} name is required.");
            var ax = MetaclassMap.Instantiate(MetaTypeName);
            MetaclassMap.SetName(ax, name);
            BuildCommon(ax, json);
            BuildTypeSpecific(ax, json);
            return ax;
        }

        protected override object ApplyPatch(object current, JObject patch)
        {
            BuildCommon(current, patch);
            BuildTypeSpecific(current, patch);
            return current;
        }

        protected override JObject ReadToJson(object ax)
        {
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            EmitCommon(jo, ax);
            EmitTypeSpecific(jo, ax);
            return jo;
        }

        // ===================================================================
        private void BuildCommon(object ax, JObject json)
        {
            foreach (var p in json.Properties())
            {
                if (StructuralKeys.Contains(p.Name)) continue;
                MetaclassJson.Assign(ax, MetaclassMap.Pascal(p.Name), p.Value);
            }
            if (json["advanced"] is JObject adv) MetaclassMap.AssignAll(ax, adv);
            if (json["propertyModifications"] is JArray pms)
                BuildPropertyMods(ax, "PropertyModifications", pms);
        }

        private void EmitCommon(JObject jo, object ax)
        {
            var reference = MetaclassMap.Reference(ax.GetType());
            MetaclassJson.EmitDefaulted(jo, ax, reference, "IsObsolete", "isObsolete", EmitAs.Bool);
            MetaclassJson.EmitDefaulted(jo, ax, reference, "Tags", "tags", EmitAs.Raw);
            foreach (var (prop, key, kind) in ExtraScalars)
                MetaclassJson.EmitDefaulted(jo, ax, reference, prop, key, kind);

            var vis = MetaclassJson.ReadEnumCamel(ax, "Visibility");
            if (vis != null && vis != "public") jo["advanced"] = new JObject { ["visibility"] = vis };

            var pms = EmitPropertyMods(ax, "PropertyModifications");
            if (pms.Count > 0) jo["propertyModifications"] = pms;
        }

        // ===================================================================
        // Modification primitives — shared by every extension type.
        // ===================================================================
        protected static void BuildPropertyMods(object parent, string collProp, JArray? arr)
        {
            if (arr == null) return;
            if (Prop(parent, collProp) is not IList list) return;
            ClearAllowDup(list);
            foreach (var pj in arr.OfType<JObject>())
            {
                var pm = MetaclassMap.Instantiate("AxPropertyModification");
                MetaclassMap.SetName(pm, (string?)pj["name"] ?? string.Empty);
                MetaclassJson.Assign(pm, "Value", pj["value"]);
                MetaclassJson.Assign(pm, "Tags", pj["tags"]);
                list.Add(pm);
            }
        }

        protected static JArray EmitPropertyMods(object parent, string collProp)
        {
            var arr = new JArray();
            if (Prop(parent, collProp) is not IEnumerable en) return arr;
            foreach (var pm in en)
            {
                var o = new JObject { ["name"] = MetaclassMap.GetName(pm) };
                EmitStr(o, pm, "Value", "value");
                EmitStr(o, pm, "Tags", "tags");
                arr.Add(o);
            }
            return arr;
        }

        /// <summary>Build a *Modifications collection of AxExtensionModification
        /// ({ Name, PropertyModifications(coll), Tags }).</summary>
        protected static void BuildExtensionMods(object parent, string collProp, JArray? arr)
        {
            if (arr == null) return;
            if (Prop(parent, collProp) is not IList list) return;
            ClearAllowDup(list);
            foreach (var mj in arr.OfType<JObject>())
            {
                var em = MetaclassMap.Instantiate("AxExtensionModification");
                MetaclassMap.SetName(em, (string?)mj["name"] ?? string.Empty);
                MetaclassJson.Assign(em, "Parent", mj["parent"]);
                MetaclassJson.Assign(em, "Tags", mj["tags"]);
                BuildPropertyMods(em, "PropertyModifications", mj["propertyModifications"] as JArray);
                list.Add(em);
            }
        }

        protected static JArray EmitExtensionMods(object parent, string collProp)
        {
            var arr = new JArray();
            if (Prop(parent, collProp) is not IEnumerable en) return arr;
            foreach (var em in en)
            {
                var o = new JObject { ["name"] = MetaclassMap.GetName(em) };
                EmitStr(o, em, "Parent", "parent");
                EmitStr(o, em, "Tags", "tags");
                var pms = EmitPropertyMods(em, "PropertyModifications");
                if (pms.Count > 0) o["propertyModifications"] = pms;
                arr.Add(o);
            }
            return arr;
        }

        // ===================================================================
        // Generic "simple element collection": instantiate a fixed element
        // type, blind-assign every non-structural scalar, add. Read emits a
        // scalar table. Used for flat added-element collections (edt array
        // elements, enum values without nesting, field-group extensions).
        // ===================================================================
        protected static void BuildSimpleColl(object parent, string collProp, string elemType, JArray? arr, params string[] skipKeys)
        {
            if (arr == null) return;
            if (Prop(parent, collProp) is not IList list) return;
            ClearAllowDup(list);
            var skip = new HashSet<string>(skipKeys, StringComparer.OrdinalIgnoreCase) { "name" };
            foreach (var ej in arr.OfType<JObject>())
            {
                var e = MetaclassMap.Instantiate(elemType);
                MetaclassMap.SetName(e, (string?)ej["name"] ?? string.Empty);
                foreach (var p in ej.Properties())
                {
                    if (skip.Contains(p.Name)) continue;
                    MetaclassJson.Assign(e, MetaclassMap.Pascal(p.Name), p.Value);
                }
                list.Add(e);
            }
        }

        protected static JArray EmitSimpleColl(object parent, string collProp, (string Prop, string Key, EmitAs Kind)[] scalars)
        {
            var arr = new JArray();
            if (Prop(parent, collProp) is not IEnumerable en) return arr;
            foreach (var e in en)
            {
                var reference = MetaclassMap.Reference(e.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(e) };
                foreach (var (prop, key, kind) in scalars)
                    MetaclassJson.EmitDefaulted(o, e, reference, prop, key, kind);
                arr.Add(o);
            }
            return arr;
        }

        // FieldGroupExtension { Name, Fields[] (string list), Tags } — shared by
        // Table/View/Entity extensions. On-disk element AxTableFieldGroupExtension
        // with child AxTableFieldGroupField { DataField }.
        protected static void BuildFieldGroupExtensions(object parent, string collProp, JArray? arr)
        {
            if (arr == null) return;
            if (Prop(parent, collProp) is not IList list) return;
            ClearAllowDup(list);
            foreach (var gj in arr.OfType<JObject>())
            {
                var g = MetaclassMap.Instantiate("AxTableFieldGroupExtension");
                MetaclassMap.SetName(g, (string?)gj["name"] ?? string.Empty);
                MetaclassJson.Assign(g, "Tags", gj["tags"]);
                if (gj["fields"] is JArray fields && Prop(g, "Fields") is IList fl)
                {
                    ClearAllowDup(fl);
                    foreach (var jf in fields)
                    {
                        var fieldName = jf.Type == JTokenType.String ? (string)jf! : (string?)(jf as JObject)?["dataField"];
                        if (string.IsNullOrEmpty(fieldName)) continue;
                        var gf = MetaclassMap.Instantiate("AxTableFieldGroupField");
                        gf.GetType().GetProperty("DataField")?.SetValue(gf, fieldName);
                        fl.Add(gf);
                    }
                }
                list.Add(g);
            }
        }

        protected static JArray EmitFieldGroupExtensions(object parent, string collProp)
        {
            var arr = new JArray();
            if (Prop(parent, collProp) is not IEnumerable en) return arr;
            foreach (var g in en)
            {
                var reference = MetaclassMap.Reference(g.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(g) };
                MetaclassJson.EmitDefaulted(o, g, reference, "Tags", "tags", EmitAs.Raw);
                var fields = new JArray();
                if (Prop(g, "Fields") is IEnumerable fe)
                    foreach (var jf in fe)
                    {
                        var df = jf.GetType().GetProperty("DataField")?.GetValue(jf) as string;
                        if (!string.IsNullOrEmpty(df)) fields.Add(df);
                    }
                if (fields.Count > 0) o["fields"] = fields;
                arr.Add(o);
            }
            return arr;
        }

        // ===================================================================
        // added-element collection helpers — map JSON items through a reused
        // base-type element builder/emitter into the named collection.
        // ===================================================================
        protected static void AddEach(object parent, string collProp, JToken? arr, Func<JObject, object> build)
        {
            if (arr is not JArray ja || Prop(parent, collProp) is not IList list) return;
            ClearAllowDup(list);
            foreach (var item in ja.OfType<JObject>()) list.Add(build(item));
        }

        protected static JArray EmitEach(object parent, string collProp, Func<object, JObject> emit)
        {
            var arr = new JArray();
            if (Prop(parent, collProp) is IEnumerable en)
                foreach (var item in en) arr.Add(emit(item));
            return arr;
        }

        protected static void Put(JObject jo, string key, JArray arr)
        {
            if (arr.Count > 0) jo[key] = arr;
        }

        // ===================================================================
        // helpers
        // ===================================================================
        protected static object? Prop(object o, string prop) => o.GetType().GetProperty(prop)?.GetValue(o);

        protected static void ClearAllowDup(object coll)
        {
            if (coll is IList il) il.Clear();
            MetaclassJson.AllowDuplicates(coll);
        }

        protected static void EmitStr(JObject o, object source, string prop, string key)
        {
            var v = source.GetType().GetProperty(prop)?.GetValue(source) as string;
            if (!string.IsNullOrEmpty(v)) o[key] = v;
        }
    }
}
