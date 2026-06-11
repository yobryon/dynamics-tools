using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Tables;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxTable. Mirrors the AxEnum /
/// AxEdt pattern with two added dimensions of polymorphism — fields
/// (AxTableFieldString / Int / Enum / etc.) and relation constraints
/// (AxTableRelationConstraintField / Fixed / RelatedFixed) — both
/// handled inside the mapper, not exposed as xsi:type to the agent.
/// </summary>
[McpServerToolType]
public sealed class TableDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public TableDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_create_table"), Description(
        "Create a new AxTable in the active dynamics-xpp project's model. " +
        "Takes a typed CreateTableRequest covering ~25 common scalar " +
        "properties, fields (all 10 FieldType variants with shared + " +
        "type-gated properties), indexes, relations with constraints " +
        "(Field/Fixed/RelatedFixed), field groups, delete actions, and " +
        "X++ source code (Declaration + Methods). Method bodies are " +
        "opaque text preserved verbatim. Exotic surface (state " +
        "machines, mappings, full-text indexes, a handful of advanced " +
        "scalars) is escape-hatched to xpp_update_object — see " +
        "plugins/xpp/docs/domain-coverage.md.")]
    public async Task<string> CreateTable(
        [Description("The AxTable to create. See CreateTableRequest schema for the full property surface.")]
        CreateTableRequest request,
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
                AxType = "AxTable",
                Model = resolved!.Model,
                DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxTable", "create_table", rx); }

        var sideEffects = await RecordPostWriteAsync("AxTable", resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: sideEffects.AddedToProject,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: sideEffects.Warnings);
    }

    [McpServerTool(Name = "xpp_get_table"), Description(
        "Read an existing AxTable as its typed domain shape. The " +
        "response can be sent back into xpp_create_table to clone, or " +
        "used as the starting point for xpp_patch_table.\n\n" +
        "PATH-ADDRESSABLE NAVIGATION for wide tables (200+ fields, many " +
        "relations): set outline=true for a depth-bounded skeleton " +
        "(fields/indexes/relations/fieldGroups/methods as nodes with " +
        "addressable paths). Set atPath (e.g. '/relations/PaymTerm' or " +
        "'/fields/AccountNum') to root the read at one subtree. Use " +
        "xpp_find_in_object to locate a path first. Paths are reusable as " +
        "patch targets.")]
    public async Task<string> GetTable(
        [Description("The AxTable name to read.")] string name,
        [Description("Return a structural skeleton (nodes + addressable paths, scalars elided) instead of full content. Pair with depth.")]
        bool outline = false,
        [Description("Root the read at this path, e.g. '/relations/PaymTerm/constraints/PaymTermId' or '/methods/validateWrite'. Empty = whole table.")]
        string? atPath = null,
        [Description("Outline depth below the root. depth=0 (default) = collection counts only — the compact orient for a wide table (e.g. 'fields:184, relations:84, ...'). depth=1 lists members one level (every field/relation/index by name); on a wide table prefer atPath into one collection (/fields, /relations) over a whole depth=1. depth=2 adds sub-counts.")]
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
                _conn.Client, "AxTable", name, outline, atPath, depth, ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxTable", "get_table", rx); }

        if (navJson != null) return navJson;

        using var doc = JsonDocument.Parse(resp.DomainJson);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType,
            name = resp.Name,
            domain = doc.RootElement,
        });
    }

    [McpServerTool(Name = "xpp_patch_table"), Description(
        "Apply a partial update to an existing AxTable. Merge-patch " +
        "semantics: null fields preserve current state, non-null fields " +
        "replace. Collections (Fields / Indexes / Relations / etc.) " +
        "replace the entire list when non-null — read with xpp_get_table, " +
        "mutate, patch back.")]
    public async Task<string> PatchTable(
        [Description("The AxTable to patch.")] string name,
        [Description("Partial update. See PatchTableRequest schema.")]
        PatchTableRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync("AxTable", name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }


        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = "AxTable",
                Model = resolved!.Model,
                Name = name,
                PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxTable", "patch_table", rx); }

        var sideEffects = await RecordPostWriteAsync("AxTable", resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.ToList();
        if (scmPreWarning != null) warnings.Insert(0, $"scm: {scmPreWarning}");
        return WriteResponseSerializer.Serialize(resp, "patch",
            addedToProject: null,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: warnings);
    }

    // ---- shared helpers (near-clones of EnumDomainTools / EdtDomainTools)

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
