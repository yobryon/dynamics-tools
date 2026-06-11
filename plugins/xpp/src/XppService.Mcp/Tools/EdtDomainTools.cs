using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Edts;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxEdt. Mirrors the AxEnum pattern
/// established by EnumDomainTools, with the addition that EDTs are
/// polymorphic at the file root (the BaseType discriminator drives which
/// nested options block applies).
///
/// Three tools:
/// - xpp_create_edt
/// - xpp_get_edt
/// - xpp_patch_edt (BaseType not patchable — change subtype = recreate)
/// </summary>
[McpServerToolType]
public sealed class EdtDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public EdtDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_create_edt"), Description(
        "Create a new AxEdt in the active dynamics-xpp project's model. " +
        "Takes a typed CreateEdtRequest with a BaseType discriminator " +
        "(String / Int / Int64 / Real / Enum / Date / Time / UtcDateTime / " +
        "Container / Guid) that drives which nested options block applies. " +
        "Pure-inheritance EDTs (Extends set, no overrides) are valid; the " +
        "base's properties are inherited. Returns {axType, model, name, " +
        "created, addedToProject, changesetUpdated}.")]
    public async Task<string> CreateEdt(
        [Description("The AxEdt to create. See CreateEdtRequest schema for full property semantics — note nested options blocks (String, Numeric, Real, Enum, Date, Time, Utc) gate to specific BaseType values.")]
        CreateEdtRequest request,
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
                AxType = "AxEdt",
                Model = resolved!.Model,
                DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxEdt", "create_edt", rx); }

        var sideEffects = await RecordPostWriteAsync("AxEdt", resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: sideEffects.AddedToProject,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: sideEffects.Warnings);
    }

    [McpServerTool(Name = "xpp_get_edt"), Description(
        "Read an existing AxEdt as its typed domain shape. Returns the full " +
        "EDT including BaseType, the matching nested options block, " +
        "Extends, Relations, TableReferences, ArrayElements, and advanced " +
        "properties. The response can be sent straight back as a Create " +
        "payload to clone the EDT.")]
    public async Task<string> GetEdt(
        [Description("The AxEdt name to read.")] string name,
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
                AxType = "AxEdt",
                Name = name,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxEdt", "get_edt", rx); }

        using var doc = JsonDocument.Parse(resp.DomainJson);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType,
            name = resp.Name,
            domain = doc.RootElement,
        });
    }

    [McpServerTool(Name = "xpp_patch_edt"), Description(
        "Apply a partial update to an existing AxEdt. Merge-patch " +
        "semantics: null fields preserve current state, non-null fields " +
        "replace. BaseType is NOT patchable (changing the discriminator " +
        "is a re-create operation — use xpp_create_edt). " +
        "Nested option blocks (String / Numeric / etc.) replace " +
        "wholesale when non-null; pass the whole block, not a partial.")]
    public async Task<string> PatchEdt(
        [Description("The AxEdt to patch.")] string name,
        [Description("Partial update. See PatchEdtRequest schema.")]
        PatchEdtRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync("AxEdt", name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }


        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = "AxEdt",
                Model = resolved!.Model,
                Name = name,
                PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxEdt", "patch_edt", rx); }

        var sideEffects = await RecordPostWriteAsync("AxEdt", resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    // ---- shared helpers (intentionally near-clones of EnumDomainTools)
    //
    // Per-AOT-type tool classes duplicate this scaffolding rather than
    // depending on a shared base — the duplication is small, predictable,
    // and keeps each tool class self-contained for readability. We'll
    // factor when there are 3+ types and the pattern stabilizes.

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
                configured = false,
                cwd = Environment.CurrentDirectory,
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
            axType,
            code = rx.Status.StatusCode.ToString(),
            message = rx.Status.Detail,
            hint = "Domain mapping or bridge write rejected the payload. " +
                   "Check the message text — it usually names the offending field.",
        });

    private readonly record struct SideEffectResult(bool AddedToProject, bool ChangesetUpdated, string[] Warnings);
}
