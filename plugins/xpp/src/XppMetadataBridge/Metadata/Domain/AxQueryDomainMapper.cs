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
    /// AxQuery domain mapper, bridge-side. Scope mirrors the service domain
    /// shape: AxQuerySimple only (AxQueryComposite stays on the raw escape
    /// hatch). The data-source tree is recursive — a Root data source holds
    /// Embedded children (joins), which hold their own Embedded children,
    /// plus Derived siblings; depth unbounded.
    /// </summary>
    internal sealed class AxQueryDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxQuery";
        protected override string AccessorProperty => "Queries";
        private const string MetaNs = "Microsoft.Dynamics.AX.Metadata.MetaModel.";

        private static readonly HashSet<string> QueryStructural = new(StringComparer.OrdinalIgnoreCase)
        {
            "name", "sourceCode", "dataSources", "advanced",
        };

        protected override void ValidateRead(object meta)
        {
            if (meta is not AxQuerySimple)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams,
                    $"Only AxQuerySimple is supported in the domain layer; got i:type='{meta.GetType().Name}'. " +
                    "Use the raw xpp_update_object escape hatch for AxQueryComposite.");
        }

        protected override object BuildFromJson(JObject json) => BuildQuery(json);

        protected override object ApplyPatch(object current, JObject patch)
        {
            var simple = (AxQuerySimple)current; // ValidateRead already gated this
            ApplyQueryScalars(simple, patch);
            if (patch["advanced"] is JObject adv) AssignAll(simple, adv);
            if (patch["sourceCode"] is JObject sc) ApplySourceCode(simple, sc);
            if (patch["dataSources"] is JArray dss)
            {
                simple.DataSources.Clear();
                MetaclassJson.AllowDuplicates(simple.DataSources);
                foreach (var d in dss.OfType<JObject>()) simple.DataSources.Add(BuildDataSource(d));
            }
            return simple;
        }

        protected override JObject ReadToJson(object meta) => ToDomainJson((AxQuerySimple)meta);

        // ===================================================================
        // BUILD
        // ===================================================================
        private static AxQuerySimple BuildQuery(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxQuery name is required.");
            var ax = new AxQuerySimple { Name = name };
            ApplyQueryScalars(ax, json);
            if (json["advanced"] is JObject adv) AssignAll(ax, adv);
            if (json["sourceCode"] is JObject sc) ApplySourceCode(ax, sc);
            if (json["dataSources"] is JArray dss)
            {
                MetaclassJson.AllowDuplicates(ax.DataSources);
                foreach (var d in dss.OfType<JObject>()) ax.DataSources.Add(BuildDataSource(d));
            }
            return ax;
        }

        private static void ApplyQueryScalars(AxQuerySimple ax, JObject json)
        {
            foreach (var p in json.Properties())
            {
                if (QueryStructural.Contains(p.Name)) continue;
                MetaclassJson.Assign(ax, Pascal(p.Name), p.Value);
            }
        }

        private static void AssignAll(object target, JObject block)
        {
            foreach (var p in block.Properties())
                MetaclassJson.Assign(target, Pascal(p.Name), p.Value);
        }

        private static void ApplySourceCode(AxQuerySimple ax, JObject sc)
        {
            if (sc["methods"] is JArray methods)
            {
                ax.Methods.Clear();
                MetaclassJson.AllowDuplicates(ax.Methods);
                foreach (var m in methods.OfType<JObject>())
                {
                    var am = new AxMethod { Name = (string?)m["name"] ?? string.Empty };
                    if (m["source"] is JToken s && s.Type == JTokenType.String) am.Source = MethodSource.NormalizeIndent((string)s!);
                    ax.Methods.Add(am);
                }
            }
        }

        private static readonly HashSet<string> DsStructural = new(StringComparer.OrdinalIgnoreCase)
        {
            "name", "kind", "dataSources", "derivedDataSources", "fields", "ranges",
            "orderBy", "groupBy", "having", "relations", "dataSource",
        };

        // internal: reused by AxDataEntityViewDomainMapper for the AxQuerySimple
        // tree embedded in an entity's ViewMetadata, and by the view/entity
        // extension mappers for the AxQueryExtension* top-level data sources
        // (children always revert to the AxQuerySimple family).
        internal static AxQuerySimpleDataSource BuildDataSource(JObject json)
            => (AxQuerySimpleDataSource)BuildDataSource(json, "AxQuerySimple");

        // family-parameterized — returns object because the AxQueryExtension*
        // data-source family (used by view/entity extensions) does NOT derive
        // from AxQuerySimpleDataSource, so it can't be cast to it. Callers add
        // it to a KeyedObjectCollection via IList.Add(object). Nested children
        // always recurse through the single-arg (AxQuerySimple) overload.
        internal static object BuildDataSource(JObject json, string family)
        {
            var kind = Pascal((string?)json["kind"] ?? "Root");
            var ds = MetaclassMap.Instantiate(family + kind + "DataSource");
            MetaclassMap.SetName(ds, (string?)json["name"] ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "DataSource name is required."));
            foreach (var p in json.Properties())
            {
                if (DsStructural.Contains(p.Name)) continue;
                MetaclassJson.Assign(ds, Pascal(p.Name), p.Value);
            }

            BuildChildCollection(ds, "DataSources", json["dataSources"], BuildDataSource);
            BuildChildCollection(ds, "DerivedDataSources", json["derivedDataSources"], BuildDataSource);
            BuildChildCollection(ds, "Fields", json["fields"], BuildField);
            BuildChildCollection(ds, "Ranges", json["ranges"], BuildRange);
            BuildChildCollection(ds, "Relations", json["relations"], BuildRelation);
            BuildChildCollection(ds, "OrderBy", json["orderBy"], BuildOrderBy);
            BuildChildCollection(ds, "GroupBy", json["groupBy"], BuildGroupBy);
            BuildChildCollection(ds, "Having", json["having"], BuildHaving);

            // AxQueryExtension* data sources (view/entity extensions) wrap the
            // real query content in a nested .DataSource (an AxQuerySimple* DS).
            var nestedProp = ds.GetType().GetProperty("DataSource");
            if (nestedProp != null && json["dataSource"] is JObject nestedJson)
                nestedProp.SetValue(ds, BuildDataSource(nestedJson, "AxQuerySimple"));
            return ds;
        }

        private static void BuildChildCollection(object ds, string propName, JToken? arr, Func<JObject, object> build)
        {
            if (arr is not JArray ja) return;
            var coll = ds.GetType().GetProperty(propName)?.GetValue(ds);
            if (coll == null) return; // absent on this DS subtype (e.g. OrderBy on Embedded)
            MetaclassJson.AllowDuplicates(coll);
            var add = coll.GetType().GetMethod("Add", new[] { coll.GetType().GetGenericArguments().Length > 0 ? coll.GetType().GetGenericArguments()[0] : typeof(object) });
            foreach (var item in ja.OfType<JObject>())
            {
                var built = build(item);
                add?.Invoke(coll, new[] { built });
            }
        }

        private static object BuildField(JObject j) => BuildSimple("AxQuerySimpleDataSourceField", j,
            ("Name", "name"), ("Field", "field"), ("DerivedTable", "derivedTable"), ("Tags", "tags"));

        private static object BuildRange(JObject j) => BuildSimple("AxQuerySimpleDataSourceRange", j,
            ("Name", "name"), ("Field", "field"), ("Value", "value"), ("Status", "status"),
            ("Enabled", "enabled"), ("Label", "label"), ("DerivedTable", "derivedTable"), ("Tags", "tags"));

        private static object BuildRelation(JObject j) => BuildSimple("AxQuerySimpleDataSourceRelation", j,
            ("Name", "name"), ("Field", "field"), ("RelatedField", "relatedField"),
            ("JoinDataSource", "joinDataSource"), ("JoinRelationName", "joinRelationName"),
            ("JoinDerivedTable", "joinDerivedTable"), ("DerivedTable", "derivedTable"), ("Tags", "tags"));

        private static object BuildOrderBy(JObject j) => BuildSimple("AxQuerySimpleOrderByField", j,
            ("Name", "name"), ("Field", "field"), ("DataSource", "dataSource"), ("Direction", "direction"),
            ("DerivedTable", "derivedTable"), ("Tags", "tags"));

        private static object BuildGroupBy(JObject j) => BuildSimple("AxQuerySimpleGroupByField", j,
            ("Name", "name"), ("Field", "field"), ("DataSource", "dataSource"),
            ("DerivedTable", "derivedTable"), ("Tags", "tags"));

        private static object BuildHaving(JObject j) => BuildSimple("AxQuerySimpleHavingPredicate", j,
            ("Name", "name"), ("Field", "field"), ("DataSource", "dataSource"), ("Type", "type"),
            ("Value", "value"), ("Status", "status"), ("Enabled", "enabled"), ("Label", "label"),
            ("DerivedTable", "derivedTable"), ("Tags", "tags"));

        private static object BuildSimple(string typeName, JObject j, params (string Prop, string Key)[] map)
        {
            var inst = Instantiate<object>(typeName, $"{typeName} not found");
            foreach (var (prop, key) in map) MetaclassJson.Assign(inst, prop, j[key]);
            return inst;
        }

        // ===================================================================
        // READ
        // ===================================================================
        private static readonly (string Prop, string Key, EmitAs Kind)[] QueryScalars =
        {
            ("Title", "title", EmitAs.Raw), ("Description", "description", EmitAs.Raw),
            ("QueryType", "queryType", EmitAs.EnumCamel),
            ("AllowCrossCompany", "allowCrossCompany", EmitAs.Bool), ("AllowCheck", "allowCheck", EmitAs.Bool),
            ("Importable", "importable", EmitAs.Bool), ("Interactive", "interactive", EmitAs.Bool),
            ("Searchable", "searchable", EmitAs.Bool), ("UserUpdate", "userUpdate", EmitAs.Bool),
            ("Form", "form", EmitAs.Raw), ("Literals", "literals", EmitAs.EnumCamel),
            ("IsObsolete", "isObsolete", EmitAs.Bool), ("Tags", "tags", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] DsScalars =
        {
            ("Table", "table", EmitAs.Raw), ("Label", "label", EmitAs.Raw),
            ("DynamicFields", "dynamicFields", EmitAs.Bool), ("AllowAdd", "allowAdd", EmitAs.Bool),
            ("Enabled", "enabled", EmitAs.Bool), ("FirstOnly", "firstOnly", EmitAs.Bool),
            ("FirstFast", "firstFast", EmitAs.Bool), ("IsReadOnly", "isReadOnly", EmitAs.Bool),
            ("Update", "update", EmitAs.Bool), ("Company", "company", EmitAs.Raw),
            ("UnionType", "unionType", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw),
            ("ApplyDateFilter", "applyDateFilter", EmitAs.Bool), ("ChangeTrackingEnabled", "changeTrackingEnabled", EmitAs.Bool),
            ("ConcurrencyModel", "concurrencyModel", EmitAs.Raw), ("PolicyContext", "policyContext", EmitAs.Raw),
            ("SelectWithRepeatableRead", "selectWithRepeatableRead", EmitAs.Bool),
            ("ValidTimeStateUpdate", "validTimeStateUpdate", EmitAs.Bool),
            ("JoinMode", "joinMode", EmitAs.EnumCamel), ("FetchMode", "fetchMode", EmitAs.Raw),
            ("UseRelations", "useRelations", EmitAs.Bool),
            // AxQueryExtension* wrapper link (no-op on AxQuerySimple* DS).
            ("Parent", "parent", EmitAs.Raw),
        };

        private static JObject ToDomainJson(AxQuerySimple ax)
        {
            var jo = new JObject { ["name"] = ax.Name };
            var qRef = Reference(typeof(AxQuerySimple));
            foreach (var (prop, key, kind) in QueryScalars)
                MetaclassJson.EmitDefaulted(jo, ax, qRef, prop, key, kind);

            var vis = MetaclassJson.ReadEnumCamel(ax, "Visibility");
            if (vis != null && vis != "public") jo["advanced"] = new JObject { ["visibility"] = vis };

            // Source code.
            var methods = new JArray();
            foreach (var m in ax.Methods)
            {
                var am = (AxMethod)m;
                var mj = new JObject { ["name"] = am.Name };
                if (!string.IsNullOrEmpty(am.Source)) mj["source"] = am.Source;
                methods.Add(mj);
            }
            if (methods.Count > 0) jo["sourceCode"] = new JObject { ["methods"] = methods };

            var dss = new JArray();
            foreach (var d in ax.DataSources) dss.Add(EmitDataSource(d));
            if (dss.Count > 0) jo["dataSources"] = dss;
            return jo;
        }

        // ===================================================================
        // ViewMetadata: an AxView / AxDataEntityView ViewMetadata is a nested
        // AxQuerySimple carrying the backing data-source tree, query scalars,
        // declaration, and methods. Shared so both view + entity round-trip it
        // identically (AxView previously dropped it wholesale).
        // ===================================================================
        internal static object BuildViewMetadata(JObject vm)
        {
            var q = new AxQuerySimple();
            q.Name = (string?)vm["name"] ?? "Metadata";
            foreach (var p in vm.Properties())
            {
                if (p.Name is "name" or "declaration" or "methods" or "dataSources") continue;
                MetaclassJson.Assign(q, Pascal(p.Name), p.Value);
            }
            if (vm["declaration"] is JToken d && d.Type == JTokenType.String)
                q.GetType().GetProperty("Declaration")?.SetValue(q, (string)d!);
            if (vm["methods"] is JArray ms)
            {
                q.Methods.Clear();
                MetaclassJson.AllowDuplicates(q.Methods);
                foreach (var m in ms.OfType<JObject>())
                {
                    var am = new AxMethod { Name = (string?)m["name"] ?? string.Empty };
                    if (m["source"] is JToken s && s.Type == JTokenType.String) am.Source = MethodSource.NormalizeIndent((string)s!);
                    q.Methods.Add(am);
                }
            }
            if (vm["dataSources"] is JArray dss)
            {
                MetaclassJson.AllowDuplicates(q.DataSources);
                foreach (var ds in dss.OfType<JObject>()) q.DataSources.Add(BuildDataSource(ds));
            }
            return q;
        }

        internal static JObject? EmitViewMetadata(object meta)
        {
            var q = (AxQuerySimple)meta;
            var o = new JObject();
            var qRef = Reference(typeof(AxQuerySimple));
            foreach (var (prop, key, kind) in QueryScalars)
                MetaclassJson.EmitDefaulted(o, q, qRef, prop, key, kind);
            if (q.GetType().GetProperty("Declaration")?.GetValue(q) as string is { Length: > 0 } decl)
                o["declaration"] = decl;
            var methods = new JArray();
            foreach (var m in q.Methods)
            {
                var am = (AxMethod)m;
                var mj = new JObject { ["name"] = am.Name };
                if (!string.IsNullOrEmpty(am.Source)) mj["source"] = am.Source;
                methods.Add(mj);
            }
            if (methods.Count > 0) o["methods"] = methods;
            var dss = new JArray();
            foreach (var ds in q.DataSources) dss.Add(EmitDataSource(ds));
            if (dss.Count > 0) o["dataSources"] = dss;
            // Surface only when non-trivial (mirrors the empty-shell default).
            if (o.Count == 0 && (string.IsNullOrEmpty(q.Name) || q.Name == "Metadata")) return null;
            o["name"] = q.Name;
            return o;
        }

        internal static JObject EmitDataSource(object ds) => EmitDataSource(ds, "AxQuerySimple");

        internal static JObject EmitDataSource(object ds, string family)
        {
            var t = ds.GetType();
            var reference = Reference(t);
            var kind = t.Name.Replace(family, "").Replace("DataSource", ""); // Root/Embedded/Derived
            var o = new JObject
            {
                ["name"] = (string)t.GetProperty("Name")!.GetValue(ds)!,
                ["kind"] = MetaclassJson.ToCamel(kind),
            };
            foreach (var (prop, key, k) in DsScalars)
                MetaclassJson.EmitDefaulted(o, ds, reference, prop, key, k);

            EmitChildArray(o, ds, "DataSources", "dataSources", EmitDataSource);
            EmitChildArray(o, ds, "DerivedDataSources", "derivedDataSources", EmitDataSource);
            EmitChildArray(o, ds, "Fields", "fields", x => EmitSimple(x, FieldMap));
            EmitChildArray(o, ds, "Ranges", "ranges", x => EmitSimple(x, RangeMap));
            EmitChildArray(o, ds, "Relations", "relations", x => EmitSimple(x, RelationMap));
            EmitChildArray(o, ds, "OrderBy", "orderBy", x => EmitSimple(x, OrderByMap));
            EmitChildArray(o, ds, "GroupBy", "groupBy", x => EmitSimple(x, GroupByMap));
            EmitChildArray(o, ds, "Having", "having", x => EmitSimple(x, HavingMap));

            // Extension wrapper's nested .DataSource (the AxQuerySimple* payload).
            if (t.GetProperty("DataSource")?.GetValue(ds) is object nested)
                o["dataSource"] = EmitDataSource(nested, "AxQuerySimple");
            return o;
        }

        private static void EmitChildArray(JObject o, object ds, string prop, string key, Func<object, JObject> emit)
        {
            var coll = ds.GetType().GetProperty(prop)?.GetValue(ds);
            if (coll is not IEnumerable en) return;
            var arr = new JArray();
            foreach (var item in en) arr.Add(emit(item));
            if (arr.Count > 0) o[key] = arr;
        }

        // (Prop, Key, Kind) maps for the simple sub-records.
        private static readonly (string, string, EmitAs)[] FieldMap =
            { ("Field", "field", EmitAs.Raw), ("DerivedTable", "derivedTable", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw) };
        private static readonly (string, string, EmitAs)[] RangeMap =
            { ("Field", "field", EmitAs.Raw), ("Value", "value", EmitAs.Raw), ("Status", "status", EmitAs.EnumCamel),
              ("Enabled", "enabled", EmitAs.Bool), ("Label", "label", EmitAs.Raw), ("DerivedTable", "derivedTable", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw) };
        private static readonly (string, string, EmitAs)[] RelationMap =
            { ("Field", "field", EmitAs.Raw), ("RelatedField", "relatedField", EmitAs.Raw), ("JoinDataSource", "joinDataSource", EmitAs.Raw),
              ("JoinRelationName", "joinRelationName", EmitAs.Raw), ("JoinDerivedTable", "joinDerivedTable", EmitAs.Raw),
              ("DerivedTable", "derivedTable", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw) };
        private static readonly (string, string, EmitAs)[] OrderByMap =
            { ("Field", "field", EmitAs.Raw), ("DataSource", "dataSource", EmitAs.Raw), ("Direction", "direction", EmitAs.EnumCamel),
              ("DerivedTable", "derivedTable", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw) };
        private static readonly (string, string, EmitAs)[] GroupByMap =
            { ("Field", "field", EmitAs.Raw), ("DataSource", "dataSource", EmitAs.Raw),
              ("DerivedTable", "derivedTable", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw) };
        private static readonly (string, string, EmitAs)[] HavingMap =
            { ("Field", "field", EmitAs.Raw), ("DataSource", "dataSource", EmitAs.Raw), ("Type", "type", EmitAs.EnumCamel),
              ("Value", "value", EmitAs.Raw), ("Status", "status", EmitAs.EnumCamel), ("Enabled", "enabled", EmitAs.Bool),
              ("Label", "label", EmitAs.Raw), ("DerivedTable", "derivedTable", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw) };

        private static JObject EmitSimple(object item, (string Prop, string Key, EmitAs Kind)[] map)
        {
            var reference = Reference(item.GetType());
            var o = new JObject { ["name"] = (string)item.GetType().GetProperty("Name")!.GetValue(item)! };
            foreach (var (prop, key, kind) in map)
                MetaclassJson.EmitDefaulted(o, item, reference, prop, key, kind);
            return o;
        }

        // ===================================================================
        private static readonly Dictionary<Type, object> _refCache = new();
        private static object Reference(Type t)
        {
            if (!_refCache.TryGetValue(t, out var inst)) { inst = Activator.CreateInstance(t)!; _refCache[t] = inst; }
            return inst;
        }

        private static T Instantiate<T>(string simpleName, string errMsg) where T : class
        {
            var type = typeof(AxQuery).Assembly.GetType(MetaNs + simpleName);
            if (type == null) throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, errMsg);
            return (T)Activator.CreateInstance(type)!;
        }

        private static string Pascal(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
