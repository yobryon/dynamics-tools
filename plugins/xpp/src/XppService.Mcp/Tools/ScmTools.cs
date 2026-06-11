using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Xpp.Service.Mcp.Project;
using Xpp.Service.Mcp.Scm;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// SCM-side tooling. Today only TFVC (Azure DevOps-backed) is supported,
/// because that's what F&amp;O developer workspaces use almost universally.
/// </summary>
[McpServerToolType]
public sealed class ScmTools
{
    private readonly ProjectContext _project;
    private readonly ILogger<ScmTools> _logger;

    public ScmTools(ProjectContext project, ILogger<ScmTools> logger)
    {
        _project = project;
        _logger = logger;
    }

    [McpServerTool(Name = "xpp_scm_status"), Description(
        "List pending changes in the configured TFVC workspace (tf status). " +
        "Returns add / edit / delete entries with both server path and local " +
        "path so the agent can correlate them against the active model and " +
        "the agent-maintained changeset.json. Side-effect-free.")]
    public async Task<string> Status(CancellationToken ct = default)
    {
        var (scm, client, gate) = ResolveTfvcOrGate();
        if (gate != null) return gate;

        var result = await client!.StatusAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            scm = "tfvc",
            workspaceRoot = scm!.MetadataPath,
            success = result.Success,
            error = result.Error,
            count = result.Changes.Count,
            changes = result.Changes,
        });
    }

    [McpServerTool(Name = "xpp_scm_audit"), Description(
        "Diff the agent-maintained .dynamics-xpp/changeset.json against " +
        "TFVC pending changes (tf status). Surfaces drift: VS-side / " +
        "pre-existing edits the agent doesn't know about, plus changeset " +
        "entries with no corresponding pending change (agent-tracked but " +
        "not pending in SCM — usually a sign of an SCM-unconfigured " +
        "session that wrote files without tf-adding them). " +
        "Pass autoFix=true to recover: first `tf add` per-file for every " +
        "'agent-tracked but not pending' entry (with per-file reporting), " +
        "then a single `tf add /noignore /recursive` against the active " +
        "module root to catch any other on-disk orphans TF doesn't know " +
        "about yet — TF itself decides what's already tracked vs. truly " +
        "untracked, far faster than per-file Process spawns. Newly-pending " +
        "files are folded into the agent changeset.")]
    public async Task<string> Audit(
        [Description("When true, run `tf add` on agent-tracked-but-untracked files and any on-disk metadata under the active module that isn't yet pending. Default false (audit-only).")] bool autoFix = false,
        CancellationToken ct = default)
    {
        var (scm, client, gate) = ResolveTfvcOrGate();
        if (gate != null) return gate;

        var status = await client!.StatusAsync(ct).ConfigureAwait(false);
        if (!status.Success)
        {
            return JsonSerializer.Serialize(new
            {
                scm = "tfvc",
                success = false,
                error = status.Error,
            });
        }

        var resolved = _project.Resolve()!;
        var changeset = _project.ReadChangeset();

        // Build a set of (axType, name) from the changeset for fast lookup,
        // and a parallel set of expected file paths to test against the TFVC
        // changes' local paths.
        var changesetEntries = changeset.Objects ?? new List<ChangesetEntry>();
        var changesetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in changesetEntries)
        {
            var p = _project.ResolveMetadataFilePath(e.AxType, e.Name);
            if (p != null) changesetPaths.Add(p);
        }

        var tfvcPaths = new HashSet<string>(
            status.Changes.Select(c => c.LocalPath),
            StringComparer.OrdinalIgnoreCase);

        var inTfvcNotInChangeset = status.Changes
            .Where(c => !changesetPaths.Contains(c.LocalPath))
            .ToList();
        var inChangesetNotInTfvc = changesetEntries
            .Where(e =>
            {
                var p = _project.ResolveMetadataFilePath(e.AxType, e.Name);
                return p != null && !tfvcPaths.Contains(p);
            })
            .ToList();

        // Orphan-on-disk detection used to walk every XML under the model
        // root and treat anything not in tfvcPaths/changesetPaths as orphan.
        // That was catastrophically wrong: tfvcPaths is only PENDING
        // changes, so every already-checked-in file looked "orphan" and
        // got a per-file `tf add` — thousands of process spawns at ~1-2s
        // each on a model like ContosoRetail. Removed.
        //
        // The audit-only mode now reports `agentTrackedButNotPending`
        // only. Orphan detection (files on disk that TFVC never heard of)
        // is performed in autoFix mode via a single recursive `tf add`,
        // and rediscovered post-hoc by diffing tf status snapshots.
        var moduleRoot = Path.Combine(scm!.MetadataPath, resolved.Model, resolved.Module);

        var fixAttempts = new List<object>();
        var fixedCount = 0;
        var fixedAddedToChangeset = 0;
        var orphanFiles = new List<string>();
        if (autoFix)
        {
            // 1. Add agent-tracked-but-not-pending paths individually so we
            //    get per-file success/failure reporting (small set).
            foreach (var e in inChangesetNotInTfvc)
            {
                var path = _project.ResolveMetadataFilePath(e.AxType, e.Name);
                if (path == null || !File.Exists(path)) continue;
                var op = await client.AddAsync(path, ct).ConfigureAwait(false);
                fixAttempts.Add(new { path, success = op.Success, kind = op.Kind, detail = op.Detail });
                if (op.Success) fixedCount++;
            }

            // 2. One-shot recursive add for any other on-disk orphans. TF
            //    itself decides what's already tracked vs. truly untracked;
            //    far faster than per-file Process spawns.
            if (Directory.Exists(moduleRoot))
            {
                var batchOp = await client.AddRecursiveAsync(moduleRoot, ct).ConfigureAwait(false);
                fixAttempts.Add(new { path = moduleRoot, success = batchOp.Success, kind = "batch_" + batchOp.Kind, detail = batchOp.Detail });

                // 3. Re-query status to discover what's now newly pending.
                //    Anything pending now that wasn't pending in the pre-snapshot
                //    is an orphan we just added — fold into the changeset.
                var postStatus = await client.StatusAsync(ct).ConfigureAwait(false);
                var preTfvcPathsSet = tfvcPaths;
                foreach (var c in postStatus.Changes)
                {
                    if (preTfvcPathsSet.Contains(c.LocalPath)) continue;
                    if (!c.LocalPath.StartsWith(moduleRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    orphanFiles.Add(c.LocalPath);
                    var parsed = TryParseAxTypeAndName(c.LocalPath, moduleRoot);
                    if (parsed.HasValue)
                    {
                        try
                        {
                            await _project.UpsertChangesetAsync(parsed.Value.axType, parsed.Value.name, createdHere: true, ct)
                                .ConfigureAwait(false);
                            fixedAddedToChangeset++;
                        }
                        catch { /* best-effort */ }
                    }
                }
                if (batchOp.Success) fixedCount += orphanFiles.Count;
            }
        }

        return JsonSerializer.Serialize(new
        {
            scm = "tfvc",
            success = true,
            workspaceRoot = scm!.MetadataPath,
            changesetCount = changesetEntries.Count,
            tfvcChangeCount = status.Changes.Count,
            orphanOnDiskCount = orphanFiles.Count,
            unknownToAgent = inTfvcNotInChangeset.Select(c => new
            {
                action = c.Action,
                file = c.File,
                localPath = c.LocalPath,
                hint = "VS-side edit or pre-existing pending change; consider folding into changeset via xpp_project_add_object.",
            }),
            agentTrackedButNotPending = inChangesetNotInTfvc.Select(e => new
            {
                axType = e.AxType,
                name = e.Name,
                createdHere = e.CreatedHere,
                lastTouchedAt = e.LastTouchedAt,
                hint = "Agent thinks it wrote this object but TFVC has no pending change — possibly a failed write or a session that ran without scm configured. Pass autoFix=true to tf-add.",
            }),
            orphanFiles = orphanFiles.Select(f => new
            {
                localPath = f,
                hint = "On disk under the active module but TFVC had no record of it before autoFix ran. Has been tf-added and folded into the changeset.",
            }),
            autoFixApplied = autoFix,
            autoFix = autoFix ? new
            {
                attempted = fixAttempts.Count,
                succeeded = fixedCount,
                addedToChangeset = fixedAddedToChangeset,
                results = fixAttempts,
            } : null,
        });
    }

    /// <summary>
    /// Parse an axType + object name from a path that matches the canonical
    /// <c>&lt;moduleRoot&gt;/&lt;AxType&gt;/&lt;Name&gt;.xml</c> layout. Returns null
    /// when the path doesn't fit that pattern (resource files, label
    /// resources, etc. — the audit folds them as orphans without trying
    /// to add a changeset entry).
    /// </summary>
    private static (string axType, string name)? TryParseAxTypeAndName(string filePath, string moduleRoot)
    {
        if (!filePath.StartsWith(moduleRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = filePath.Substring(moduleRoot.Length).TrimStart('\\', '/');
        var parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null; // expect AxType/Name.xml exactly
        var axType = parts[0];
        var name = Path.GetFileNameWithoutExtension(parts[1]);
        if (!axType.StartsWith("Ax", StringComparison.Ordinal)) return null;
        return (axType, name);
    }

    [McpServerTool(Name = "xpp_scm_checkout"), Description(
        "Check out one or more files for edit in TFVC. Use when about to " +
        "edit through a tool that doesn't auto-checkout (rare — typed " +
        "xpp_create_* / xpp_patch_* do this automatically when scm is " +
        "configured). Idempotent. Paths can be absolute or, when prefixed " +
        "with the model name, are resolved against the metadata path.")]
    public async Task<string> Checkout(
        [Description("List of file paths to check out. Absolute paths preferred; relative paths resolve against the configured metadata workspace root.")]
        string[] paths,
        CancellationToken ct = default)
    {
        var (scm, client, gate) = ResolveTfvcOrGate();
        if (gate != null) return gate;

        var results = new List<object>();
        foreach (var raw in paths)
        {
            var path = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(scm!.MetadataPath, raw));
            var op = await client!.CheckoutAsync(path, ct).ConfigureAwait(false);
            results.Add(new
            {
                path,
                success = op.Success,
                kind = op.Kind,
                detail = op.Detail,
            });
        }
        return JsonSerializer.Serialize(new { scm = "tfvc", count = results.Count, results });
    }

    // ---- helpers --------------------------------------------------------

    private (ResolvedScm? scm, TfvcClient? client, string? gate) ResolveTfvcOrGate()
    {
        ResolvedConfig? resolved;
        try { resolved = _project.Resolve(); }
        catch (ProjectConfigException pcx)
        {
            return (null, null, JsonSerializer.Serialize(new
            {
                configured = false,
                error = "project_config_invalid",
                message = pcx.Message,
            }));
        }
        if (resolved == null)
        {
            return (null, null, JsonSerializer.Serialize(new
            {
                configured = false,
                message = "SCM tools require a .dynamics-xpp/config.json in the launch directory.",
                skill = "dynamics-xpp:xpp-project",
            }));
        }
        if (resolved.Scm == null)
        {
            return (null, null, JsonSerializer.Serialize(new
            {
                configured = false,
                error = "scm_not_configured",
                message = "No 'scm' block in .dynamics-xpp/config.json. Add { scm: { kind: 'tfvc', metadataPath: '...' } } to enable.",
                skill = "dynamics-xpp:xpp-scm-tfvc",
            }));
        }
        var client = TfvcClient.FromConfig(resolved.Scm, _logger as ILogger<TfvcClient>);
        if (client == null)
        {
            return (resolved.Scm, null, JsonSerializer.Serialize(new
            {
                configured = true,
                error = "tf_exe_not_found",
                message = "tf.exe could not be located. Install Visual Studio 2022 (any edition) or set scm.tfExePath in config.",
                skill = "dynamics-xpp:xpp-scm-tfvc",
            }));
        }
        return (resolved.Scm, client, null);
    }
}
