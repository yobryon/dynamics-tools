using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Scm;

/// <summary>
/// Thin wrapper around tf.exe (Visual Studio's TFVC CLI). All methods are
/// "try-then-surface" — they return a structured result rather than throwing,
/// so callers (domain-mutation pipelines, scm tools) can either fold the
/// outcome into a tool response or surface it as a warning without
/// blocking the underlying write.
///
/// tf.exe rides on the current user's VS credential cache for auth. There
/// is no credential management here. When auth fails, the operation
/// returns Failed with the raw tf.exe stderr; the agent decides what to
/// do (usually: tell the user to open VS and sign in).
/// </summary>
public sealed class TfvcClient
{
    private readonly string _tfExePath;
    private readonly string _workspaceRoot;
    private readonly ILogger<TfvcClient>? _logger;

    public TfvcClient(string tfExePath, string workspaceRoot, ILogger<TfvcClient>? logger = null)
    {
        _tfExePath = tfExePath;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
    }

    /// <summary>
    /// Build a TfvcClient from a ResolvedScm block. Falls back to the
    /// discovered tf.exe path when scm.TfExePath is null.
    /// </summary>
    public static TfvcClient? FromConfig(ResolvedScm scm, ILogger<TfvcClient>? logger = null)
    {
        var tfExe = scm.TfExePath ?? DiscoverTfExe();
        if (tfExe == null || !File.Exists(tfExe))
        {
            logger?.LogWarning("tf.exe not found (config={Configured}, discovered=auto-discovery failed)", scm.TfExePath);
            return null;
        }
        return new TfvcClient(tfExe, scm.MetadataPath, logger);
    }

    /// <summary>
    /// Search for tf.exe under the latest VS2022 install. Returns null when
    /// not found — caller falls back to "no SCM behavior" silently.
    /// </summary>
    public static string? DiscoverTfExe()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\tf.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\tf.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\tf.exe",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\tf.exe",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\tf.exe",
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    public string TfExePath => _tfExePath;
    public string WorkspaceRoot => _workspaceRoot;

    /// <summary>
    /// Run <c>tf status</c> against the workspace and parse the pending
    /// changes into structured rows. Empty result means "no pending
    /// changes" — that's a valid state, distinct from failure.
    /// </summary>
    public async Task<TfvcStatusResult> StatusAsync(CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await RunAsync(new[] { "status" }, _workspaceRoot, ct).ConfigureAwait(false);
        if (exit != 0)
        {
            return new TfvcStatusResult(
                Success: false,
                Changes: Array.Empty<TfvcChange>(),
                Error: NormalizeError(stderr));
        }
        var changes = ParseStatus(stdout);
        return new TfvcStatusResult(Success: true, Changes: changes, Error: null);
    }

    /// <summary>
    /// Check out a single file for edit. Idempotent — already-checked-out
    /// is silently treated as success. The file must already exist on
    /// disk; for new-file authoring, use AddAsync after the file is
    /// written.
    /// </summary>
    public async Task<TfvcOpResult> CheckoutAsync(string localPath, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
        {
            // Not-yet-existing file is fine; the bridge is about to
            // create it. We'll Add it after the write.
            return new TfvcOpResult(Success: true, Kind: "skipped_missing", Detail: null);
        }
        var (exit, _, stderr) = await RunAsync(new[] { "checkout", localPath }, _workspaceRoot, ct).ConfigureAwait(false);
        if (exit == 0) return new TfvcOpResult(Success: true, Kind: "checked_out", Detail: null);
        // tf.exe returns nonzero with "file is already checked out" / "no
        // working folder mapping" / "auth failed" etc. Treat already-
        // checked-out / already-pending as SUCCESS (the file is editable and the
        // metadata write will proceed — that's the whole point of the checkout);
        // everything else is a real, structured warning.
        var lower = stderr.ToLowerInvariant();
        if (lower.Contains("is already checked out") || lower.Contains("there are no changes to undo")
            // Already pending-add / incompatible pending change (TF14050) or a
            // conflicting pending change (TF203069): benign here — the file is
            // already tracked-and-pending, the write goes through. Suppressing
            // this keeps sideEffectWarnings high-signal (SCM "just works").
            || lower.Contains("already has a pending change") || lower.Contains("tf14050")
            || lower.Contains("tf203069") || lower.Contains("conflicts with one or more other pending changes"))
        {
            return new TfvcOpResult(Success: true, Kind: "already_pending", Detail: null);
        }
        return new TfvcOpResult(Success: false, Kind: ClassifyError(lower), Detail: NormalizeError(stderr));
    }

    /// <summary>
    /// Add a new file to source control. Idempotent — already-tracked is
    /// treated as success. The file must exist on disk.
    /// </summary>
    public async Task<TfvcOpResult> AddAsync(string localPath, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
        {
            return new TfvcOpResult(Success: false, Kind: "missing_file", Detail: $"file not on disk: {localPath}");
        }
        var (exit, _, stderr) = await RunAsync(new[] { "add", "/noignore", localPath }, _workspaceRoot, ct).ConfigureAwait(false);
        if (exit == 0) return new TfvcOpResult(Success: true, Kind: "added", Detail: null);
        var lower = stderr.ToLowerInvariant();
        if (lower.Contains("is already pending") || lower.Contains("already exists in the workspace"))
        {
            return new TfvcOpResult(Success: true, Kind: "already_tracked", Detail: null);
        }
        return new TfvcOpResult(Success: false, Kind: ClassifyError(lower), Detail: NormalizeError(stderr));
    }

    /// <summary>
    /// Bulk-add every untracked file under a folder in one tf.exe call.
    /// Lets TF handle the "is this already tracked?" decision itself —
    /// far faster than walking the directory and calling AddAsync per
    /// file (which spawns tf.exe per file at 1-2s each). Returns the
    /// raw operation result; the caller can re-query StatusAsync to
    /// discover what's now pending-add.
    /// </summary>
    public async Task<TfvcOpResult> AddRecursiveAsync(string folder, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
        {
            return new TfvcOpResult(Success: false, Kind: "missing_folder", Detail: $"folder not on disk: {folder}");
        }
        var (exit, _, stderr) = await RunAsync(new[] { "add", "/noignore", "/recursive", folder }, _workspaceRoot, ct).ConfigureAwait(false);
        if (exit == 0) return new TfvcOpResult(Success: true, Kind: "added", Detail: null);
        var lower = stderr.ToLowerInvariant();
        if (lower.Contains("no file matches") || lower.Contains("nothing to add"))
        {
            return new TfvcOpResult(Success: true, Kind: "nothing_to_add", Detail: null);
        }
        return new TfvcOpResult(Success: false, Kind: ClassifyError(lower), Detail: NormalizeError(stderr));
    }

    /// <summary>
    /// Delete a file under source control. <c>tf delete</c> marks it for
    /// deletion in the pending change set AND removes the local file —
    /// the caller doesn't need to also <c>File.Delete</c>. Idempotent on
    /// already-pending-delete; treats missing-file as success (nothing
    /// to do).
    /// </summary>
    public async Task<TfvcOpResult> DeleteAsync(string localPath, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
            return new TfvcOpResult(Success: true, Kind: "missing_file", Detail: null);

        // If this file is an ADD that was never checked in (created this
        // session), `tf delete` fails with TF203069 — you can't stage a delete
        // on top of an add, and it leaves residue the human must clean by hand.
        // Two flavors, both handled here:
        //   - a real PENDING add → `tf undo` discards it (and its file);
        //   - a local-workspace DETECTED add (TFVC notices the new file but it
        //     isn't a formal pending change) → `tf undo` no-ops, so we delete
        //     the file ourselves, which clears the detected add.
        // So: undo (best-effort) THEN ensure the file is gone. Zero residue in
        // both cases. Common shape when an agent course-corrects: create an
        // object, then delete it before any check-in.
        if (await IsPendingAddAsync(localPath, ct).ConfigureAwait(false))
        {
            await UndoAsync(localPath, ct).ConfigureAwait(false);
            try { if (File.Exists(localPath)) File.Delete(localPath); }
            catch (Exception ex)
            {
                return new TfvcOpResult(Success: false, Kind: "file_delete_failed", Detail: ex.Message);
            }
            return new TfvcOpResult(Success: true, Kind: "undone_pending_add", Detail: null);
        }

        var (exit, _, stderr) = await RunAsync(new[] { "delete", localPath }, _workspaceRoot, ct).ConfigureAwait(false);
        if (exit == 0) return new TfvcOpResult(Success: true, Kind: "deleted", Detail: null);
        var lower = stderr.ToLowerInvariant();
        if (lower.Contains("is already pending") && lower.Contains("delete"))
            return new TfvcOpResult(Success: true, Kind: "already_pending_delete", Detail: null);
        return new TfvcOpResult(Success: false, Kind: ClassifyError(lower), Detail: NormalizeError(stderr));
    }

    /// <summary>
    /// True when <paramref name="localPath"/> has a pending ADD in the
    /// workspace. Scoped <c>tf status &lt;path&gt;</c> so we only inspect the
    /// one file. On any error we return false — caller falls back to delete.
    /// </summary>
    private async Task<bool> IsPendingAddAsync(string localPath, CancellationToken ct)
    {
        // Use /format:detailed, NOT the brief status. Brief output is
        // fixed-width columns; a filename that fills the "File name" column
        // leaves only a single space before the change, which defeats the
        // whitespace split in ParseStatus and silently drops the row. The
        // detailed format prints one "Change : <type>" line per item, robust
        // to column widths. Scoped to the single path, so any add-change here
        // is this file.
        var (exit, stdout, _) = await RunAsync(new[] { "status", localPath, "/format:detailed" }, _workspaceRoot, ct).ConfigureAwait(false);
        if (exit != 0) return false;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith("Change", StringComparison.OrdinalIgnoreCase)) continue;
            var idx = line.IndexOf(':');
            // "Change     : add"  /  "Change     : add, edit"
            if (idx >= 0 && line[(idx + 1)..].IndexOf("add", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Undo pending changes on a single file. For a pending add this removes
    /// the pending change AND deletes the working-copy file. Treats
    /// "no pending changes" as success (idempotent).
    /// </summary>
    public async Task<TfvcOpResult> UndoAsync(string localPath, CancellationToken ct = default)
    {
        var (exit, _, stderr) = await RunAsync(new[] { "undo", "/noprompt", localPath }, _workspaceRoot, ct).ConfigureAwait(false);
        if (exit == 0) return new TfvcOpResult(Success: true, Kind: "undone", Detail: null);
        var lower = stderr.ToLowerInvariant();
        if (lower.Contains("no pending changes") || lower.Contains("there are no changes to undo"))
            return new TfvcOpResult(Success: true, Kind: "nothing_to_undo", Detail: null);
        return new TfvcOpResult(Success: false, Kind: ClassifyError(lower), Detail: NormalizeError(stderr));
    }


    /// <summary>
    /// Rename a file under source control. <c>tf rename</c> requires the
    /// SOURCE file to exist and the destination to be in the same
    /// workspace. The operation moves the file on disk AND records the
    /// rename in the pending change set, so the caller doesn't need to
    /// <c>File.Move</c> separately.
    /// </summary>
    public async Task<TfvcOpResult> RenameAsync(string oldLocalPath, string newLocalPath, CancellationToken ct = default)
    {
        if (!File.Exists(oldLocalPath))
            return new TfvcOpResult(Success: false, Kind: "missing_file", Detail: $"source not on disk: {oldLocalPath}");
        var (exit, _, stderr) = await RunAsync(new[] { "rename", oldLocalPath, newLocalPath }, _workspaceRoot, ct).ConfigureAwait(false);
        if (exit == 0) return new TfvcOpResult(Success: true, Kind: "renamed", Detail: null);
        return new TfvcOpResult(Success: false, Kind: ClassifyError(stderr.ToLowerInvariant()), Detail: NormalizeError(stderr));
    }

    // -- internals ----------------------------------------------------------

    private static List<TfvcChange> ParseStatus(string stdout)
    {
        // tf status output groups rows by server folder. Each row looks
        // like:
        //   FileName.xml      edit   J:\local\path\FileName.xml
        // Header / blank lines / server-path headers are skipped.
        var changes = new List<TfvcChange>();
        string? currentServerFolder = null;
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("File name") || line.StartsWith("------")) continue;
            if (line.StartsWith("$/"))
            {
                currentServerFolder = line.Trim();
                continue;
            }
            // Row format is column-fixed (file column 0-49, change column 49-55,
            // local-path column 56-end). Use a relaxed parse so width drift
            // doesn't kill us — split on 2+ spaces.
            var parts = System.Text.RegularExpressions.Regex.Split(line, @"\s{2,}");
            if (parts.Length < 3) continue;
            var file = parts[0].Trim();
            var action = parts[1].Trim();
            var localPath = parts[2].Trim();
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(action) || string.IsNullOrEmpty(localPath)) continue;
            changes.Add(new TfvcChange(
                Action: action,
                File: file,
                LocalPath: localPath,
                ServerFolder: currentServerFolder));
        }
        return changes;
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string[] args, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _tfExePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _logger?.LogDebug("Invoking tf {Args} (cwd={Cwd})", string.Join(" ", args), workingDirectory);

        using var proc = new Process { StartInfo = psi };
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }
        return (proc.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
    }

    /// <summary>
    /// Map tf.exe stderr to a stable error-kind string we surface as the
    /// 'kind' field on the structured result. Lets the agent reason about
    /// the failure class without parsing prose.
    /// </summary>
    private static string ClassifyError(string lowerStderr)
    {
        if (lowerStderr.Contains("tf30063") || lowerStderr.Contains("not authorized"))
            return "auth_failed";
        if (lowerStderr.Contains("no working folder mapping"))
            return "workspace_not_mapped";
        if (lowerStderr.Contains("cannot be locked") || lowerStderr.Contains("is locked by"))
            return "locked";
        if (lowerStderr.Contains("not found in workspace") || lowerStderr.Contains("could not be opened"))
            return "not_found";
        return "tf_error";
    }

    private static string NormalizeError(string stderr) =>
        string.Join(" | ",
            stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                  .Select(l => l.TrimEnd('\r').Trim())
                  .Where(l => l.Length > 0));
}

public sealed record TfvcChange(
    string Action,
    string File,
    string LocalPath,
    string? ServerFolder);

public sealed record TfvcStatusResult(
    bool Success,
    IReadOnlyList<TfvcChange> Changes,
    string? Error);

public sealed record TfvcOpResult(
    bool Success,
    string Kind,
    string? Detail);
