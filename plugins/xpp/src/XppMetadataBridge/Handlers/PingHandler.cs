using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// Health-check / wire-test handler. Returns the echo value the caller
    /// sent (or empty if none), the current server UTC time, and the bridge
    /// assembly version. Used by smoke tests and the parent service's
    /// readiness probe to confirm the bridge is up and responsive.
    /// </summary>
    internal sealed class PingHandler : IRpcHandler
    {
        public string Method => "ping";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            // params is optional. When present, echo back whatever was in
            // the "echo" field. Wrong shapes (e.g. positional array) come
            // through as JArray/JValue here and we just ignore them rather
            // than rejecting — ping is meant to be permissive.
            var echo = string.Empty;
            if (@params is JObject obj && obj["echo"] is JToken echoToken && echoToken.Type == JTokenType.String)
            {
                echo = echoToken.Value<string>() ?? string.Empty;
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

            return Task.FromResult<object?>(new
            {
                echo,
                serverTime = DateTime.UtcNow.ToString("O"),
                bridgeVersion = version
            });
        }
    }
}
