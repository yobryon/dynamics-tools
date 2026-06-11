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
    /// Shared base for the AxDataEntity family — AxTable, AxView, and
    /// AxDataEntityView. MS models all three as descendants of AxDataEntity in
    /// the metamodel, and that shared ancestry is real: the three carry an
    /// identical common scalar block plus structurally-identical Relations
    /// (+ Field/Fixed/RelatedFixed constraints), FieldGroups (the very same
    /// AxTableFieldGroup element on Table and View), SourceCode (Declaration +
    /// AxMethod), and a SubscriberAccessLevel AccessGrant struct. Only the
    /// metaclass <see cref="MetaPrefix"/> differs (AxTable* / AxView* /
    /// AxDataEntityView*).
    ///
    /// This base owns that common surface. Subclasses supply:
    ///   - MetaPrefix + AccessorProperty
    ///   - StructuralKeys (the JSON keys handled by collections, not scalars)
    ///   - their own scalar emit tables (TopScalars / AdvancedScalars)
    ///   - BuildTypeSpecific / EmitTypeSpecific for the unique collections
    ///     (Table: Fields/Indexes/DeleteActions; View: Fields/Indexes/Query/
    ///     ViewMetadata; Entity: Fields/Keys/Mappings/Ranges/...).
    ///
    /// Build side blind-assigns every non-structural scalar by PascalCased
    /// name (Assign no-ops unknown/read-only props, so a superset is safe).
    /// Read side emits with reference-default suppression. Relations use a
    /// shared SUPERSET scalar table — EmitDefaulted no-ops props a given
    /// family member lacks, so one table serves all three.
    /// </summary>
    internal abstract class AxDataEntityDomainMapperBase : DomainBridgeMapperBase
    {
        /// <summary>Metaclass type-name prefix for this family member:
        /// "AxTable", "AxView", or "AxDataEntityView".</summary>
        protected abstract string MetaPrefix { get; }

        /// <summary>Top-level JSON keys handled structurally (collections /
        /// nested blocks), excluded from the blind scalar assign.</summary>
        protected abstract ISet<string> StructuralKeys { get; }

        protected abstract void BuildTypeSpecific(object ax, JObject json);
        protected abstract void EmitTypeSpecific(JObject jo, object ax);

        // Read-side scalar tables — supplied by each subclass.
        protected abstract (string Prop, string Key, EmitAs Kind)[] TopScalars { get; }
        protected abstract (string Prop, string Key, EmitAs Kind)[] AdvancedScalars { get; }

        // ===================================================================
        // Template: Create / Patch / Read flow through here.
        // ===================================================================
        protected override object BuildFromJson(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"{AxType} name is required.");
            var ax = MetaclassMap.Instantiate(MetaPrefix);
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
        // Common BUILD
        // ===================================================================
        private void BuildCommon(object ax, JObject json)
        {
            foreach (var p in json.Properties())
            {
                if (StructuralKeys.Contains(p.Name)) continue;
                MetaclassJson.Assign(ax, MetaclassMap.Pascal(p.Name), p.Value);
            }
            if (json["advanced"] is JObject adv) MetaclassMap.AssignAll(ax, adv);
            if (json["relations"] is JArray rels) BuildRelationsInto(MetaPrefix, ax, rels);
            if (json["fieldGroups"] is JArray fgs) BuildFieldGroupsInto(ax, fgs);
            if (json["sourceCode"] is JObject sc) BuildSourceCode(ax, sc);
            if (json["subscriberAccessLevel"] is JObject sal) MetaclassMap.ApplySubscriberAccess(ax, sal);
        }

        // ===================================================================
        // Common READ
        // ===================================================================
        private void EmitCommon(JObject jo, object ax)
        {
            var reference = MetaclassMap.Reference(ax.GetType());
            foreach (var (prop, key, kind) in TopScalars)
                MetaclassJson.EmitDefaulted(jo, ax, reference, prop, key, kind);

            // Advanced block (+ visibility, which is its own enum member).
            var adv = new JObject();
            foreach (var (prop, key, kind) in AdvancedScalars)
                MetaclassJson.EmitDefaulted(adv, ax, reference, prop, key, kind);
            var vis = MetaclassJson.ReadEnumCamel(ax, "Visibility");
            if (adv.Count > 0 || (vis != null && vis != "public"))
            {
                adv["visibility"] = vis ?? "public";
                jo["advanced"] = adv;
            }

            var rels = EmitRelationsFrom(MetaPrefix, ax);
            if (rels.Count > 0) jo["relations"] = rels;
            var fgs = EmitFieldGroupsFrom(ax);
            if (fgs.Count > 0) jo["fieldGroups"] = fgs;
            var sc = EmitSourceCode(ax);
            if (sc.Count > 0) jo["sourceCode"] = sc;
            var sal = MetaclassMap.EmitSubscriberAccess(ax);
            if (sal.Count > 0) jo["subscriberAccessLevel"] = sal;
        }

        // ===================================================================
        // Relations (+ constraints) — shared, prefix-parameterized.
        // ===================================================================
        // Superset scalar table; EmitDefaulted no-ops props a member lacks.
        protected static readonly (string Prop, string Key, EmitAs Kind)[] RelationScalars =
        {
            ("RelatedTable", "relatedTable", EmitAs.Raw), ("RelationshipType", "relationshipType", EmitAs.Raw),
            ("Cardinality", "cardinality", EmitAs.EnumCamel), ("RelatedTableCardinality", "relatedTableCardinality", EmitAs.EnumCamel),
            ("Role", "role", EmitAs.Raw), ("RelatedTableRole", "relatedTableRole", EmitAs.Raw),
            ("OnDelete", "onDelete", EmitAs.EnumCamel), ("Validate", "validate", EmitAs.Bool),
            ("CreateNavigationPropertyMethods", "createNavigationPropertyMethods", EmitAs.Bool),
            ("UseDefaultRoleNames", "useDefaultRoleNames", EmitAs.Bool),
            ("EDTRelation", "eDTRelation", EmitAs.Raw), ("Index", "index", EmitAs.Raw),
            ("EntityRelationshipRole", "entityRelationshipRole", EmitAs.Raw), ("Key", "key", EmitAs.Raw),
            ("NavigationPropertyMethodNameOverride", "navigationPropertyMethodNameOverride", EmitAs.Raw),
            // AxDataEntityViewRelation-specific (no-op on Table/View relations).
            ("RelatedDataEntity", "relatedDataEntity", EmitAs.Raw),
            ("RelatedDataEntityCardinality", "relatedDataEntityCardinality", EmitAs.EnumCamel),
            ("RelatedDataEntityRole", "relatedDataEntityRole", EmitAs.Raw),
            ("Tags", "tags", EmitAs.Raw),
        };

        // internal static + prefix-parameterized so the extension mappers
        // (AxTableExtension etc.) can reuse the relation/constraint shape.
        internal static void BuildRelationsInto(string prefix, object parent, JArray relations)
        {
            var coll = parent.GetType().GetProperty("Relations")?.GetValue(parent);
            if (coll is not IList list) return;
            ClearAllowDup(coll);
            var relType = prefix + "Relation";
            var fkType = relType + "ForeignKey";
            foreach (var rj in relations.OfType<JObject>())
            {
                var isFk = rj["isForeignKey"]?.Type == JTokenType.Boolean && (bool)rj["isForeignKey"]!;
                var rel = MetaclassMap.Instantiate(isFk ? fkType : relType);
                MetaclassMap.SetName(rel, (string?)rj["name"]
                    ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Relation name is required."));
                foreach (var p in rj.Properties())
                {
                    if (p.Name is "name" or "isForeignKey" or "constraints") continue;
                    MetaclassJson.Assign(rel, MetaclassMap.Pascal(p.Name), p.Value);
                }
                if (rj["constraints"] is JArray cons) BuildConstraintsInto(prefix, rel, "Constraints", cons);
                list.Add(rel);
            }
        }

        internal static void BuildConstraintsInto(string prefix, object parent, string collProp, JArray cons)
        {
            var coll = parent.GetType().GetProperty(collProp)?.GetValue(parent);
            if (coll is not IList list) return;
            ClearAllowDup(coll);
            foreach (var cj in cons.OfType<JObject>())
            {
                var type = MetaclassMap.Pascal((string?)cj["type"] ?? "Field");
                var c = MetaclassMap.Instantiate(prefix + "RelationConstraint" + type);
                MetaclassMap.SetName(c, (string?)cj["name"] ?? string.Empty);
                // Assign the union of constraint-subtype members; Assign no-ops
                // the ones absent on this subtype.
                MetaclassJson.Assign(c, "Field", cj["field"]);
                MetaclassJson.Assign(c, "RelatedField", cj["relatedField"]);
                MetaclassJson.Assign(c, "SourceEDT", cj["sourceEDT"]);
                MetaclassJson.Assign(c, "Value", cj["value"]);
                MetaclassJson.Assign(c, "ValueStr", cj["valueStr"]);
                MetaclassJson.Assign(c, "Tags", cj["tags"]);
                list.Add(c);
            }
        }

        internal static JArray EmitRelationsFrom(string prefix, object parent)
        {
            var arr = new JArray();
            if (parent.GetType().GetProperty("Relations")?.GetValue(parent) is not IEnumerable en) return arr;
            foreach (var rel in en)
            {
                var reference = MetaclassMap.Reference(rel.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(rel) };
                if (rel.GetType().Name.EndsWith("ForeignKey")) o["isForeignKey"] = true;
                foreach (var (prop, key, kind) in RelationScalars)
                    MetaclassJson.EmitDefaulted(o, rel, reference, prop, key, kind);
                var cons = EmitConstraintsFrom(prefix, rel, "Constraints");
                if (cons.Count > 0) o["constraints"] = cons;
                arr.Add(o);
            }
            return arr;
        }

        internal static JArray EmitConstraintsFrom(string prefix, object parent, string collProp)
        {
            var arr = new JArray();
            if (parent.GetType().GetProperty(collProp)?.GetValue(parent) is not IEnumerable en) return arr;
            var ctorPrefix = prefix + "RelationConstraint";
            foreach (var c in en)
            {
                var typeName = c.GetType().Name;
                var sub = typeName.StartsWith(ctorPrefix) ? typeName.Substring(ctorPrefix.Length) : typeName;
                var o = new JObject { ["name"] = MetaclassMap.GetName(c), ["type"] = MetaclassJson.ToCamel(sub) };
                EmitStr(o, c, "Field", "field");
                EmitStr(o, c, "RelatedField", "relatedField");
                EmitStr(o, c, "SourceEDT", "sourceEDT");
                EmitStr(o, c, "ValueStr", "valueStr");
                EmitStr(o, c, "Tags", "tags");
                // Value is int on Fixed/RelatedFixed, string on Table/RelatedTable.
                var vp = c.GetType().GetProperty("Value");
                if (vp != null)
                {
                    var v = vp.GetValue(c);
                    if (vp.PropertyType == typeof(int)) { if (v is int n && n != 0) o["value"] = n.ToString(); }
                    else if (v is string s && s.Length > 0) o["value"] = s;
                }
                arr.Add(o);
            }
            return arr;
        }

        // ===================================================================
        // Mappings — AxTableMapping { Name, MappingTable, Tags, Connections
        // (AxTableMappingConnection { MapField, MapFieldTo, Tags }) }. Shared by
        // AxTable + AxDataEntityView (both expose a Mappings collection).
        // ===================================================================
        internal static void BuildMappingsInto(object ax, JArray maps)
        {
            var coll = ax.GetType().GetProperty("Mappings")?.GetValue(ax);
            if (coll is not IList list) return;
            ClearAllowDup(coll);
            foreach (var mj in maps.OfType<JObject>())
            {
                var m = MetaclassMap.Instantiate("AxTableMapping");
                MetaclassMap.SetName(m, (string?)mj["name"] ?? string.Empty);
                MetaclassJson.Assign(m, "MappingTable", mj["mappingTable"]);
                MetaclassJson.Assign(m, "Tags", mj["tags"]);
                if (mj["connections"] is JArray cons && m.GetType().GetProperty("Connections")?.GetValue(m) is IList cl)
                {
                    ClearAllowDup((object)cl);
                    foreach (var cj in cons.OfType<JObject>())
                    {
                        var c = MetaclassMap.Instantiate("AxTableMappingConnection");
                        MetaclassJson.Assign(c, "MapField", cj["mapField"]);
                        MetaclassJson.Assign(c, "MapFieldTo", cj["mapFieldTo"]);
                        MetaclassJson.Assign(c, "Tags", cj["tags"]);
                        cl.Add(c);
                    }
                }
                list.Add(m);
            }
        }

        internal static JArray EmitMappingsFrom(object ax)
        {
            var arr = new JArray();
            if (ax.GetType().GetProperty("Mappings")?.GetValue(ax) is not IEnumerable en) return arr;
            foreach (var m in en)
            {
                var reference = MetaclassMap.Reference(m.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(m) };
                MetaclassJson.EmitDefaulted(o, m, reference, "MappingTable", "mappingTable", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, m, reference, "Tags", "tags", EmitAs.Raw);
                var cons = new JArray();
                if (m.GetType().GetProperty("Connections")?.GetValue(m) is IEnumerable ce)
                    foreach (var c in ce)
                    {
                        var cRef = MetaclassMap.Reference(c.GetType());
                        var co = new JObject();
                        MetaclassJson.EmitDefaulted(co, c, cRef, "MapField", "mapField", EmitAs.Raw);
                        MetaclassJson.EmitDefaulted(co, c, cRef, "MapFieldTo", "mapFieldTo", EmitAs.Raw);
                        MetaclassJson.EmitDefaulted(co, c, cRef, "Tags", "tags", EmitAs.Raw);
                        cons.Add(co);
                    }
                if (cons.Count > 0) o["connections"] = cons;
                arr.Add(o);
            }
            return arr;
        }

        // ===================================================================
        // FieldGroups — the on-disk element is AxTableFieldGroup for every
        // family member (shared between tables and views by MS).
        // ===================================================================
        internal static void BuildFieldGroupsInto(object ax, JArray groups)
        {
            var coll = ax.GetType().GetProperty("FieldGroups")?.GetValue(ax);
            if (coll is not IList list) return;
            ClearAllowDup(coll);
            foreach (var gj in groups.OfType<JObject>())
            {
                var g = MetaclassMap.Instantiate("AxTableFieldGroup");
                MetaclassMap.SetName(g, (string?)gj["name"]
                    ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "FieldGroup name is required."));
                MetaclassJson.Assign(g, "Label", gj["label"]);
                MetaclassJson.Assign(g, "AutoPopulate", gj["autoPopulate"]);
                MetaclassJson.Assign(g, "Tags", gj["tags"]);
                if (gj["fields"] is JArray fields && g.GetType().GetProperty("Fields")?.GetValue(g) is IList fl)
                {
                    ClearAllowDup((object)fl);
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

        internal static JArray EmitFieldGroupsFrom(object ax)
        {
            var arr = new JArray();
            if (ax.GetType().GetProperty("FieldGroups")?.GetValue(ax) is not IEnumerable en) return arr;
            foreach (var g in en)
            {
                var reference = MetaclassMap.Reference(g.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(g) };
                MetaclassJson.EmitDefaulted(o, g, reference, "Label", "label", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, g, reference, "AutoPopulate", "autoPopulate", EmitAs.Bool);
                MetaclassJson.EmitDefaulted(o, g, reference, "Tags", "tags", EmitAs.Raw);
                var fields = new JArray();
                if (g.GetType().GetProperty("Fields")?.GetValue(g) is IEnumerable fe)
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
        // SourceCode — Declaration string + Methods (AxMethod).
        // ===================================================================
        protected void BuildSourceCode(object ax, JObject sc)
        {
            if (sc["declaration"] is JToken d && d.Type == JTokenType.String)
                ax.GetType().GetProperty("Declaration")?.SetValue(ax, (string)d!);
            if (sc["methods"] is JArray methods)
            {
                var coll = ax.GetType().GetProperty("Methods")?.GetValue(ax);
                if (coll != null)
                {
                    if (coll is IList il) il.Clear();
                    MetaclassMap.AddMethods(coll, methods);
                }
            }
        }

        protected JObject EmitSourceCode(object ax)
        {
            var sc = new JObject();
            var decl = ax.GetType().GetProperty("Declaration")?.GetValue(ax) as string;
            if (!string.IsNullOrEmpty(decl)) sc["declaration"] = decl;
            var methods = MetaclassMap.EmitMethods(ax.GetType().GetProperty("Methods")?.GetValue(ax));
            if (methods.Count > 0) sc["methods"] = methods;
            return sc;
        }

        // ===================================================================
        // helpers
        // ===================================================================
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
