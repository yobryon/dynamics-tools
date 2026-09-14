using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XppMetadataBridge.Rpc
{
    /// <summary>
    /// JSON-RPC loop for the debug bridge. Same wire format as
    /// <see cref="JsonRpcServer"/> (one request per line in, one response per
    /// line out) but requests are dispatched CONCURRENTLY.
    ///
    /// The metadata bridge is deliberately sequential because it wraps a
    /// single-threaded API. The debug bridge must not be: a debugger call can
    /// legitimately block for a long time (a wait for a hit; an automation call
    /// the paused VS is slow to answer), and the one request that has to get
    /// through regardless is "detach / release the AOS". With a sequential loop
    /// that request queues behind the blocked one and the box stays frozen --
    /// which is exactly what happened.
    ///
    /// Each request runs on its own task; the session serializes what needs
    /// serializing; writes to stdout are serialized here.
    /// </summary>
    internal sealed class ConcurrentJsonRpcServer
    {
        private readonly TextReader _input;
        private readonly TextWriter _output;
        private readonly Dictionary<string, IRpcHandler> _handlers;
        private readonly JsonSerializerSettings _serializerSettings;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

        public ConcurrentJsonRpcServer(TextReader input, TextWriter output, IEnumerable<IRpcHandler> handlers)
        {
            _input = input;
            _output = output;
            _handlers = new Dictionary<string, IRpcHandler>(StringComparer.Ordinal);
            foreach (var h in handlers) _handlers[h.Method] = h;
            _serializerSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
            };
        }

        public async Task RunAsync(CancellationToken ct)
        {
            Log("server loop started (concurrent); awaiting requests on stdin");
            var inFlight = new List<Task>();
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await _input.ReadLineAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log($"input read failed, exiting: {ex.Message}"); break; }
                if (line == null) { Log("stdin closed (EOF) -- shutting down"); break; }
                if (string.IsNullOrWhiteSpace(line)) continue;

                var captured = line;
                inFlight.RemoveAll(t => t.IsCompleted);
                inFlight.Add(Task.Run(() => ProcessOneAsync(captured, ct), ct));
            }
            // Give in-flight handlers a moment; the session's Dispose does the
            // real cleanup (resume + detach) regardless of what is still pending.
            try { await Task.WhenAny(Task.WhenAll(inFlight), Task.Delay(3000)).ConfigureAwait(false); } catch { }
        }

        private async Task ProcessOneAsync(string line, CancellationToken ct)
        {
            JToken? requestId = null;
            JsonRpcRequest? request;
            try
            {
                request = JsonConvert.DeserializeObject<JsonRpcRequest>(line);
                if (request == null) { await WriteErrorAsync(null, JsonRpcErrorCodes.InvalidRequest, "Request deserialized to null").ConfigureAwait(false); return; }
                requestId = request.Id;
            }
            catch (JsonException jex)
            {
                await WriteErrorAsync(null, JsonRpcErrorCodes.ParseError, $"Invalid JSON: {jex.Message}").ConfigureAwait(false);
                return;
            }

            if (request.JsonRpc != "2.0") { await WriteErrorAsync(requestId, JsonRpcErrorCodes.InvalidRequest, "jsonrpc field must be '2.0'").ConfigureAwait(false); return; }
            if (string.IsNullOrEmpty(request.Method)) { await WriteErrorAsync(requestId, JsonRpcErrorCodes.InvalidRequest, "method field is required").ConfigureAwait(false); return; }
            if (!_handlers.TryGetValue(request.Method, out var handler))
            {
                if (request.IsNotification) return;
                await WriteErrorAsync(requestId, JsonRpcErrorCodes.MethodNotFound, $"Method not found: {request.Method}").ConfigureAwait(false);
                return;
            }

            object? result;
            try { result = await handler.HandleAsync(request.Params, ct).ConfigureAwait(false); }
            catch (JsonRpcException rpcEx)
            {
                if (request.IsNotification) return;
                await WriteErrorAsync(requestId, rpcEx.Code, rpcEx.Message, rpcEx.Payload).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Log($"handler {request.Method} threw: {ex.GetType().Name}: {ex.Message}");
                if (request.IsNotification) return;
                var cur = ex;
                while (cur.InnerException != null && (cur is System.Reflection.TargetInvocationException || cur is AggregateException || cur is XppDebugBridge.Vs.VsCallException)) cur = cur.InnerException;
                await WriteErrorAsync(requestId, JsonRpcErrorCodes.InternalError, cur.Message).ConfigureAwait(false);
                return;
            }
            if (request.IsNotification) return;
            await WriteAsync(new JsonRpcResponse { Id = requestId, Result = result }).ConfigureAwait(false);
        }

        private Task WriteErrorAsync(JToken? id, int code, string message, object? data = null)
            => WriteAsync(new JsonRpcErrorResponse { Id = id, Error = new JsonRpcError { Code = code, Message = message, Data = data } });

        private async Task WriteAsync(object payload)
        {
            var json = JsonConvert.SerializeObject(payload, _serializerSettings);
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _output.WriteLineAsync(json).ConfigureAwait(false);
                await _output.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }

        private static void Log(string m) => Console.Error.WriteLine("[debug-bridge] " + m);
    }
}
