using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Menus;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxMenu and the three AxMenuItem*
/// types. One single MCP tool per CRUD op covers all three menu-item
/// kinds — the Kind enum on the request dispatches to the right
/// underlying ax_type.
/// </summary>
[McpServerToolType]
public sealed class MenuDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public MenuDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    // ---- AxMenu ----------------------------------------------------------

    [McpServerTool(Name = "xpp_create_menu"), Description(
        "Create a new AxMenu. A menu is a tree of elements: MenuItem " +
        "(references an AxMenuItem*), MenuReference (links to another " +
        "AxMenu), Separator, SubMenu (recursive nested menu), or Tile. " +
        "Use this for navigation containers — module menus, sub-area " +
        "menus, the workspace tile grid. Element children carry their " +
        "own Kind discriminator.")]
    public async Task<string> CreateMenu(
        [Description("The AxMenu to create. See CreateMenuRequest schema.")]
        CreateMenuRequest request,
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
                AxType = "AxMenu",
                Model = resolved!.Model,
                DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxMenu", "create_menu", rx); }

        var sideEffects = await RecordPostWriteAsync("AxMenu", resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: sideEffects.AddedToProject,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: sideEffects.Warnings);
    }

    [McpServerTool(Name = "xpp_get_menu"), Description(
        "Read an existing AxMenu as its typed domain shape — the menu's " +
        "scalar properties plus its element tree (with recursive sub-menus).")]
    public async Task<string> GetMenu(
        [Description("The AxMenu name to read.")] string name,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate(requireProject: false);
        if (gate != null) return gate;
        _ = resolved;

        GetDomainObjectResponse resp;
        try
        {
            resp = await _conn.Client.GetDomainObjectAsync(new GetDomainObjectRequest
            {
                AxType = "AxMenu",
                Name = name,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxMenu", "get_menu", rx); }

        using var doc = JsonDocument.Parse(resp.DomainJson);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType, name = resp.Name, domain = doc.RootElement,
        });
    }

    [McpServerTool(Name = "xpp_patch_menu"), Description(
        "Apply a partial update to an existing AxMenu. Merge-patch " +
        "semantics. Elements non-null replaces the whole element tree.")]
    public async Task<string> PatchMenu(
        [Description("The AxMenu to patch.")] string name,
        [Description("Partial update. See PatchMenuRequest schema.")]
        PatchMenuRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync("AxMenu", name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }


        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = "AxMenu", Model = resolved!.Model, Name = name, PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxMenu", "patch_menu", rx); }

        var sideEffects = await RecordPostWriteAsync("AxMenu", resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    // ---- AxMenuItem* (single tool, Kind discriminator) ---------------------

    [McpServerTool(Name = "xpp_create_menuitem"), Description(
        "Create a new menu item (AxMenuItemDisplay / Output / Action). " +
        "Set Kind on the request to pick the target type. Display opens " +
        "an AxForm. Output runs an SSRS AxReport. Action invokes an " +
        "AxClass main() method. The Object field names the target.")]
    public async Task<string> CreateMenuItem(
        [Description("The menu item to create. See CreateMenuItemRequest schema. Kind drives whether the on-disk AxType is AxMenuItemDisplay / AxMenuItemOutput / AxMenuItemAction.")]
        CreateMenuItemRequest request,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;

        var axType = "AxMenuItem" + request.Kind;
        var domainJson = DomainJson.Serialize(request);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.CreateDomainObjectAsync(new CreateDomainObjectRequest
            {
                AxType = axType, Model = resolved!.Model, DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure(axType, "create_menuitem", rx); }

        var sideEffects = await RecordPostWriteAsync(axType, resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: sideEffects.AddedToProject,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: sideEffects.Warnings);
    }

    [McpServerTool(Name = "xpp_get_menuitem"), Description(
        "Read an existing menu item as its typed domain shape. Provide " +
        "the kind so the right ax_type (AxMenuItemDisplay / Output / " +
        "Action) is read from disk.")]
    public async Task<string> GetMenuItem(
        [Description("The menu-item name to read.")] string name,
        [Description("The menu-item kind: Display / Output / Action.")] MenuItemKind kind,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate(requireProject: false);
        if (gate != null) return gate;
        _ = resolved;
        var axType = "AxMenuItem" + kind;

        GetDomainObjectResponse resp;
        try
        {
            resp = await _conn.Client.GetDomainObjectAsync(new GetDomainObjectRequest
            {
                AxType = axType, Name = name,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure(axType, "get_menuitem", rx); }

        using var doc = JsonDocument.Parse(resp.DomainJson);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType, name = resp.Name, domain = doc.RootElement,
        });
    }

    [McpServerTool(Name = "xpp_patch_menuitem"), Description(
        "Apply a partial update to an existing menu item. Merge-patch " +
        "semantics. Provide the kind so the right ax_type is targeted.")]
    public async Task<string> PatchMenuItem(
        [Description("The menu-item name to patch.")] string name,
        [Description("The menu-item kind: Display / Output / Action.")] MenuItemKind kind,
        [Description("Partial update. See PatchMenuItemRequest schema.")]
        PatchMenuItemRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        var axType = "AxMenuItem" + kind;

        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync(axType, name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }

        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = axType, Model = resolved!.Model, Name = name, PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure(axType, "patch_menuitem", rx); }

        var sideEffects = await RecordPostWriteAsync(axType, resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    // ---- shared helpers ---------------------------------------------------

    private (ResolvedConfig? resolved, string? gate) ResolveOrGate(bool requireProject = true)
    {
        ResolvedConfig? resolved;
        try { resolved = _project.Resolve(); }
        catch (ProjectConfigException pcx)
        {
            return (null, JsonSerializer.Serialize(new
            {
                configured = false,
                error = "project_config_invalid",
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
