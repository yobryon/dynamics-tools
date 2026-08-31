using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Xpp.Service.Mcp.Project;

/// <summary>
/// Project-context service. Resolves the .dynamics-xpp/config.json convention
/// (object prefix, extension suffix, active .rnrproj pointer) from the
/// directory the MCP launched in. Owns:
///
///   - config.json read (no upward walk; CWD-strict per the dynamics-xpp:xpp-project skill).
///   - .rnrproj parse to extract <Model> and the existing &lt;Content&gt; entries.
///   - .rnrproj mutation to idempotently add a new (axType, name) entry.
///   - changeset.json read / upsert / clear.
///
/// Lifecycle: registered as a singleton. The config is loaded lazily on first
/// access and cached for the MCP process lifetime — restart to pick up
/// external config edits. Writes (rnrproj mutate, changeset upsert) are
/// serialized through a single semaphore since MCP tool calls can run
/// concurrently.
/// </summary>
public sealed class ProjectContext
{
    private const string ConfigDirName = ".dynamics-xpp";
    private const string ConfigFileName = "config.json";
    private const string ChangesetFileName = "changeset.json";
    private const string MsbuildNs = "http://schemas.microsoft.com/developer/msbuild/2003";

    private readonly string _cwd;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ResolvedConfig? _cached;
    private DateTime _cachedConfigMtimeUtc;
    // One-shot: full scm_not_configured nudge on the first unconfigured write,
    // terse marker thereafter (avoids per-write warning habituation).
    private bool _scmNotConfiguredWarned;

    public ProjectContext()
    {
        _cwd = Environment.CurrentDirectory;
    }

    /// <summary>
    /// Resolve the project state, or null if no .dynamics-xpp/config.json
    /// exists in the launch directory. Errors during resolution (malformed
    /// JSON, missing rnrproj, missing &lt;Model&gt;) are thrown as
    /// <see cref="ProjectConfigException"/> so callers can surface them with
    /// useful context.
    /// </summary>
    public ResolvedConfig? Resolve()
    {
        var configPath = Path.Combine(_cwd, ConfigDirName, ConfigFileName);
        if (!File.Exists(configPath))
        {
            _cached = null;
            return null;
        }

        // Reuse cached resolution only when the on-disk config hasn't changed
        // since we last read it. Lets users edit .dynamics-xpp/config.json
        // (e.g. tune bestPractices.suppress) without restarting the MCP.
        var mtime = File.GetLastWriteTimeUtc(configPath);
        if (_cached != null && mtime == _cachedConfigMtimeUtc) return _cached;

        ConfigFile cfg;
        try
        {
            var raw = File.ReadAllText(configPath);
            cfg = JsonSerializer.Deserialize<ConfigFile>(raw, ConfigJson)
                ?? throw new ProjectConfigException($"{configPath} parsed as null.");
        }
        catch (JsonException jx)
        {
            throw new ProjectConfigException($"{configPath} is not valid JSON: {jx.Message}", jx);
        }

        if (string.IsNullOrWhiteSpace(cfg.RnprojPath))
            throw new ProjectConfigException($"{configPath} is missing the 'rnprojPath' field.");

        var rnproj = Path.IsPathRooted(cfg.RnprojPath)
            ? cfg.RnprojPath
            : Path.GetFullPath(Path.Combine(_cwd, cfg.RnprojPath));

        if (!File.Exists(rnproj))
            throw new ProjectConfigException($"rnprojPath does not resolve to an existing file: {rnproj}");

        XDocument doc;
        try { doc = XDocument.Load(rnproj, LoadOptions.PreserveWhitespace); }
        catch (Exception ex) { throw new ProjectConfigException($"failed to parse {rnproj}: {ex.Message}", ex); }

        var ns = (XNamespace)MsbuildNs;
        var model = doc.Root?.Elements(ns + "PropertyGroup")
            .Elements(ns + "Model")
            .FirstOrDefault()?.Value?.Trim();
        if (string.IsNullOrEmpty(model))
            throw new ProjectConfigException($"{rnproj} does not declare a <Model> in any <PropertyGroup>.");

        // Locate the .sln devenv.com needs. xpp_compile takes /Project,
        // but devenv ALSO requires the containing solution file. We
        // REQUIRE the user to declare slnPath explicitly in config —
        // the previous ancestor-walk discovery picked the wrong .sln
        // on real-world repo layouts (sibling rnrprojs sharing a
        // parent dir with an unrelated .sln) and silently produced
        // build failures against a different project. Hard intake
        // gate eliminates that whole failure class.
        if (string.IsNullOrWhiteSpace(cfg.SlnPath))
            throw new ProjectConfigException(
                $"{configPath} is missing the 'slnPath' field. " +
                $"Set it to the .sln that lists '{Path.GetFileName(rnproj)}' " +
                $"under its Project(...) lines — that's the solution " +
                $"xpp_compile will hand to devenv.com. Load the " +
                $"dynamics-xpp:xpp-project skill if you need help picking it.");

        var slnPath = Path.IsPathRooted(cfg.SlnPath)
            ? cfg.SlnPath!
            : Path.GetFullPath(Path.Combine(_cwd, cfg.SlnPath!));

        if (!File.Exists(slnPath))
            throw new ProjectConfigException($"slnPath does not resolve to an existing file: {slnPath}");

        // Verify the configured .sln actually references the active
        // .rnrproj. If it doesn't, xpp_compile would just build an
        // unrelated project — the worst failure mode is silent because
        // devenv's error then has nothing to do with the agent's work.
        // Compare by .rnrproj file name (case-insensitive); .sln Project
        // lines store paths as the project file basename plus relative
        // path, so matching the basename is robust to absolute-vs-
        // relative-path differences.
        var rnprojFileName = Path.GetFileName(rnproj);
        string slnText;
        try { slnText = File.ReadAllText(slnPath); }
        catch (Exception ex) { throw new ProjectConfigException($"failed to read {slnPath}: {ex.Message}", ex); }
        if (slnText.IndexOf(rnprojFileName, StringComparison.OrdinalIgnoreCase) < 0)
            throw new ProjectConfigException(
                $"slnPath '{slnPath}' does not reference '{rnprojFileName}'. " +
                $"xpp_compile would build the wrong project. Either point " +
                $"slnPath at a .sln that lists this rnrproj, or create a " +
                $"project-local .sln referencing only this rnrproj and " +
                $"update the config. See dynamics-xpp:xpp-project.");

        ResolvedScm? scm = null;
        if (cfg.Scm is { Kind: { Length: > 0 } kind } scmBlock)
        {
            if (!string.Equals(kind, "tfvc", StringComparison.OrdinalIgnoreCase))
                throw new ProjectConfigException($"scm.kind '{kind}' is not supported. Only 'tfvc' is recognized today.");
            if (string.IsNullOrWhiteSpace(scmBlock.MetadataPath))
                throw new ProjectConfigException("scm.metadataPath is required when scm.kind is set.");
            scm = new ResolvedScm(
                Kind: "tfvc",
                MetadataPath: Path.GetFullPath(scmBlock.MetadataPath),
                TfExePath: scmBlock.TfExePath);
        }

        _cached = new ResolvedConfig(
            ConfigPath: configPath,
            RepoRoot: _cwd,
            RnprojPath: rnproj,
            SlnPath: slnPath,
            Model: model!,
            Module: string.IsNullOrWhiteSpace(cfg.ModuleName) ? model! : cfg.ModuleName!,
            ObjectPrefix: cfg.Naming?.ObjectPrefix ?? string.Empty,
            ExtensionSuffix: cfg.Naming?.ExtensionSuffix ?? model!,
            BpSuppress: (cfg.BestPractices?.Suppress ?? new List<string>()).AsReadOnly(),
            BpEscalate: (cfg.BestPractices?.Escalate ?? new List<string>()).AsReadOnly(),
            Scm: scm);
        _cachedConfigMtimeUtc = mtime;
        return _cached;
    }

    /// <summary>
    /// Read the existing changeset. Returns an empty payload if the file is
    /// missing; never throws on absence. Malformed JSON is surfaced.
    /// </summary>
    /// <summary>
    /// Resolve the on-disk metadata file path for a given (axType, name)
    /// pair against the configured SCM metadata root, using the standard
    /// F&amp;O convention:
    ///   <c>&lt;metadataPath&gt;/&lt;Model&gt;/&lt;Module&gt;/&lt;axType&gt;/&lt;name&gt;.xml</c>
    /// Returns null when SCM isn't configured (no metadata path known).
    /// The file may or may not exist on disk — that's caller's concern.
    /// </summary>
    public string? ResolveMetadataFilePath(string axType, string name)
    {
        var cfg = Resolve();
        if (cfg?.Scm == null) return null;
        return Path.Combine(cfg.Scm.MetadataPath, cfg.Model, cfg.Module, axType, $"{name}.xml");
    }

    /// <summary>
    /// Path of the RUNTIME (deployment) copy of an element, under the package's
    /// <c>XppMetadata</c> tree: <c>&lt;metadataPath&gt;/&lt;Module&gt;/XppMetadata/&lt;Model&gt;/&lt;axType&gt;/&lt;name&gt;.xml</c>.
    /// This is a generated artifact (not TFVC-tracked) that a create writes
    /// alongside the source copy; deletes must remove it too or the element
    /// keeps showing up on disk (and to the running AOS) until the next build.
    /// Null when SCM (and thus the metadata root) isn't configured.
    /// </summary>
    public string? ResolveRuntimeMetadataFilePath(string axType, string name)
    {
        var cfg = Resolve();
        if (cfg?.Scm == null) return null;
        return Path.Combine(cfg.Scm.MetadataPath, cfg.Module, "XppMetadata", cfg.Model, axType, $"{name}.xml");
    }

    /// <summary>
    /// SCM pre-write hook for the patch flow. Computes the file path
    /// for (axType, name) and runs tf checkout against it. No-op when
    /// SCM isn't configured. Returns a structured warning when checkout
    /// fails so the calling Patch tool can surface it; the underlying
    /// patch still proceeds (the bridge will see the read-only file and
    /// produce its own access-denied error if checkout truly failed).
    ///
    /// Idempotent — already-checked-out is treated as success.
    /// </summary>
    public async Task<string?> ScmCheckoutAsync(string axType, string name, CancellationToken ct = default)
    {
        var cfg = Resolve();
        if (cfg?.Scm == null) return null;
        var path = ResolveMetadataFilePath(axType, name);
        if (path == null) return null;
        var client = Scm.TfvcClient.FromConfig(cfg.Scm);
        if (client == null) return "tfvc_not_configured: tf.exe not located";
        var result = await client.CheckoutAsync(path, ct).ConfigureAwait(false);
        return result.Success ? null : $"tfvc_{result.Kind}: {result.Detail}";
    }

    /// <summary>
    /// SCM post-write hook for the create flow. Runs tf add for the
    /// written file. Idempotent. When SCM isn't configured, returns a
    /// loud warning string so the agent knows new files aren't being
    /// tracked — silent no-op was the previous behavior and bit a real
    /// agent badly (their sprint's worth of new files sat on disk
    /// untracked because their config didn't include the scm block).
    /// </summary>
    public async Task<string?> ScmAddAsync(string axType, string name, CancellationToken ct = default)
    {
        var cfg = Resolve();
        if (cfg?.Scm == null)
        {
            // Emit the full setup nudge ONCE per process, then go quiet. Repeating
            // the same multi-line warning on every write trains the agent to skim
            // sideEffectWarnings — the one channel a genuinely important warning
            // would later arrive on. First write: full guidance; after: a terse
            // breadcrumb so it's still visible but not noisy.
            if (_scmNotConfiguredWarned)
                return "scm_not_configured (see earlier warning; not repeated per-write)";
            _scmNotConfiguredWarned = true;
            return "scm_not_configured: no scm block in .dynamics-xpp/config.json — the new file is on disk but NOT pending-add in SCM. Add { scm: { kind: 'tfvc', metadataPath: '...' } } to track future writes; run xpp_scm_audit(autoFix=true) to recover what's already on disk. (This warning is shown once per session; subsequent writes will show a terse marker.)";
        }
        var path = ResolveMetadataFilePath(axType, name);
        if (path == null) return null;
        var client = Scm.TfvcClient.FromConfig(cfg.Scm);
        if (client == null) return "tfvc_not_configured: tf.exe not located";
        var result = await client.AddAsync(path, ct).ConfigureAwait(false);
        return result.Success ? null : $"tfvc_{result.Kind}: {result.Detail}";
    }

    /// <summary>
    /// SCM checkout for an arbitrary local file path (rather than the
    /// canonical <c>&lt;metadata&gt;/&lt;Model&gt;/&lt;Module&gt;/&lt;axType&gt;/&lt;Name&gt;.xml</c>
    /// layout that ScmCheckoutAsync resolves). Useful for the rnrproj and
    /// other project-adjacent files outside the metadata tree but still
    /// under the TFVC workspace mapping.
    /// </summary>
    public async Task<string?> ScmCheckoutPathAsync(string localPath, CancellationToken ct = default)
    {
        var cfg = Resolve();
        if (cfg?.Scm == null) return null;
        var client = Scm.TfvcClient.FromConfig(cfg.Scm);
        if (client == null) return "tfvc_not_configured: tf.exe not located";
        var result = await client.CheckoutAsync(localPath, ct).ConfigureAwait(false);
        return result.Success ? null : $"tfvc_{result.Kind}: {result.Detail}";
    }

    /// <summary>
    /// SCM hook for the delete flow. Runs <c>tf delete</c> which marks
    /// the file for deletion in the pending change set AND removes the
    /// local file. When SCM isn't configured, the caller must handle
    /// the local-file deletion themselves — this helper returns
    /// <c>(handledLocalDelete: false, ...)</c> in that case.
    /// </summary>
    public async Task<ScmDeleteResult> ScmDeleteAsync(string axType, string name, CancellationToken ct = default)
    {
        var cfg = Resolve();
        if (cfg?.Scm == null) return new ScmDeleteResult(HandledLocalDelete: false, Warning: null);
        var path = ResolveMetadataFilePath(axType, name);
        if (path == null) return new ScmDeleteResult(HandledLocalDelete: false, Warning: null);
        var client = Scm.TfvcClient.FromConfig(cfg.Scm);
        if (client == null) return new ScmDeleteResult(HandledLocalDelete: false, Warning: "tfvc_not_configured: tf.exe not located");
        var result = await client.DeleteAsync(path, ct).ConfigureAwait(false);
        return new ScmDeleteResult(
            HandledLocalDelete: result.Success && result.Kind != "missing_file",
            Warning: result.Success ? null : $"tfvc_{result.Kind}: {result.Detail}");
    }

    /// <summary>
    /// SCM hook for the rename flow. Runs <c>tf rename</c> which moves
    /// the file on disk AND records the rename in the pending change
    /// set. Returns <c>(handledLocalRename: false, ...)</c> when SCM
    /// isn't configured — the caller does the <c>File.Move</c>.
    /// </summary>
    public async Task<ScmRenameResult> ScmRenameAsync(string axType, string oldName, string newName, CancellationToken ct = default)
    {
        var cfg = Resolve();
        if (cfg?.Scm == null) return new ScmRenameResult(HandledLocalRename: false, Warning: null);
        var oldPath = ResolveMetadataFilePath(axType, oldName);
        var newPath = ResolveMetadataFilePath(axType, newName);
        if (oldPath == null || newPath == null) return new ScmRenameResult(HandledLocalRename: false, Warning: null);
        var client = Scm.TfvcClient.FromConfig(cfg.Scm);
        if (client == null) return new ScmRenameResult(HandledLocalRename: false, Warning: "tfvc_not_configured: tf.exe not located");
        var result = await client.RenameAsync(oldPath, newPath, ct).ConfigureAwait(false);
        return new ScmRenameResult(
            HandledLocalRename: result.Success,
            Warning: result.Success ? null : $"tfvc_{result.Kind}: {result.Detail}");
    }

    /// <summary>
    /// Remove a (axType, name) entry from the changeset. Returns true if
    /// an entry was actually removed; false if no matching entry was
    /// present. Safe to call when no changeset file exists.
    /// </summary>
    public async Task<bool> RemoveFromChangesetAsync(string axType, string name, CancellationToken ct = default)
    {
        var resolved = Resolve();
        if (resolved == null) return false;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(resolved.RepoRoot, ConfigDirName, ChangesetFileName);
            if (!File.Exists(path)) return false;
            var file = JsonSerializer.Deserialize<ChangesetFile>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), ConfigJson) ?? new ChangesetFile();
            var removed = file.Objects.RemoveAll(o =>
                string.Equals(o.AxType, axType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;
            var json = JsonSerializer.Serialize(file, ConfigJson);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Rename a (axType, oldName) entry in the changeset to newName.
    /// Returns true if an entry was actually renamed; false if no
    /// matching entry was present.
    /// </summary>
    public async Task<bool> RenameInChangesetAsync(string axType, string oldName, string newName, CancellationToken ct = default)
    {
        var resolved = Resolve();
        if (resolved == null) return false;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(resolved.RepoRoot, ConfigDirName, ChangesetFileName);
            if (!File.Exists(path)) return false;
            var file = JsonSerializer.Deserialize<ChangesetFile>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), ConfigJson) ?? new ChangesetFile();
            var match = file.Objects.FirstOrDefault(o =>
                string.Equals(o.AxType, axType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (match == null) return false;
            match.Name = newName;
            match.LastTouchedAt = DateTimeOffset.UtcNow.ToString("o");
            var json = JsonSerializer.Serialize(file, ConfigJson);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ChangesetFile ReadChangeset()
    {
        var resolved = Resolve();
        if (resolved == null) return new ChangesetFile();

        var path = Path.Combine(resolved.RepoRoot, ConfigDirName, ChangesetFileName);
        if (!File.Exists(path)) return new ChangesetFile();
        var raw = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ChangesetFile>(raw, ConfigJson) ?? new ChangesetFile();
    }

    /// <summary>
    /// Count the &lt;Content&gt; entries in the active rnrproj's main ItemGroup
    /// (the one that holds AOT objects — distinct from the Folder ItemGroup).
    /// </summary>
    public int CountProjectObjects()
    {
        var resolved = Resolve();
        if (resolved == null) return 0;
        try
        {
            var doc = XDocument.Load(resolved.RnprojPath, LoadOptions.PreserveWhitespace);
            var ns = (XNamespace)MsbuildNs;
            return doc.Root?.Elements(ns + "ItemGroup")
                .SelectMany(g => g.Elements(ns + "Content"))
                .Count() ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Idempotent add. Returns true if the entry was appended, false if it
    /// was already there. The new Content element is placed in the same
    /// ItemGroup as existing Content entries; if there is no such group yet,
    /// a new one is appended to the Project root.
    /// </summary>
    public async Task<bool> AddToRnprojAsync(string axType, string name, CancellationToken ct = default)
    {
        var resolved = Resolve() ?? throw new ProjectConfigException("Project is not configured.");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = XDocument.Load(resolved.RnprojPath, LoadOptions.PreserveWhitespace);
            var ns = (XNamespace)MsbuildNs;
            var include = $"{axType}\\{name}";

            // Already referenced? — quick scan, case-insensitive on Include
            // since FS is case-insensitive on Windows.
            var existing = doc.Root!.Elements(ns + "ItemGroup")
                .SelectMany(g => g.Elements(ns + "Content"))
                .FirstOrDefault(c => string.Equals(
                    (string?)c.Attribute("Include"), include,
                    StringComparison.OrdinalIgnoreCase));
            if (existing != null) return false;

            // Find the ItemGroup that already holds Content entries (so we
            // don't pollute the Folder ItemGroup). If there isn't one, create.
            var targetGroup = doc.Root!.Elements(ns + "ItemGroup")
                .FirstOrDefault(g => g.Elements(ns + "Content").Any());
            if (targetGroup == null)
            {
                targetGroup = new XElement(ns + "ItemGroup");
                doc.Root!.Add(targetGroup);
            }

            var linkFolder = LinkFolderForAxType(axType);
            var entry = new XElement(ns + "Content",
                new XAttribute("Include", include),
                new XElement(ns + "SubType", "Content"),
                new XElement(ns + "Name", name),
                new XElement(ns + "Link", $"{linkFolder}\\{name}"));

            InsertPreservingIndent(targetGroup, entry, ns);

            // AxLabelFile needs a SECOND <Content> entry — the label-text file,
            // marked DependentUpon the descriptor. Without it VS shows the label
            // file half-declared (the .label.txt doesn't nest under the object),
            // which forces the exclude-from-project + re-add-from-AOT dance to
            // make it show correctly. The object name is "<id>_<lang>" (e.g.
            // "ConL_en-US"); the text file is "<id>.<lang>.label.txt" — split on
            // the LAST underscore (language codes use hyphens, not underscores,
            // so the id is everything before it).
            if (string.Equals(axType, "AxLabelFile", StringComparison.OrdinalIgnoreCase))
            {
                var us = name.LastIndexOf('_');
                if (us > 0 && us < name.Length - 1)
                {
                    var txtInclude = $"{name.Substring(0, us)}.{name.Substring(us + 1)}.label.txt";
                    var alreadyTxt = doc.Root!.Elements(ns + "ItemGroup")
                        .SelectMany(g => g.Elements(ns + "Content"))
                        .Any(c => string.Equals((string?)c.Attribute("Include"), txtInclude, StringComparison.OrdinalIgnoreCase));
                    if (!alreadyTxt)
                    {
                        var txtEntry = new XElement(ns + "Content",
                            new XAttribute("Include", txtInclude),
                            new XElement(ns + "SubType", "Content"),
                            new XElement(ns + "Name", txtInclude),
                            new XElement(ns + "DependentUpon", include));
                        InsertPreservingIndent(targetGroup, txtEntry, ns);
                    }
                }
            }

            // Ensure the VS-project DISPLAY folder this object links into is
            // DECLARED. A <Content> whose <Link> points at an undeclared folder
            // breaks the project: on a brand-new (empty) project with no folder
            // defs yet, VS chokes and devenv engages 0 projects (which used to
            // surface as a false-green compile). Definition only — no disk dir.
            EnsureFolderDef(doc.Root!, ns, linkFolder, targetGroup);

            await SaveDocPreservingEncodingAsync(doc, resolved.RnprojPath, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Idempotent remove. Returns true if a matching &lt;Content&gt; entry was
    /// removed, false if no such entry was present. Removes the preceding
    /// whitespace text node along with the element so we don't leave a
    /// dangling indent in the file.
    /// </summary>
    public async Task<bool> RemoveFromRnprojAsync(string axType, string name, CancellationToken ct = default)
    {
        var resolved = Resolve() ?? throw new ProjectConfigException("Project is not configured.");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = XDocument.Load(resolved.RnprojPath, LoadOptions.PreserveWhitespace);
            var ns = (XNamespace)MsbuildNs;
            var include = $"{axType}\\{name}";

            // Includes to strip: the object descriptor, plus (for a label file)
            // its paired ".label.txt" DependentUpon entry that AddToRnprojAsync
            // wrote alongside it.
            var includes = new List<string> { include };
            if (string.Equals(axType, "AxLabelFile", StringComparison.OrdinalIgnoreCase))
            {
                var us = name.LastIndexOf('_');
                if (us > 0 && us < name.Length - 1)
                    includes.Add($"{name.Substring(0, us)}.{name.Substring(us + 1)}.label.txt");
            }

            var removedAny = false;
            foreach (var inc in includes)
            {
                var match = doc.Root!.Elements(ns + "ItemGroup")
                    .SelectMany(g => g.Elements(ns + "Content"))
                    .FirstOrDefault(c => string.Equals(
                        (string?)c.Attribute("Include"), inc,
                        StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;

                if (match.PreviousNode is XText prev && string.IsNullOrWhiteSpace(prev.Value))
                    prev.Remove();
                match.Remove();
                removedAny = true;
            }
            if (!removedAny) return false;

            await SaveDocPreservingEncodingAsync(doc, resolved.RnprojPath, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Enumerate every &lt;Content&gt; entry across the rnrproj's ItemGroups.
    /// The Include attribute is split on '\' to recover (axType, name); the
    /// Link element provides the logical-folder hint when present.
    /// </summary>
    public IReadOnlyList<RnprojObject> ListRnprojObjects()
    {
        var resolved = Resolve();
        if (resolved == null) return Array.Empty<RnprojObject>();
        try
        {
            var doc = XDocument.Load(resolved.RnprojPath, LoadOptions.PreserveWhitespace);
            var ns = (XNamespace)MsbuildNs;
            var results = new List<RnprojObject>();
            foreach (var content in doc.Root!.Elements(ns + "ItemGroup").SelectMany(g => g.Elements(ns + "Content")))
            {
                var include = (string?)content.Attribute("Include") ?? string.Empty;
                var parts = include.Split('\\', 2);
                var axType = parts.Length == 2 ? parts[0] : string.Empty;
                var name = parts.Length == 2 ? parts[1] : include;
                var link = content.Element(ns + "Link")?.Value;
                results.Add(new RnprojObject(axType, name, link));
            }
            return results;
        }
        catch { return Array.Empty<RnprojObject>(); }
    }

    /// <summary>
    /// Clear the changeset by deleting the file. Returns the prior entry count
    /// so callers can confirm what was discarded.
    /// </summary>
    public async Task<int> ClearChangesetAsync(CancellationToken ct = default)
    {
        var resolved = Resolve() ?? throw new ProjectConfigException("Project is not configured.");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(resolved.RepoRoot, ConfigDirName, ChangesetFileName);
            if (!File.Exists(path)) return 0;
            var raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<ChangesetFile>(raw, ConfigJson) ?? new ChangesetFile();
            var prior = file.Objects.Count;
            File.Delete(path);
            return prior;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Read the active rnrproj's effective DBSyncInBuild setting: "True",
    /// "False", or "True (default)" when the element is absent (MS treats a
    /// missing DBSyncInBuild as True). Best-effort — returns null on any read
    /// failure so callers can omit it rather than assert a wrong value.
    /// </summary>
    public string? ReadDbSyncInBuildEffective()
    {
        try
        {
            var resolved = Resolve();
            if (resolved == null) return null;
            var doc = System.Xml.Linq.XDocument.Load(resolved.RnprojPath);
            var ns = (System.Xml.Linq.XNamespace)"http://schemas.microsoft.com/developer/msbuild/2003";
            var els = doc.Root?.Elements(ns + "PropertyGroup")
                .Elements(ns + "DBSyncInBuild").ToList();
            if (els == null || els.Count == 0) return "True (default; element absent)";
            // MSBuild is last-assignment-wins, so the EFFECTIVE value is the last
            // occurrence — not the first. Reading the first is what made the tool
            // disagree with the build when duplicates carried different values.
            var effective = els[els.Count - 1].Value;
            if (els.Count > 1 && els.Select(e => e.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                return $"{effective} (effective; WARNING: {els.Count} conflicting <DBSyncInBuild> elements " +
                       $"[{string.Join(", ", els.Select(e => e.Value))}] — run xpp_project_set_db_sync_in_build to normalize)";
            return effective;
        }
        catch { return null; }
    }

    /// <summary>
    /// Set the active rnrproj's &lt;DBSyncInBuild&gt; property. Shared by
    /// xpp_project_set_db_sync_in_build and the xpp_compile syncDb flag —
    /// toggling it before a build is how a sync is requested (the build still
    /// runs the sync only as a product of a SUCCESSFUL compile, per the
    /// project's property). tf-checks-out the rnrproj when SCM is configured.
    /// </summary>
    public async Task<DbSyncSetResult> SetDbSyncInBuildAsync(bool enable, CancellationToken ct = default)
    {
        var resolved = Resolve() ?? throw new ProjectConfigException("Project is not configured.");
        var doc = System.Xml.Linq.XDocument.Load(resolved.RnprojPath, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        var ns = (System.Xml.Linq.XNamespace)"http://schemas.microsoft.com/developer/msbuild/2003";
        var firstPropGroup = doc.Root?.Elements(ns + "PropertyGroup").FirstOrDefault()
            ?? throw new ProjectConfigException("rnrproj has no <PropertyGroup> element. Refusing to invent one.");

        var newValue = enable ? "True" : "False";
        var warnings = new List<string>();

        // Set EVERY <DBSyncInBuild> across ALL PropertyGroups — not just the
        // first. MSBuild evaluates properties top-to-bottom with last-assignment
        // wins, so a stale later occurrence in another PropertyGroup silently
        // governs the build. Setting only the first reported success while the
        // effective value stayed wrong (schema sync never ran). Set all so the
        // effective value is unambiguous; add one to the first group only when
        // none exist.
        var allExisting = doc.Root!.Elements(ns + "PropertyGroup")
            .Elements(ns + "DBSyncInBuild").ToList();
        string previousValue;
        if (allExisting.Count > 0)
        {
            previousValue = allExisting.Count == 1
                ? allExisting[0].Value
                : $"[{allExisting.Count} occurrences: {string.Join(", ", allExisting.Select(e => e.Value))}]";
            foreach (var e in allExisting) e.SetValue(newValue);
            if (allExisting.Count > 1)
                warnings.Add($"rnrproj had {allExisting.Count} <DBSyncInBuild> elements across PropertyGroups " +
                             $"(MSBuild uses the last); set all to {newValue} so the effective value is unambiguous.");
        }
        else
        {
            previousValue = "(absent — defaults to True)";
            firstPropGroup.Add(new System.Xml.Linq.XElement(ns + "DBSyncInBuild", newValue));
        }
        try
        {
            var scm = await ScmCheckoutPathAsync(resolved.RnprojPath, ct).ConfigureAwait(false);
            if (scm != null) warnings.Add($"scm: {scm}");
        }
        catch (Exception ex) { warnings.Add($"scm op failed: {ex.Message}"); }

        await File.WriteAllTextAsync(resolved.RnprojPath,
            doc.Declaration + Environment.NewLine + doc.Root!.ToString(System.Xml.Linq.SaveOptions.DisableFormatting),
            ct).ConfigureAwait(false);

        return new DbSyncSetResult(resolved.RnprojPath, previousValue, newValue, warnings.ToArray());
    }

    /// <summary>
    /// Upsert a changeset entry. Sets firstTouchedAt the first time a given
    /// (axType, name) is seen; refreshes lastTouchedAt every call. The
    /// <paramref name="createdHere"/> flag is set true only on create flows
    /// (xpp_create_object) and preserved as true on subsequent updates of
    /// the same object.
    /// </summary>
    public async Task UpsertChangesetAsync(string axType, string name, bool createdHere, CancellationToken ct = default)
    {
        var resolved = Resolve() ?? throw new ProjectConfigException("Project is not configured.");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(resolved.RepoRoot, ConfigDirName, ChangesetFileName);
            var file = File.Exists(path)
                ? (JsonSerializer.Deserialize<ChangesetFile>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), ConfigJson) ?? new ChangesetFile())
                : new ChangesetFile();

            var now = DateTimeOffset.UtcNow.ToString("o");
            var match = file.Objects.FirstOrDefault(o =>
                string.Equals(o.AxType, axType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                file.Objects.Add(new ChangesetEntry
                {
                    AxType = axType,
                    Name = name,
                    FirstTouchedAt = now,
                    LastTouchedAt = now,
                    CreatedHere = createdHere
                });
            }
            else
            {
                match.LastTouchedAt = now;
                if (createdHere) match.CreatedHere = true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(file, ConfigJson);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Append <paramref name="entry"/> to <paramref name="group"/> while
    /// matching the existing indentation pattern of sibling Content elements.
    /// XDocument loaded with PreserveWhitespace keeps the whitespace between
    /// elements as XText nodes; we clone those siblings' patterns onto the
    /// new element so the resulting file has clean diff-friendly formatting
    /// instead of a flattened one-liner crowding the closing tag.
    /// </summary>
    /// <summary>
    /// Ensure a &lt;Folder Include="&lt;name&gt;\"&gt; definition exists in the rnrproj
    /// for the given display folder. Scans ALL ItemGroups for an existing def
    /// (case-insensitive); if absent, adds a self-closing Folder element to the
    /// first Folder-bearing ItemGroup, creating that ItemGroup (placed just
    /// before the Content group) if the project has none yet. Definition only —
    /// VS project virtual folder, not a disk directory.
    /// </summary>
    private static void EnsureFolderDef(XElement root, XNamespace ns, string folderName, XElement contentGroup)
    {
        var include = $"{folderName}\\";
        var folderGroups = root.Elements(ns + "ItemGroup")
            .Where(g => g.Elements(ns + "Folder").Any())
            .ToList();

        var already = folderGroups
            .SelectMany(g => g.Elements(ns + "Folder"))
            .Any(f => string.Equals((string?)f.Attribute("Include"), include, StringComparison.OrdinalIgnoreCase));
        if (already) return;

        var folderGroup = folderGroups.FirstOrDefault();
        if (folderGroup == null)
        {
            // No folder ItemGroup yet — create one right before the Content group.
            folderGroup = new XElement(ns + "ItemGroup");
            contentGroup.AddBeforeSelf(new XText("\n  "), folderGroup);
        }

        var folder = new XElement(ns + "Folder", new XAttribute("Include", include));
        var last = folderGroup.Elements(ns + "Folder").LastOrDefault();
        var siblingIndent = "\n    ";
        var closeIndent = "\n  ";
        if (last?.PreviousNode is XText pw && pw.Value.Contains('\n')) siblingIndent = pw.Value;
        if (last != null)
        {
            var needClose = !last.NodesAfterSelf().OfType<XText>().Any();
            last.AddAfterSelf(new XText(siblingIndent), folder);
            if (needClose) folder.AddAfterSelf(new XText(closeIndent));
        }
        else
        {
            folderGroup.Add(new XText(siblingIndent), folder, new XText(closeIndent));
        }
    }

    private static void InsertPreservingIndent(XElement group, XElement entry, XNamespace ns)
    {
        // Prefer a "healthy" reference (multi-line, has child whitespace) over
        // any collapsed/hand-edited single-line sibling. Sample sibling and
        // child indents from it.
        XElement? healthy = null;
        foreach (var content in group.Elements(ns + "Content").Reverse())
        {
            var firstText = content.Nodes().OfType<XText>().FirstOrDefault();
            if (firstText != null && firstText.Value.Contains('\n'))
            {
                healthy = content;
                break;
            }
        }
        var anyReference = healthy ?? group.Elements(ns + "Content").LastOrDefault();

        string siblingIndent = "\n    ";
        string childIndent = "\n      ";
        string groupCloseIndent = "\n  ";

        if (healthy != null)
        {
            if (healthy.PreviousNode is XText prevWs && prevWs.Value.Contains('\n'))
                siblingIndent = prevWs.Value;
            var firstChildPrev = healthy.Nodes().OfType<XText>().FirstOrDefault();
            if (firstChildPrev != null && firstChildPrev.Value.Contains('\n'))
                childIndent = firstChildPrev.Value;
        }

        var grandchildren = entry.Elements().ToList();
        entry.RemoveNodes();
        foreach (var c in grandchildren)
        {
            entry.Add(new XText(childIndent));
            entry.Add(c);
        }
        entry.Add(new XText(siblingIndent));

        if (anyReference != null)
        {
            // If nothing currently sits between the reference and </ItemGroup>,
            // append a closing-indent text node so the close tag lands on its
            // own line.
            bool needTrailingClose = !anyReference.NodesAfterSelf().OfType<XText>().Any();
            anyReference.AddAfterSelf(new XText(siblingIndent), entry);
            if (needTrailingClose)
                entry.AddAfterSelf(new XText(groupCloseIndent));
        }
        else
        {
            var trailingWs = group.Nodes().OfType<XText>().LastOrDefault();
            if (trailingWs != null)
                trailingWs.AddBeforeSelf(new XText(siblingIndent), entry);
            else
                group.Add(new XText(siblingIndent), entry, new XText(groupCloseIndent));
        }
    }

    private static async Task SaveDocPreservingEncodingAsync(XDocument doc, string path, CancellationToken ct)
    {
        // .rnrproj files are UTF-8 (no BOM) per VS convention. The declaration
        // node already says encoding="utf-8". Save via XDocument.Save which
        // honors the declaration. Use a temp+replace pattern so a crash mid-
        // write doesn't corrupt the user's project file.
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            doc.Save(stream, SaveOptions.DisableFormatting);
        }
        File.Replace(tmp, path, null);
        await Task.CompletedTask;
        _ = ct;
    }

    /// <summary>
    /// Best-effort logical-folder mapping for the &lt;Link&gt; child of the
    /// rnrproj Content entry. Mirrors the conventions in the dev VM's
    /// sample project; VS will re-bucket if the user reorganizes manually.
    /// </summary>
    // The VS-project DISPLAY folder each AOT type is grouped under (NOT a disk
    // folder). Strictly type-driven — the agent has no say in placement. Names
    // verified against a real VS-authored project (Project Contoso.rnrproj). The
    // matching <Folder Include="<name>\"> definition is auto-created on first
    // use by AddToRnprojAsync (a missing def is what broke a brand-new project's
    // build). EDTs: VS subdivides base EDTs by base type (EDT Strings / EDT
    // Enums / ...), but we land all EDTs in one "Extended Data Types" folder —
    // we don't know the base type at add-time, and a unified folder is cleaner.
    private static string LinkFolderForAxType(string axType) => axType switch
    {
        "AxClass" => "Classes",
        "AxTable" => "Tables",
        "AxTableExtension" => "Table Extensions",
        "AxForm" => "Forms",
        "AxFormExtension" => "Form Extensions",
        "AxEdt" => "Extended Data Types",
        "AxEdtExtension" => "Extended Data Type Extensions",
        "AxEnum" => "Base Enums",
        "AxEnumExtension" => "Base Enum Extensions",
        "AxView" => "Views",
        "AxViewExtension" => "View Extensions",
        "AxQuery" => "Simple Queries",
        "AxQueryComposite" => "Composite Queries",
        "AxMenuItemAction" => "Action Menu Items",
        "AxMenuItemDisplay" => "Display Menu Items",
        "AxMenuItemOutput" => "Output Menu Items",
        "AxMenu" => "Menus",
        "AxMenuExtension" => "Menu Extensions",
        "AxLabelFile" => "Label Files",
        "AxSecurityPrivilege" => "Security Privileges",
        "AxSecurityDuty" => "Security Duties",
        "AxSecurityRole" => "Security Roles",
        "AxSecurityPolicy" => "Security Policies",
        "AxTile" => "Tiles",
        "AxService" => "Services",
        "AxServiceGroup" => "Service Groups",
        "AxResource" => "Resources",
        "AxDataEntityView" => "Data Entities",
        "AxCompositeDataEntityView" => "Data Entities",
        "AxDataEntityViewExtension" => "Data Entities",
        _ => axType
    };

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record RnprojObject(string AxType, string Name, string? Link);

public sealed record ResolvedConfig(
    string ConfigPath,
    string RepoRoot,
    string RnprojPath,
    string SlnPath,
    string Model,
    string Module,
    string ObjectPrefix,
    string ExtensionSuffix,
    IReadOnlyList<string> BpSuppress,
    IReadOnlyList<string> BpEscalate,
    ResolvedScm? Scm);

/// <summary>
/// SCM integration config. Currently only TFVC is supported. Other kinds
/// (none, git) may surface as union variants in the future; today an
/// absent <c>scm</c> block in the config means "no SCM behavior."
/// </summary>
public sealed record ResolvedScm(
    string Kind,
    string MetadataPath,
    string? TfExePath);

public sealed class ProjectConfigException : Exception
{
    public ProjectConfigException(string message) : base(message) { }
    public ProjectConfigException(string message, Exception inner) : base(message, inner) { }
}

// -- on-disk file shapes ----------------------------------------------------

public sealed class ConfigFile
{
    public int Version { get; set; } = 1;
    public string? RnprojPath { get; set; }
    // Optional explicit .sln path for xpp_compile / devenv.com. When omitted,
    // ProjectContext walks up from RnprojPath looking for *.sln (the usual
    // convention for D365 projects).
    public string? SlnPath { get; set; }
    // Optional F&O module name. xppbp.exe / xppc.exe require both -module
    // and -model. When omitted, the resolved Module falls back to the
    // Model — true for the common single-model-module convention.
    public string? ModuleName { get; set; }
    public NamingBlock? Naming { get; set; }
    public BestPracticesBlock? BestPractices { get; set; }
    public ScmBlock? Scm { get; set; }
}

public sealed class ScmBlock
{
    // Currently only "tfvc". Absent block disables all SCM behavior.
    public string? Kind { get; set; }
    // The TFVC workspace root — the F&O PackagesLocalDirectory mapped to
    // a server path like '$/<Project>/Trunk/Dev/Metadata'. Required when
    // Kind is set.
    public string? MetadataPath { get; set; }
    // Optional override for tf.exe. When null, discovered under the VS2022
    // install.
    public string? TfExePath { get; set; }
}

public sealed class NamingBlock
{
    public string? ObjectPrefix { get; set; }
    public string? ExtensionSuffix { get; set; }
}

public sealed class BestPracticesBlock
{
    // Monikers (e.g. "BPXmlDocNoDocumentationComments") to silence both in the
    // summary counts and the diagnostics array. Users can also pass them
    // explicitly via xpp_bp_check(monikers=[...]) to drill in despite the
    // suppression. See plugins/xpp/docs/bp-rules-reference.md for the roster.
    public List<string>? Suppress { get; set; }
    // Monikers to promote from Warning to Error via xppbp -TreatWarningsAsErrors.
    public List<string>? Escalate { get; set; }
}

public sealed class ChangesetFile
{
    public int Version { get; set; } = 1;
    public List<ChangesetEntry> Objects { get; set; } = new();
}

public sealed class ChangesetEntry
{
    public string AxType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FirstTouchedAt { get; set; } = string.Empty;
    public string LastTouchedAt { get; set; } = string.Empty;
    public bool CreatedHere { get; set; }
}

/// <summary>
/// Outcome of <see cref="ProjectContext.ScmDeleteAsync"/>. When
/// <c>HandledLocalDelete</c> is true, tf.exe removed the local
/// file as part of the delete; the caller must NOT also File.Delete.
/// When false, SCM was unconfigured or skipped; the caller is
/// responsible for File.Delete.
/// </summary>
public sealed record ScmDeleteResult(bool HandledLocalDelete, string? Warning);

/// <summary>Outcome of <see cref="ProjectContext.SetDbSyncInBuildAsync"/>.</summary>
public sealed record DbSyncSetResult(string RnprojPath, string Previous, string Current, string[] Warnings);

/// <summary>
/// Outcome of <see cref="ProjectContext.ScmRenameAsync"/>. When
/// <c>HandledLocalRename</c> is true, tf.exe moved the local file
/// as part of the rename; the caller must NOT also File.Move.
/// When false, SCM was unconfigured or skipped; the caller is
/// responsible for File.Move.
/// </summary>
public sealed record ScmRenameResult(bool HandledLocalRename, string? Warning);
