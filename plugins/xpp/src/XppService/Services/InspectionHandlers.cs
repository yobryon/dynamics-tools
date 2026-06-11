using Grpc.Core;
using Microsoft.Data.Sqlite;
using Xpp.Service.Bridge;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Services;

/// <summary>
/// Inspection handlers. Cache-first, with bridge fallback for anything the
/// indexer hasn't seen yet. The two responsibilities are deliberately
/// separated:
///
///   GetObjectMethods       summary list (no bodies) for navigation
///   GetMethodSource        single method body for actual reading
///
/// The split keeps the typical "show me what's available" call cheap and
/// the "give me this specific source" call obvious to the agent.
/// </summary>
public sealed partial class PingGrpcService
{
    public override async Task GetObjectMethods(
        ObjectRef request,
        IServerStreamWriter<MethodSummary> responseStream,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.AxType) ||
            string.IsNullOrWhiteSpace(request.Model))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "name, ax_type, and model are all required"));
        }

        // ---- cache path -------------------------------------------------
        // Pull all methods for the object in one go. We don't bother filtering
        // by anything else - methods.object_id resolves the FK chain, and
        // ax_type / model filters on the join make it precise across
        // multi-model namespaces.
        using var conn = _db.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT m.name, m.signature, m.is_static, m.access_level, m.return_type, m.line_count
                FROM methods m
                JOIN objects o ON o.id = m.object_id
                WHERE o.name = $name AND o.ax_type = $ax AND o.model = $model
                ORDER BY m.name;
            ";
            cmd.Parameters.AddWithValue("$name",  request.Name);
            cmd.Parameters.AddWithValue("$ax",    request.AxType);
            cmd.Parameters.AddWithValue("$model", request.Model);

            using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            var emitted = 0;
            while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new MethodSummary
                {
                    Name = reader.GetString(0),
                    Signature = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    IsStatic = reader.GetInt64(2) != 0,
                    AccessLevel = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ReturnType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    LineCount = (int)reader.GetInt64(5)
                }).ConfigureAwait(false);
                emitted++;
            }
            if (emitted > 0) return;
        }

        // ---- bridge fallback --------------------------------------------
        // Cache was empty for this object - either phase 2 hasn't reached it
        // or the object hasn't been indexed at all. Ask the bridge directly.
        IReadOnlyList<BridgeMethodInfo> methods;
        try
        {
            methods = await _bridgeClient.GetObjectMethodsAsync(
                request.Model, request.AxType, request.Name, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            // ObjectNotFound from the bridge => surface as gRPC NotFound;
            // the agent can disambiguate without retrying.
            throw new RpcException(new Status(
                ex.Code == -32001 ? StatusCode.NotFound : StatusCode.Internal,
                $"bridge: {ex.Message}"));
        }

        foreach (var m in methods)
        {
            await responseStream.WriteAsync(new MethodSummary
            {
                Name = m.Name ?? string.Empty,
                Signature = m.Signature ?? string.Empty,
                IsStatic = m.IsStatic,
                AccessLevel = m.AccessLevel ?? string.Empty,
                ReturnType = m.ReturnType ?? string.Empty,
                LineCount = (m.Source ?? string.Empty).Length == 0 ? 0 : (m.Source ?? string.Empty).Count(c => c == '\n') + 1
            }).ConfigureAwait(false);
        }
    }

    public override async Task<MethodSource> GetMethodSource(
        GetMethodSourceRequest request,
        ServerCallContext context)
    {
        var obj = request.Object;
        if (obj == null ||
            string.IsNullOrWhiteSpace(obj.Name) ||
            string.IsNullOrWhiteSpace(obj.AxType) ||
            string.IsNullOrWhiteSpace(obj.Model) ||
            string.IsNullOrWhiteSpace(request.MethodName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "object.{name, ax_type, model} and method_name are all required"));
        }

        // ---- cache path -------------------------------------------------
        using var conn = _db.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT m.name, m.signature, m.is_static, m.access_level, m.return_type, m.source_code
                FROM methods m
                JOIN objects o ON o.id = m.object_id
                WHERE o.name = $name AND o.ax_type = $ax AND o.model = $model
                  AND m.name = $method
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("$name",   obj.Name);
            cmd.Parameters.AddWithValue("$ax",     obj.AxType);
            cmd.Parameters.AddWithValue("$model",  obj.Model);
            cmd.Parameters.AddWithValue("$method", request.MethodName);

            using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                return new MethodSource
                {
                    Name = reader.GetString(0),
                    Signature = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    IsStatic = reader.GetInt64(2) != 0,
                    AccessLevel = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ReturnType = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    SourceCode = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    FromCache = true
                };
            }
        }

        // ---- bridge fallback --------------------------------------------
        IReadOnlyList<BridgeMethodInfo> methods;
        try
        {
            methods = await _bridgeClient.GetObjectMethodsAsync(
                obj.Model, obj.AxType, obj.Name, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            throw new RpcException(new Status(
                ex.Code == -32001 ? StatusCode.NotFound : StatusCode.Internal,
                $"bridge: {ex.Message}"));
        }

        var match = methods.FirstOrDefault(m =>
            string.Equals(m.Name, request.MethodName, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound,
                $"method '{request.MethodName}' not found on {obj.AxType}:{obj.Name} in model {obj.Model}"));
        }

        return new MethodSource
        {
            Name = match.Name ?? string.Empty,
            Signature = match.Signature ?? string.Empty,
            IsStatic = match.IsStatic,
            AccessLevel = match.AccessLevel ?? string.Empty,
            ReturnType = match.ReturnType ?? string.Empty,
            SourceCode = match.Source ?? string.Empty,
            FromCache = false
        };
    }
}
