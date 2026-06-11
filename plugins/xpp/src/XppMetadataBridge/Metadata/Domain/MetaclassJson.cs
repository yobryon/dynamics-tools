using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// Shared JSON ↔ MS metaclass coercion helpers used by the bridge-side
    /// domain mappers. The service serializes its typed request records with
    /// System.Text.Json + JsonStringEnumConverter(CamelCase), so:
    ///   - enum values arrive camelCased ("upperCase", "auto", "aMPM")
    ///   - service bool? fields often map to metaclass NoYes / AutoNoYes enums
    ///   - service string fields map 1:1
    /// These helpers centralize the type coercion so each mapper stays a
    /// straight property list rather than re-deriving the conversion rules.
    /// </summary>
    internal static class MetaclassJson
    {
        /// <summary>
        /// Assign a JSON token to a named property on the metaclass target,
        /// coercing JSON kind → property type. No-ops on null token, unknown
        /// property, read-only property, or an unconvertible kind/type pair.
        /// </summary>
        /// <summary>
        /// Request-scoped switch: when true, <see cref="EmitDefaulted"/> stops
        /// suppressing values that equal the metaclass default and emits them
        /// anyway. Used only for the drift round-trip read, so a property the
        /// caller set to its default value compares equal (request value present
        /// on both sides) instead of looking "dropped." The bridge processes one
        /// request at a time per worker, so a thread-static flag is safe; the
        /// getDomainObject handler sets/clears it in a try/finally.
        /// </summary>
        [ThreadStatic]
        public static bool IncludeDefaults;

        public static void Assign(object target, string propName, JToken? value)
        {
            if (value == null || value.Type == JTokenType.Null) return;
            var prop = ResolveProperty(target.GetType(), propName);
            if (prop == null || !prop.CanWrite) return;
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            try
            {
                var converted = Coerce(t, value);
                if (converted != null) prop.SetValue(target, converted);
            }
            catch { /* type mismatch — leave the property at its default */ }
        }

        /// <summary>
        /// Resolve a metaclass property by name, exact-case first then
        /// case-insensitive. Callers pass <c>Pascal(jsonKey)</c>, which only
        /// capitalizes the first letter — so an acronym property like
        /// <c>EDTRelation</c> (whose camelCase JSON form is "eDTRelation")
        /// is reached as "EdtRelation" and would silently miss. The
        /// case-insensitive fallback binds it, killing a class of silent
        /// drops when a caller hand-cases a property key. Falls back to exact
        /// on an ambiguous case-only collision (none known in the metaclass).
        /// </summary>
        private static System.Reflection.PropertyInfo? ResolveProperty(Type type, string propName)
        {
            var exact = type.GetProperty(propName);
            if (exact != null) return exact;
            try
            {
                return type.GetProperty(propName,
                    System.Reflection.BindingFlags.IgnoreCase
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance);
            }
            catch (System.Reflection.AmbiguousMatchException) { return null; }
        }

        private static object? Coerce(Type t, JToken value)
        {
            if (t == typeof(string))
                return value.Type == JTokenType.String ? (string)value! : value.ToString();
            if (t == typeof(int) || t == typeof(long))
            {
                if (value.Type == JTokenType.Integer) return (int)value;
                // Some domain fields model an int-on-the-metaclass value as a
                // string (e.g. AxTableRelationConstraintFixed.Value). Parse it.
                if (value.Type == JTokenType.String &&
                    int.TryParse((string)value!, System.Globalization.NumberStyles.Integer,
                                 System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
                return null;
            }
            if (t == typeof(bool))
                return value.Type == JTokenType.Boolean ? (object)(bool)value : null;
            if (t.IsEnum)
            {
                switch (value.Type)
                {
                    case JTokenType.Boolean:
                        // Service bool? → metaclass NoYes / AutoNoYes.
                        var yn = (bool)value ? "Yes" : "No";
                        return Enum.IsDefined(t, yn) ? Enum.Parse(t, yn) : null;
                    case JTokenType.String:
                        return Enum.Parse(t, (string)value!, ignoreCase: true);
                    case JTokenType.Integer:
                        return Enum.ToObject(t, (int)value);
                }
            }
            return null;
        }

        /// <summary>
        /// Read an enum-typed metaclass property and return its value as the
        /// camelCase string the service's JsonStringEnumConverter would emit,
        /// so drift comparison (request string vs round-trip string) matches.
        /// Returns null when the property is absent or its value is empty.
        /// </summary>
        public static string? ReadEnumCamel(object source, string propName)
        {
            var prop = source.GetType().GetProperty(propName);
            var val = prop?.GetValue(source)?.ToString();
            return string.IsNullOrEmpty(val) ? null : ToCamel(val);
        }

        /// <summary>Lowercase the first character (matches JsonNamingPolicy.CamelCase).</summary>
        public static string ToCamel(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        /// <summary>
        /// How a metaclass property value renders into the domain JSON:
        ///   Int       — integer scalar
        ///   Bool      — NoYes/AutoNoYes → true/false (domain models it as bool?)
        ///   EnumCamel — domain models it as an enum → camelCase string
        ///   Raw       — domain models it as a string (even when the metaclass
        ///               backs it with an enum, e.g. GDPR) → ToString verbatim
        /// </summary>
        public enum EmitAs { Int, Bool, EnumCamel, Raw }

        /// <summary>
        /// Emit <paramref name="source"/>.<paramref name="prop"/> into
        /// <paramref name="o"/>[<paramref name="key"/>], suppressing the value
        /// when it equals the same property on <paramref name="reference"/>
        /// (a freshly-constructed metaclass instance — i.e. the natural
        /// default). Suppressing defaults keeps Read output minimal AND
        /// guarantees Read/Build mutual consistency: Build only sets the
        /// non-default props Read emitted, a fresh instance carries the rest
        /// at default, so the next Read suppresses them identically.
        /// </summary>
        public static void EmitDefaulted(JObject o, object source, object? reference, string prop, string key, EmitAs kind)
        {
            var pi = source.GetType().GetProperty(prop);
            if (pi == null) return;
            var sv = pi.GetValue(source);
            if (sv == null) return;
            // Bool (NoYes) has no "unset" sentinel — both Yes and No are
            // meaningful values an agent may explicitly send (e.g. a unique
            // index's allowDuplicates=false). Suppressing No-equals-default
            // would elide a load-bearing value and trip spurious drift, so we
            // always emit bools. The one-directional drift detector tolerates
            // the extra round-trip fields when the agent omitted them.
            // Other kinds DO suppress their default/sentinel against the
            // reference instance (enum NotSpecified/None/Auto, int 0, "").
            if (kind != EmitAs.Bool && reference != null && !IncludeDefaults)
            {
                var rv = reference.GetType().GetProperty(prop)?.GetValue(reference);
                if (string.Equals(sv.ToString(), rv?.ToString(), StringComparison.Ordinal)) return;
            }
            switch (kind)
            {
                case EmitAs.Int:
                    if (sv is int n && n != 0) o[key] = n;
                    break;
                case EmitAs.Bool:
                    var bs = sv.ToString();
                    if (bs == "Yes") o[key] = true;
                    else if (bs == "No") o[key] = false;
                    break;
                case EmitAs.EnumCamel:
                    var e = sv.ToString();
                    if (!string.IsNullOrEmpty(e)) o[key] = ToCamel(e);
                    break;
                case EmitAs.Raw:
                    var s = sv.ToString();
                    if (!string.IsNullOrEmpty(s)) o[key] = s;
                    break;
            }
        }

        public static string? GetString(JObject o, string key)
            => o[key] is JToken t && t.Type == JTokenType.String ? (string)t! : null;

        /// <summary>
        /// Disable a KeyedObjectCollection's duplicate-key guard before bulk
        /// add. MS-shipped metadata can carry collection entries that share a
        /// computed key (e.g. two AxEdtRelationFixed to the same Table#Field,
        /// differing only by Value). MS's own FromFile deserializer flips this
        /// flag off to populate such collections; the typed Create path keeps
        /// it on by default and would throw on the duplicate. No-ops when the
        /// property is absent.
        /// </summary>
        public static void AllowDuplicates(object keyedCollection)
        {
            var prop = keyedCollection.GetType().GetProperty("DuplicateCheckingEnabled");
            try { prop?.SetValue(keyedCollection, false); } catch { /* best effort */ }
        }
    }
}
