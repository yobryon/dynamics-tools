using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Label CRUD + search surface. Always prefer these over reading / editing
/// .label.txt files directly — the resource files can be tens of thousands of
/// lines and the round-trip preserves BOM, ordering, and translator-description
/// continuation lines. The matching authoring guide is the dynamics-xpp:xpp-labelfile
/// skill.
/// </summary>
[McpServerToolType]
public sealed class LabelTools
{
    private readonly XppServiceConnection _conn;

    public LabelTools(XppServiceConnection conn)
    {
        _conn = conn;
    }

    [McpServerTool(Name = "xpp_label_search"), Description(
        "Case-insensitive regex search over one or more label files. Pattern " +
        "is a .NET regex (always case-insensitive). By default it's tested " +
        "against each entry's value; set matchDescription=true to also match " +
        "the translator description. Returns matches as JSON with " +
        "{labelFileId, language, labelId, value, description, line, matchedIn}. " +
        "Use this instead of grepping a .label.txt file directly. For broad " +
        "'where is this string used anywhere' searches across the indexed " +
        "corpus, prefer xpp_search_code.")]
    public async Task<string> LabelSearch(
        [Description("One or more LabelFileIds (e.g. 'CONL', 'SYS'). The logical file name, NOT the AxLabelFile artifact name.")] string[] labelFileIds,
        [Description(".NET regex pattern. Evaluated case-insensitively.")] string pattern,
        [Description("BCP-47 language tag. Empty defaults to 'en-US'.")] string? language = null,
        [Description("Also match against translator descriptions. Default false.")] bool matchDescription = false,
        [Description("Maximum results to return. 0 = no cap. Default 200.")] int limit = 200,
        CancellationToken ct = default)
    {
        var request = new LabelSearchRequest
        {
            Language = language ?? string.Empty,
            Pattern = pattern,
            MatchDescription = matchDescription,
            Limit = limit
        };
        if (labelFileIds != null)
            request.LabelFileIds.AddRange(labelFileIds);

        var hits = new List<object>();
        using var call = _conn.Client.LabelSearch(request);
        while (await call.ResponseStream.MoveNext(ct))
        {
            var m = call.ResponseStream.Current;
            hits.Add(new
            {
                labelFileId = m.Entry.Ref.LabelFileId,
                language = m.Entry.Ref.Language,
                labelId = m.Entry.Ref.LabelId,
                value = m.Entry.Value,
                description = m.Entry.Description,
                line = m.Line,
                matchedIn = m.MatchedIn
            });
        }

        return JsonSerializer.Serialize(new { count = hits.Count, results = hits });
    }

    [McpServerTool(Name = "xpp_label_read"), Description(
        "Read a single label by (labelFileId, language, labelId). Returns " +
        "{labelFileId, language, labelId, value, description}. Use this when " +
        "you have the exact label id; for substring / regex lookup use " +
        "xpp_label_search.")]
    public async Task<string> LabelRead(
        [Description("LabelFileId, e.g. 'CONL'.")] string labelFileId,
        [Description("Label key inside the resource file (the text before '=').")] string labelId,
        [Description("BCP-47 language tag. Empty defaults to 'en-US'.")] string? language = null,
        CancellationToken ct = default)
    {
        var request = new LabelReadRequest
        {
            Ref = new LabelRef
            {
                LabelFileId = labelFileId,
                Language = language ?? string.Empty,
                LabelId = labelId
            }
        };
        var resp = await _conn.Client.LabelReadAsync(request, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            labelFileId = resp.Ref.LabelFileId,
            language = resp.Ref.Language,
            labelId = resp.Ref.LabelId,
            value = resp.Value,
            description = resp.Description
        });
    }

    [McpServerTool(Name = "xpp_label_add"), Description(
        "Add one or more new labels to a label file in a single round-trip. " +
        "Batch-capable: pass the full set you intend to create. Fails if any " +
        "labelId already exists in the file, or if the batch contains internal " +
        "duplicates — no partial writes. Each entry is {labelId, value, " +
        "description?}.")]
    public async Task<string> LabelAdd(
        [Description("LabelFileId, e.g. 'CONL'.")] string labelFileId,
        [Description("Array of {labelId, value, description?} entries to insert.")] LabelEntryPayload[] labels,
        [Description("BCP-47 language tag. Empty defaults to 'en-US'.")] string? language = null,
        CancellationToken ct = default)
    {
        var request = BuildMutationRequest(labelFileId, language, labels);
        var resp = await _conn.Client.LabelAddAsync(request, cancellationToken: ct);
        return SerializeMutation(resp, "added");
    }

    [McpServerTool(Name = "xpp_label_update"), Description(
        "Update one or more existing labels' value (and optionally description) " +
        "in a single round-trip. Batch-capable. Fails if any labelId does not " +
        "exist — no partial writes. To create a new label use xpp_label_add. " +
        "Pass an empty description to clear an existing description line.")]
    public async Task<string> LabelUpdate(
        [Description("LabelFileId, e.g. 'CONL'.")] string labelFileId,
        [Description("Array of {labelId, value, description?} entries to update.")] LabelEntryPayload[] labels,
        [Description("BCP-47 language tag. Empty defaults to 'en-US'.")] string? language = null,
        CancellationToken ct = default)
    {
        var request = BuildMutationRequest(labelFileId, language, labels);
        var resp = await _conn.Client.LabelUpdateAsync(request, cancellationToken: ct);
        return SerializeMutation(resp, "updated");
    }

    [McpServerTool(Name = "xpp_label_delete"), Description(
        "Delete one or more labels from a label file in a single round-trip. " +
        "Batch-capable. Fails if any labelId does not exist — no partial " +
        "writes.")]
    public async Task<string> LabelDelete(
        [Description("LabelFileId, e.g. 'CONL'.")] string labelFileId,
        [Description("Array of labelIds to remove.")] string[] labelIds,
        [Description("BCP-47 language tag. Empty defaults to 'en-US'.")] string? language = null,
        CancellationToken ct = default)
    {
        var request = new LabelDeleteRequest
        {
            LabelFileId = labelFileId,
            Language = language ?? string.Empty
        };
        if (labelIds != null)
            request.LabelIds.AddRange(labelIds);

        var resp = await _conn.Client.LabelDeleteAsync(request, cancellationToken: ct);
        return SerializeMutation(resp, "deleted");
    }

    private static LabelMutationRequest BuildMutationRequest(string labelFileId, string? language, LabelEntryPayload[] labels)
    {
        var request = new LabelMutationRequest
        {
            LabelFileId = labelFileId,
            Language = language ?? string.Empty
        };
        if (labels != null)
        {
            foreach (var l in labels)
            {
                request.Labels.Add(new LabelEntryInput
                {
                    LabelId = l.LabelId ?? string.Empty,
                    Value = l.Value ?? string.Empty,
                    Description = l.Description ?? string.Empty
                });
            }
        }
        return request;
    }

    private static string SerializeMutation(LabelMutationResponse resp, string verb)
    {
        return JsonSerializer.Serialize(new
        {
            labelFileId = resp.LabelFileId,
            language = resp.Language,
            affected = resp.Affected,
            resourcePath = resp.ResourcePath,
            verb
        });
    }

    public sealed class LabelEntryPayload
    {
        [Description("Label key (the text before '=').")]
        public string LabelId { get; set; } = string.Empty;

        [Description("Label value (the displayed text).")]
        public string Value { get; set; } = string.Empty;

        [Description("Optional translator description (the ' ;' continuation line). Empty to omit / clear.")]
        public string? Description { get; set; }
    }
}
