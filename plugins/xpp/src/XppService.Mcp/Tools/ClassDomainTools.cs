using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Domain;
using Xpp.Service.Domain.Classes;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Domain-shaped authoring tools for AxClass. Smallest typed surface
/// so far — AxClass at the XML root is just Name + SourceCode +
/// occasionally IsObsolete/Tags. The agent provides X++ source
/// directly (declaration block + methods); the mapper preserves the
/// text verbatim through round-trip. Class-level semantics
/// (extends, abstract, final, visibility) are expressed in X++
/// keywords in the Declaration, not via separate XML properties.
/// </summary>
[McpServerToolType]
public sealed class ClassDomainTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public ClassDomainTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_create_class"), Description(
        "Create a new AxClass in the active dynamics-xpp project's " +
        "model. Provide Name plus SourceCode (Declaration + Methods). " +
        "Method bodies are opaque X++ text — the mapper preserves them " +
        "verbatim. Class semantics (extends, abstract, final, public/" +
        "private, static, interface) are expressed in X++ keywords in " +
        "the Declaration source, not as separate XML properties.")]
    public async Task<string> CreateClass(
        [Description("The AxClass to create. See CreateClassRequest schema.")]
        CreateClassRequest request,
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
                AxType = "AxClass",
                Model = resolved!.Model,
                DomainJson = domainJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxClass", "create_class", rx); }

        var sideEffects = await RecordPostWriteAsync("AxClass", resp.Name, createdHere: true, ct).ConfigureAwait(false);
        return WriteResponseSerializer.Serialize(resp, "create",
            addedToProject: sideEffects.AddedToProject,
            changesetUpdated: sideEffects.ChangesetUpdated,
            sideEffectWarnings: sideEffects.Warnings);
    }

    [McpServerTool(Name = "xpp_get_class"), Description(
        "Read an existing AxClass as its typed domain shape. Returns " +
        "Name + SourceCode (Declaration + every method's name and " +
        "verbatim X++ source). The response can be sent straight back " +
        "into xpp_create_class to clone, or used as the starting point " +
        "for a patch.\n\n" +
        "PATH-ADDRESSABLE NAVIGATION for big classes (100s of methods): set " +
        "outline=true for a skeleton (every method as a node with its " +
        "signature, bodies elided), and/or atPath (e.g. " +
        "'/sourceCode/methods/validateWrite') to read one method's full " +
        "source. Use xpp_find_in_object to locate a method by name first.")]
    public async Task<string> GetClass(
        [Description("The AxClass name to read.")] string name,
        [Description("Return a structural skeleton (methods as nodes with signatures, bodies elided) instead of full content.")]
        bool outline = false,
        [Description("Root the read at this path, e.g. '/sourceCode/methods/validateWrite'. Empty = whole class.")]
        string? atPath = null,
        [Description("Outline depth below the root. depth=0 (default) = counts; depth=1 lists members (method names + signatures).")]
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
                _conn.Client, "AxClass", name, outline, atPath, depth, ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxClass", "get_class", rx); }

        if (navJson != null) return navJson;

        using var doc = JsonDocument.Parse(resp.DomainJson);
        return JsonSerializer.Serialize(new
        {
            axType = resp.AxType,
            name = resp.Name,
            domain = doc.RootElement,
        });
    }

    [McpServerTool(Name = "xpp_patch_class"), Description(
        "Apply a partial update to an existing AxClass. Merge-patch " +
        "semantics: null fields preserve current state, non-null fields " +
        "replace. SourceCode replacement replaces both Declaration AND " +
        "the entire Methods list — to patch just methods, read with " +
        "xpp_get_class, mutate the methods list in-process, and patch " +
        "back. Same for Methods within SourceCode.")]
    public async Task<string> PatchClass(
        [Description("The AxClass to patch.")] string name,
        [Description("Partial update. See PatchClassRequest schema.")]
        PatchClassRequest patch,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;
        string? scmPreWarning = null;
        try { scmPreWarning = await _project.ScmCheckoutAsync("AxClass", name, ct).ConfigureAwait(false); }
        catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }


        var patchJson = DomainJson.Serialize(patch);
        WriteObjectResponse resp;
        try
        {
            resp = await _conn.Client.PatchDomainObjectAsync(new PatchDomainObjectRequest
            {
                AxType = "AxClass",
                Model = resolved!.Model,
                Name = name,
                PatchJson = patchJson,
            }, cancellationToken: ct);
        }
        catch (RpcException rx) { return BridgeFailure("AxClass", "patch_class", rx); }

        var sideEffects = await RecordPostWriteAsync("AxClass", resp.Name, createdHere: false, ct).ConfigureAwait(false);
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
