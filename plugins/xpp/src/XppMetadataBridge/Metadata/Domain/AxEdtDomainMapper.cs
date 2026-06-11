using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using NoYes = Microsoft.Dynamics.AX.Metadata.Core.MetaModel.NoYes;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// AxEdt domain mapper, bridge-side. The polymorphic one: the BaseType
    /// discriminator selects a concrete metaclass subtype (AxEdtString,
    /// AxEdtInt, AxEdtReal, AxEdtEnum, AxEdtDate, AxEdtTime, AxEdtUtcDateTime,
    /// AxEdtInt64, AxEdtContainer, AxEdtGuid). We construct the right subtype,
    /// assign properties by name (reflection — can't transpose names the way
    /// the hand-written XML emitter could), and let MS serialize.
    ///
    /// Relations / TableReferences carry their own polymorphism
    /// (AxEdtRelationFixed / AxEdtTableReferenceFilter add a Value member).
    ///
    /// JSON mirrors CreateEdtRequest: name, baseType, label, helpText,
    /// extends, one of {string,numeric,real,enum,date,time,utc} option blocks,
    /// arrayElements[], relations[], tableReferences[], advanced{}.
    /// </summary>
    internal sealed class AxEdtDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxEdt";
        protected override string AccessorProperty => "Edts";

        private const string MetaNs = "Microsoft.Dynamics.AX.Metadata.MetaModel.";

        protected override object BuildFromJson(JObject json) => BuildAxEdt(json);

        protected override object ApplyPatch(object current, JObject patch)
        {
            ApplyPatchToEdt((AxEdt)current, patch);
            return current;
        }

        protected override JObject ReadToJson(object meta) => ToDomainJson((AxEdt)meta);

        // ===================================================================
        // Per-block property tables. propName == metaclass property name ==
        // PascalCase(jsonKey). EmitKind drives the READ-side JSON kind so the
        // round-trip matches the service's System.Text.Json serialization.
        // ===================================================================
        private enum K { Int, BoolYesNo, EnumCamel, Str }

        private static readonly (string Prop, K Kind)[] StringProps =
        {
            ("StringSize", K.Int), ("StringSizeIsExtensible", K.BoolYesNo),
            ("ChangeCase", K.EnumCamel), ("DisplayHeight", K.Int),
            ("Adjustment", K.EnumCamel), ("DatabaseStringSize", K.Int),
        };
        private static readonly (string Prop, K Kind)[] NumericProps =
        {
            ("AllowNegative", K.BoolYesNo), ("ShowZero", K.BoolYesNo),
            ("SignDisplay", K.EnumCamel), ("DisplaceNegative", K.BoolYesNo),
            ("RotateSign", K.BoolYesNo),
        };
        private static readonly (string Prop, K Kind)[] RealProps =
        {
            ("NoOfDecimals", K.Int), ("NoOfDecimalsIsExtensible", K.BoolYesNo),
            ("Scale", K.Int), ("DecimalSeparator", K.Str), ("ThousandSeparator", K.Str),
            ("AutoInsSeparator", K.BoolYesNo), ("FormatMST", K.BoolYesNo),
        };
        private static readonly (string Prop, K Kind)[] EnumProps =
        {
            ("EnumType", K.Str), ("Style", K.EnumCamel),
        };
        private static readonly (string Prop, K Kind)[] DateProps =
        {
            ("DateFormat", K.EnumCamel), ("DateDay", K.EnumCamel), ("DateMonth", K.EnumCamel),
            ("DateYear", K.EnumCamel), ("DateSeparator", K.Str), ("MaxDateLabel", K.Str),
        };
        private static readonly (string Prop, K Kind)[] TimeProps =
        {
            ("TimeFormat", K.EnumCamel), ("TimeHours", K.EnumCamel), ("TimeMinute", K.EnumCamel),
            ("TimeSeconds", K.EnumCamel), ("TimeSeparator", K.Str),
        };
        private static readonly (string Prop, K Kind)[] UtcProps =
            DateProps.Concat(TimeProps).Append(("TimezonePreference", K.EnumCamel)).ToArray();

        // Advanced block (base AxEdt scalars). IsObsolete + Visibility are
        // non-nullable in the service shape, so always emit them when the
        // advanced block is present.
        private static readonly (string Prop, K Kind)[] AdvancedProps =
        {
            ("CollectionLabel", K.Str), ("FormHelp", K.Str), ("ConfigurationKey", K.Str),
            ("CountryRegionCodes", K.Str), ("Tags", K.Str),
            ("ButtonImage", K.EnumCamel), ("ControlClass", K.Str), ("DataInteractorFactory", K.Str),
            ("PresenceClass", K.Str), ("PresenceMethod", K.Str), ("PresenceIndicatorAllowed", K.BoolYesNo),
            ("Alignment", K.EnumCamel), ("Direction", K.EnumCamel), ("DisplayLength", K.Int),
            ("EnforceHierarchy", K.BoolYesNo), ("ReferenceTable", K.Str), ("Literals", K.EnumCamel),
        };

        private static (string Block, (string Prop, K Kind)[] Props)? SubtypeBlock(string baseType) => baseType switch
        {
            "String" => ("string", StringProps),
            "Int" or "Int64" or "Real" => ("numeric", NumericProps),
            "Enum" => ("enum", EnumProps),
            "Date" => ("date", DateProps),
            "Time" => ("time", TimeProps),
            "UtcDateTime" => ("utc", UtcProps),
            _ => null,
        };

        // ===================================================================
        // BUILD — JSON → metaclass
        // ===================================================================
        private static AxEdt BuildAxEdt(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxEdt name is required.");
            var baseTypeRaw = (string?)json["baseType"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxEdt baseType is required.");
            // Service serializes EdtBaseType camelCase ("string","utcDateTime");
            // metaclass type names are PascalCase ("AxEdtString","AxEdtUtcDateTime").
            // Upper-casing the first char recovers the member name in every case
            // (the inner capitals like the T in utcDateTime are preserved).
            var baseType = Pascal(baseTypeRaw);

            var ax = Instantiate<AxEdt>("AxEdt" + baseType, $"unknown EDT baseType '{baseTypeRaw}'");
            ax.Name = name;

            MetaclassJson.Assign(ax, "Label", json["label"]);
            MetaclassJson.Assign(ax, "HelpText", json["helpText"]);
            MetaclassJson.Assign(ax, "Extends", json["extends"]);

            // Real-only NoOfDecimals etc. + numeric/string/etc.: assign every
            // key in the matching option block by PascalCased name.
            if (SubtypeBlock(baseType) is var sb && sb != null && json[sb.Value.Block] is JObject opts)
                AssignAll(ax, opts);

            // Advanced (base scalars).
            if (json["advanced"] is JObject adv) AssignAll(ax, adv);

            // Collections. Disable dup-checking first — MS-shipped EDTs can
            // carry same-key relations (see MetaclassJson.AllowDuplicates).
            MetaclassJson.AllowDuplicates(ax.ArrayElements);
            MetaclassJson.AllowDuplicates(ax.Relations);
            MetaclassJson.AllowDuplicates(ax.TableReferences);
            if (json["arrayElements"] is JArray aes)
                foreach (var ae in aes.OfType<JObject>()) ax.ArrayElements.Add(BuildArrayElement(ae));
            if (json["relations"] is JArray rels)
                foreach (var r in rels.OfType<JObject>()) ax.Relations.Add(BuildRelation(r));
            if (json["tableReferences"] is JArray trs)
                foreach (var t in trs.OfType<JObject>()) ax.TableReferences.Add(BuildTableReference(t));

            return ax;
        }

        private static void AssignAll(object target, JObject block)
        {
            foreach (var prop in block.Properties())
                MetaclassJson.Assign(target, Pascal(prop.Name), prop.Value);
        }

        private static AxEdtArrayElement BuildArrayElement(JObject json)
        {
            var ae = new AxEdtArrayElement
            {
                Name = (string?)json["name"] ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "ArrayElement name is required."),
            };
            MetaclassJson.Assign(ae, "Index", json["index"]);
            MetaclassJson.Assign(ae, "Label", json["label"]);
            MetaclassJson.Assign(ae, "HelpText", json["helpText"]);
            MetaclassJson.Assign(ae, "CollectionLabel", json["collectionLabel"]);
            MetaclassJson.Assign(ae, "Tags", json["tags"]);
            MetaclassJson.AllowDuplicates(ae.Relations);
            MetaclassJson.AllowDuplicates(ae.TableReferences);
            if (json["relations"] is JArray rels)
                foreach (var r in rels.OfType<JObject>()) ae.Relations.Add(BuildRelation(r));
            if (json["tableReferences"] is JArray trs)
                foreach (var t in trs.OfType<JObject>()) ae.TableReferences.Add(BuildTableReference(t));
            return ae;
        }

        private static AxEdtRelation BuildRelation(JObject json)
        {
            var fixedValue = MetaclassJson.GetString(json, "fixedValue");
            AxEdtRelation rel = fixedValue != null
                ? Instantiate<AxEdtRelation>("AxEdtRelationFixed", "AxEdtRelationFixed not found")
                : new AxEdtRelation();
            MetaclassJson.Assign(rel, "Table", json["table"]);
            MetaclassJson.Assign(rel, "RelatedField", json["relatedField"]);
            MetaclassJson.Assign(rel, "Tags", json["tags"]);
            if (fixedValue != null) MetaclassJson.Assign(rel, "Value", json["fixedValue"]);
            return rel;
        }

        private static AxEdtTableReference BuildTableReference(JObject json)
        {
            var filterValue = MetaclassJson.GetString(json, "filterValue");
            AxEdtTableReference tr = filterValue != null
                ? Instantiate<AxEdtTableReference>("AxEdtTableReferenceFilter", "AxEdtTableReferenceFilter not found")
                : new AxEdtTableReference();
            MetaclassJson.Assign(tr, "Table", json["table"]);
            MetaclassJson.Assign(tr, "RelatedField", json["relatedField"]);
            MetaclassJson.Assign(tr, "Tags", json["tags"]);
            if (filterValue != null) MetaclassJson.Assign(tr, "Value", json["filterValue"]);
            return tr;
        }

        // ===================================================================
        // PATCH — merge onto the existing instance. Collections replace
        // wholesale when present (matches legacy semantics). BaseType is not
        // patchable, so we never re-instantiate.
        // ===================================================================
        private static void ApplyPatchToEdt(AxEdt ax, JObject patch)
        {
            MetaclassJson.Assign(ax, "Label", patch["label"]);
            MetaclassJson.Assign(ax, "HelpText", patch["helpText"]);
            MetaclassJson.Assign(ax, "Extends", patch["extends"]);

            var baseType = ax.GetType().Name.Substring("AxEdt".Length);
            if (SubtypeBlock(baseType) is var sb && sb != null && patch[sb.Value.Block] is JObject opts)
                AssignAll(ax, opts);
            if (patch["advanced"] is JObject adv) AssignAll(ax, adv);

            if (patch["arrayElements"] is JArray aes)
            {
                ax.ArrayElements.Clear();
                MetaclassJson.AllowDuplicates(ax.ArrayElements);
                foreach (var ae in aes.OfType<JObject>()) ax.ArrayElements.Add(BuildArrayElement(ae));
            }
            if (patch["relations"] is JArray rels)
            {
                ax.Relations.Clear();
                MetaclassJson.AllowDuplicates(ax.Relations);
                foreach (var r in rels.OfType<JObject>()) ax.Relations.Add(BuildRelation(r));
            }
            if (patch["tableReferences"] is JArray trs)
            {
                ax.TableReferences.Clear();
                MetaclassJson.AllowDuplicates(ax.TableReferences);
                foreach (var t in trs.OfType<JObject>()) ax.TableReferences.Add(BuildTableReference(t));
            }
        }

        // ===================================================================
        // READ — metaclass → JSON (matches CreateEdtRequest shape)
        // ===================================================================
        private static JObject ToDomainJson(AxEdt ax)
        {
            var baseType = ax.GetType().Name.Substring("AxEdt".Length);
            var jo = new JObject
            {
                ["name"] = ax.Name,
                // Service expects camelCase enum value ("string","utcDateTime").
                ["baseType"] = MetaclassJson.ToCamel(baseType),
            };
            if (!string.IsNullOrEmpty(ax.Label)) jo["label"] = ax.Label;
            if (!string.IsNullOrEmpty(ax.HelpText)) jo["helpText"] = ax.HelpText;
            if (!string.IsNullOrEmpty(ax.Extends)) jo["extends"] = ax.Extends;

            // Subtype option block.
            if (SubtypeBlock(baseType) is var sb && sb != null)
            {
                var block = EmitBlock(ax, sb.Value.Props);
                if (block.Count > 0) jo[sb.Value.Block] = block;
            }

            // Advanced block.
            var adv = EmitBlock(ax, AdvancedProps);
            if (adv.Count > 0)
            {
                // IsObsolete + Visibility are non-nullable in the service shape;
                // once the block exists they're always materialized.
                adv["isObsolete"] = TryEnumIsYes(ax, "IsObsolete");
                adv["visibility"] = MetaclassJson.ReadEnumCamel(ax, "Visibility") ?? "public";
                jo["advanced"] = adv;
            }
            else
            {
                // No advanced scalar set — but if IsObsolete/Visibility are
                // non-default we still need the block.
                var obs = TryEnumIsYes(ax, "IsObsolete");
                var vis = MetaclassJson.ReadEnumCamel(ax, "Visibility");
                if (obs || (vis != null && vis != "public"))
                {
                    jo["advanced"] = new JObject
                    {
                        ["isObsolete"] = obs,
                        ["visibility"] = vis ?? "public",
                    };
                }
            }

            // Collections.
            var aes = EmitRelationsArray(ax.ArrayElements, isArrayElement: true);
            if (aes.Count > 0) jo["arrayElements"] = aes;
            var rels = EmitRelationsArray(ax.Relations, isArrayElement: false);
            if (rels.Count > 0) jo["relations"] = rels;
            var trs = EmitTableRefsArray(ax.TableReferences);
            if (trs.Count > 0) jo["tableReferences"] = trs;

            return jo;
        }

        private static JObject EmitBlock(object source, (string Prop, K Kind)[] props)
        {
            var o = new JObject();
            foreach (var (prop, kind) in props)
            {
                var pi = source.GetType().GetProperty(prop);
                if (pi == null) continue;
                var raw = pi.GetValue(source);
                if (raw == null) continue;
                var key = MetaclassJson.ToCamel(prop);
                switch (kind)
                {
                    case K.Int:
                        if (raw is int n && n != 0) o[key] = n;
                        break;
                    case K.Str:
                        if (raw is string s && s.Length > 0) o[key] = s;
                        break;
                    case K.BoolYesNo:
                        // metaclass NoYes / AutoNoYes → emit bool only for Yes/No.
                        var sv = raw.ToString();
                        if (sv == "Yes") o[key] = true;
                        else if (sv == "No") o[key] = false;
                        break;
                    case K.EnumCamel:
                        var ev = raw.ToString();
                        // Suppress the metaclass "neutral" defaults so the block
                        // mirrors a request that omitted them.
                        if (!string.IsNullOrEmpty(ev) && ev != "Auto" && ev != "None")
                            o[key] = MetaclassJson.ToCamel(ev);
                        break;
                }
            }
            return o;
        }

        private static bool TryEnumIsYes(object source, string prop)
            => source.GetType().GetProperty(prop)?.GetValue(source)?.ToString() == "Yes";

        private static JArray EmitRelationsArray(IEnumerable col, bool isArrayElement)
        {
            var arr = new JArray();
            foreach (var item in col)
            {
                if (isArrayElement)
                {
                    var ae = (AxEdtArrayElement)item;
                    var o = new JObject { ["name"] = ae.Name };
                    if (ae.Index != 0) o["index"] = ae.Index;
                    if (!string.IsNullOrEmpty(ae.Label)) o["label"] = ae.Label;
                    if (!string.IsNullOrEmpty(ae.HelpText)) o["helpText"] = ae.HelpText;
                    if (!string.IsNullOrEmpty(ae.CollectionLabel)) o["collectionLabel"] = ae.CollectionLabel;
                    if (!string.IsNullOrEmpty(ae.Tags)) o["tags"] = ae.Tags;
                    var r = EmitRelationsArray(ae.Relations, false);
                    if (r.Count > 0) o["relations"] = r;
                    var t = EmitTableRefsArray(ae.TableReferences);
                    if (t.Count > 0) o["tableReferences"] = t;
                    arr.Add(o);
                }
                else
                {
                    var rel = (AxEdtRelation)item;
                    var o = new JObject
                    {
                        ["table"] = rel.Table,
                        ["relatedField"] = rel.RelatedField,
                    };
                    if (rel.GetType().Name == "AxEdtRelationFixed")
                    {
                        var v = rel.GetType().GetProperty("Value")?.GetValue(rel) as string;
                        if (!string.IsNullOrEmpty(v)) o["fixedValue"] = v;
                    }
                    if (!string.IsNullOrEmpty(rel.Tags)) o["tags"] = rel.Tags;
                    arr.Add(o);
                }
            }
            return arr;
        }

        private static JArray EmitTableRefsArray(IEnumerable col)
        {
            var arr = new JArray();
            foreach (var item in col)
            {
                var tr = (AxEdtTableReference)item;
                var o = new JObject
                {
                    ["table"] = tr.Table,
                    ["relatedField"] = tr.RelatedField,
                };
                if (tr.GetType().Name == "AxEdtTableReferenceFilter")
                {
                    var v = tr.GetType().GetProperty("Value")?.GetValue(tr) as string;
                    if (!string.IsNullOrEmpty(v)) o["filterValue"] = v;
                }
                if (!string.IsNullOrEmpty(tr.Tags)) o["tags"] = tr.Tags;
                arr.Add(o);
            }
            return arr;
        }

        // ===================================================================
        private static T Instantiate<T>(string simpleName, string errMsg) where T : class
        {
            var type = typeof(AxEdt).Assembly.GetType(MetaNs + simpleName);
            if (type == null) throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, errMsg);
            return (T)Activator.CreateInstance(type)!;
        }

        private static string Pascal(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
