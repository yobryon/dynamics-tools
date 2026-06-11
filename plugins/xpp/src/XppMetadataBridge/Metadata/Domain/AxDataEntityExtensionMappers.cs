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
    /// AxTableExtension — adds fields / indexes / relations / field groups to a
    /// base table, plus the modification surface. The added-element collections
    /// reuse the AxTable / AxDataEntity-family builders (exposed internal
    /// static): same Fields/Indexes/Relations/FieldGroups shapes as the base
    /// table. RelationExtensions add constraints into an existing relation;
    /// Field/RelationModifications change existing children's properties.
    /// </summary>
    internal sealed class AxTableExtensionDomainMapper : AxExtensionDomainMapperBase
    {
        public override string AxType => "AxTableExtension";
        protected override string AccessorProperty => "TableExtensions";
        protected override string MetaTypeName => "AxTableExtension";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "fields", "fieldGroups", "fieldGroupExtensions", "fieldModifications",
            "indexes", "relations", "relationExtensions", "relationModifications",
            "propertyModifications", "advanced",
        };

        protected override (string Prop, string Key, EmitAs Kind)[] ExtraScalars { get; } =
            new[] { ("FormRef", "formRef", EmitAs.Raw) };

        protected override void BuildTypeSpecific(object ax, JObject json)
        {
            AddEach(ax, "Fields", json["fields"], AxTableDomainMapper.BuildField);
            AddEach(ax, "Indexes", json["indexes"], AxTableDomainMapper.BuildIndex);
            if (json["relations"] is JArray rels) AxDataEntityDomainMapperBase.BuildRelationsInto("AxTable", ax, rels);
            if (json["fieldGroups"] is JArray fgs) AxDataEntityDomainMapperBase.BuildFieldGroupsInto(ax, fgs);
            BuildFieldGroupExtensions(ax, "FieldGroupExtensions", json["fieldGroupExtensions"] as JArray);
            BuildRelationExtensions(ax, json["relationExtensions"] as JArray);
            BuildExtensionMods(ax, "FieldModifications", json["fieldModifications"] as JArray);
            BuildExtensionMods(ax, "RelationModifications", json["relationModifications"] as JArray);
        }

        protected override void EmitTypeSpecific(JObject jo, object ax)
        {
            Put(jo, "fields", EmitEach(ax, "Fields", AxTableDomainMapper.EmitField));
            Put(jo, "indexes", EmitEach(ax, "Indexes", AxTableDomainMapper.EmitIndex));
            Put(jo, "relations", AxDataEntityDomainMapperBase.EmitRelationsFrom("AxTable", ax));
            Put(jo, "fieldGroups", AxDataEntityDomainMapperBase.EmitFieldGroupsFrom(ax));
            Put(jo, "fieldGroupExtensions", EmitFieldGroupExtensions(ax, "FieldGroupExtensions"));
            Put(jo, "relationExtensions", EmitRelationExtensions(ax));
            Put(jo, "fieldModifications", EmitExtensionMods(ax, "FieldModifications"));
            Put(jo, "relationModifications", EmitExtensionMods(ax, "RelationModifications"));
        }

        private static void BuildRelationExtensions(object ax, JArray? arr)
        {
            if (arr == null || Prop(ax, "RelationExtensions") is not IList list) return;
            ClearAllowDup(list);
            foreach (var rj in arr.OfType<JObject>())
            {
                var re = MetaclassMap.Instantiate("AxTableRelationExtension");
                MetaclassMap.SetName(re, (string?)rj["name"] ?? string.Empty);
                MetaclassJson.Assign(re, "Tags", rj["tags"]);
                if (rj["relationConstraints"] is JArray cons)
                    AxDataEntityDomainMapperBase.BuildConstraintsInto("AxTable", re, "RelationConstraints", cons);
                list.Add(re);
            }
        }

        private static JArray EmitRelationExtensions(object ax)
        {
            var arr = new JArray();
            if (Prop(ax, "RelationExtensions") is not IEnumerable en) return arr;
            foreach (var re in en)
            {
                var o = new JObject { ["name"] = MetaclassMap.GetName(re) };
                EmitStr(o, re, "Tags", "tags");
                var cons = AxDataEntityDomainMapperBase.EmitConstraintsFrom("AxTable", re, "RelationConstraints");
                if (cons.Count > 0) o["relationConstraints"] = cons;
                arr.Add(o);
            }
            return arr;
        }
    }

    /// <summary>
    /// AxViewExtension — adds view fields / data sources / field groups to a
    /// base view. Fields reuse AxView's polymorphic Bound/Computed builder;
    /// DataSources reuse the AxQuerySimple tree.
    /// </summary>
    internal sealed class AxViewExtensionDomainMapper : AxExtensionDomainMapperBase
    {
        public override string AxType => "AxViewExtension";
        protected override string AccessorProperty => "ViewExtensions";
        protected override string MetaTypeName => "AxViewExtension";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "fields", "fieldGroups", "fieldGroupExtensions", "fieldModifications",
            "dataSources", "ranges", "propertyModifications", "advanced",
        };

        protected override void BuildTypeSpecific(object ax, JObject json)
        {
            AddEach(ax, "Fields", json["fields"], AxViewDomainMapper.BuildField);
            AddEach(ax, "DataSources", json["dataSources"], j => AxQueryDomainMapper.BuildDataSource(j, "AxQueryExtension"));
            if (json["fieldGroups"] is JArray fgs) AxDataEntityDomainMapperBase.BuildFieldGroupsInto(ax, fgs);
            BuildFieldGroupExtensions(ax, "FieldGroupExtensions", json["fieldGroupExtensions"] as JArray);
            BuildExtensionMods(ax, "FieldModifications", json["fieldModifications"] as JArray);
        }

        protected override void EmitTypeSpecific(JObject jo, object ax)
        {
            Put(jo, "fields", EmitEach(ax, "Fields", AxViewDomainMapper.EmitField));
            Put(jo, "dataSources", EmitEach(ax, "DataSources", d => AxQueryDomainMapper.EmitDataSource(d, "AxQueryExtension")));
            Put(jo, "fieldGroups", AxDataEntityDomainMapperBase.EmitFieldGroupsFrom(ax));
            Put(jo, "fieldGroupExtensions", EmitFieldGroupExtensions(ax, "FieldGroupExtensions"));
            Put(jo, "fieldModifications", EmitExtensionMods(ax, "FieldModifications"));
        }
    }

    /// <summary>
    /// AxDataEntityViewExtension — adds entity fields / data sources / relations
    /// to a base entity. Fields reuse the entity's polymorphic Mapped/Unmapped
    /// builder; DataSources reuse the AxQuerySimple tree.
    /// </summary>
    internal sealed class AxDataEntityViewExtensionDomainMapper : AxExtensionDomainMapperBase
    {
        public override string AxType => "AxDataEntityViewExtension";
        protected override string AccessorProperty => "DataEntityViewExtensions";
        protected override string MetaTypeName => "AxDataEntityViewExtension";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "fields", "fieldGroups", "fieldGroupExtensions", "fieldModifications",
            "dataSources", "relations", "propertyModifications", "advanced",
        };

        protected override void BuildTypeSpecific(object ax, JObject json)
        {
            AddEach(ax, "Fields", json["fields"], AxDataEntityViewDomainMapper.BuildField);
            AddEach(ax, "DataSources", json["dataSources"], j => AxQueryDomainMapper.BuildDataSource(j, "AxQueryExtension"));
            if (json["relations"] is JArray rels) AxDataEntityDomainMapperBase.BuildRelationsInto("AxDataEntityView", ax, rels);
            if (json["fieldGroups"] is JArray fgs) AxDataEntityDomainMapperBase.BuildFieldGroupsInto(ax, fgs);
            BuildFieldGroupExtensions(ax, "FieldGroupExtensions", json["fieldGroupExtensions"] as JArray);
            BuildExtensionMods(ax, "FieldModifications", json["fieldModifications"] as JArray);
        }

        protected override void EmitTypeSpecific(JObject jo, object ax)
        {
            Put(jo, "fields", EmitEach(ax, "Fields", AxDataEntityViewDomainMapper.EmitField));
            Put(jo, "dataSources", EmitEach(ax, "DataSources", d => AxQueryDomainMapper.EmitDataSource(d, "AxQueryExtension")));
            Put(jo, "relations", AxDataEntityDomainMapperBase.EmitRelationsFrom("AxDataEntityView", ax));
            Put(jo, "fieldGroups", AxDataEntityDomainMapperBase.EmitFieldGroupsFrom(ax));
            Put(jo, "fieldGroupExtensions", EmitFieldGroupExtensions(ax, "FieldGroupExtensions"));
            Put(jo, "fieldModifications", EmitExtensionMods(ax, "FieldModifications"));
        }
    }
}
