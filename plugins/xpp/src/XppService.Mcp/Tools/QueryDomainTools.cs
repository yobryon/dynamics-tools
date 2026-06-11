using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Queries;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxQuery. Scope: AxQuerySimple
/// (the modern join/filter shape that covers ~95% of queries).
/// AxQueryComposite (union/aggregate) is escape-hatched.
///
/// Data sources are recursive (joins inside joins) and polymorphic
/// (Kind: Root / Embedded / Derived). The discriminator surfaces as
/// the Kind enum on each QueryDataSource — the mapper handles the
/// on-disk xsi:type translation.
/// </summary>
[McpServerToolType]
public sealed class QueryDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public QueryDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_create_query"), Description(
        "Create a new AxQuery in the active dynamics-xpp project's model. " +
        "Scope: AxQuerySimple — the join/filter query type. Provide " +
        "DataSources (typically one Root with nested Embedded children) " +
        "plus title / description / behavior flags. Method bodies are " +
        "opaque X++ text (most queries need only the [Query] " +
        "classDeclaration, which the mapper emits by default when " +
        "SourceCode is omitted). For union/aggregate queries " +
        "(AxQueryComposite), use the raw xpp_update_object escape hatch.")]
    public async Task<string> CreateQuery(
        [Description("The AxQuery to create. See CreateQueryRequest schema.")]
        CreateQueryRequest request,
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
                AxType = "AxQuery",
                Model = resolved!.Model,
                DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxQuery", "create_query", rx); }

        var sideEffects = await RecordPostWriteAsync("AxQuery", resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: sideEffects.AddedToProject,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: sideEffects.Warnings);
    }

    [McpServerTool(Name = "xpp_get_query"), Description(
        "Read an existing AxQuery as its typed domain shape. Returns " +
        "the full query including the recursive data-source tree, " +
        "ranges, relations, order-by, group-by, having predicates, and " +
        "the methods source. The response can be passed straight back " +
        "into xpp_create_query to clone, or used as a starting point " +
        "for xpp_patch_query.")]
    public async Task<string> GetQuery(
        [Description("The AxQuery name to read.")] string name,
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
                AxType = "AxQuery",
                Name = name,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxQuery", "get_query", rx); }

        using var doc = JsonDocument.Parse(resp.DomainJson);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType,
            name = resp.Name,
            domain = doc.RootElement,
        });
    }

    [McpServerTool(Name = "xpp_patch_query"), Description(
        "Apply a partial update to an existing AxQuery. Merge-patch " +
        "semantics: null fields preserve current state, non-null fields " +
        "replace. Collections (DataSources, Ranges, etc.) replace the " +
        "entire list when non-null — read with xpp_get_query, mutate " +
        "the tree in-process, and patch back.")]
    public async Task<string> PatchQuery(
        [Description("The AxQuery to patch.")] string name,
        [Description("Partial update. See PatchQueryRequest schema.")]
        PatchQueryRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync("AxQuery", name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }


        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = "AxQuery",
                Model = resolved!.Model,
                Name = name,
                PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxQuery", "patch_query", rx); }

        var sideEffects = await RecordPostWriteAsync("AxQuery", resp.Name, createdHere: false, ct).ConfigureAwait(false);
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
