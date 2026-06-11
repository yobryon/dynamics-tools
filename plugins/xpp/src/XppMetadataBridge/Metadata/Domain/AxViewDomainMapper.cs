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
    /// AxView — a stored AxQuery (referenced by the Query scalar) plus
    /// promoted/computed columns and SQL-view metadata. An AxDataEntity-family
    /// member, so the common surface (scalars, relations, field groups, source
    /// code, subscriber access) is inherited. This subclass owns the
    /// view-unique collections: polymorphic Fields (AxViewFieldBound /
    /// AxViewFieldComputed{String|Int|Int64|Real|Date|Enum|UtcDateTime}) and
    /// Indexes (AxViewIndex). ViewMetadata (an AxQuerySimple designer shell) is
    /// left at its metaclass default — it's designer-helper state the domain
    /// shape doesn't surface.
    /// </summary>
    internal sealed class AxViewDomainMapper : AxDataEntityDomainMapperBase
    {
        public override string AxType => "AxView";
        protected override string AccessorProperty => "Views";
        protected override string MetaPrefix => "AxView";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "fields", "indexes", "relations", "fieldGroups",
            "sourceCode", "subscriberAccessLevel", "advanced", "viewMetadata",
        };

        // ===================================================================
        // Type-specific BUILD — Fields, Indexes.
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
            // ViewMetadata: the backing AxQuerySimple query tree. Previously
            // dropped wholesale; reuse the shared query-tree builder.
            if (json["viewMetadata"] is JObject vm)
                ax.GetType().GetProperty("ViewMetadata")?.SetValue(ax, AxQueryDomainMapper.BuildViewMetadata(vm));
        }

        internal static object BuildField(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "View field name is required.");
            var kind = MetaclassMap.Pascal((string?)json["kind"] ?? "Bound");
            var f = MetaclassMap.Instantiate("AxViewField" + kind);
            MetaclassMap.SetName(f, name);
            // Blind-assign every scalar; Assign no-ops members absent on the
            // chosen subtype (e.g. DataField on a Computed field).
            foreach (var p in json.Properties())
            {
                if (p.Name is "name" or "kind") continue;
                MetaclassJson.Assign(f, MetaclassMap.Pascal(p.Name), p.Value);
            }
            return f;
        }

        private static object BuildIndex(JObject json)
        {
            var idx = MetaclassMap.Instantiate("AxViewIndex");
            MetaclassMap.SetName(idx, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "View index name is required."));
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
                    var ixf = MetaclassMap.Instantiate("AxViewIndexField");
                    ixf.GetType().GetProperty("DataField")?.SetValue(ixf, (string?)jf["dataField"] ?? string.Empty);
                    fl.Add(ixf);
                }
            }
            return idx;
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

            if (Coll(ax, "ViewMetadata") is object vm && AxQueryDomainMapper.EmitViewMetadata(vm) is JObject vmJson)
                jo["viewMetadata"] = vmJson;
        }

        internal static JObject EmitField(object f)
        {
            var reference = MetaclassMap.Reference(f.GetType());
            var kindName = f.GetType().Name.Substring("AxViewField".Length); // Bound / ComputedString / ...
            var o = new JObject { ["name"] = MetaclassMap.GetName(f), ["kind"] = MetaclassJson.ToCamel(kindName) };
            foreach (var (prop, key, kind) in FieldCommonScalars)
                MetaclassJson.EmitDefaulted(o, f, reference, prop, key, kind);
            if (kindName == "Bound")
            {
                foreach (var (prop, key, kind) in FieldBoundScalars)
                    MetaclassJson.EmitDefaulted(o, f, reference, prop, key, kind);
            }
            else // any Computed*
            {
                foreach (var (prop, key, kind) in FieldComputedScalars)
                    MetaclassJson.EmitDefaulted(o, f, reference, prop, key, kind);
                if (kindName == "ComputedString")
                {
                    MetaclassJson.EmitDefaulted(o, f, reference, "Adjustment", "adjustment", EmitAs.EnumCamel);
                    MetaclassJson.EmitDefaulted(o, f, reference, "StringSize", "stringSize", EmitAs.Int);
                }
                else if (kindName == "ComputedEnum")
                    MetaclassJson.EmitDefaulted(o, f, reference, "EnumType", "enumType", EmitAs.Raw);
            }
            return o;
        }

        private static JObject EmitIndex(object idx)
        {
            var reference = MetaclassMap.Reference(idx.GetType());
            var o = new JObject { ["name"] = MetaclassMap.GetName(idx) };
            foreach (var (prop, key, kind) in IndexScalars)
                MetaclassJson.EmitDefaulted(o, idx, reference, prop, key, kind);
            var fields = new JArray();
            if (Coll(idx, "Fields") is IEnumerable fe)
                foreach (var ixf in fe)
                {
                    var df = ixf.GetType().GetProperty("DataField")?.GetValue(ixf) as string;
                    if (!string.IsNullOrEmpty(df)) fields.Add(new JObject { ["dataField"] = df });
                }
            if (fields.Count > 0) o["fields"] = fields;
            return o;
        }

        private static object? Coll(object o, string prop) => o.GetType().GetProperty(prop)?.GetValue(o);

        // ===================================================================
        // Scalar emit tables.
        // ===================================================================
        protected override (string Prop, string Key, EmitAs Kind)[] TopScalars => ViewScalars;
        protected override (string Prop, string Key, EmitAs Kind)[] AdvancedScalars => ViewAdvancedScalars;

        private static readonly (string Prop, string Key, EmitAs Kind)[] ViewScalars =
        {
            ("Label", "label", EmitAs.Raw), ("SingularLabel", "singularLabel", EmitAs.Raw),
            ("DeveloperDocumentation", "developerDocumentation", EmitAs.Raw),
            ("TableGroup", "tableGroup", EmitAs.Raw), ("ConfigurationKey", "configurationKey", EmitAs.Raw),
            ("CountryRegionCodes", "countryRegionCodes", EmitAs.Raw), ("IsObsolete", "isObsolete", EmitAs.Bool),
            ("FormRef", "formRef", EmitAs.Raw), ("ListPageRef", "listPageRef", EmitAs.Raw),
            ("PreviewPartRef", "previewPartRef", EmitAs.Raw), ("OperationalDomain", "operationalDomain", EmitAs.Raw),
            ("ReportRef", "reportRef", EmitAs.Raw), ("EntityRelationshipType", "entityRelationshipType", EmitAs.Raw),
            ("TitleField1", "titleField1", EmitAs.Raw), ("TitleField2", "titleField2", EmitAs.Raw),
            ("AosAuthorization", "aosAuthorization", EmitAs.Raw), ("CollectionName", "collectionName", EmitAs.Raw),
            ("IsPublic", "isPublic", EmitAs.Bool), ("IsStaged", "isStaged", EmitAs.Bool),
            ("MessagingRole", "messagingRole", EmitAs.Raw), ("Query", "query", EmitAs.Raw),
            ("ReplacementKey", "replacementKey", EmitAs.Raw), ("Updatable", "updatable", EmitAs.Bool),
            ("ValidTimeStateEnabled", "validTimeStateEnabled", EmitAs.Bool), ("Version", "version", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] ViewAdvancedScalars =
        {
            ("Visible", "visible", EmitAs.Bool),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] FieldCommonScalars =
        {
            ("AccessModifier", "accessModifier", EmitAs.Raw), ("AosAuthorization", "aosAuthorization", EmitAs.Raw),
            ("ConfigurationKey", "configurationKey", EmitAs.Raw), ("CountryRegionCodes", "countryRegionCodes", EmitAs.Raw),
            ("FeatureClass", "featureClass", EmitAs.Raw), ("GroupPrompt", "groupPrompt", EmitAs.Raw),
            ("HelpText", "helpText", EmitAs.Raw), ("IsObsolete", "isObsolete", EmitAs.Bool),
            ("Label", "label", EmitAs.Raw), ("RelationContext", "relationContext", EmitAs.Raw),
            ("Tags", "tags", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] FieldBoundScalars =
        {
            ("Aggregation", "aggregation", EmitAs.Raw), ("DataField", "dataField", EmitAs.Raw),
            ("DataSource", "dataSource", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] FieldComputedScalars =
        {
            ("ExtendedDataType", "extendedDataType", EmitAs.Raw), ("IsVirtual", "isVirtual", EmitAs.Bool),
            ("Method", "method", EmitAs.Raw), ("ViewMethod", "viewMethod", EmitAs.Raw),
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] IndexScalars =
        {
            ("AllowDuplicates", "allowDuplicates", EmitAs.Bool), ("AlternateKey", "alternateKey", EmitAs.Bool),
            ("ConfigurationKey", "configurationKey", EmitAs.Raw), ("Enabled", "enabled", EmitAs.Bool),
            ("Tags", "tags", EmitAs.Raw),
        };
    }
}
