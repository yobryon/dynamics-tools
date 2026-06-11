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
    /// AxEnumExtension — adds enum values + value/property modifications to a
    /// base enum. EnumValues mirror the AxEnum value shape (name, label,
    /// value, nested advanced { configurationKey, countryRegionCodes,
    /// featureClass, tags }) so the agent-facing shape matches AxEnum.
    /// </summary>
    internal sealed class AxEnumExtensionDomainMapper : AxExtensionDomainMapperBase
    {
        public override string AxType => "AxEnumExtension";
        protected override string AccessorProperty => "EnumExtensions";
        protected override string MetaTypeName => "AxEnumExtension";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "enumValues", "valueModifications", "propertyModifications", "advanced",
        };

        protected override void BuildTypeSpecific(object ax, JObject json)
        {
            if (json["enumValues"] is JArray vals && Prop(ax, "EnumValues") is IList vl)
            {
                ClearAllowDup(vl);
                foreach (var v in vals.OfType<JObject>()) vl.Add(BuildEnumValue(v));
            }
            BuildExtensionMods(ax, "ValueModifications", json["valueModifications"] as JArray);
        }

        protected override void EmitTypeSpecific(JObject jo, object ax)
        {
            var vals = new JArray();
            if (Prop(ax, "EnumValues") is IEnumerable en) foreach (var v in en) vals.Add(EmitEnumValue(v));
            if (vals.Count > 0) jo["enumValues"] = vals;
            var vm = EmitExtensionMods(ax, "ValueModifications");
            if (vm.Count > 0) jo["valueModifications"] = vm;
        }

        private static object BuildEnumValue(JObject json)
        {
            var ev = MetaclassMap.Instantiate("AxEnumValue");
            MetaclassMap.SetName(ev, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxEnumValue name is required."));
            MetaclassJson.Assign(ev, "Label", json["label"]);
            MetaclassJson.Assign(ev, "Value", json["value"]);
            if (json["advanced"] is JObject adv)
            {
                MetaclassJson.Assign(ev, "ConfigurationKey", adv["configurationKey"]);
                MetaclassJson.Assign(ev, "CountryRegionCodes", adv["countryRegionCodes"]);
                MetaclassJson.Assign(ev, "FeatureClass", adv["featureClass"]);
                MetaclassJson.Assign(ev, "Tags", adv["tags"]);
            }
            return ev;
        }

        private static JObject EmitEnumValue(object v)
        {
            var t = v.GetType();
            string? S(string p) => t.GetProperty(p)?.GetValue(v) as string;
            var o = new JObject { ["name"] = MetaclassMap.GetName(v) };
            if (!string.IsNullOrEmpty(S("Label"))) o["label"] = S("Label");
            if (t.GetProperty("Value")?.GetValue(v) is int n) o["value"] = n;
            var adv = new JObject();
            if (!string.IsNullOrEmpty(S("ConfigurationKey"))) adv["configurationKey"] = S("ConfigurationKey");
            if (!string.IsNullOrEmpty(S("CountryRegionCodes"))) adv["countryRegionCodes"] = S("CountryRegionCodes");
            if (!string.IsNullOrEmpty(S("FeatureClass"))) adv["featureClass"] = S("FeatureClass");
            if (!string.IsNullOrEmpty(S("Tags"))) adv["tags"] = S("Tags");
            if (adv.Count > 0) o["advanced"] = adv;
            return o;
        }
    }

    /// <summary>
    /// AxEdtExtension — adds array elements + property modifications to a base
    /// EDT. Array elements are flat (Name, Index, Label, HelpText,
    /// CollectionLabel, Tags); their rare nested relations/tableReferences are
    /// out of the extension's 80% scope.
    /// </summary>
    internal sealed class AxEdtExtensionDomainMapper : AxExtensionDomainMapperBase
    {
        public override string AxType => "AxEdtExtension";
        protected override string AccessorProperty => "EdtExtensions";
        protected override string MetaTypeName => "AxEdtExtension";

        protected override ISet<string> StructuralKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "arrayElements", "propertyModifications", "advanced",
        };

        private static readonly (string Prop, string Key, EmitAs Kind)[] ArrayElementScalars =
        {
            ("Index", "index", EmitAs.Int), ("Label", "label", EmitAs.Raw),
            ("HelpText", "helpText", EmitAs.Raw), ("CollectionLabel", "collectionLabel", EmitAs.Raw),
            ("Tags", "tags", EmitAs.Raw),
        };

        protected override void BuildTypeSpecific(object ax, JObject json)
            => BuildSimpleColl(ax, "ArrayElements", "AxEdtArrayElement", json["arrayElements"] as JArray,
                "relations", "tableReferences");

        protected override void EmitTypeSpecific(JObject jo, object ax)
        {
            var aes = EmitSimpleColl(ax, "ArrayElements", ArrayElementScalars);
            if (aes.Count > 0) jo["arrayElements"] = aes;
        }
    }
}
