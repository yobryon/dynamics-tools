using Grpc.Core;
using Xpp.Service.Bridge;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Services;

/// <summary>
/// Label CRUD handlers. Thin pass-throughs to the bridge — the bridge owns
/// the .label.txt parsing, file IO, and ordering preservation; the service
/// is here to translate gRPC envelopes and fan out search across multiple
/// label_file_ids.
///
/// LabelSearch is the one method with non-trivial logic here: callers pass
/// one or more LabelFileIds and the result stream interleaves matches from
/// each. We don't parallelise the per-file searches yet — typical scoping
/// is one to three files and the bridge resolves each in well under a
/// second even on the larger module files. If usage shifts toward "search
/// across an entire model" we'd switch to Task.WhenAll with backpressure.
/// </summary>
public sealed partial class PingGrpcService
{
    public override async Task LabelSearch(LabelSearchRequest request, IServerStreamWriter<LabelMatch> responseStream, ServerCallContext context)
    {
        if (request.LabelFileIds == null || request.LabelFileIds.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "label_file_ids must contain at least one id"));
        if (string.IsNullOrWhiteSpace(request.Pattern))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "pattern is required"));

        var language = string.IsNullOrWhiteSpace(request.Language) ? "en-US" : request.Language;
        int remaining = request.Limit;

        foreach (var labelFileId in request.LabelFileIds)
        {
            if (string.IsNullOrWhiteSpace(labelFileId)) continue;
            BridgeLabelSearchResult result;
            try
            {
                result = await _bridgeClient.LabelSearchAsync(
                    labelFileId, language, request.Pattern, request.MatchDescription,
                    // Bridge enforces its own per-file cap; pass remaining quota
                    // so we don't fetch more than we'll stream.
                    request.Limit > 0 ? remaining : 0,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (BridgeRpcException ex)
            {
                throw MapBridgeError(ex);
            }

            if (result.Matches == null) continue;

            foreach (var hit in result.Matches)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await responseStream.WriteAsync(new LabelMatch
                {
                    Entry = new LabelEntry
                    {
                        Ref = new LabelRef
                        {
                            LabelFileId = hit.LabelFileId,
                            Language = hit.Language,
                            LabelId = hit.LabelId
                        },
                        Value = hit.Value ?? string.Empty,
                        Description = hit.Description ?? string.Empty
                    },
                    Line = hit.Line,
                    MatchedIn = hit.MatchedIn ?? string.Empty
                }).ConfigureAwait(false);

                if (request.Limit > 0)
                {
                    remaining--;
                    if (remaining <= 0) return;
                }
            }
        }
    }

    public override async Task<LabelEntry> LabelRead(LabelReadRequest request, ServerCallContext context)
    {
        if (request.Ref == null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ref is required"));
        if (string.IsNullOrWhiteSpace(request.Ref.LabelFileId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ref.label_file_id is required"));
        if (string.IsNullOrWhiteSpace(request.Ref.LabelId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ref.label_id is required"));

        var language = string.IsNullOrWhiteSpace(request.Ref.Language) ? "en-US" : request.Ref.Language;

        BridgeLabelReadResult result;
        try
        {
            result = await _bridgeClient.LabelReadAsync(
                request.Ref.LabelFileId, language, request.Ref.LabelId,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            throw MapBridgeError(ex);
        }

        return new LabelEntry
        {
            Ref = new LabelRef
            {
                LabelFileId = result.LabelFileId,
                Language = result.Language,
                LabelId = result.LabelId
            },
            Value = result.Value ?? string.Empty,
            Description = result.Description ?? string.Empty
        };
    }

    public override Task<LabelMutationResponse> LabelAdd(LabelMutationRequest request, ServerCallContext context)
        => InvokeMutationAsync(request, context, isAdd: true);

    public override Task<LabelMutationResponse> LabelUpdate(LabelMutationRequest request, ServerCallContext context)
        => InvokeMutationAsync(request, context, isAdd: false);

    public override async Task<LabelMutationResponse> LabelDelete(LabelDeleteRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.LabelFileId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "label_file_id is required"));
        if (request.LabelIds == null || request.LabelIds.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "label_ids must contain at least one id"));

        var language = string.IsNullOrWhiteSpace(request.Language) ? "en-US" : request.Language;

        BridgeLabelMutationResult result;
        try
        {
            result = await _bridgeClient.LabelDeleteAsync(
                request.LabelFileId, language, request.LabelIds.ToList(),
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            throw MapBridgeError(ex);
        }

        return new LabelMutationResponse
        {
            LabelFileId = result.LabelFileId,
            Language = result.Language,
            Affected = result.Affected,
            ResourcePath = result.ResourcePath ?? string.Empty
        };
    }

    private async Task<LabelMutationResponse> InvokeMutationAsync(LabelMutationRequest request, ServerCallContext context, bool isAdd)
    {
        if (string.IsNullOrWhiteSpace(request.LabelFileId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "label_file_id is required"));
        if (request.Labels == null || request.Labels.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "labels must contain at least one entry"));

        var language = string.IsNullOrWhiteSpace(request.Language) ? "en-US" : request.Language;
        var inputs = request.Labels.Select(l => new BridgeLabelMutationInput(
            l.LabelId,
            l.Value ?? string.Empty,
            string.IsNullOrEmpty(l.Description) ? null : l.Description)).ToList();

        BridgeLabelMutationResult result;
        try
        {
            result = isAdd
                ? await _bridgeClient.LabelAddAsync(request.LabelFileId, language, inputs, context.CancellationToken).ConfigureAwait(false)
                : await _bridgeClient.LabelUpdateAsync(request.LabelFileId, language, inputs, context.CancellationToken).ConfigureAwait(false);
        }
        catch (BridgeRpcException ex)
        {
            throw MapBridgeError(ex);
        }

        return new LabelMutationResponse
        {
            LabelFileId = result.LabelFileId,
            Language = result.Language,
            Affected = result.Affected,
            ResourcePath = result.ResourcePath ?? string.Empty
        };
    }
}
