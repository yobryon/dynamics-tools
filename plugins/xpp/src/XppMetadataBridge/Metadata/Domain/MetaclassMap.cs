using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// Shared plumbing for the bridge-side metaclass domain mappers. These
    /// helpers were originally copy-pasted into each per-type mapper; once the
    /// six core types proved the pattern they were lifted here. Pure utility —
    /// no per-type knowledge.
    /// </summary>
    internal static class MetaclassMap
    {
        public const string MetaNs = "Microsoft.Dynamics.AX.Metadata.MetaModel.";

        // Any MetaModel type anchors the assembly the metaclasses live in.
        private static readonly Assembly MetaAssembly = typeof(AxEnum).Assembly;

        // ---- instantiation -------------------------------------------------
        public static Type ResolveType(string simpleName)
            => MetaAssembly.GetType(MetaNs + simpleName)
               ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams,
                   $"metaclass type '{simpleName}' not found");

        public static object Instantiate(string simpleName)
            => Activator.CreateInstance(ResolveType(simpleName))!;

        public static T Instantiate<T>(string simpleName) where T : class
            => (T)Instantiate(simpleName);

        // ---- fresh-default reference cache (for EmitDefaulted suppression) --
        private static readonly Dictionary<Type, object> _refCache = new();
        public static object Reference(Type t)
        {
            if (!_refCache.TryGetValue(t, out var inst))
            {
                inst = Activator.CreateInstance(t)!;
                _refCache[t] = inst;
            }
            return inst;
        }

        // ---- string helpers ------------------------------------------------
        public static string Pascal(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        public static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        // ---- scalar assignment ---------------------------------------------
        /// <summary>Assign every property of a JSON block by PascalCased name,
        /// skipping the given keys. MetaclassJson.Assign coerces + no-ops
        /// unknown/read-only props.</summary>
        public static void AssignAll(object target, JObject block, ISet<string>? skip = null)
        {
            foreach (var p in block.Properties())
            {
                if (skip != null && skip.Contains(p.Name)) continue;
                MetaclassJson.Assign(target, Pascal(p.Name), p.Value);
            }
        }

        public static void SetName(object o, string name) => o.GetType().GetProperty("Name")?.SetValue(o, name);
        public static string GetName(object o) => (string)(o.GetType().GetProperty("Name")?.GetValue(o) ?? string.Empty);

        // ---- collection helpers --------------------------------------------
        /// <summary>Reflect the element-typed Add(T) method of a KeyedObjectCollection.</summary>
        public static MethodInfo? AddMethodFor(object coll, string elementSimpleName)
        {
            var et = MetaAssembly.GetType(MetaNs + elementSimpleName);
            return et == null ? null : coll.GetType().GetMethod("Add", new[] { et });
        }

        public static void AddTo(MethodInfo? add, object coll, object item) => add?.Invoke(coll, new[] { item });

        // ---- AxMethod (source-bearing) collections -------------------------
        public static void AddMethods(object methodColl, JArray? methods)
        {
            if (methods == null) return;
            MetaclassJson.AllowDuplicates(methodColl);
            var add = methodColl.GetType().GetMethod("Add", new[] { typeof(AxMethod) });
            foreach (var m in methods.OfType<JObject>())
            {
                var am = new AxMethod { Name = (string?)m["name"] ?? string.Empty };
                if (m["source"] is JToken s && s.Type == JTokenType.String) am.Source = (string)s!;
                add?.Invoke(methodColl, new object[] { am });
            }
        }

        public static JArray EmitMethods(object? methodColl)
        {
            var arr = new JArray();
            if (methodColl is IEnumerable en)
                foreach (var m in en)
                {
                    var am = (AxMethod)m;
                    var o = new JObject { ["name"] = am.Name };
                    if (!string.IsNullOrEmpty(am.Source)) o["source"] = am.Source;
                    arr.Add(o);
                }
            return arr;
        }

        // ---- property-bag pattern (typed fields + otherProperties dump) ----
        // A typed field maps a domain JSON key to a metaclass property, with a
        // flag for whether the domain models it as a bool (NoYes -> true/false)
        // vs raw string. (EnumCamel-typed fields are handled per-mapper since
        // they're rarer; Raw covers domain-string-backed-by-metaclass-enum.)
        public readonly struct TypedField
        {
            public readonly string Key;
            public readonly string Prop;
            public readonly MetaclassJson.EmitAs Kind;
            public TypedField(string key, string prop, MetaclassJson.EmitAs kind) { Key = key; Prop = prop; Kind = kind; }
        }

        public static TypedField Bool(string key, string prop) => new(key, prop, MetaclassJson.EmitAs.Bool);
        public static TypedField Raw(string key, string prop) => new(key, prop, MetaclassJson.EmitAs.Raw);
        public static TypedField Enum(string key, string prop) => new(key, prop, MetaclassJson.EmitAs.EnumCamel);
        public static TypedField Int(string key, string prop) => new(key, prop, MetaclassJson.EmitAs.Int);

        /// <summary>Write the typed fields + any otherProperties dict back onto
        /// the metaclass instance.</summary>
        public static void ApplyBag(object target, JObject json, TypedField[] typed)
        {
            foreach (var f in typed) MetaclassJson.Assign(target, f.Prop, json[f.Key]);
            if (json["otherProperties"] is JObject op)
                foreach (var kv in op.Properties())
                    MetaclassJson.Assign(target, kv.Name, kv.Value);
        }

        /// <summary>Emit the typed fields (reference-suppressed) plus every
        /// other non-structural scalar metaclass property into otherProperties
        /// (PascalName -> ToString string).</summary>
        public static void EmitBag(JObject o, object source, TypedField[] typed, ISet<string> structural)
        {
            var reference = Reference(source.GetType());
            foreach (var f in typed)
                MetaclassJson.EmitDefaulted(o, source, reference, f.Prop, f.Key, f.Kind);

            var typedProps = new HashSet<string>(typed.Select(t => t.Prop), StringComparer.Ordinal);
            var op = new JObject();
            foreach (var pi in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanWrite) continue;
                if (typedProps.Contains(pi.Name) || structural.Contains(pi.Name)) continue;
                if (!IsScalar(pi.PropertyType)) continue;
                MetaclassJson.EmitDefaulted(op, source, reference, pi.Name, pi.Name, MetaclassJson.EmitAs.Raw);
            }
            if (op.Count > 0) o["otherProperties"] = op;
        }

        public static bool IsScalar(Type t)
            => t == typeof(string) || t == typeof(int) || t == typeof(bool) || t.IsEnum;

        // ---- AccessGrant (SubscriberAccessLevel) value-type member ----------
        private static readonly string[] AccessPerms = { "Read", "Create", "Update", "Delete", "Correct", "Invoke" };

        /// <summary>Apply an access-grant JObject onto the parent's named
        /// AccessGrant struct member. AccessGrant is a value type — box once,
        /// mutate that box, unbox back through the setter.</summary>
        public static void ApplyAccessGrant(object meta, string propName, JObject grantJson)
        {
            var prop = meta.GetType().GetProperty(propName);
            if (prop == null) return;
            object boxed = prop.GetValue(meta)!;
            foreach (var perm in AccessPerms)
                MetaclassJson.Assign(boxed, perm, grantJson[MetaclassJson.ToCamel(perm)]);
            prop.SetValue(meta, boxed);
        }

        /// <summary>Emit the parent's named AccessGrant struct member as a
        /// JObject (Unset permissions omitted, values camelCased).</summary>
        public static JObject EmitAccessGrant(object meta, string propName)
        {
            var o = new JObject();
            var grant = meta.GetType().GetProperty(propName)?.GetValue(meta);
            if (grant == null) return o;
            foreach (var perm in AccessPerms)
            {
                var v = grant.GetType().GetProperty(perm)?.GetValue(grant)?.ToString();
                if (string.IsNullOrEmpty(v)) continue;
                if (v == "Unset")
                {
                    // Unset is the metaclass form of the domain's "NoAccess" default
                    // (MS strips NoAccess to absent on write; it reads back as Unset).
                    // Suppress it on a normal read for clean output, but under the
                    // drift round-trip emit it as "noAccess" so a caller who set
                    // NoAccess explicitly compares equal instead of seeing false drift.
                    if (MetaclassJson.IncludeDefaults) o[MetaclassJson.ToCamel(perm)] = "noAccess";
                    continue;
                }
                o[MetaclassJson.ToCamel(perm)] = MetaclassJson.ToCamel(v);
            }
            return o;
        }

        public static void ApplySubscriberAccess(object meta, JObject sal) => ApplyAccessGrant(meta, "SubscriberAccessLevel", sal);
        public static JObject EmitSubscriberAccess(object meta) => EmitAccessGrant(meta, "SubscriberAccessLevel");

        // ---- reference collections ({Name [+Enabled +Tags]} items) ---------
        public static void BuildRefs(object parent, string collProp, string elementSimpleName, JArray? arr)
        {
            if (arr == null) return;
            var coll = parent.GetType().GetProperty(collProp)?.GetValue(parent);
            if (coll == null) return;
            // Replace-wholesale: on a PATCH the metaclass collection already holds
            // the current members, so clear before re-adding — otherwise the
            // provided list APPENDS onto the existing one (e.g. patching a role's
            // duties to [existing, new] yielded [existing, existing, new], which
            // failed compile as a duplicate AxSecurityDutyReference). On CREATE the
            // collection is empty, so the clear is a no-op. arr==null above already
            // means "not provided -> preserve", matching the documented semantics.
            if (coll is System.Collections.IList il) il.Clear();
            MetaclassJson.AllowDuplicates(coll);
            var add = AddMethodFor(coll, elementSimpleName);
            foreach (var rj in arr.OfType<JObject>())
            {
                var r = Instantiate(elementSimpleName);
                SetName(r, (string?)rj["name"] ?? string.Empty);
                MetaclassJson.Assign(r, "Enabled", rj["enabled"]);
                MetaclassJson.Assign(r, "Tags", rj["tags"]);
                AddTo(add, coll, r);
            }
        }

        public static JArray EmitRefs(object parent, string collProp)
        {
            var arr = new JArray();
            var coll = parent.GetType().GetProperty(collProp)?.GetValue(parent);
            if (coll is not IEnumerable en) return arr;
            foreach (var r in en)
            {
                var reference = Reference(r.GetType());
                var o = new JObject { ["name"] = GetName(r) };
                MetaclassJson.EmitDefaulted(o, r, reference, "Enabled", "enabled", MetaclassJson.EmitAs.Bool);
                MetaclassJson.EmitDefaulted(o, r, reference, "Tags", "tags", MetaclassJson.EmitAs.Raw);
                arr.Add(o);
            }
            return arr;
        }
    }
}
