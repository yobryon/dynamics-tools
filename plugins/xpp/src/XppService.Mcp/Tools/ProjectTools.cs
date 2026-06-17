using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Project-context inspection. The single tool here reports the current
/// .dynamics-xpp config state, the active rnrproj, the model the project
/// targets, the naming conventions, the changeset, and a count of how many
/// AOT items the project already references. Authoring tools call into the
/// same <see cref="ProjectContext"/> to enforce the convention; this is the
/// read-only surface the agent uses to orient.
/// </summary>
[McpServerToolType]
public sealed class ProjectTools
{
    private readonly ProjectContext _project;
    private readonly XppServiceConnection _conn;

    public ProjectTools(ProjectContext project, XppServiceConnection conn)
    {
        _project = project;
        _conn = conn;
    }

    [McpServerTool(Name = "xpp_project_status"), Description(
        "Report the active dynamics-xpp project state. When configured, " +
        "returns {configured: true, rnprojPath, slnPath, model, naming: " +
        "{objectPrefix, extensionSuffix}, changeset: {count, recentlyTouched}, " +
        "projectObjectCount}. The slnPath field tells you which .sln " +
        "xpp_compile will hand to devenv.com — confirm it's the right one " +
        "before relying on compile output. When the launch directory has no " +
        ".dynamics-xpp/config.json, returns {configured: false} with a " +
        "pointer at the dynamics-xpp:xpp-project skill so the agent can drive " +
        "first-time setup. Call this before any write operation to confirm " +
        "you're operating in the right project / model.")]
    public Task<string> ProjectStatus(CancellationToken ct = default)
    {
        ResolvedConfig? resolved;
        try { resolved = _project.Resolve(); }
        catch (ProjectConfigException pcx)
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                configured = false,
                error = "project_config_invalid",
                message = pcx.Message,
                hint = "Load the dynamics-xpp:xpp-project skill for the .dynamics-xpp/config.json shape."
            }));
        }

        if (resolved == null)
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                configured = false,
                cwd = Environment.CurrentDirectory,
                message = "No .dynamics-xpp/config.json in the current directory. Load the dynamics-xpp:xpp-project skill and walk the user through first-time setup before any write operation.",
                skill = "dynamics-xpp:xpp-project"
            }));
        }

        var changeset = _project.ReadChangeset();
        var recent = changeset.Objects
            .OrderByDescending(o => o.LastTouchedAt)
            .Take(10)
            .Select(o => new { axType = o.AxType, name = o.Name, lastTouchedAt = o.LastTouchedAt, createdHere = o.CreatedHere })
            .ToArray();

        var payload = new
        {
            configured = true,
            rnprojPath = resolved.RnprojPath,
            slnPath = resolved.SlnPath,
            model = resolved.Model,
            module = resolved.Module,
            naming = new
            {
                objectPrefix = resolved.ObjectPrefix,
                extensionSuffix = resolved.ExtensionSuffix
            },
            changeset = new
            {
                count = changeset.Objects.Count,
                recentlyTouched = recent
            },
            projectObjectCount = _project.CountProjectObjects()
        };

        _ = ct;
        return Task.FromResult(JsonSerializer.Serialize(payload));
    }

    [McpServerTool(Name = "xpp_project_add_object"), Description(
        "Idempotently add an existing on-disk AOT object to the active " +
        ".rnrproj. Use this when an object exists in the model directory but " +
        "isn't yet referenced by the project (e.g., the user authored it in " +
        "VS without saving the project, or it was created out-of-band). Does " +
        "NOT create the object — call xpp_create_object for that. Returns " +
        "{added: bool, alreadyPresent: bool}. Use the AxType the object was " +
        "created with (e.g., AxClass, AxTable, AxTableExtension).")]
    public async Task<string> ProjectAddObject(
        [Description("AOT type (AxClass, AxTable, AxTableExtension, AxForm, etc.)")] string axType,
        [Description("Object name exactly as it lives on disk.")] string name,
        CancellationToken ct = default)
    {
        var gate = RequireConfig();
        if (gate != null) return gate;

        var added = await _project.AddToRnprojAsync(axType, name, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            added,
            alreadyPresent = !added,
            axType,
            name
        });
    }

    [McpServerTool(Name = "xpp_project_remove_object"), Description(
        "Remove an AOT object's reference from the active .rnrproj. Does NOT " +
        "delete the on-disk files — VS treats the project as a view onto the " +
        "model, so the object stays in the model directory and remains part " +
        "of the build. Use this to tidy the project when an object was added " +
        "by mistake. Returns {removed: bool, notPresent: bool}.")]
    public async Task<string> ProjectRemoveObject(
        [Description("AOT type (AxClass, AxTable, AxTableExtension, AxForm, etc.)")] string axType,
        [Description("Object name as referenced in the project.")] string name,
        CancellationToken ct = default)
    {
        var gate = RequireConfig();
        if (gate != null) return gate;

        var removed = await _project.RemoveFromRnprojAsync(axType, name, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            removed,
            notPresent = !removed,
            axType,
            name
        });
    }

    [McpServerTool(Name = "xpp_project_list_objects"), Description(
        "List the AOT objects currently referenced by the active .rnrproj. " +
        "Optional axType filter restricts to a single type (e.g. only " +
        "AxClass). Returns {count, objects: [{axType, name, link}]}. Useful " +
        "for orienting in an unfamiliar project before write operations.")]
    public Task<string> ProjectListObjects(
        [Description("Optional AOT type filter; null/empty returns every object.")] string? axType = null,
        CancellationToken ct = default)
    {
        var gate = RequireConfig();
        if (gate != null) return Task.FromResult(gate);

        var all = _project.ListRnprojObjects();
        var filtered = string.IsNullOrWhiteSpace(axType)
            ? all
            : all.Where(o => string.Equals(o.AxType, axType, StringComparison.OrdinalIgnoreCase)).ToArray();

        var payload = new
        {
            count = filtered.Count,
            objects = filtered.Select(o => new { axType = o.AxType, name = o.Name, link = o.Link }).ToArray()
        };
        _ = ct;
        return Task.FromResult(JsonSerializer.Serialize(payload));
    }

    [McpServerTool(Name = "xpp_changeset_clear"), Description(
        "Clear the .dynamics-xpp/changeset.json file. Use after a successful " +
        "compile + check-in to start a fresh scope for the next batch of " +
        "edits. Returns {cleared: true, previousCount: int}. Safe to call " +
        "when no changeset exists (returns previousCount: 0).")]
    public async Task<string> ChangesetClear(CancellationToken ct = default)
    {
        var gate = RequireConfig();
        if (gate != null) return gate;

        var prior = await _project.ClearChangesetAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { cleared = true, previousCount = prior });
    }

    [McpServerTool(Name = "xpp_delete_object"), Description(
        "Delete an AOT object from the active project's model. Removes " +
        "the on-disk XML, the .rnrproj reference, the changeset entry, " +
        "and (when TFVC is configured) marks the file for deletion in the " +
        "pending change set via 'tf delete'. " +
        "REFUSES by default if the object has inbound references in the " +
        "index — pass force=true to proceed anyway (you take responsibility " +
        "for the dangling references; the index will surface them as broken " +
        "after the next sweep). REFUSES if the object lives in a different " +
        "model than the active project — dynamics-xpp doesn't mutate " +
        "external models. Does NOT chase down or rewrite X++ source-code " +
        "references in other files — that's still the caller's job. " +
        "Returns {deleted: bool, fileRemoved, projectRemoved, " +
        "changesetRemoved, scmWarning, sideEffectWarnings}.")]
    public async Task<string> DeleteObject(
        [Description("AOT type, e.g. 'AxClass', 'AxTable', 'AxForm'. Case-insensitive.")] string axType,
        [Description("Object name (PascalCase) as referenced in the project / on disk.")] string name,
        [Description("Override the inbound-references safety check. Default false.")] bool force = false,
        CancellationToken ct = default)
    {
        var gate = RequireConfig();
        if (gate != null) return gate;
        var resolved = _project.Resolve()!;

        // Active-module gate: refuse if the target lives in a foreign model.
        // Different message than the update path's "use extensions" hint —
        // delete has no extension analogue.
        var modelMismatch = await CheckTargetIsInActiveModelAsync(axType, name, resolved, ct).ConfigureAwait(false);
        if (modelMismatch != null) return modelMismatch;

        // Inbound-refs check unless --force.
        if (!force)
        {
            var inbound = await CountInboundRefsAsync(axType, name, ct).ConfigureAwait(false);
            if (inbound.count > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "inbound_references_present",
                    message = $"{axType} '{name}' has {inbound.count} indexed inbound reference(s). Pass force=true to delete anyway.",
                    inboundReferenceCount = inbound.count,
                    sampleReferences = inbound.samples,
                });
            }
        }

        var warnings = new List<string>();

        // SCM delete of the source copy (tf-tracked): pending-delete on a
        // checked-in file, or undo on a pending-add. Best-effort.
        var scmResult = await _project.ScmDeleteAsync(axType, name, ct).ConfigureAwait(false);
        if (scmResult.Warning != null) warnings.Add($"scm: {scmResult.Warning}");

        // Then ensure BOTH on-disk copies are physically gone and report
        // fileRemoved from a real existence check — never from the SCM op
        // "succeeding". The element lives in two trees: the source copy (which
        // tf delete/undo usually removes) AND the generated XppMetadata runtime
        // copy (not tf-tracked; tf never touches it). Leaving the runtime copy
        // was the bug: the object kept showing up via find_object (source:disk)
        // and to the running AOS even though delete reported success.
        var sourcePath = _project.ResolveMetadataFilePath(axType, name);
        var runtimePath = _project.ResolveRuntimeMetadataFilePath(axType, name);
        foreach (var (path, label) in new[] { (sourcePath, "source"), (runtimePath, "runtime") })
        {
            if (path != null && File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { warnings.Add($"{label} metadata delete failed ({path}): {ex.Message}"); }
            }
        }
        // fileRemoved = the canonical source copy is gone; flag a lingering
        // runtime copy loudly rather than silently reporting success.
        var fileRemoved = sourcePath == null || !File.Exists(sourcePath);
        if (runtimePath != null && File.Exists(runtimePath))
            warnings.Add($"runtime metadata copy still present: {runtimePath}");

        // .rnrproj removal.
        bool projectRemoved;
        try { projectRemoved = await _project.RemoveFromRnprojAsync(axType, name, ct).ConfigureAwait(false); }
        catch (Exception ex) { projectRemoved = false; warnings.Add($"rnrproj remove failed: {ex.Message}"); }

        // Changeset removal.
        bool changesetRemoved;
        try { changesetRemoved = await _project.RemoveFromChangesetAsync(axType, name, ct).ConfigureAwait(false); }
        catch (Exception ex) { changesetRemoved = false; warnings.Add($"changeset remove failed: {ex.Message}"); }

        // Evict the search-index row so xpp_find_object / search stop returning
        // the just-deleted object immediately, rather than until the next full
        // sweep. Direct row delete (cascades to methods/refs/FTS) — no bridge
        // re-read, whose metadata provider could still have it cached.
        bool indexEvicted = false;
        try
        {
            var ev = await _conn.Client.EvictObjectFromIndexAsync(new EvictObjectRequest
            {
                AxType = axType, Name = name, Model = resolved.Model,
            }, cancellationToken: ct);
            indexEvicted = ev.Evicted;
        }
        catch (Exception ex) { warnings.Add($"index evict failed: {ex.Message}"); }

        return JsonSerializer.Serialize(new
        {
            deleted = true,
            axType,
            name,
            fileRemoved,
            projectRemoved,
            changesetRemoved,
            indexEvicted,
            forced = force,
            sideEffectWarnings = warnings.ToArray(),
        });
    }

    [McpServerTool(Name = "xpp_rename_object"), Description(
        "Rename an AOT object in the active project's model. Moves the " +
        "on-disk XML to the new filename, updates the inner <Name> element " +
        "so the metadata reader binds correctly, rewrites the .rnrproj " +
        "reference, updates the changeset, and (when TFVC is configured) " +
        "records the rename in the pending change set via 'tf rename'. " +
        "REFUSES if the object lives in a different model than the active " +
        "project. Does NOT chase down or rewrite X++ source-code references " +
        "in other files — but it DETECTS them: the response carries " +
        "'dependentCount' and 'dependents' (the indexed inbound referrers of " +
        "the old name, with sourceAxType / sourceName / kind) so you have an " +
        "exact to-fix list instead of guessing. The within-file X++ class " +
        "declaration IS updated automatically (class oldName extends... -> " +
        "class newName extends...). Returns {renamed, fileMoved, " +
        "nameElementUpdated, classDeclarationUpdated, projectUpdated, " +
        "changesetUpdated, dependentCount, dependents, sideEffectWarnings}.")]
    public async Task<string> RenameObject(
        [Description("AOT type, e.g. 'AxClass', 'AxTable', 'AxForm'.")] string axType,
        [Description("Current object name on disk.")] string oldName,
        [Description("New object name. Must follow the type's naming convention; pre-existing object with this name on disk fails the rename.")] string newName,
        CancellationToken ct = default)
    {
        var gate = RequireConfig();
        if (gate != null) return gate;
        var resolved = _project.Resolve()!;

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(new
            {
                error = "no_op",
                message = "oldName and newName are identical (case-insensitive). Nothing to do.",
            });
        }

        var modelMismatch = await CheckTargetIsInActiveModelAsync(axType, oldName, resolved, ct).ConfigureAwait(false);
        if (modelMismatch != null) return modelMismatch;

        var oldPath = _project.ResolveMetadataFilePath(axType, oldName);
        var newPath = _project.ResolveMetadataFilePath(axType, newName);
        if (oldPath == null || newPath == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = "no_metadata_path",
                message = "Could not resolve on-disk metadata path. SCM metadataPath must be configured for rename.",
                hint = "Configure scm.metadataPath in .dynamics-xpp/config.json (see dynamics-xpp:xpp-project).",
            });
        }
        if (!File.Exists(oldPath))
        {
            return JsonSerializer.Serialize(new
            {
                error = "source_not_found",
                message = $"{axType} '{oldName}' file does not exist on disk at {oldPath}.",
            });
        }
        if (File.Exists(newPath))
        {
            return JsonSerializer.Serialize(new
            {
                error = "destination_exists",
                message = $"An on-disk file already exists for {axType} '{newName}' at {newPath}. Refusing to overwrite.",
            });
        }

        // Capture inbound referrers of the OLD name BEFORE the rename, while the
        // index still resolves them. Rename does NOT rewrite these (X++ source,
        // form bindings, menu-item Objects, privilege entry points, ...) — but
        // surfacing exactly WHICH objects point at the old name turns the generic
        // "search manually" nudge into an actionable to-fix list. Same
        // indexer-backed detection xpp_delete_object uses.
        var dependents = await CountInboundRefsAsync(axType, oldName, ct).ConfigureAwait(false);

        var warnings = new List<string>();
        var fileMoved = false;

        // SCM rename if configured — handles File.Move atomically with the pending change.
        var scmResult = await _project.ScmRenameAsync(axType, oldName, newName, ct).ConfigureAwait(false);
        if (scmResult.Warning != null) warnings.Add($"scm: {scmResult.Warning}");
        if (scmResult.HandledLocalRename)
        {
            fileMoved = true;
        }
        else
        {
            try { File.Move(oldPath, newPath); fileMoved = true; }
            catch (Exception ex) { warnings.Add($"file move failed: {ex.Message}"); }
        }

        if (!fileMoved)
        {
            return JsonSerializer.Serialize(new
            {
                error = "rename_failed",
                message = "Could not move the on-disk file. See sideEffectWarnings.",
                sideEffectWarnings = warnings.ToArray(),
            });
        }

        // Patch the inner XML — <Name> element MUST match the file name,
        // and the within-file X++ class declaration must also match the
        // new name or the form/class won't compile.
        bool nameElementUpdated = false;
        bool classDeclarationUpdated = false;
        try
        {
            var xml = await File.ReadAllTextAsync(newPath, ct).ConfigureAwait(false);
            var nameRegex = new Regex(@"(?<open><Name>\s*)" + Regex.Escape(oldName) + @"(?<close>\s*</Name>)",
                RegexOptions.Compiled);
            var (updatedXml1, count1) = ReplaceCounted(nameRegex, xml, "${open}" + newName + "${close}");
            if (count1 > 0) nameElementUpdated = true;
            // Class declaration: "class <oldName> extends" or "class <oldName> implements".
            // Match \b boundaries so we don't substring-collide with longer
            // names; case-sensitive since X++ identifiers are conventionally
            // PascalCase even though the compiler is case-insensitive.
            var classRegex = new Regex(@"\bclass\s+" + Regex.Escape(oldName) + @"\b",
                RegexOptions.Compiled);
            var (updatedXml2, count2) = ReplaceCounted(classRegex, updatedXml1, "class " + newName);
            if (count2 > 0) classDeclarationUpdated = true;
            await File.WriteAllTextAsync(newPath, updatedXml2, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            warnings.Add($"xml self-reference rewrite failed: {ex.Message}");
        }

        if (!nameElementUpdated)
            warnings.Add("the inner <Name> element did not match oldName and was not updated — the on-disk file may not deserialize correctly. Inspect manually.");

        // .rnrproj: remove old entry, add new.
        bool projectUpdated;
        try
        {
            await _project.RemoveFromRnprojAsync(axType, oldName, ct).ConfigureAwait(false);
            projectUpdated = await _project.AddToRnprojAsync(axType, newName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { projectUpdated = false; warnings.Add($"rnrproj update failed: {ex.Message}"); }

        // Changeset rename.
        bool changesetUpdated;
        try { changesetUpdated = await _project.RenameInChangesetAsync(axType, oldName, newName, ct).ConfigureAwait(false); }
        catch (Exception ex) { changesetUpdated = false; warnings.Add($"changeset rename failed: {ex.Message}"); }

        warnings.Add(dependents.count == 0
            ? "No inbound references detected in the index — but the index can lag; "
              + "a quick xpp_find_references / grep on the old name before compiling is cheap insurance."
            : $"{dependents.count} object(s) reference the OLD name and were NOT rewritten "
              + "(rename moves the artifact only). See 'dependents' for the list — update each "
              + "(X++ source, form bindings, menu-item Objects, privilege entry points, ...) before the next compile.");

        return JsonSerializer.Serialize(new
        {
            renamed = true,
            axType,
            fromName = oldName,
            toName = newName,
            fileMoved,
            nameElementUpdated,
            classDeclarationUpdated,
            projectUpdated,
            changesetUpdated,
            dependentCount = dependents.count,
            dependents = dependents.samples,
            sideEffectWarnings = warnings.ToArray(),
        });
    }

    [McpServerTool(Name = "xpp_project_set_db_sync_in_build"), Description(
        "Toggle the active rnrproj's <DBSyncInBuild> property. When true, " +
        "xpp_compile / VS Build also runs the database synchronization step " +
        "after a successful build, so new tables / fields / indexes are " +
        "materialized in SQL. When false, build only compiles X++ — DBSync " +
        "must be triggered separately. Use true for schema-touching work; " +
        "leave false for code-only iterations to save ~30s per build. " +
        "Idempotent. Returns {previous: bool, current: bool}.")]
    public async Task<string> SetDbSyncInBuild(
        [Description("Whether the rnrproj's DBSyncInBuild flag should be set to True.")] bool enable,
        CancellationToken ct = default)
    {
        var gate = RequireConfig();
        if (gate != null) return gate;

        DbSyncSetResult r;
        try { r = await _project.SetDbSyncInBuildAsync(enable, ct).ConfigureAwait(false); }
        catch (ProjectConfigException pcx)
        {
            return JsonSerializer.Serialize(new { error = "rnrproj_malformed", message = pcx.Message });
        }

        return JsonSerializer.Serialize(new
        {
            rnprojPath = r.RnprojPath,
            previous = r.Previous,
            current = r.Current,
            sideEffectWarnings = r.Warnings,
        });
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Refuse the operation if the target object lives in a model different
    /// from the active project's model. Returns null when the target is in
    /// the active model (or wasn't found — best-effort check, the bridge
    /// will produce a more specific error downstream).
    /// </summary>
    private async Task<string?> CheckTargetIsInActiveModelAsync(string axType, string name, ResolvedConfig resolved, CancellationToken ct)
    {
        try
        {
            using var call = _conn.Client.FindObject(new FindObjectRequest { Name = name, AxType = axType });
            while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                var m = call.ResponseStream.Current;
                if (string.Equals(m.Ref.Model, resolved.Model, StringComparison.OrdinalIgnoreCase))
                    return null; // proceed
                return JsonSerializer.Serialize(new
                {
                    error = "out_of_model_mutation",
                    message = $"{axType} '{name}' lives in '{m.Ref.Model}', not the active project's model '{resolved.Model}'. dynamics-xpp only deletes/renames objects in the active project's model — use TFVC tools directly or open the foreign model's project in VS for changes there.",
                    target = new { axType = m.Ref.AxType, name = m.Ref.Name, in_model = m.Ref.Model, current_model = resolved.Model },
                });
            }
        }
        catch
        {
            // FindObject failed; best-effort fall-through — caller may
            // still proceed if the object exists on disk under the active
            // model's path. The file-existence check downstream will
            // catch a typo.
        }
        return null;
    }

    /// <summary>
    /// Count inbound references via FindReferences. Returns the count
    /// plus a sample list (first 5) of referencing objects for the agent
    /// to inspect when refusing the delete.
    /// </summary>
    private async Task<(int count, object[] samples)> CountInboundRefsAsync(string axType, string name, CancellationToken ct)
    {
        var samples = new List<object>();
        var count = 0;
        try
        {
            using var call = _conn.Client.FindReferences(new ReferenceQuery
            {
                TargetName = name,
                TargetType = axType,
                IncludeSourceMentions = false,
                Limit = 50,
            });
            while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                count++;
                if (samples.Count < 5)
                {
                    var hit = call.ResponseStream.Current;
                    samples.Add(new
                    {
                        sourceAxType = hit.Source.AxType,
                        sourceName = hit.Source.Name,
                        kind = hit.Kind,
                    });
                }
            }
        }
        catch (RpcException) { /* best-effort */ }
        return (count, samples.ToArray());
    }

    private static (string text, int count) ReplaceCounted(Regex regex, string input, string replacement)
    {
        int n = 0;
        var result = regex.Replace(input, m => { n++; return m.Result(replacement); });
        return (result, n);
    }

    /// <summary>
    /// Shared gate for the mutation/list tools. Returns a serialized
    /// "not configured" payload when no project is active, or null when the
    /// caller may proceed.
    /// </summary>
    private string? RequireConfig()
    {
        ResolvedConfig? resolved;
        try { resolved = _project.Resolve(); }
        catch (ProjectConfigException pcx)
        {
            return JsonSerializer.Serialize(new
            {
                configured = false,
                error = "project_config_invalid",
                message = pcx.Message,
                hint = "Load the dynamics-xpp:xpp-project skill for the .dynamics-xpp/config.json shape."
            });
        }
        if (resolved == null)
        {
            return JsonSerializer.Serialize(new
            {
                configured = false,
                cwd = Environment.CurrentDirectory,
                message = "No .dynamics-xpp/config.json in the current directory. Load the dynamics-xpp:xpp-project skill and walk the user through first-time setup.",
                skill = "dynamics-xpp:xpp-project"
            });
        }
        return null;
    }
}
