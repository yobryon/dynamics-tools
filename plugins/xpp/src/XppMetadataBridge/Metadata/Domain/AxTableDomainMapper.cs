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
    /// AxTable — the richest AxDataEntity-family member. The common surface
    /// (scalars, relations + constraints, field groups, source code,
    /// subscriber access) lives in <see cref="AxDataEntityDomainMapperBase"/>.
    /// This subclass owns only what's unique to tables: the polymorphic Fields
    /// collection (10 AxTableField subtypes), Indexes, and DeleteActions, plus
    /// the table-specific scalar emit tables.
    /// </summary>
    internal sealed class AxTableDomainMapper : AxDataEntityDomainMapperBase
    {
        public override string AxType => "AxTable";
        protected override string AccessorProperty => "Tables";
        protected override string MetaPrefix => "AxTable";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "fields", "indexes", "relations", "fieldGroups", "deleteActions",
            "mappings", "sourceCode", "subscriberAccessLevel", "advanced",
        };

        // ===================================================================
        // Type-specific BUILD — Fields, Indexes, DeleteActions.
        // ===================================================================
        protected override void BuildTypeSpecific(object ax, JObject json)
        {
            if (json["fields"] is JArray fields && Coll(ax, "Fields") is IList fl)
            {
                ClearAllowDup(fl);
                foreach (var f in fields.OfType<JObject>()) fl.Add(BuildField(f));
            }
            if (json["indexes"] is JArray indexes && Coll(ax, "Indexes") is IList il)
            {
                ClearAllowDup(il);
                foreach (var i in indexes.OfType<JObject>()) il.Add(BuildIndex(i));
            }
            if (json["deleteActions"] is JArray das && Coll(ax, "DeleteActions") is IList dl)
            {
                ClearAllowDup(dl);
                foreach (var d in das.OfType<JObject>()) dl.Add(BuildDeleteAction(d));
            }
            if (json["mappings"] is JArray maps) BuildMappingsInto(ax, maps);
        }

        internal static object BuildField(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Field name is required.");
            var fieldType = MetaclassMap.Pascal((string?)json["fieldType"] ?? "String");
            var f = MetaclassMap.Instantiate("AxTableField" + fieldType);
            MetaclassMap.SetName(f, name);
            foreach (var p in json.Properties())
            {
                if (p.Name is "name" or "fieldType" or "advanced") continue;
                MetaclassJson.Assign(f, MetaclassMap.Pascal(p.Name), p.Value);
            }
            if (json["advanced"] is JObject adv) MetaclassMap.AssignAll(f, adv);
            return f;
        }

        internal static object BuildIndex(JObject json)
        {
            var idx = MetaclassMap.Instantiate("AxTableIndex");
            MetaclassMap.SetName(idx, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Index name is required."));
            foreach (var p in json.Properties())
            {
                if (p.Name is "name" or "fields") continue;
                MetaclassJson.Assign(idx, MetaclassMap.Pascal(p.Name), p.Value);
            }
            if (json["fields"] is JArray fields && Coll(idx, "Fields") is IList fl)
            {
                ClearAllowDup(fl);
                foreach (var jf in fields.OfType<JObject>())
                {
                    var ixf = MetaclassMap.Instantiate("AxTableIndexField");
                    ixf.GetType().GetProperty("DataField")?.SetValue(ixf, (string?)jf["dataField"] ?? string.Empty);
                    MetaclassJson.Assign(ixf, "IncludedColumn", jf["includedColumn"]);
                    MetaclassJson.Assign(ixf, "Optional", jf["optional"]);
                    fl.Add(ixf);
                }
            }
            return idx;
        }

        private static object BuildDeleteAction(JObject json)
        {
            var d = MetaclassMap.Instantiate("AxTableDeleteAction");
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

            var indexes = new JArray();
            if (Coll(ax, "Indexes") is IEnumerable ie) foreach (var i in ie) indexes.Add(EmitIndex(i));
            if (indexes.Count > 0) jo["indexes"] = indexes;

            var das = new JArray();
            if (Coll(ax, "DeleteActions") is IEnumerable de) foreach (var d in de) das.Add(EmitDeleteAction(d));
            if (das.Count > 0) jo["deleteActions"] = das;

            var maps = EmitMappingsFrom(ax);
            if (maps.Count > 0) jo["mappings"] = maps;
        }

        internal static JObject EmitField(object f)
        {
            var reference = MetaclassMap.Reference(f.GetType());
            var o = new JObject
            {
                ["name"] = MetaclassMap.GetName(f),
                ["fieldType"] = MetaclassJson.ToCamel(f.GetType().Name.Substring("AxTableField".Length)),
            };
            foreach (var (prop, key, kind) in FieldScalars)
                MetaclassJson.EmitDefaulted(o, f, reference, prop, key, kind);
            var adv = new JObject();
            foreach (var (prop, key, kind) in FieldAdvancedScalars)
                MetaclassJson.EmitDefaulted(adv, f, reference, prop, key, kind);
            if (adv.Count > 0) o["advanced"] = adv;
            return o;
        }

        internal static JObject EmitIndex(object idx)
        {
            var reference = MetaclassMap.Reference(idx.GetType());
            var o = new JObject { ["name"] = MetaclassMap.GetName(idx) };
            foreach (var (prop, key, kind) in IndexScalars)
                MetaclassJson.EmitDefaulted(o, idx, reference, prop, key, kind);
            var fields = new JArray();
            if (Coll(idx, "Fields") is IEnumerable fe)
                foreach (var ixf in fe)
                {
                    var ixfRef = MetaclassMap.Reference(ixf.GetType());
                    var fo = new JObject { ["dataField"] = ixf.GetType().GetProperty("DataField")?.GetValue(ixf) as string ?? string.Empty };
                    MetaclassJson.EmitDefaulted(fo, ixf, ixfRef, "IncludedColumn", "includedColumn", EmitAs.Bool);
                    MetaclassJson.EmitDefaulted(fo, ixf, ixfRef, "Optional", "optional", EmitAs.Bool);
                    fields.Add(fo);
                }
            if (fields.Count > 0) o["fields"] = fields;
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
        protected override (string Prop, string Key, EmitAs Kind)[] TopScalars => TableScalars;
        protected override (string Prop, string Key, EmitAs Kind)[] AdvancedScalars => TableAdvancedScalars;

        private static readonly (string Prop, string Key, EmitAs Kind)[] TableScalars =
        {
            ("Label", "label", EmitAs.Raw), ("SingularLabel", "singularLabel", EmitAs.Raw),
            ("DeveloperDocumentation", "developerDocumentation", EmitAs.Raw),
            ("Extends", "extends", EmitAs.Raw), ("TableGroup", "tableGroup", EmitAs.Raw),
            ("TableType", "tableType", EmitAs.EnumCamel), ("TableContents", "tableContents", EmitAs.EnumCamel),
            ("CacheLookup", "cacheLookup", EmitAs.EnumCamel),
            ("PrimaryIndex", "primaryIndex", EmitAs.Raw), ("ClusteredIndex", "clusteredIndex", EmitAs.Raw),
            ("SaveDataPerCompany", "saveDataPerCompany", EmitAs.Bool),
            ("SaveDataPerPartition", "saveDataPerPartition", EmitAs.Bool),
            ("TitleField1", "titleField1", EmitAs.Raw), ("TitleField2", "titleField2", EmitAs.Raw),
            ("ConfigurationKey", "configurationKey", EmitAs.Raw), ("CountryRegionCodes", "countryRegionCodes", EmitAs.Raw),
            ("Modules", "modules", EmitAs.Raw), ("Tags", "tags", EmitAs.Raw),
            ("IsObsolete", "isObsolete", EmitAs.Bool), ("CreateRecIdIndex", "createRecIdIndex", EmitAs.Bool),
            ("ReplacementKey", "replacementKey", EmitAs.Raw), ("FormRef", "formRef", EmitAs.Raw),
            ("ListPageRef", "listPageRef", EmitAs.Raw), ("PreviewPartRef", "previewPartRef", EmitAs.Raw),
            ("OperationalDomain", "operationalDomain", EmitAs.Raw),
            ("CreatedBy", "createdBy", EmitAs.Bool), ("CreatedDateTime", "createdDateTime", EmitAs.Bool),
            ("ModifiedBy", "modifiedBy", EmitAs.Bool), ("ModifiedDateTime", "modifiedDateTime", EmitAs.Bool),
            ("CreatedTransactionId", "createdTransactionId", EmitAs.Bool),
            ("ModifiedTransactionId", "modifiedTransactionId", EmitAs.Bool),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] TableAdvancedScalars =
        {
            ("AllowChangeTracking", "allowChangeTracking", EmitAs.Bool),
            ("AllowRowVersionChangeTracking", "allowRowVersionChangeTracking", EmitAs.Bool),
            ("AosAuthorization", "aosAuthorization", EmitAs.Raw),
            ("AllowArchival", "allowArchival", EmitAs.Bool), ("AllowOverride", "allowOverride", EmitAs.Bool),
            ("AllowRetention", "allowRetention", EmitAs.Bool),
            ("DisableDatabaseLogging", "disableDatabaseLogging", EmitAs.Bool),
            ("DisableLockEscalation", "disableLockEscalation", EmitAs.Bool),
            ("Durability", "durability", EmitAs.Raw), ("EntityRelationshipType", "entityRelationshipType", EmitAs.Raw),
            ("InstanceRelationType", "instanceRelationType", EmitAs.Raw), ("OccEnabled", "occEnabled", EmitAs.Bool),
            ("ReportRef", "reportRef", EmitAs.Raw), ("StorageMode", "storageMode", EmitAs.Raw),
            ("SupportInheritance", "supportInheritance", EmitAs.Bool), ("SystemTable", "systemTable", EmitAs.Bool),
            ("ValidTimeStateFieldType", "validTimeStateFieldType", EmitAs.Raw), ("Visible", "visible", EmitAs.Bool),
            ("Abstract", "abstract", EmitAs.Bool), ("DataSharingType", "dataSharingType", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] FieldScalars =
        {
            ("ExtendedDataType", "extendedDataType", EmitAs.Raw), ("EnumType", "enumType", EmitAs.Raw),
            ("Label", "label", EmitAs.Raw), ("HelpText", "helpText", EmitAs.Raw),
            ("Mandatory", "mandatory", EmitAs.Bool), ("AllowEdit", "allowEdit", EmitAs.Bool),
            ("AllowEditOnCreate", "allowEditOnCreate", EmitAs.Bool), ("Visible", "visible", EmitAs.Bool),
            ("AssetClassification", "assetClassification", EmitAs.Raw),
            ("GeneralDataProtectionRegulation", "generalDataProtectionRegulation", EmitAs.Raw),
            ("ConfigurationKey", "configurationKey", EmitAs.Raw), ("CountryRegionCodes", "countryRegionCodes", EmitAs.Raw),
            ("IsObsolete", "isObsolete", EmitAs.Bool), ("Tags", "tags", EmitAs.Raw),
            ("StringSize", "stringSize", EmitAs.Int), ("Adjustment", "adjustment", EmitAs.EnumCamel),
            ("Scale", "scale", EmitAs.Int), ("FeatureClass", "featureClass", EmitAs.Raw),
            ("SaveContents", "saveContents", EmitAs.Bool), ("RelationContext", "relationContext", EmitAs.Raw),
            ("SysSharingType", "sysSharingType", EmitAs.EnumCamel), ("Null", "null", EmitAs.Bool),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] FieldAdvancedScalars =
        {
            ("AliasFor", "aliasFor", EmitAs.Raw), ("IgnoreEDTRelation", "ignoreEDTRelation", EmitAs.Bool),
            ("GroupPrompt", "groupPrompt", EmitAs.Raw), ("IsSystemGenerated", "isSystemGenerated", EmitAs.Bool),
            ("CountryRegionContextField", "countryRegionContextField", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] IndexScalars =
        {
            ("AllowDuplicates", "allowDuplicates", EmitAs.Bool), ("AllowPageLocks", "allowPageLocks", EmitAs.Bool),
            ("AlternateKey", "alternateKey", EmitAs.Bool), ("ConfigurationKey", "configurationKey", EmitAs.Raw),
            ("Enabled", "enabled", EmitAs.Bool), ("IndexType", "indexType", EmitAs.Raw),
        };
    }
}
