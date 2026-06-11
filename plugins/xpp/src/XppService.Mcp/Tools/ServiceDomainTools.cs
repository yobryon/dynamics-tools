using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Services;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxService and AxServiceGroup —
/// SOAP/REST endpoints and the deployment groups that bundle them.
/// </summary>
[McpServerToolType]
public sealed class ServiceDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public ServiceDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    // ---- AxService -------------------------------------------------------

    [McpServerTool(Name = "xpp_create_service"), Description(
        "Create a new AxService — a SOAP/REST service endpoint. Service " +
        "operations name X++ methods on the backing Class. Conventionally " +
        "the Name ends with 'Service' and the Namespace is " +
        "'http://schemas.microsoft.com/dynamics/<year>/services'.")]
    public Task<string> CreateService(
        [Description("The service to create.")] CreateServiceRequest request,
        CancellationToken ct = default) =>
        Create("AxService", request, "create_service", ct);

    [McpServerTool(Name = "xpp_get_service"), Description("Read an existing service as its typed domain shape.")]
    public Task<string> GetService(
        [Description("The service name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxService", name, "get_service", ct);

    [McpServerTool(Name = "xpp_patch_service"), Description("Apply a partial update to a service. Collections replace wholesale.")]
    public Task<string> PatchService(
        [Description("The service name to patch.")] string name,
        [Description("Partial update.")] PatchServiceRequest patch,
        CancellationToken ct = default) =>
        Patch("AxService", name, patch, "patch_service", ct);

    // ---- AxServiceGroup --------------------------------------------------

    [McpServerTool(Name = "xpp_create_service_group"), Description(
        "Create a new AxServiceGroup — a deployment bundle for related " +
        "AxService objects. AutoDeploy=true makes the group activate on " +
        "model deployment.")]
    public Task<string> CreateServiceGroup(
        [Description("The service group to create.")] CreateServiceGroupRequest request,
        CancellationToken ct = default) =>
        Create("AxServiceGroup", request, "create_service_group", ct);

    [McpServerTool(Name = "xpp_get_service_group"), Description("Read an existing service group as its typed domain shape.")]
    public Task<string> GetServiceGroup(
        [Description("The service-group name to read.")] string name,
        CancellationToken ct = default) =>
        Get("AxServiceGroup", name, "get_service_group", ct);

    [McpServerTool(Name = "xpp_patch_service_group"), Description("Apply a partial update to a service group.")]
    public Task<string> PatchServiceGroup(
        [Description("The service-group name to patch.")] string name,
        [Description("Partial update.")] PatchServiceGroupRequest patch,
        CancellationToken ct = default) =>
        Patch("AxServiceGroup", name, patch, "patch_service_group", ct);

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
