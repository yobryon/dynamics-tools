using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Security;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for the AxSecurity* family —
/// Privilege, Duty, Role, Policy. The four types share a common
/// "Grant" record (per-CRUD access levels) and a few collection
/// shapes (entry points, data-entity references) factored into the
/// Security domain namespace.
/// </summary>
[McpServerToolType]
public sealed class SecurityDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public SecurityDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    // ---- AxSecurityPrivilege ---------------------------------------------

    [McpServerTool(Name = "xpp_create_privilege"), Description(
        "Create a new AxSecurityPrivilege. Grants access to a set of " +
        "entry points (menu items, forms, tiles) + tables/data entities, " +
        "with optional per-control overrides. Privileges are the leaf " +
        "node of the AX security model: roles aggregate duties, duties " +
        "aggregate privileges. Convention: '<Area><Function>{Maintain|View}'.")]
    public Task<string> CreatePrivilege(
        [Description("The privilege to create.")] CreatePrivilegeRequest request,
        CancellationToken ct = default) =>
        Create("AxSecurityPrivilege", request, "create_privilege", ct);

    [McpServerTool(Name = "xpp_get_privilege"), Description("Read an existing privilege as its typed domain shape.")]
    public Task<string> GetPrivilege(
        [Description("The privilege name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxSecurityPrivilege", name, "get_privilege", ct);

    [McpServerTool(Name = "xpp_patch_privilege"), Description("Apply a partial update to a privilege. Collections replace wholesale.")]
    public Task<string> PatchPrivilege(
        [Description("The privilege name to patch.")] string name,
        [Description("Partial update.")] PatchPrivilegeRequest patch,
        CancellationToken ct = default) =>
        Patch("AxSecurityPrivilege", name, patch, "patch_privilege", ct);

    // ---- AxSecurityDuty --------------------------------------------------

    [McpServerTool(Name = "xpp_create_duty"), Description(
        "Create a new AxSecurityDuty. A duty groups privileges that " +
        "together represent a coherent task / job function (e.g. " +
        "AccountingDistCustFreeInvoiceMaintain). Convention: " +
        "'<Area><Function>{Maintain|View|Inquire}'.")]
    public Task<string> CreateDuty(
        [Description("The duty to create.")] CreateDutyRequest request,
        CancellationToken ct = default) =>
        Create("AxSecurityDuty", request, "create_duty", ct);

    [McpServerTool(Name = "xpp_get_duty"), Description("Read an existing duty as its typed domain shape.")]
    public Task<string> GetDuty(
        [Description("The duty name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxSecurityDuty", name, "get_duty", ct);

    [McpServerTool(Name = "xpp_patch_duty"), Description("Apply a partial update to a duty.")]
    public Task<string> PatchDuty(
        [Description("The duty name to patch.")] string name,
        [Description("Partial update.")] PatchDutyRequest patch,
        CancellationToken ct = default) =>
        Patch("AxSecurityDuty", name, patch, "patch_duty", ct);

    // ---- AxSecurityRole --------------------------------------------------

    [McpServerTool(Name = "xpp_create_role"), Description(
        "Create a new AxSecurityRole. A role represents a job title " +
        "(LedgerAccountant, CustOrderClerk) and aggregates duties, " +
        "privileges, sub-roles, and direct table access. Convention: " +
        "'<JobTitle>'.")]
    public Task<string> CreateRole(
        [Description("The role to create.")] CreateRoleRequest request,
        CancellationToken ct = default) =>
        Create("AxSecurityRole", request, "create_role", ct);

    [McpServerTool(Name = "xpp_get_role"), Description("Read an existing role as its typed domain shape.")]
    public Task<string> GetRole(
        [Description("The role name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxSecurityRole", name, "get_role", ct);

    [McpServerTool(Name = "xpp_patch_role"), Description("Apply a partial update to a role.")]
    public Task<string> PatchRole(
        [Description("The role name to patch.")] string name,
        [Description("Partial update.")] PatchRoleRequest patch,
        CancellationToken ct = default) =>
        Patch("AxSecurityRole", name, patch, "patch_role", ct);

    // ---- AxSecurityPolicy ------------------------------------------------

    [McpServerTool(Name = "xpp_create_policy"), Description(
        "Create a new AxSecurityPolicy — row-level security. A policy " +
        "pins a PrimaryTable + a row-filter Query, and propagates the " +
        "filter to related tables listed in ConstrainedTables " +
        "(polymorphic: Table entries name a relation back to the " +
        "primary; Expression entries group further restrictions).")]
    public Task<string> CreatePolicy(
        [Description("The policy to create.")] CreatePolicyRequest request,
        CancellationToken ct = default) =>
        Create("AxSecurityPolicy", request, "create_policy", ct);

    [McpServerTool(Name = "xpp_get_policy"), Description("Read an existing policy as its typed domain shape.")]
    public Task<string> GetPolicy(
        [Description("The policy name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxSecurityPolicy", name, "get_policy", ct);

    [McpServerTool(Name = "xpp_patch_policy"), Description("Apply a partial update to a policy.")]
    public Task<string> PatchPolicy(
        [Description("The policy name to patch.")] string name,
        [Description("Partial update.")] PatchPolicyRequest patch,
        CancellationToken ct = default) =>
        Patch("AxSecurityPolicy", name, patch, "patch_policy", ct);

    // ---- shared helpers --------------------------------------------------

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

    private async Task<string> Get(string axType, string name, string opName, CancellationToken ct)
    {
        var (resolved, gate) = ResolveOrGate(requireProject: false);
        if (gate != null) return gate;
        _ = resolved;
        GetDomainObjectResponse resp;
        try
        {
            resp = await _conn.Client.GetDomainObjectAsync(new GetDomainObjectRequest
            {
                AxType = axType, Name = name,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure(axType, opName, rx); }
        using var doc = JsonDocument.Parse(resp.DomainJson);
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
