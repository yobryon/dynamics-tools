using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Tiles;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxTile — workspace / dashboard tile.
/// Tiles wrap a menu-item / query / KPI and render onto the workspace
/// grid as a clickable surface.
/// </summary>
[McpServerToolType]
public sealed class TileDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public TileDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_create_tile"), Description(
        "Create a new AxTile (workspace / dashboard tile). Type=Link is a " +
        "static tile that opens MenuItemName on click; Type=Count renders " +
        "the row-count of Query; Type=KPI renders a KPI gauge. " +
        "TileDisplay controls the visual (BackgroundImage / TextAndImage / " +
        "TextOnly), Size sets the grid footprint (Small / Medium / Wide / " +
        "Large / ShortWide).")]
    public async Task<string> CreateTile(
        [Description("The tile to create.")] CreateTileRequest request,
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
                AxType = "AxTile", Model = resolved!.Model, DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxTile", "create_tile", rx); }
        var se = await RecordPostWriteAsync("AxTile", resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: se.AddedToProject,
            changesetUpdated: se.ChangesetUpdated,
            sideEffectWarnings: se.Warnings);
    }

    [McpServerTool(Name = "xpp_get_tile"), Description("Read an existing tile as its typed domain shape.")]
    public async Task<string> GetTile(
        [Description("The tile name to read.")] string name,
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
                AxType = "AxTile", Name = name,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxTile", "get_tile", rx); }
        using var doc = JsonDocument.Parse(resp.DomainJson);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType, name = resp.Name, domain = doc.RootElement,
        });
    }

    [McpServerTool(Name = "xpp_patch_tile"), Description("Apply a partial update to a tile.")]
    public async Task<string> PatchTile(
        [Description("The tile name to patch.")] string name,
        [Description("Partial update.")] PatchTileRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync("AxTile", name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }

        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = "AxTile", Model = resolved!.Model, Name = name, PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxTile", "patch_tile", rx); }
        var se = await RecordPostWriteAsync("AxTile", resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var warnings = se.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: se.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    // ---- helpers (same shape as ExtensionDomainTools / SecurityDomainTools) ----

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
