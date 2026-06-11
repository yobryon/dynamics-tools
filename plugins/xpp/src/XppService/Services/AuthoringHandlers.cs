using Grpc.Core;
using Xpp.Service.Bridge;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Services;

/// <summary>
/// Authoring handlers. Thin pass-throughs to the bridge - the metadata
/// provider in net48-land does the actual work; the service is here to
/// expose the wire surface and translate errors.
///
/// There's deliberately no caching layer on the write side. Writes are
/// rare enough relative to reads that the round-trip overhead is fine,
/// and any client-side cache would race the file-system watcher on the
/// next index rebuild.
/// </summary>
public sealed partial class PingGrpcService
{
    public override async Task<ObjectXml> GetObjectXml(ObjectRef request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));
        if (string.IsNullOrWhiteSpace(request.AxType))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ax_type is required"));

        string xml;
        try
        {
            xml = await _bridgeClient.GetObjectXmlAsync(
                request.AxType, request.Name,
                string.IsNullOrWhiteSpace(request.Model) ? null : request.Model,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            throw MapBridgeError(ex);
        }

        // If the caller didn't pass a model, resolve from the index so
        // the response carries something useful. Previously this was
        // echoed back as empty string, which surprised agents who'd
        // expect "give me the XML for ContosoRetail:CustTable" to
        // confirm-back what model it found.
        var resolvedModel = request.Model;
        if (string.IsNullOrEmpty(resolvedModel))
        {
            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT model FROM objects WHERE name=$n AND ax_type=$t LIMIT 1";
                cmd.Parameters.AddWithValue("$n", request.Name);
                cmd.Parameters.AddWithValue("$t", request.AxType);
                var raw = cmd.ExecuteScalar();
                if (raw != null && raw is not DBNull) resolvedModel = raw.ToString() ?? string.Empty;
            }
            catch { /* best-effort — empty model is acceptable fallback */ }
        }

        return new ObjectXml
        {
            Ref = new ObjectRef
            {
                Name = request.Name,
                AxType = request.AxType,
                Model = resolvedModel ?? string.Empty
            },
            Xml = xml
        };
    }

    public override async Task<WriteObjectResponse> CreateObject(WriteObjectRequest request, ServerCallContext context)
    {
        ValidateWriteRequest(request);
        string name;
        try
        {
            name = await _bridgeClient.CreateObjectAsync(
                request.AxType, request.Model, request.Xml, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            throw MapBridgeError(ex);
        }

        // Index write-through — same as the typed CreateDomainObject path. The
        // raw XML write surface previously skipped this, so the index (incl.
        // reference edges) went stale until the next sweep — which then made a
        // subsequent delete refuse on references the write had just removed.
        await _lifecycle.EnqueueWriteThroughAsync(request.Model, request.AxType, name, context.CancellationToken)
            .ConfigureAwait(false);

        return new WriteObjectResponse
        {
            AxType = request.AxType,
            Model = request.Model,
            Name = name
        };
    }

    public override async Task<WriteObjectResponse> UpdateObject(WriteObjectRequest request, ServerCallContext context)
    {
        ValidateWriteRequest(request);
        string name;
        try
        {
            name = await _bridgeClient.UpdateObjectAsync(
                request.AxType, request.Model, request.Xml, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            throw MapBridgeError(ex);
        }

        // Index write-through — refresh the just-written object (and its
        // reference edges) so a follow-up read/delete sees the new state. Raw
        // XML updates previously left the index stale: a reference removed by
        // this update still showed as an inbound ref and blocked a delete.
        await _lifecycle.EnqueueWriteThroughAsync(request.Model, request.AxType, name, context.CancellationToken)
            .ConfigureAwait(false);

        return new WriteObjectResponse
        {
            AxType = request.AxType,
            Model = request.Model,
            Name = name
        };
    }

    private static void ValidateWriteRequest(WriteObjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AxType))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ax_type is required"));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "model is required"));
        if (string.IsNullOrWhiteSpace(request.Xml))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "xml is required"));
    }

    private static RpcException MapBridgeError(BridgeRpcException ex)
    {
        // Bridge error codes - mirror the JsonRpcErrorCodes constants used
        // on the net48 side. NotFound is the one structural case worth
        // translating to a gRPC status the client can branch on; everything
        // else surfaces as Internal with the bridge's message intact.
        var status = ex.Code switch
        {
            -32001 => StatusCode.NotFound,        // ObjectNotFound
            -32602 => StatusCode.InvalidArgument, // InvalidParams
            -32000 => StatusCode.FailedPrecondition, // MetadataUnavailable
            _ => StatusCode.Internal
        };
        return new RpcException(new Status(status, ex.Message));
    }
}
