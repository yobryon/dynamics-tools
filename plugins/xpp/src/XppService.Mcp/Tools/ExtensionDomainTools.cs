using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Extensions;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for the extension family:
/// Tier 1a — AxTableExtension, AxEdtExtension, AxEnumExtension.
/// Tier 1b — AxFormExtension, AxViewExtension, AxDataEntityViewExtension.
///
/// Extension naming convention: '<TargetName>.<Suffix>' where the
/// suffix is conventionally the model name or 'Extension'
/// (e.g. 'CustTable.ContosoRetail', 'NoYes.Extension'). The bridge
/// validates the convention when writing.
/// </summary>
[McpServerToolType]
public sealed class ExtensionDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public ExtensionDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    // ---- AxTableExtension -------------------------------------------------

    [McpServerTool(Name = "xpp_create_table_extension"), Description(
        "Create a new AxTableExtension. Add new Fields / FieldGroups / " +
        "Indexes / Relations to an MS-shipped table, extend existing " +
        "field groups (FieldGroupExtensions) or relations (RelationExtensions), " +
        "modify existing fields' or relations' properties " +
        "(FieldModifications / RelationModifications), or modify the " +
        "table's own properties (PropertyModifications). " +
        "Name convention: '<TableName>.<Suffix>'.")]
    public Task<string> CreateTableExtension(
        [Description("The table extension to create. See CreateTableExtensionRequest schema.")]
        CreateTableExtensionRequest request,
        CancellationToken ct = default) =>
        Create("AxTableExtension", request, "create_table_extension", ct);

    [McpServerTool(Name = "xpp_get_table_extension"), Description(
        "Read an existing table extension as its typed domain shape. " +
        "Supports path-addressable navigation: set outline=true for a " +
        "depth-bounded skeleton, and/or atPath to root the read at one " +
        "subtree (e.g. '/relationExtensions/MyRel'). Use xpp_find_in_object " +
        "to locate a path first.")]
    public Task<string> GetTableExtension(
        [Description("The table-extension name to read (e.g. 'CustTable.MyExtension').")] string name,
        [Description("Return a structural skeleton (nodes + addressable paths) instead of full content.")]
        bool outline = false,
        [Description("Root the read at this path. Empty = whole extension.")]
        string? atPath = null,
        [Description("Outline depth below the root. depth=0 (default) = collection counts only; depth=1 lists members one level; depth=2 adds sub-counts.")]
        int depth = 0,
        CancellationToken ct = default) =>
        Get("AxTableExtension", name, "get_table_extension", ct, hintFn: null, outline, atPath, depth);

    [McpServerTool(Name = "xpp_patch_table_extension"), Description("Apply a partial update to a table extension. Merge-patch semantics; collections replace wholesale.")]
    public Task<string> PatchTableExtension(
        [Description("The table-extension name to patch.")] string name,
        [Description("Partial update.")] PatchTableExtensionRequest patch,
        CancellationToken ct = default) =>
        Patch("AxTableExtension", name, patch, "patch_table_extension", ct);

    // ---- AxEdtExtension ---------------------------------------------------

    [McpServerTool(Name = "xpp_create_edt_extension"), Description(
        "Create a new AxEdtExtension. Add ArrayElements or modify the " +
        "underlying EDT's properties (PropertyModifications). " +
        "Name convention: '<EdtName>.<Suffix>'.")]
    public Task<string> CreateEdtExtension(
        [Description("The EDT extension to create.")]
        CreateEdtExtensionRequest request,
        CancellationToken ct = default) =>
        Create("AxEdtExtension", request, "create_edt_extension", ct);

    [McpServerTool(Name = "xpp_get_edt_extension"), Description("Read an existing EDT extension as its typed domain shape.")]
    public Task<string> GetEdtExtension(
        [Description("The EDT-extension name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxEdtExtension", name, "get_edt_extension", ct);

    [McpServerTool(Name = "xpp_patch_edt_extension"), Description("Apply a partial update to an EDT extension.")]
    public Task<string> PatchEdtExtension(
        [Description("The EDT-extension name to patch.")] string name,
        [Description("Partial update.")] PatchEdtExtensionRequest patch,
        CancellationToken ct = default) =>
        Patch("AxEdtExtension", name, patch, "patch_edt_extension", ct);

    // ---- AxEnumExtension --------------------------------------------------

    [McpServerTool(Name = "xpp_create_enum_extension"), Description(
        "Create a new AxEnumExtension. Add EnumValues, modify existing " +
        "values' properties (ValueModifications), or modify the enum's " +
        "own properties (PropertyModifications). Requires the target " +
        "enum to have IsExtensible=true. Name convention: '<EnumName>.<Suffix>'.")]
    public Task<string> CreateEnumExtension(
        [Description("The enum extension to create.")]
        CreateEnumExtensionRequest request,
        CancellationToken ct = default) =>
        Create("AxEnumExtension", request, "create_enum_extension", ct);

    [McpServerTool(Name = "xpp_get_enum_extension"), Description("Read an existing enum extension.")]
    public Task<string> GetEnumExtension(
        [Description("The enum-extension name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxEnumExtension", name, "get_enum_extension", ct);

    [McpServerTool(Name = "xpp_patch_enum_extension"), Description("Apply a partial update to an enum extension.")]
    public Task<string> PatchEnumExtension(
        [Description("The enum-extension name to patch.")] string name,
        [Description("Partial update.")] PatchEnumExtensionRequest patch,
        CancellationToken ct = default) =>
        Patch("AxEnumExtension", name, patch, "patch_enum_extension", ct);

    // ---- AxFormExtension --------------------------------------------------

    [McpServerTool(Name = "xpp_create_form_extension"), Description(
        "Create a new AxFormExtension. Add new Controls / DataSources / Parts " +
        "to an MS-shipped form, modify existing controls / data sources / parts " +
        "(ControlModifications / DataSourceModifications / PropertyModifications), " +
        "or reference additional data sources (DataSourceReferences). " +
        "Name convention: '<FormName>.<Suffix>'. Each control entry needs " +
        "Name + FormControl (with its Type discriminator) + Parent (parent control or design root).")]
    public Task<string> CreateFormExtension(
        [Description("The form extension to create.")]
        CreateFormExtensionRequest request,
        CancellationToken ct = default) =>
        Create("AxFormExtension", request, "create_form_extension", ct);

    [McpServerTool(Name = "xpp_get_form_extension"), Description("Read an existing form extension as its typed domain shape.")]
    public Task<string> GetFormExtension(
        [Description("The form-extension name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxFormExtension", name, "get_form_extension", ct, FormPatternHints.ForFormExtension);

    [McpServerTool(Name = "xpp_patch_form_extension"), Description("Apply a partial update to a form extension. Collections replace wholesale.")]
    public Task<string> PatchFormExtension(
        [Description("The form-extension name to patch.")] string name,
        [Description("Partial update.")] PatchFormExtensionRequest patch,
        CancellationToken ct = default) =>
        Patch("AxFormExtension", name, patch, "patch_form_extension", ct);

    // ---- AxViewExtension --------------------------------------------------

    [McpServerTool(Name = "xpp_create_view_extension"), Description(
        "Create a new AxViewExtension. Add new Fields / FieldGroups / " +
        "Ranges / Mappings / DataSources to an MS-shipped view, extend " +
        "existing field groups (FieldGroupExtensions), modify existing " +
        "fields' properties (FieldModifications), or modify the view's own " +
        "properties (PropertyModifications). Name convention: '<ViewName>.<Suffix>'.")]
    public Task<string> CreateViewExtension(
        [Description("The view extension to create.")]
        CreateViewExtensionRequest request,
        CancellationToken ct = default) =>
        Create("AxViewExtension", request, "create_view_extension", ct);

    [McpServerTool(Name = "xpp_get_view_extension"), Description("Read an existing view extension as its typed domain shape.")]
    public Task<string> GetViewExtension(
        [Description("The view-extension name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxViewExtension", name, "get_view_extension", ct);

    [McpServerTool(Name = "xpp_patch_view_extension"), Description("Apply a partial update to a view extension.")]
    public Task<string> PatchViewExtension(
        [Description("The view-extension name to patch.")] string name,
        [Description("Partial update.")] PatchViewExtensionRequest patch,
        CancellationToken ct = default) =>
        Patch("AxViewExtension", name, patch, "patch_view_extension", ct);

    // ---- AxDataEntityViewExtension ----------------------------------------

    [McpServerTool(Name = "xpp_create_entity_extension"), Description(
        "Create a new AxDataEntityViewExtension. Add new Fields / FieldGroups / " +
        "Relations / Mappings / DataSources to an MS-shipped data entity, " +
        "extend existing field groups (FieldGroupExtensions), modify existing " +
        "fields' properties (FieldModifications), or modify the entity's own " +
        "properties (PropertyModifications). Name convention: '<EntityName>.<Suffix>'.")]
    public Task<string> CreateEntityExtension(
        [Description("The data-entity extension to create.")]
        CreateEntityExtensionRequest request,
        CancellationToken ct = default) =>
        Create("AxDataEntityViewExtension", request, "create_entity_extension", ct);

    [McpServerTool(Name = "xpp_get_entity_extension"), Description("Read an existing data-entity extension as its typed domain shape.")]
    public Task<string> GetEntityExtension(
        [Description("The entity-extension name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxDataEntityViewExtension", name, "get_entity_extension", ct);

    [McpServerTool(Name = "xpp_patch_entity_extension"), Description("Apply a partial update to a data-entity extension.")]
    public Task<string> PatchEntityExtension(
        [Description("The entity-extension name to patch.")] string name,
        [Description("Partial update.")] PatchEntityExtensionRequest patch,
        CancellationToken ct = default) =>
        Patch("AxDataEntityViewExtension", name, patch, "patch_entity_extension", ct);

    // ---- AxMenuExtension --------------------------------------------------

    [McpServerTool(Name = "xpp_create_menu_extension"), Description(
        "Create a new AxMenuExtension. Add new elements (MenuItem / " +
        "MenuReference / Separator / SubMenu / Tile) into an MS-shipped " +
        "menu's tree by referencing a Parent element on the base menu " +
        "(with optional PositionType + PreviousSibling for ordering), " +
        "modify existing menu elements' properties (MenuElementModifications), " +
        "or modify the host menu's own properties (PropertyModifications). " +
        "Name convention: '<MenuName>.<Suffix>'.")]
    public Task<string> CreateMenuExtension(
        [Description("The menu extension to create.")]
        CreateMenuExtensionRequest request,
        CancellationToken ct = default) =>
        Create("AxMenuExtension", request, "create_menu_extension", ct);

    [McpServerTool(Name = "xpp_get_menu_extension"), Description("Read an existing menu extension as its typed domain shape.")]
    public Task<string> GetMenuExtension(
        [Description("The menu-extension name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxMenuExtension", name, "get_menu_extension", ct);

    [McpServerTool(Name = "xpp_patch_menu_extension"), Description("Apply a partial update to a menu extension. Collections replace wholesale.")]
    public Task<string> PatchMenuExtension(
        [Description("The menu-extension name to patch.")] string name,
        [Description("Partial update.")] PatchMenuExtensionRequest patch,
        CancellationToken ct = default) =>
        Patch("AxMenuExtension", name, patch, "patch_menu_extension", ct);

    // ---- shared helpers ---------------------------------------------------

    private async Task<string> Create<T>(string axType, T request, string opName, CancellationToken ct)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        var domainJson = DomainJson.Serialize(request);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.CreateDomainObjectAsync(new CreateDomainObjectRequest
            {
                AxType = axType, Model = resolved!.Model, DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure(axType, opName, rx); }

        var se = await RecordPostWriteAsync(axType, resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: se.AddedToProject,
            changesetUpdated: se.ChangesetUpdated,
            sideEffectWarnings: se.Warnings);
    }

    private async Task<string> Get(string axType, string name, string opName, CancellationToken ct,
        Func<JsonElement, string[]>? hintFn = null,
        bool outline = false, string? atPath = null, int depth = 0)
    {
        var (resolved, gate) = ResolveOrGate(requireProject: false);
        if (gate != null) return gate;
        _ = resolved;
        GetDomainObjectResponse resp;
        string? navJson;
        try
        {
            (resp, navJson) = await DomainGetNav.ReadAsync(
                _conn.Client, axType, name, outline, atPath, depth, ct);
        }
        catch (RpcException rx) { return BridgeFailure(axType, opName, rx); }
        if (navJson != null) return navJson;  // outline / atPath read
        using var doc = JsonDocument.Parse(resp.DomainJson);
        if (hintFn != null)
        {
            return JsonSerializer.Serialize(new
            {
                axType = resp.AxType, name = resp.Name, domain = doc.RootElement,
                patternHints = hintFn(doc.RootElement),
            });
        }
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType, name = resp.Name, domain = doc.RootElement,
        });
    }

    private async Task<string> Patch<T>(string axType, string name, T patch, string opName, CancellationToken ct)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;

        // SCM pre-flight: best-effort checkout so the bridge can overwrite the
        // file. Failures don''t block � they fall through as side-effect
        // warnings, and the bridge''s own access-denied error (if checkout
        // truly failed) surfaces via the usual bridge-failure path.
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
        catch (RpcException rx) { return BridgeFailure(axType, opName, rx); }
        var se = await RecordPostWriteAsync(axType, resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var warnings = se.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: se.ChangesetUpdated,
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
