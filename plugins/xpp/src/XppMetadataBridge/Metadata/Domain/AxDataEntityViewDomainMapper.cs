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
    /// AxDataEntityView — the OData/DMF-writable layer over a table or join.
    /// The richest AxDataEntity-family member: three-tier metamodel ancestry
    /// (AxDataEntity → AxDataEntityViewBase → AxDataEntityView). The common
    /// surface (scalars, relations, field groups, source code, subscriber
    /// access) is inherited from <see cref="AxDataEntityDomainMapperBase"/>.
    ///
    /// This subclass owns the entity-unique collections: polymorphic Fields
    /// (AxDataEntityViewMappedField / AxDataEntityViewUnmappedField{String|
    /// Int|Int64|Real|Date|Enum|UtcDateTime|Time|Guid|Container}), Keys,
    /// Ranges, DeleteActions, and — distinctively — ViewMetadata, which (unlike
    /// AxView's empty shell) carries the full backing AxQuerySimple data-source
    /// tree. That tree is the exact shape AxQueryDomainMapper already handles,
    /// so we reuse its BuildDataSource / EmitDataSource recursion.
    /// </summary>
    internal sealed class AxDataEntityViewDomainMapper : AxDataEntityDomainMapperBase
    {
        public override string AxType => "AxDataEntityView";
        protected override string AccessorProperty => "DataEntityViews";
        protected override string MetaPrefix => "AxDataEntityView";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "fields", "keys", "ranges", "relations", "fieldGroups", "deleteActions",
            "mappings", "sourceCode", "subscriberAccessLevel", "advanced", "viewMetadata",
        };

        // ===================================================================
        // Type-specific BUILD.
        // ===================================================================
        protected override void BuildTypeSpecific(object ax, JObject json)
        {
            if (json["fields"] is JArray fields && Coll(ax, "Fields") is IList fl)
            {
                ClearAllowDup(fl);
                foreach (var f in fields.OfType<JObject>()) fl.Add(BuildField(f));
            }
            if (json["keys"] is JArray keys && Coll(ax, "Keys") is IList kl)
            {
                ClearAllowDup(kl);
                foreach (var k in keys.OfType<JObject>()) kl.Add(BuildKey(k));
            }
            if (json["ranges"] is JArray ranges && Coll(ax, "Ranges") is IList rl)
            {
                ClearAllowDup(rl);
                foreach (var r in ranges.OfType<JObject>()) rl.Add(BuildRange(r));
            }
            if (json["deleteActions"] is JArray das && Coll(ax, "DeleteActions") is IList dl)
            {
                ClearAllowDup(dl);
                foreach (var d in das.OfType<JObject>()) dl.Add(BuildDeleteAction(d));
            }
            if (json["mappings"] is JArray maps) BuildMappingsInto(ax, maps);
            if (json["viewMetadata"] is JObject vm)
                ax.GetType().GetProperty("ViewMetadata")?.SetValue(ax, AxQueryDomainMapper.BuildViewMetadata(vm));
        }


        internal static object BuildField(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Entity field name is required.");
            var f = MetaclassMap.Instantiate(FieldTypeForKind((string?)json["kind"] ?? "mapped"));
            MetaclassMap.SetName(f, name);
            // Blind-assign every scalar; Assign no-ops members absent on the
            // chosen subtype (e.g. DataField on an Unmapped field).
            foreach (var p in json.Properties())
            {
                if (p.Name is "name" or "kind") continue;
                MetaclassJson.Assign(f, MetaclassMap.Pascal(p.Name), p.Value);
            }
            return f;
        }

        private static string FieldTypeForKind(string kind)
        {
            if (kind.Equals("mapped", StringComparison.OrdinalIgnoreCase))
                return "AxDataEntityViewMappedField";
            // unmappedString -> AxDataEntityViewUnmappedFieldString
            var prim = MetaclassMap.Pascal(kind).Substring("Unmapped".Length);
            return "AxDataEntityViewUnmappedField" + prim;
        }

        private static object BuildKey(JObject json)
        {
            var k = MetaclassMap.Instantiate("AxDataEntityViewKey");
            MetaclassMap.SetName(k, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Entity key name is required."));
            MetaclassJson.Assign(k, "Tags", json["tags"]);
            if (json["fields"] is JArray fields && Coll(k, "Fields") is IList fl)
            {
                ClearAllowDup(fl);
                foreach (var jf in fields.OfType<JObject>())
                {
                    var kf = MetaclassMap.Instantiate("AxDataEntityViewKeyField");
                    kf.GetType().GetProperty("DataField")?.SetValue(kf, (string?)jf["dataField"] ?? string.Empty);
                    fl.Add(kf);
                }
            }
            return k;
        }

        private static object BuildRange(JObject json)
        {
            var r = MetaclassMap.Instantiate("AxDataEntityViewRange");
            MetaclassMap.SetName(r, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Entity range name is required."));
            foreach (var p in json.Properties())
            {
                if (p.Name == "name") continue;
                MetaclassJson.Assign(r, MetaclassMap.Pascal(p.Name), p.Value);
            }
            return r;
        }

        private static object BuildDeleteAction(JObject json)
        {
            var d = MetaclassMap.Instantiate("AxDataEntityViewDeleteAction");
            MetaclassJson.Assign(d, "Name", json["name"]);
            MetaclassJson.Assign(d, "DeleteAction", json["deleteAction"]);
            MetaclassJson.Assign(d, "Relation", json["relation"]);
            MetaclassJson.Assign(d, "Table", json["table"]);
            MetaclassJson.Assign(d, "Tags", json["tags"]);
            return d;
        }


        // ===================================================================
        // Type-specific READ.
        // ===================================================================
        protected override void EmitTypeSpecific(JObject jo, object ax)
        {
            var fields = new JArray();
            if (Coll(ax, "Fields") is IEnumerable fe) foreach (var f in fe) fields.Add(EmitField(f));
            if (fields.Count > 0) jo["fields"] = fields;

            var keys = new JArray();
            if (Coll(ax, "Keys") is IEnumerable ke) foreach (var k in ke) keys.Add(EmitKey(k));
            if (keys.Count > 0) jo["keys"] = keys;

            var ranges = new JArray();
            if (Coll(ax, "Ranges") is IEnumerable re) foreach (var r in re) ranges.Add(EmitRange(r));
            if (ranges.Count > 0) jo["ranges"] = ranges;

            var das = new JArray();
            if (Coll(ax, "DeleteActions") is IEnumerable de) foreach (var d in de) das.Add(EmitDeleteAction(d));
            if (das.Count > 0) jo["deleteActions"] = das;

            var maps = EmitMappingsFrom(ax);
            if (maps.Count > 0) jo["mappings"] = maps;

            if (ax.GetType().GetProperty("ViewMetadata")?.GetValue(ax) is object vm
                && AxQueryDomainMapper.EmitViewMetadata(vm) is JObject vmJson)
                jo["viewMetadata"] = vmJson;
        }

        internal static JObject EmitField(object f)
        {
            var reference = MetaclassMap.Reference(f.GetType());
            var typeName = f.GetType().Name; // AxDataEntityViewMappedField / ...UnmappedFieldString
            var isMapped = typeName == "AxDataEntityViewMappedField";
            var kind = isMapped ? "mapped"
                : "unmapped" + typeName.Substring("AxDataEntityViewUnmappedField".Length);
            var o = new JObject { ["name"] = MetaclassMap.GetName(f), ["kind"] = kind };
            foreach (var (prop, key, k) in FieldCommonScalars)
                MetaclassJson.EmitDefaulted(o, f, reference, prop, key, k);
            if (isMapped)
            {
                foreach (var (prop, key, k) in FieldMappedScalars)
                    MetaclassJson.EmitDefaulted(o, f, reference, prop, key, k);
            }
            else
            {
                foreach (var (prop, key, k) in FieldUnmappedScalars)
                    MetaclassJson.EmitDefaulted(o, f, reference, prop, key, k);
                if (typeName == "AxDataEntityViewUnmappedFieldString")
                {
                    MetaclassJson.EmitDefaulted(o, f, reference, "Adjustment", "adjustment", EmitAs.EnumCamel);
                    MetaclassJson.EmitDefaulted(o, f, reference, "StringSize", "stringSize", EmitAs.Int);
                }
            }
            return o;
        }

        private static JObject EmitKey(object k)
        {
            var reference = MetaclassMap.Reference(k.GetType());
            var o = new JObject { ["name"] = MetaclassMap.GetName(k) };
            MetaclassJson.EmitDefaulted(o, k, reference, "Tags", "tags", EmitAs.Raw);
            var fields = new JArray();
            if (Coll(k, "Fields") is IEnumerable fe)
                foreach (var kf in fe)
                {
                    var df = kf.GetType().GetProperty("DataField")?.GetValue(kf) as string;
                    if (!string.IsNullOrEmpty(df)) fields.Add(new JObject { ["dataField"] = df });
                }
            if (fields.Count > 0) o["fields"] = fields;
            return o;
        }

        private static JObject EmitRange(object r)
        {
            var reference = MetaclassMap.Reference(r.GetType());
            var o = new JObject { ["name"] = MetaclassMap.GetName(r) };
            foreach (var (prop, key, kind) in RangeScalars)
                MetaclassJson.EmitDefaulted(o, r, reference, prop, key, kind);
            return o;
        }

        private static JObject EmitDeleteAction(object d)
        {
            var reference = MetaclassMap.Reference(d.GetType());
            var o = new JObject();
            var nm = MetaclassMap.GetName(d);
            if (!string.IsNullOrEmpty(nm)) o["name"] = nm;
            MetaclassJson.EmitDefaulted(o, d, reference, "DeleteAction", "deleteAction", EmitAs.EnumCamel);
            EmitStr(o, d, "Relation", "relation");
            EmitStr(o, d, "Table", "table");
            EmitStr(o, d, "Tags", "tags");
            return o;
        }

        private static object? Coll(object o, string prop) => o.GetType().GetProperty(prop)?.GetValue(o);

        // ===================================================================
        // Scalar emit tables.
        // ===================================================================
        protected override (string Prop, string Key, EmitAs Kind)[] TopScalars => EntityScalars;
        protected override (string Prop, string Key, EmitAs Kind)[] AdvancedScalars => EntityAdvancedScalars;

        private static readonly (string Prop, string Key, EmitAs Kind)[] EntityScalars =
        {
            // AxDataEntity base
            ("Label", "label", EmitAs.Raw), ("SingularLabel", "singularLabel", EmitAs.Raw),
            ("DeveloperDocumentation", "developerDocumentation", EmitAs.Raw),
            ("TableGroup", "tableGroup", EmitAs.Raw), ("ConfigurationKey", "configurationKey", EmitAs.Raw),
            ("CountryRegionCodes", "countryRegionCodes", EmitAs.Raw), ("IsObsolete", "isObsolete", EmitAs.Bool),
            ("FormRef", "formRef", EmitAs.Raw), ("ListPageRef", "listPageRef", EmitAs.Raw),
            ("PreviewPartRef", "previewPartRef", EmitAs.Raw), ("OperationalDomain", "operationalDomain", EmitAs.Raw),
            ("ReportRef", "reportRef", EmitAs.Raw), ("EntityRelationshipType", "entityRelationshipType", EmitAs.Raw),
            ("TitleField1", "titleField1", EmitAs.Raw), ("TitleField2", "titleField2", EmitAs.Raw),
            // AxDataEntityViewBase
            ("PublicEntityName", "publicEntityName", EmitAs.Raw), ("PublicCollectionName", "publicCollectionName", EmitAs.Raw),
            ("PrimaryKey", "primaryKey", EmitAs.Raw), ("PrimaryCompanyContext", "primaryCompanyContext", EmitAs.Raw),
            ("IsPublic", "isPublic", EmitAs.Bool), ("IsReadOnly", "isReadOnly", EmitAs.Bool),
            ("DataManagementEnabled", "dataManagementEnabled", EmitAs.Bool),
            ("DataManagementStagingTable", "dataManagementStagingTable", EmitAs.Raw),
            ("SupportsSetBasedSqlOperations", "supportsSetBasedSqlOperations", EmitAs.Bool),
            ("EnableSetBasedSqlOperations", "enableSetBasedSqlOperations", EmitAs.Bool),
            ("EntityCategory", "entityCategory", EmitAs.Raw), ("AosAuthorization", "aosAuthorization", EmitAs.Raw),
            ("MessagingRole", "messagingRole", EmitAs.Raw), ("Modules", "modules", EmitAs.Raw),
            ("ValidTimeStateEnabled", "validTimeStateEnabled", EmitAs.Bool),
            ("AllowArchival", "allowArchival", EmitAs.Bool), ("AllowRetention", "allowRetention", EmitAs.Bool),
            ("AllowRowVersionChangeTracking", "allowRowVersionChangeTracking", EmitAs.Bool),
            ("AutoCreateDataverse", "autoCreateDataverse", EmitAs.Bool),
            ("EnableDataverseSearch", "enableDataverseSearch", EmitAs.Bool),
            ("Query", "query", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] EntityAdvancedScalars =
        {
            ("Visible", "visible", EmitAs.Bool),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] FieldCommonScalars =
        {
            ("AccessModifier", "accessModifier", EmitAs.Raw), ("AllowEdit", "allowEdit", EmitAs.Bool),
            ("AllowEditOnCreate", "allowEditOnCreate", EmitAs.Bool), ("ConfigurationKey", "configurationKey", EmitAs.Raw),
            ("CountryRegionCodes", "countryRegionCodes", EmitAs.Raw),
            ("CountryRegionContextField", "countryRegionContextField", EmitAs.Raw),
            ("FeatureClass", "featureClass", EmitAs.Raw), ("GroupPrompt", "groupPrompt", EmitAs.Raw),
            ("HelpText", "helpText", EmitAs.Raw), ("IsObsolete", "isObsolete", EmitAs.Bool),
            ("Label", "label", EmitAs.Raw), ("Mandatory", "mandatory", EmitAs.Bool),
            ("RelationContext", "relationContext", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] FieldMappedScalars =
        {
            ("Aggregation", "aggregation", EmitAs.Raw), ("DataField", "dataField", EmitAs.Raw),
            ("DataSource", "dataSource", EmitAs.Raw),
            ("DimensionLegalEntityContextField", "dimensionLegalEntityContextField", EmitAs.Raw),
            ("DynamicDimensionEnumerationField", "dynamicDimensionEnumerationField", EmitAs.Raw),
            ("EnableDataverseSearch", "enableDataverseSearch", EmitAs.Bool),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] FieldUnmappedScalars =
        {
            ("ComputedFieldMethod", "computedFieldMethod", EmitAs.Raw),
            ("ExtendedDataType", "extendedDataType", EmitAs.Raw),
            ("EnumType", "enumType", EmitAs.Raw),
            ("IsComputedField", "isComputedField", EmitAs.Bool),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] RangeScalars =
        {
            ("Field", "field", EmitAs.Raw), ("Value", "value", EmitAs.Raw),
            ("Status", "status", EmitAs.EnumCamel), ("Enabled", "enabled", EmitAs.Bool),
            ("Label", "label", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw),
        };
    }
}
