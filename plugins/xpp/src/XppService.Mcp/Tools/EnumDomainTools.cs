using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Enums;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxEnum. First type to use the new
/// domain-model surface — supersedes the XML round-trip path for enums.
/// Three tools per type:
/// - xpp_create_enum  — author a new AxEnum from a typed request.
/// - xpp_get_enum     — read an existing AxEnum as its domain shape.
/// - xpp_patch_enum   — apply a partial update; null fields preserve
///                      current state, non-null replace it.
///
/// The raw <c>xpp_create_object</c> / <c>xpp_update_object</c> tools stay
/// available as escape hatch for cases the domain shape doesn't cover.
/// </summary>
[McpServerToolType]
public sealed class EnumDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public EnumDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_create_enum"), Description(
        "Create a new AxEnum in the active dynamics-xpp project's model. " +
        "Takes a typed CreateEnumRequest (name, values, optional label/help, " +
        "extensibility, style, advanced options). Sensible defaults: " +
        "IsExtensible=true (modern convention), Style=ComboBox, " +
        "UseExplicitValues=false (auto-assign by ordinal), Visibility=Public. " +
        "Returns {axType, model, name, created, addedToProject, " +
        "changesetUpdated}. Use this for new enum authoring; the raw " +
        "xpp_create_object surface remains available for unusual cases.")]
    public async Task<string> CreateEnum(
        [Description("The AxEnum to create. See CreateEnumRequest schema for full property semantics.")]
        CreateEnumRequest request,
        CancellationToken ct = default)
    {
        var (resolved, gate) = await ResolveOrGateAsync().ConfigureAwait(false);
        if (gate != null) return gate;

        var domainJson = DomainJson.Serialize(request);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.CreateDomainObjectAsync(new CreateDomainObjectRequest
            {
                AxType = "AxEnum",
                Model = resolved!.Model,
                DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxEnum", "create_enum", rx); }

        var sideEffects = await RecordPostWriteAsync("AxEnum", resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: sideEffects.AddedToProject,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: sideEffects.Warnings);
    }

    [McpServerTool(Name = "xpp_get_enum"), Description(
        "Read an existing AxEnum as its typed domain shape. Returns the " +
        "full enum (name, values, label, help, IsExtensible, Style, " +
        "UseExplicitValues, advanced options). Defaults that match MS's " +
        "on-disk defaults are returned as their actual values, so the " +
        "response can be sent straight back as a Create payload to " +
        "duplicate / clone the enum.\n\n" +
        "EXTENSIBLE ENUMS: when IsExtensible=true the member integers are NOT " +
        "authoritative (dbsync allocates them at deployment; they differ across " +
        "environments). The response flags this (valuesAuthoritative=false + " +
        "valueGuidance) and omits the meaningless per-member integers — never " +
        "hardcode an extensible enum's numeric value; dereference the symbol in " +
        "X++ (enum2int(Enum::Member)).")]
    public async Task<string> GetEnum(
        [Description("The AxEnum name to read.")] string name,
        CancellationToken ct = default)
    {
        var (resolved, gate) = await ResolveOrGateAsync(requireProject: false).ConfigureAwait(false);
        if (gate != null) return gate;
        _ = resolved;

        GetDomainObjectResponse resp;
        try
        {
            resp = await _conn.Client.GetDomainObjectAsync(new GetDomainObjectRequest
            {
                AxType = "AxEnum",
                Name = name,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxEnum", "get_enum", rx); }

        var domain = System.Text.Json.Nodes.JsonNode.Parse(resp.DomainJson)?.AsObject();
        var payload = new Dictionary<string, object?>
        {
            ["axType"] = resp.AxType,
            ["name"] = resp.Name,
        };

        // Extensible enum: the member integers are allocated by dbsync at deploy
        // and are NOT authoritative (dev-box != prod). Be loud and don't hand the
        // agent misleading numbers to key off — the source bug here was an agent
        // nearly hardcoding a (wrong) dev-box ordinal. Non-extensible enums have
        // AOT-dictated values and are returned unchanged.
        var extensible = domain?["isExtensible"]?.GetValue<bool>() ?? false;
        if (domain != null && extensible)
        {
            var explicitVals = domain["useExplicitValues"]?.GetValue<bool>() ?? false;
            domain["valuesAuthoritative"] = false;
            payload["valueGuidance"] =
                "isExtensible=true: member integer values are allocated by the dbsync engine at DEPLOYMENT and differ " +
                "across environments — the numbers on this dev box are NOT what production will assign. Never hardcode " +
                "or compare the integer; always dereference the symbol in X++ (e.g. enum2int(" + resp.Name + "::Member), " +
                "or switch on the symbol). " +
                (explicitVals
                    ? "This enum sets explicit values: they're fixed for existing members, but members added later (here or in other models) are allocated at deploy — still don't hardcode."
                    : "Per-member integers are omitted below because they're non-authoritative (positional/dev-only).");

            if (domain["values"] is System.Text.Json.Nodes.JsonArray vals)
                foreach (var v in vals.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    if (!explicitVals) v.Remove("value");   // meaningless; don't tempt the agent
                    else v["valueAuthoritative"] = false;
                }
        }

        payload["domain"] = domain;
        return JsonSerializer.Serialize(payload);
    }

    [McpServerTool(Name = "xpp_patch_enum"), Description(
        "Apply a partial update to an existing AxEnum. Merge-patch " +
        "semantics: every field on the patch is nullable; null means " +
        "\"leave the current value unchanged.\" Values list non-null " +
        "replaces wholesale. Returns {axType, model, name, updated, " +
        "changesetUpdated}. The model comes from the active " +
        ".dynamics-xpp project; you cannot patch enums in other models.")]
    public async Task<string> PatchEnum(
        [Description("The AxEnum to patch.")] string name,
        [Description("Partial update. See PatchEnumRequest schema for what's patchable.")]
        PatchEnumRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = await ResolveOrGateAsync().ConfigureAwait(false);
        if (gate != null) return gate;
        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync("AxEnum", name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }


        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = "AxEnum",
                Model = resolved!.Model,
                Name = name,
                PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxEnum", "patch_enum", rx); }

        var sideEffects = await RecordPostWriteAsync("AxEnum", resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    // ---- helpers ---------------------------------------------------------

    private async Task<(ResolvedConfig? resolved, string? gate)> ResolveOrGateAsync(bool requireProject = true)
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
        await Task.CompletedTask;
        return (resolved, null);
    }

    private async Task<SideEffectResult> RecordPostWriteAsync(string axType, string name, bool createdHere, CancellationToken ct)
    {
        // Mirror AuthoringTools post-write side effects: add to rnrproj +
        // update changeset. Failure of either is recorded as a warning;
        // the underlying write already succeeded so we don't fail the
        // whole call.
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
