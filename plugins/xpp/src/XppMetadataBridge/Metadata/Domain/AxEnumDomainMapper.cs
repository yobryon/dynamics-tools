using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Storage;
using NoYes = Microsoft.Dynamics.AX.Metadata.Core.MetaModel.NoYes;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// AxEnum domain mapper, bridge-side.
    ///
    /// Replaces <c>Xpp.Service.Domain.AxEnumMapper</c>: instead of
    /// hand-emitting XML that mimics MS canonical, we populate an
    /// <see cref="AxEnum"/> instance and let MS's provider serialize it.
    /// All element ordering / default elision / enum-string formatting is
    /// handled by MS — no chance of the symmetric-loss bugs that bit us
    /// in the XML emission layer.
    ///
    /// JSON shape mirrors <c>CreateEnumRequest</c> on the service side
    /// (camelCase keys, AdvancedEnumOptions nested under "advanced",
    /// per-value AdvancedEnumValueOptions nested under "advanced").
    /// </summary>
    internal sealed class AxEnumDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxEnum";
        protected override string AccessorProperty => "Enums";

        protected override object BuildFromJson(JObject json) => BuildAxEnum(json);

        protected override object ApplyPatch(object current, JObject patch)
        {
            ApplyPatchToAxEnum((AxEnum)current, patch);
            return current;
        }

        protected override JObject ReadToJson(object meta) => ToDomainJson((AxEnum)meta);

        // -------------------------------------------------------------------
        // CreateEnumRequest JSON → AxEnum metaclass
        // -------------------------------------------------------------------
        private static AxEnum BuildAxEnum(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxEnum name is required.");

            var ax = new AxEnum { Name = name };

            ApplyScalarsFromJson(ax, json);

            // Values collection. Service-side ordinal/explicit-value logic:
            // when useExplicitValues=false, Value defaults to the ordinal
            // position. When true, Value is honored verbatim.
            var useExplicit = json["useExplicitValues"]?.Type == JTokenType.Boolean && (bool)json["useExplicitValues"];
            var valuesArr = json["values"] as JArray;
            if (valuesArr != null)
            {
                var ordinal = 0;
                foreach (var v in valuesArr.OfType<JObject>())
                {
                    ax.EnumValues.Add(BuildAxEnumValue(v, useExplicit ? null : ordinal));
                    ordinal++;
                }
            }

            return ax;
        }

        private static AxEnumValue BuildAxEnumValue(JObject json, int? ordinalDefault)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxEnumValue name is required.");
            var ev = new AxEnumValue { Name = name };
            if (json["label"] is JToken lbl && lbl.Type == JTokenType.String) ev.Label = (string)lbl!;
            if (json["value"] is JToken val && val.Type == JTokenType.Integer) ev.Value = (int)val;
            else if (ordinalDefault is int o) ev.Value = o;

            var adv = json["advanced"] as JObject;
            if (adv != null)
            {
                if (adv["configurationKey"] is JToken ck && ck.Type == JTokenType.String) ev.ConfigurationKey = (string)ck!;
                if (adv["countryRegionCodes"] is JToken crc && crc.Type == JTokenType.String) ev.CountryRegionCodes = (string)crc!;
                if (adv["tags"] is JToken tg && tg.Type == JTokenType.String) ev.Tags = (string)tg!;
                // FeatureClass — exists on AxEnumValue per the metaclass.
                if (adv["featureClass"] is JToken fc && fc.Type == JTokenType.String) ev.FeatureClass = (string)fc!;
            }
            return ev;
        }

        // -------------------------------------------------------------------
        // PatchEnumRequest JSON → mutates an existing AxEnum
        // -------------------------------------------------------------------
        private static void ApplyPatchToAxEnum(AxEnum ax, JObject patch)
        {
            // Scalars: only override when the patch key is present + non-null.
            if (patch["label"] is JToken lbl) ax.Label = lbl.Type == JTokenType.Null ? null : (string)lbl;
            if (patch["help"] is JToken help && help.Type == JTokenType.String) ax.Help = (string)help;
            if (patch["isExtensible"] is JToken iext && iext.Type == JTokenType.Boolean) ax.IsExtensible = (bool)iext;
            if (patch["style"] is JToken style && style.Type == JTokenType.String) AssignEnumByName(ax, "Style", (string)style);
            if (patch["useExplicitValues"] is JToken uev && uev.Type == JTokenType.Boolean)
                ax.UseEnumValue = (bool)uev ? NoYes.Yes : NoYes.No;
            if (patch["advanced"] is JObject adv) ApplyAdvancedScalarsToAxEnum(ax, adv);

            // Values: replace the entire collection when non-null.
            if (patch["values"] is JArray valuesArr)
            {
                ax.EnumValues.Clear();
                var useExplicit = patch["useExplicitValues"]?.Type == JTokenType.Boolean ? (bool)patch["useExplicitValues"]! : (ax.UseEnumValue == NoYes.Yes);
                var ordinal = 0;
                foreach (var v in valuesArr.OfType<JObject>())
                {
                    ax.EnumValues.Add(BuildAxEnumValue(v, useExplicit ? null : ordinal));
                    ordinal++;
                }
            }
        }

        // -------------------------------------------------------------------
        // Shared scalar copy: full create vs. advanced overlay
        // -------------------------------------------------------------------
        private static void ApplyScalarsFromJson(AxEnum ax, JObject json)
        {
            if (json["label"] is JToken lbl && lbl.Type == JTokenType.String) ax.Label = (string)lbl!;
            if (json["help"] is JToken help && help.Type == JTokenType.String) ax.Help = (string)help;
            if (json["isExtensible"] is JToken iext && iext.Type == JTokenType.Boolean) ax.IsExtensible = (bool)iext;
            else ax.IsExtensible = true; // service default
            if (json["style"] is JToken style && style.Type == JTokenType.String) AssignEnumByName(ax, "Style", (string)style!);
            if (json["useExplicitValues"]?.Type == JTokenType.Boolean && (bool)json["useExplicitValues"]!)
                ax.UseEnumValue = NoYes.Yes;
            if (json["advanced"] is JObject adv) ApplyAdvancedScalarsToAxEnum(ax, adv);
        }

        private static void ApplyAdvancedScalarsToAxEnum(AxEnum ax, JObject adv)
        {
            if (adv["displayLength"] is JToken dl && dl.Type == JTokenType.Integer) ax.DisplayLength = (int)dl;
            if (adv["literals"] is JToken lit && lit.Type == JTokenType.String) AssignEnumByName(ax, "Literals", (string)lit!);
            if (adv["analysisUsage"] is JToken au && au.Type == JTokenType.String) AssignEnumByName(ax, "AnalysisUsage", (string)au!);
            if (adv["configurationKey"] is JToken ck && ck.Type == JTokenType.String) ax.ConfigurationKey = (string)ck!;
            if (adv["countryRegionCodes"] is JToken crc && crc.Type == JTokenType.String) ax.CountryRegionCodes = (string)crc!;
            if (adv["tags"] is JToken tg && tg.Type == JTokenType.String) ax.Tags = (string)tg!;
            if (adv["isObsolete"] is JToken io && io.Type == JTokenType.Boolean) ax.IsObsolete = (bool)io ? NoYes.Yes : NoYes.No;
            // Visibility — emitted only when non-Public per service convention.
            if (adv["visibility"] is JToken vis && vis.Type == JTokenType.String) AssignEnumByName(ax, "Visibility", (string)vis!);
        }

        /// <summary>
        /// Reflectively assign an enum-typed property by string name. Lets
        /// us avoid bringing the MS internal text-marker (_ITxt) enum types
        /// into the bridge mapper's type closure — they live in
        /// Microsoft.Dynamics.AX.Metadata.Core.MetaModel and would clutter
        /// the using list significantly for properties we touch sparingly.
        /// </summary>
        private static void AssignEnumByName(object target, string propName, string value)
        {
            var prop = target.GetType().GetProperty(propName);
            if (prop == null || !prop.PropertyType.IsEnum) return;
            // Service JSON serializer emits enum names camelCase
            // (jsonString "comboBox") while the metaclass enum is "ComboBox".
            // Parse ignoreCase so both shapes bind.
            try { prop.SetValue(target, Enum.Parse(prop.PropertyType, value, ignoreCase: true)); }
            catch { /* unknown enum name — silently ignore (service-side validation should catch it) */ }
        }

        // -------------------------------------------------------------------
        // AxEnum metaclass → GetEnumResponse JSON
        // -------------------------------------------------------------------
        private static JObject ToDomainJson(AxEnum ax)
        {
            var values = new JArray();
            foreach (var v in ax.EnumValues)
            {
                var vj = new JObject { ["name"] = v.Name };
                if (!string.IsNullOrEmpty(v.Label)) vj["label"] = v.Label;
                vj["value"] = v.Value;

                var vAdv = new JObject();
                if (!string.IsNullOrEmpty(v.ConfigurationKey)) vAdv["configurationKey"] = v.ConfigurationKey;
                if (!string.IsNullOrEmpty(v.CountryRegionCodes)) vAdv["countryRegionCodes"] = v.CountryRegionCodes;
                if (!string.IsNullOrEmpty(v.FeatureClass)) vAdv["featureClass"] = v.FeatureClass;
                if (!string.IsNullOrEmpty(v.Tags)) vAdv["tags"] = v.Tags;
                if (vAdv.Count > 0) vj["advanced"] = vAdv;

                values.Add(vj);
            }

            var jo = new JObject
            {
                ["name"] = ax.Name,
                ["values"] = values,
                ["isExtensible"] = ax.IsExtensible,
            };
            if (!string.IsNullOrEmpty(ax.Label)) jo["label"] = ax.Label;
            if (!string.IsNullOrEmpty(ax.Help)) jo["help"] = ax.Help;
            // useExplicitValues is non-nullable in the service schema (default
            // false) — always emit so the drift detector compares apples to
            // apples on the common-case "agent didn't override the default".
            jo["useExplicitValues"] = ax.UseEnumValue == NoYes.Yes;
            // Always emit Style on read. The service-side GetEnumResponse
            // has Style as a non-nullable scalar with default ComboBox, so
            // the JSON it round-trips through always carries this field —
            // emitting it here keeps the drift detector symmetric on the
            // common case where the agent didn't override the default.
            var styleProp = typeof(AxEnum).GetProperty("Style");
            if (styleProp != null)
            {
                var styleVal = styleProp.GetValue(ax)?.ToString();
                if (!string.IsNullOrEmpty(styleVal))
                {
                    // Lowercase first letter to match the camelCase enum
                    // serialization the service uses.
                    jo["style"] = char.ToLowerInvariant(styleVal[0]) + styleVal.Substring(1);
                }
            }

            var adv = new JObject();
            if (ax.DisplayLength != 0) adv["displayLength"] = ax.DisplayLength;
            var litVal = typeof(AxEnum).GetProperty("Literals")?.GetValue(ax)?.ToString();
            if (!string.IsNullOrEmpty(litVal) && litVal != "Default") adv["literals"] = litVal;
            var auVal = typeof(AxEnum).GetProperty("AnalysisUsage")?.GetValue(ax)?.ToString();
            if (!string.IsNullOrEmpty(auVal)) adv["analysisUsage"] = auVal;
            if (!string.IsNullOrEmpty(ax.ConfigurationKey)) adv["configurationKey"] = ax.ConfigurationKey;
            if (!string.IsNullOrEmpty(ax.CountryRegionCodes)) adv["countryRegionCodes"] = ax.CountryRegionCodes;
            if (!string.IsNullOrEmpty(ax.Tags)) adv["tags"] = ax.Tags;
            if (ax.IsObsolete == NoYes.Yes) adv["isObsolete"] = true;
            var visVal = typeof(AxEnum).GetProperty("Visibility")?.GetValue(ax)?.ToString();
            if (!string.IsNullOrEmpty(visVal) && visVal != "Public") adv["visibility"] = visVal;
            if (adv.Count > 0) jo["advanced"] = adv;

            return jo;
        }

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
