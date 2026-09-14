using Newtonsoft.Json.Linq;

namespace XppMetadataBridge.Rpc
{
    /// <summary>
    /// Parameter-plucking helpers, same ritual as the metadata bridge's copy
    /// minus the metadata-specific bits (that file can't be linked wholesale
    /// because it references the metadata provider types).
    /// </summary>
    internal static class Params
    {
        public static JObject Require(JToken? @params)
        {
            if (@params is not JObject p)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "params must be an object");
            return p;
        }

        public static JObject Optional(JToken? @params) => @params as JObject ?? new JObject();

        public static string RequireString(JObject p, string name)
        {
            var v = p[name]?.Value<string>();
            if (string.IsNullOrWhiteSpace(v))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"'{name}' is required");
            return v!;
        }

        public static string? OptionalString(JObject p, string name) => p[name]?.Value<string>();

        public static int OptionalInt(JObject p, string name, int fallback)
        {
            var t = p[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return t.Value<int>();
        }

        public static bool OptionalBool(JObject p, string name, bool fallback)
        {
            var t = p[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return t.Value<bool>();
        }

        public static string[] OptionalStrings(JObject p, string name)
        {
            if (p[name] is not JArray a) return new string[0];
            var list = new System.Collections.Generic.List<string>();
            foreach (var t in a) { var s = t?.Value<string>(); if (!string.IsNullOrWhiteSpace(s)) list.Add(s!); }
            return list.ToArray();
        }
    }
}
