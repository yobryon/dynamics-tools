using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XppMetadataBridge.Rpc
{
    /// <summary>
    /// Reads JSON-RPC requests one-per-line from a TextReader, dispatches them
    /// to registered handlers, and writes responses one-per-line to a TextWriter.
    ///
    /// Two design decisions worth flagging:
    ///
    ///   1. Requests are processed strictly sequentially. The bridge's whole
    ///      reason for existing is to wrap a single-threaded metadata API;
    ///      pipelining at this layer would only invite races. The parent
    ///      process (XppService) is the one that fans out parallel work
    ///      across multiple bridge instances if it ever needs to.
    ///
    ///   2. Anything that goes wrong during a single request becomes a
    ///      JSON-RPC error response — the loop never crashes the process
    ///      over a bad message. The only way out is EOF on the input stream
    ///      (parent process ended) or an unrecoverable I/O error on stdout.
    /// </summary>
    internal sealed class JsonRpcServer
    {
        private readonly TextReader _input;
        private readonly TextWriter _output;
        private readonly Dictionary<string, IRpcHandler> _handlers;
        private readonly JsonSerializerSettings _serializerSettings;

        public JsonRpcServer(TextReader input, TextWriter output, IEnumerable<IRpcHandler> handlers)
        {
            _input = input;
            _output = output;
            _handlers = new Dictionary<string, IRpcHandler>(StringComparer.Ordinal);
            foreach (var h in handlers)
            {
                _handlers[h.Method] = h;
            }

            _serializerSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            };
        }

        public async Task RunAsync(CancellationToken ct)
        {
            Log("server loop started; awaiting requests on stdin");

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    // ReadLineAsync on TextReader doesn't take a CancellationToken
                    // until .NET 7; on net48 we just block. EOF (parent process
                    // closed stdin) is signaled by null.
                    line = await _input.ReadLineAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"input read failed, exiting: {ex.Message}");
                    return;
                }

                if (line == null)
                {
                    Log("stdin closed (EOF) — shutting down");
                    return;
                }

                // Blank lines are tolerated as keep-alives or accidental newlines.
                if (string.IsNullOrWhiteSpace(line)) continue;

                await ProcessOneAsync(line, ct).ConfigureAwait(false);
            }
        }

        private async Task ProcessOneAsync(string line, CancellationToken ct)
        {
            JToken? requestId = null;
            JsonRpcRequest? request = null;

            try
            {
                request = JsonConvert.DeserializeObject<JsonRpcRequest>(line);
                if (request == null)
                {
                    await WriteErrorAsync(null, JsonRpcErrorCodes.InvalidRequest, "Request deserialized to null").ConfigureAwait(false);
                    return;
                }
                requestId = request.Id;
            }
            catch (JsonException jex)
            {
                // Parse errors return id=null per spec, since the client's id
                // wasn't parseable.
                await WriteErrorAsync(null, JsonRpcErrorCodes.ParseError, $"Invalid JSON: {jex.Message}").ConfigureAwait(false);
                return;
            }

            if (request.JsonRpc != "2.0")
            {
                await WriteErrorAsync(requestId, JsonRpcErrorCodes.InvalidRequest, "jsonrpc field must be '2.0'").ConfigureAwait(false);
                return;
            }
            if (string.IsNullOrEmpty(request.Method))
            {
                await WriteErrorAsync(requestId, JsonRpcErrorCodes.InvalidRequest, "method field is required").ConfigureAwait(false);
                return;
            }

            if (!_handlers.TryGetValue(request.Method, out var handler))
            {
                if (request.IsNotification) return; // Notifications get no response, even on error.
                await WriteErrorAsync(requestId, JsonRpcErrorCodes.MethodNotFound, $"Method not found: {request.Method}").ConfigureAwait(false);
                return;
            }

            object? result;
            try
            {
                result = await handler.HandleAsync(request.Params, ct).ConfigureAwait(false);
            }
            catch (JsonRpcException rpcEx)
            {
                // Handlers throw JsonRpcException to surface a typed error
                // code/message without us having to swallow real bugs.
                if (request.IsNotification) return;
                await WriteErrorAsync(requestId, rpcEx.Code, rpcEx.Message, rpcEx.Payload).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Log($"handler {request.Method} threw: {ex}");
                if (request.IsNotification) return;
                // Surface the REAL cause. Reflection-driven metaclass construction
                // wraps the underlying failure in TargetInvocationException, whose
                // own message is the useless "Exception has been thrown by the
                // target of an invocation." Unwrap to the innermost meaningful
                // message so the agent sees e.g. the duplicate-name / abstract-class
                // detail instead of the generic wrapper.
                await WriteErrorAsync(requestId, JsonRpcErrorCodes.InternalError, BestMessage(ex)).ConfigureAwait(false);
                return;
            }

            if (request.IsNotification) return; // No response for notifications.

            await WriteResultAsync(requestId, result).ConfigureAwait(false);
        }

        /// <summary>Walk past reflection / aggregate wrappers (and any layer whose
        /// message is the generic "target of an invocation" text) to the innermost
        /// exception that actually describes the failure.</summary>
        private static string BestMessage(Exception ex)
        {
            var cur = ex;
            while (cur.InnerException != null &&
                   (cur is System.Reflection.TargetInvocationException ||
                    cur is System.AggregateException ||
                    cur.Message.Contains("Exception has been thrown by the target of an invocation")))
            {
                cur = cur.InnerException;
            }
            return cur.Message;
        }

        private async Task WriteResultAsync(JToken? id, object? result)
        {
            var response = new JsonRpcResponse { Id = id, Result = result };
            await WriteAsync(response).ConfigureAwait(false);
        }

        private async Task WriteErrorAsync(JToken? id, int code, string message, object? data = null)
        {
            var response = new JsonRpcErrorResponse
            {
                Id = id,
                Error = new JsonRpcError { Code = code, Message = message, Data = data }
            };
            await WriteAsync(response).ConfigureAwait(false);
        }

        private async Task WriteAsync(object payload)
        {
            var json = JsonConvert.SerializeObject(payload, _serializerSettings);

            // Single-line framing: each response is exactly one line of JSON,
            // terminated by \n. The reader on the other end splits on newlines.
            await _output.WriteLineAsync(json).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }

        private static void Log(string message)
        {
            // Everything that isn't a JSON-RPC frame goes to stderr so the
            // parent process can capture diagnostics without corrupting the
            // protocol stream.
            Console.Error.WriteLine($"[bridge] {message}");
        }
    }

    /// <summary>
    /// Handlers throw this when they want to surface a specific JSON-RPC
    /// error code rather than letting an arbitrary exception become a
    /// generic -32603 InternalError.
    /// </summary>
    internal sealed class JsonRpcException : Exception
    {
        public int Code { get; }

        /// <summary>
        /// Optional structured payload included in the JSON-RPC error
        /// response under "error.data". Renamed from Data to avoid
        /// shadowing Exception.Data (the inherited string-keyed dictionary).
        /// </summary>
        public object? Payload { get; }

        public JsonRpcException(int code, string message, object? payload = null) : base(message)
        {
            Code = code;
            Payload = payload;
        }
    }
}
