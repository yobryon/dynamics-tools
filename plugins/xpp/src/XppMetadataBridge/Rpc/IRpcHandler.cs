using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace XppMetadataBridge.Rpc
{
    /// <summary>
    /// Contract for a single JSON-RPC method handler.
    ///
    /// Handlers receive the raw <c>params</c> JToken (so they can shape-validate
    /// against typed DTOs) and return whatever object should appear under
    /// <c>result</c> on the response. Throwing is fine — the dispatcher catches
    /// and maps to a JSON-RPC error response.
    /// </summary>
    internal interface IRpcHandler
    {
        /// <summary>
        /// The wire method name. Matched case-sensitively against the
        /// incoming request's "method" field.
        /// </summary>
        string Method { get; }

        Task<object?> HandleAsync(JToken? @params, CancellationToken ct);
    }
}
