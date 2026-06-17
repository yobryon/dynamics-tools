using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Shared plumbing for the outline/atPath read knobs on the per-type get tools
/// (xpp_get_form, xpp_get_table, ...). Factored so every get tool wires the
/// navigation params identically and only owns its whole-object response shape.
/// </summary>
internal static class DomainGetNav
{
    /// <summary>
    /// Issue a GetDomainObject with the navigation params. If the response is a
    /// navigation read (outline or atPath subtree), returns the ready-to-send
    /// JSON; otherwise returns null and the caller shapes the full-object read
    /// from <paramref name="resp"/>. Throws RpcException for the caller's
    /// existing BridgeFailure handling.
    /// </summary>
    public static async Task<(GetDomainObjectResponse resp, string? navJson)> ReadAsync(
        Xpp.Service.Contracts.V1.XppService.XppServiceClient client,
        string axType, string name, bool outline, string? atPath, int depth, CancellationToken ct)
    {
        var resp = await client.GetDomainObjectAsync(new GetDomainObjectRequest
        {
            AxType = axType, Name = name,
            Outline = outline, AtPath = atPath ?? "", Depth = depth,
        }, cancellationToken: ct);

        if (!resp.IsOutline && string.IsNullOrEmpty(resp.AtPath))
            return (resp, null);  // whole-object read — caller shapes it

        using var doc = JsonDocument.Parse(resp.DomainJson);
        var json = JsonSerializer.Serialize(new
        {
            axType = resp.AxType, name = resp.Name,
            atPath = string.IsNullOrEmpty(resp.AtPath) ? "/" : resp.AtPath,
            outline = resp.IsOutline,
            domain = doc.RootElement,
        });
        return (resp, json);
    }
}

/// <summary>
/// Path-addressable navigation — the "locate" primitive. Searches one
/// object's domain tree by attribute and returns the addressable PATHS of
/// matching nodes (breadcrumbs), not their content. Pairs with
/// xpp_get_form(atPath) "zoom" and the upcoming patch(atPath) "edit".
///
/// Generic across every AxType the bridge can serialize: takes axType + name,
/// walks the bridge's domain JSON server-side, returns matches as a compact
/// JSON array. The per-type get tools (xpp_get_form, ...) carry the
/// outline/atPath knobs; this one tool covers locate for all types.
/// </summary>
[McpServerToolType]
public sealed class NavigationTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public NavigationTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_find_in_object"), Description(
        "Locate nodes inside one object's domain tree (a form's control/" +
        "datasource/method tree, a table's fields/relations/indexes, etc.) " +
        "WITHOUT reading the whole object. Returns matching addressable " +
        "PATHS (e.g. '/design/controls/Grid/controls/Grid_Name'), each with " +
        "its kind and key attributes — not the subtree content. Then zoom " +
        "with xpp_get_form(name, atPath=<path>) or patch by that path. " +
        "Provide at least one of query/kind/dataSource/dataField (an " +
        "unconstrained find returns nothing, by design — it won't dump the " +
        "tree). query is a case-insensitive substring of the node's name/" +
        "dataField.")]
    public async Task<string> FindInObject(
        [Description("AOT type, e.g. AxForm / AxTable / AxClass.")] string axType,
        [Description("Object name to search within.")] string name,
        [Description("Case-insensitive substring matched against each node's identity (name / dataField). Empty = rely on the other filters.")]
        string? query = null,
        [Description("Filter by node kind / discriminator (control kind like 'Grid' / 'ReferenceGroup', field type, relationship type).")]
        string? kind = null,
        [Description("Filter to nodes bound to this data source (form controls / datasources).")]
        string? dataSource = null,
        [Description("Filter to nodes bound to this data field.")]
        string? dataField = null,
        CancellationToken ct = default)
    {
        FindInObjectResponse resp;
        try
        {
            resp = await _conn.Client.FindInObjectAsync(new FindInObjectRequest
            {
                AxType = axType, Name = name,
                Query = query ?? "", Kind = kind ?? "",
                DataSource = dataSource ?? "", DataField = dataField ?? "",
            }, cancellationToken: ct);
        }
        catch (RpcException rx)
        {
            return JsonSerializer.Serialize(new
            {
                error = "bridge_find_in_object_failed",
                axType, code = rx.Status.StatusCode.ToString(), message = rx.Status.Detail,
            });
        }

        using var matches = JsonDocument.Parse(resp.MatchesJson);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType, name = resp.Name,
            count = matches.RootElement.GetArrayLength(),
            matches = matches.RootElement,
        });
    }

    [McpServerTool(Name = "xpp_patch_by_path"), Description(
        "Surgically edit one node of an object's domain tree by its path — the " +
        "'edit' primitive that completes the orient/locate/zoom/edit loop. You " +
        "send only {atPath, op, value}; the service reads current state, splices " +
        "the change, and writes — untouched siblings are never resent. Get the " +
        "path from xpp_find_in_object or an outline read.\n\n" +
        "Ops:\n" +
        "  set    — replace the node at atPath with value (e.g. swap a control's full definition)\n" +
        "  merge  — overlay value's top-level properties onto the object at atPath (tweak a few props, keep children); to change something nested, target that deeper path\n" +
        "  append — add value (a new member object) to the COLLECTION at atPath (e.g. atPath='/design/controls/Grid/controls', value=a new control)\n" +
        "  remove — delete the node at atPath (value omitted)\n\n" +
        "value is the JSON for the node/member, in the same domain shape the get " +
        "tools return (e.g. a control object with name/kind/dataField). The edit " +
        "still runs the full bridge validation + (for forms) pattern conformance.")]
    public async Task<string> PatchByPath(
        [Description("AOT type, e.g. AxForm / AxTable / AxTableExtension.")] string axType,
        [Description("Object name to edit.")] string name,
        [Description("Target path. For set/merge/remove: the node ('/design/controls/Grid/controls/Grid_Name'). For append: the collection ('/design/controls/Grid/controls').")]
        string atPath,
        [Description("set | merge | append | remove.")] string op,
        [Description("JSON value to splice, in the same domain shape get returns for that node. For a structural node it's an object (e.g. a control), or for append the new member object. For a SCALAR node (a string/int/bool leaf such as /sourceCode/declaration) it's the JSON-encoded scalar — a string MUST be double-quoted with escaped newlines (e.g. \"[Form]\\npublic class ...\"), not pasted raw. Omit for remove.")]
        JsonElement? value = null,
        [Description("Preview only: return the edited subtree WITHOUT writing, so you can confirm the change landed where intended before committing. Recommended before a non-trivial edit.")]
        bool dryRun = false,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;

        // Normalize the value to its JSON text. Depending on how the client
        // marshals an untyped param, it can arrive either as a JSON object
        // (use the raw text) or as a JSON STRING that itself contains the
        // object's JSON (unwrap it). Either way the service receives object text.
        var valueJson = "";
        if (value.HasValue)
        {
            var ve = value.Value;
            valueJson = ve.ValueKind == JsonValueKind.String ? (ve.GetString() ?? "") : ve.GetRawText();
        }

        // Dry run: no SCM checkout, no project bookkeeping — just preview.
        if (dryRun)
        {
            WriteObjectResponse preview;
            try
            {
                preview = await _conn.Client.PatchDomainObjectByPathAsync(new PatchByPathRequest
                {
                    AxType = axType, Model = resolved!.Model, Name = name,
                    AtPath = atPath, Op = op, ValueJson = valueJson, DryRun = true,
                }, cancellationToken: ct);
            }
            catch (RpcException rx) { return BridgeFailure(axType, "patch_by_path", rx); }
            using var pdoc = JsonDocument.Parse(string.IsNullOrEmpty(preview.PreviewJson) ? "null" : preview.PreviewJson);
            return JsonSerializer.Serialize(new
            {
                axType, name, atPath, op, dryRun = true,
                committed = false, preview = pdoc.RootElement,
            });
        }

        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync(axType, name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }

        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectByPathAsync(new PatchByPathRequest
            {
                AxType = axType, Model = resolved!.Model, Name = name,
                AtPath = atPath, Op = op, ValueJson = valueJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure(axType, "patch_by_path", rx); }

        var sideEffects = await RecordPostWriteAsync(axType, resp.Name, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    // ---- write plumbing (mirrors the per-type tool classes) ----------------

    private (ResolvedConfig? resolved, string? gate) ResolveOrGate()
    {
        ResolvedConfig? resolved;
        try { resolved = _project.Resolve(); }
        catch (ProjectConfigException pcx)
        {
            return (null, JsonSerializer.Serialize(new
            {
                configured = false, error = "project_config_invalid", message = pcx.Message,
                hint = "Load the dynamics-xpp:xpp-project skill for the .dynamics-xpp/config.json shape.",
            }));
        }
        if (resolved == null)
            return (null, JsonSerializer.Serialize(new
            {
                configured = false, cwd = Environment.CurrentDirectory,
                message = "Write operations require a .dynamics-xpp/config.json in the launch directory. Load the dynamics-xpp:xpp-project skill and walk the user through first-time setup.",
                skill = "dynamics-xpp:xpp-project",
            }));
        return (resolved, null);
    }

    private async Task<(bool ChangesetUpdated, string[] Warnings)> RecordPostWriteAsync(string axType, string name, CancellationToken ct)
    {
        var warnings = new List<string>();
        try { await _project.AddToRnprojAsync(axType, name, ct).ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"rnrproj add failed: {ex.Message}"); }
        var changesetUpdated = false;
        try { await _project.UpsertChangesetAsync(axType, name, createdHere: false, ct).ConfigureAwait(false); changesetUpdated = true; }
        catch (Exception ex) { warnings.Add($"changeset update failed: {ex.Message}"); }
        try
        {
            var scmWarning = await _project.ScmAddAsync(axType, name, ct).ConfigureAwait(false);
            if (scmWarning != null) warnings.Add($"scm: {scmWarning}");
        }
        catch (Exception ex) { warnings.Add($"scm op failed: {ex.Message}"); }
        return (changesetUpdated, warnings.ToArray());
    }

    private static string BridgeFailure(string axType, string operation, RpcException rx) =>
        JsonSerializer.Serialize(new
        {
            error = "bridge_" + operation + "_failed",
            axType, code = rx.Status.StatusCode.ToString(), message = rx.Status.Detail,
            hint = "Check the message — for a path edit it usually names a bad path/op or the offending property the bridge rejected.",
        });
}
