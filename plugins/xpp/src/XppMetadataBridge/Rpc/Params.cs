using Newtonsoft.Json.Linq;

namespace XppMetadataBridge.Rpc
{
    /// <summary>
    /// Helpers for the small ritual every handler performs at entry:
    /// confirm @params is an object, then pluck the required string fields
    /// out with a uniform error message. Cuts ~6 lines off each handler
    /// without hiding anything important.
    /// </summary>
    internal static class Params
    {
        public static JObject Require(JToken? @params)
        {
            if (@params is not JObject p)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "params must be an object");
            return p;
        }

        public static string RequireString(JObject p, string name)
        {
            var v = p[name]?.Value<string>();
            if (string.IsNullOrWhiteSpace(v))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"'{name}' is required");
            return v!;
        }

        public static string? OptionalString(JObject p, string name)
            => p[name]?.Value<string>();

        public static JObject RequireObject(JObject p, string name)
        {
            if (p[name] is not JObject o)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"'{name}' must be an object");
            return o;
        }
    }

    /// <summary>
    /// Wire-format mapping for <see cref="XppMetadataBridge.Metadata.ProviderSource"/>.
    /// Disk and Custom both come back as <c>"disk"</c> on the wire — callers
    /// don't care whether an object was under the writable workspace or the
    /// read-mostly packages path, only whether it came from on-disk XML
    /// (source-bearing) or the runtime compiled-DLL view (no source).
    /// </summary>
    internal static class SourceWire
    {
        public const string Disk = "disk";
        public const string Runtime = "runtime";

        public static string From(XppMetadataBridge.Metadata.ProviderSource s)
            => s == XppMetadataBridge.Metadata.ProviderSource.Runtime ? Runtime : Disk;
    }
}
