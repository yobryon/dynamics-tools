using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XppMetadataBridge.Rpc
{
    /// <summary>
    /// JSON-RPC 2.0 request envelope. https://www.jsonrpc.org/specification
    ///
    /// The bridge accepts ONE request object per line on stdin. We don't
    /// support batching today — it complicates concurrency for marginal
    /// benefit when the parent process can just pipeline.
    /// </summary>
    internal sealed class JsonRpcRequest
    {
        [JsonProperty("jsonrpc", Required = Required.Always)]
        public string JsonRpc { get; set; } = "2.0";

        [JsonProperty("method", Required = Required.Always)]
        public string Method { get; set; } = string.Empty;

        /// <summary>
        /// Method parameters. Always an object or null; positional arrays are
        /// rejected as invalid request to keep handler signatures simple.
        /// Stored as a raw JToken so handlers can do their own typed
        /// deserialization with strong shapes.
        /// </summary>
        [JsonProperty("params", NullValueHandling = NullValueHandling.Ignore)]
        public JToken? Params { get; set; }

        /// <summary>
        /// Correlation id. Spec allows string|number|null; treating it as
        /// JToken preserves whatever the client sent and lets us echo it back
        /// unchanged. A missing id indicates a notification (no response).
        /// </summary>
        [JsonProperty("id", NullValueHandling = NullValueHandling.Ignore)]
        public JToken? Id { get; set; }

        public bool IsNotification => Id == null || Id.Type == JTokenType.Null;
    }

    /// <summary>
    /// Successful JSON-RPC response. The result payload is whatever the
    /// handler returned; we serialize it with the bridge's standard settings.
    /// </summary>
    internal sealed class JsonRpcResponse
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonProperty("result")]
        public object? Result { get; set; }

        [JsonProperty("id")]
        public JToken? Id { get; set; }
    }

    /// <summary>
    /// Error response. Mutually exclusive with JsonRpcResponse; the wire
    /// format uses either "result" OR "error", never both.
    /// </summary>
    internal sealed class JsonRpcErrorResponse
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonProperty("error")]
        public JsonRpcError Error { get; set; } = new JsonRpcError();

        [JsonProperty("id")]
        public JToken? Id { get; set; }
    }

    internal sealed class JsonRpcError
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public object? Data { get; set; }
    }

    /// <summary>
    /// Standard JSON-RPC 2.0 error codes plus a server-defined range
    /// (-32000..-32099) for bridge-specific failures. We add new codes here
    /// rather than scattering magic numbers across handlers.
    /// </summary>
    internal static class JsonRpcErrorCodes
    {
        // Spec-defined codes.
        public const int ParseError     = -32700;  // Invalid JSON received.
        public const int InvalidRequest = -32600;  // Not a valid request object.
        public const int MethodNotFound = -32601;  // Method doesn't exist.
        public const int InvalidParams  = -32602;  // Invalid method parameters.
        public const int InternalError  = -32603;  // Unhandled handler exception.

        // Bridge-defined server errors. Range -32000..-32099 is reserved.
        public const int MetadataUnavailable = -32000;  // Provider not initialized.
        public const int ObjectNotFound      = -32001;  // Lookup miss in metadata.
    }
}
