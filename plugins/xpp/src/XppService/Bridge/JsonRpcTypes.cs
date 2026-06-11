using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Xpp.Service.Bridge;

// =============================================================================
// JSON-RPC 2.0 wire types — mirror of the ones in XppMetadataBridge.
//
// The bridge owns the canonical schema; these are the parsing-side equivalents
// the service uses to talk to it. Kept deliberately tiny: System.Text.Json
// handles serialization; we only declare the shape.
// =============================================================================

internal sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    /// <summary>
    /// Params is always an object on our side; we never use positional arrays.
    /// </summary>
    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Params { get; init; }

    [JsonPropertyName("id")]
    public required long Id { get; init; }
}

internal sealed class JsonRpcResponseEnvelope
{
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpc { get; set; }

    /// <summary>
    /// Spec allows string|number|null. We always send long; the bridge echoes
    /// it back as a number. Keep as JsonNode so we can tolerate any spec-valid
    /// shape and surface a clean error if it's missing.
    /// </summary>
    [JsonPropertyName("id")]
    public JsonNode? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonNode? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcErrorBody? Error { get; set; }
}

internal sealed class JsonRpcErrorBody
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public JsonNode? Data { get; set; }
}

/// <summary>
/// Thrown when the bridge returns a JSON-RPC error in response to a service
/// request. Carries the error code so callers can map specific failures
/// (e.g. ObjectNotFound) into typed gRPC error responses.
/// </summary>
public sealed class BridgeRpcException : Exception
{
    public int Code { get; }
    public JsonNode? Payload { get; }

    public BridgeRpcException(int code, string message, JsonNode? payload)
        : base(message)
    {
        Code = code;
        Payload = payload;
    }
}
