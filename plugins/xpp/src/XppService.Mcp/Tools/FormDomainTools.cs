using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Forms;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxForm. Biggest typed surface
/// to date: form-level metadata + DataSources + Design (with the
/// recursive Controls tree, ~17 typed control kinds) + Parts +
/// SourceCode (form methods, per-datasource handlers, per-control
/// handlers, members). Less-common control types fall back to
/// kind=Other with full round-trip preservation.
/// </summary>
[McpServerToolType]
public sealed class FormDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public FormDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_create_form"), Description(
        "Create a new AxForm. Provide DataSources (typed table refs " +
        "with Fields/Links), Design (with the recursive Controls " +
        "tree — Group/Tab/TabPage/Grid/StringEdit/IntegerEdit/etc.), " +
        "Parts (factbox refs), and optional SourceCode (form-level " +
        "methods + per-datasource/per-control event handlers, all " +
        "opaque X++). Less-common control types use kind=Other with " +
        "RawType preserving the original xsi:type.")]
    public async Task<string> CreateForm(
        [Description("The AxForm to create. See CreateFormRequest schema.")]
        CreateFormRequest request,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;

        var domainJson = DomainJson.Serialize(request);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.CreateDomainObjectAsync(new CreateDomainObjectRequest
            {
                AxType = "AxForm", Model = resolved!.Model, DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxForm", "create_form", rx); }

        var sideEffects = await RecordPostWriteAsync("AxForm", resp.Name, createdHere: true, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.Concat(FormBindingCheck.UnboundGridColumns(domainJson)).ToList();
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: sideEffects.AddedToProject,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    [McpServerTool(Name = "xpp_get_form"), Description(
        "Read an existing AxForm as its typed domain shape — full " +
        "metadata, data-source tree with Fields/Links, design with " +
        "the recursive Controls tree, parts, and SourceCode (form + " +
        "per-data-source + per-control method bodies as opaque X++). " +
        "Properties the typed shape doesn't model land in each " +
        "element's OtherProperties dict — round-trip is lossless.\n\n" +
        "PATH-ADDRESSABLE NAVIGATION for large forms (don't read the whole " +
        "thing): set outline=true for a depth-bounded structural skeleton " +
        "(controls/datasources/methods as nodes with addressable paths; " +
        "scalar properties elided). Set atPath (e.g. " +
        "'/design/controls/Grid') to root the read at one subtree — works " +
        "with outline (zoom the skeleton there) or alone (full subtree only). " +
        "Use xpp_find_in_object to locate a path by name/kind/datasource " +
        "first, then atPath into it. Paths returned are reusable as patch " +
        "targets.")]
    public async Task<string> GetForm(
        [Description("The AxForm name to read.")] string name,
        [Description("Return a structural skeleton (nodes + addressable paths, scalars elided) instead of full content. Pair with depth.")]
        bool outline = false,
        [Description("Root the read at this path, e.g. '/design/controls/Grid' or '/sourceCode/methods/init'. Empty = whole form. Identity within a collection is the member's name (or dataField).")]
        string? atPath = null,
        [Description("Outline depth below the root. depth=0 (default) = collection counts only — the compact orient (e.g. 'design has 3 controls, sourceCode has 175 methods'); bounded even on a 1.5MB form. depth=1 lists members one level (the controls/datasources/methods themselves); depth=2 adds their sub-counts. To go deeper into one area, atPath into it rather than raising depth.")]
        int depth = 0,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate(requireProject: false);
        if (gate != null) return gate;
        _ = resolved;

        GetDomainObjectResponse resp;
        string? navJson;
        try
        {
            (resp, navJson) = await DomainGetNav.ReadAsync(
                _conn.Client, "AxForm", name, outline, atPath, depth, ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxForm", "get_form", rx); }

        // Navigation reads (outline or atPath) return the skeleton / subtree
        // as-is; pattern hints only make sense for a whole-form read.
        if (navJson != null) return navJson;

        using var doc = JsonDocument.Parse(resp.DomainJson);
        var patternHints = FormPatternHints.ForForm(doc.RootElement);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType, name = resp.Name, domain = doc.RootElement,
            patternHints,
        });
    }

    [McpServerTool(Name = "xpp_patch_form"), Description(
        "Apply a partial update to an existing AxForm. Merge-patch " +
        "semantics. Collections (DataSources / Design / Parts) replace " +
        "wholesale when non-null. To patch a single control or method, " +
        "read with xpp_get_form, mutate the tree in-process, and patch " +
        "back.")]
    public async Task<string> PatchForm(
        [Description("The AxForm to patch.")] string name,
        [Description("Partial update. See PatchFormRequest schema.")]
        PatchFormRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync("AxForm", name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }


        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = "AxForm", Model = resolved!.Model, Name = name, PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxForm", "patch_form", rx); }

        var sideEffects = await RecordPostWriteAsync("AxForm", resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        warnings.AddRange(FormBindingCheck.UnboundGridColumns(patchJson));
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    private (ResolvedConfig? resolved, string? gate) ResolveOrGate(bool requireProject = true)
    {
        ResolvedConfig? resolved;
        try { resolved = _project.Resolve(); }
        catch (ProjectConfigException pcx)
        {
            return (null, JsonSerializer.Serialize(new
            {
                configured = false, error = "project_config_invalid",
                message = pcx.Message,
                hint = "Load the dynamics-xpp:xpp-project skill for the .dynamics-xpp/config.json shape.",
            }));
        }
        if (requireProject && resolved == null)
        {
            return (null, JsonSerializer.Serialize(new
            {
                configured = false, cwd = Environment.CurrentDirectory,
                message = "Write operations require a .dynamics-xpp/config.json in the launch directory. Load the dynamics-xpp:xpp-project skill and walk the user through first-time setup.",
                skill = "dynamics-xpp:xpp-project",
            }));
        }
        return (resolved, null);
    }

    private async Task<SideEffectResult> RecordPostWriteAsync(string axType, string name, bool createdHere, CancellationToken ct)
    {
        var warnings = new List<string>();
        var added = false;
        try { added = await _project.AddToRnprojAsync(axType, name, ct).ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"rnrproj add failed: {ex.Message}"); }
        var changesetUpdated = false;
        try
        {
            await _project.UpsertChangesetAsync(axType, name, createdHere, ct).ConfigureAwait(false);
            changesetUpdated = true;
        }
        catch (Exception ex) { warnings.Add($"changeset update failed: {ex.Message}"); }

        // SCM auto-action (Phase 2 of search-coverage Tier ''SCM''): if
        // .dynamics-xpp/config.json has an scm block, run tf add for the
        // file. Idempotent � already-tracked is treated as success.
        try
        {
            var scmWarning = await _project.ScmAddAsync(axType, name, ct).ConfigureAwait(false);
            if (scmWarning != null) warnings.Add($"scm: {scmWarning}");
        }
        catch (Exception ex) { warnings.Add($"scm op failed: {ex.Message}"); }
        return new SideEffectResult(added, changesetUpdated, warnings.ToArray());
    }

    private static string BridgeFailure(string axType, string operation, RpcException rx) =>
        JsonSerializer.Serialize(new
        {
            error = "bridge_" + operation + "_failed",
            axType, code = rx.Status.StatusCode.ToString(), message = rx.Status.Detail,
            hint = "Domain mapping or bridge write rejected the payload. " +
                   "Check the message text — it usually names the offending field.",
        });

    private readonly record struct SideEffectResult(bool AddedToProject, bool ChangesetUpdated, string[] Warnings);
}
