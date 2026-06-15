using System.ComponentModel;
using System.Text.Json;
using System.Xml.Linq;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Write-surface tools. Three operations cover the round-trip authoring
/// contract Microsoft picked for their own AI integration:
///
///   xpp_get_object_xml   read an object as its full AOT XML
///   xpp_create_object    write a NEW object in the current project's model
///   xpp_update_object    overwrite an EXISTING object in the current project's model
///
/// The canonical authoring flow is get -> edit locally -> update. For new
/// objects the agent constructs the XML from the type's schema (see
/// xpp://schema/{type}) and posts it via create. Per-AOT-type authoring
/// guidance (property checklists, gotchas, examples) lives in the
/// dynamics-xpp plugin's skills (dynamics-xpp:xpp-class, dynamics-xpp:xpp-table, dynamics-xpp:xpp-form, ...).
///
/// All write operations require a configured .dynamics-xpp project (see the
/// dynamics-xpp:xpp-project skill). The target model flows from the project's
/// .rnrproj — no caller-supplied override. Updates against objects in a
/// different model are rejected with a structured out_of_model_update error
/// that proposes the equivalent extension shape.
/// </summary>
[McpServerToolType]
public sealed class AuthoringTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public AuthoringTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    [McpServerTool(Name = "xpp_get_object_xml"), Description(
        "Read an AOT object as its full on-disk XML. The returned XML is " +
        "the canonical envelope - declaration, methods, fields, indexes, " +
        "relations, properties together - and is exactly what xpp_update_object " +
        "expects on the way back in. Use this to start any modification: read, " +
        "edit, write. For the XSD before editing, see xpp://schema/{type}; " +
        "for property-by-property authoring guidance, load the matching " +
        "xpp:{type} skill from the dynamics-xpp plugin.")]
    public async Task<string> GetObjectXml(
        [Description("AOT type name, e.g. 'AxClass', 'AxTable', 'AxForm'.")] string axType,
        [Description("Object name (PascalCase identifier).")] string name,
        [Description("Optional model name to disambiguate when the same name exists across models.")] string? model = null,
        CancellationToken ct = default)
    {
        var req = new ObjectRef
        {
            AxType = axType,
            Name = name,
            Model = model ?? string.Empty
        };
        var resp = await _conn.Client.GetObjectXmlAsync(req, cancellationToken: ct);
        return JsonSerializer.Serialize(new
        {
            axType = resp.Ref.AxType,
            name = resp.Ref.Name,
            model = resp.Ref.Model,
            xml = resp.Xml
        });
    }

    [McpServerTool(Name = "xpp_create_object"), Description(
        "Create a NEW AOT object in the current project's model. The model " +
        "comes from the active .dynamics-xpp project — no caller override. " +
        "Fails if an object with that name already exists in the model. The " +
        "object's Name is taken from the XML root. On success, the new " +
        "object is automatically added to the project's .rnrproj <ItemGroup> " +
        "and recorded in the changeset. For most AOT types, prefer the typed " +
        "xpp_create_{type} tool — it's lossless, schema-coupled to the " +
        "request, and catches mistakes at the tool boundary. Drop to raw " +
        "xpp_create_object only when the typed surface doesn't cover what " +
        "you need. The bridge's metadata deserializer is the validator; " +
        "invalid XML / unknown enum values / missing properties surface as " +
        "bridge_create_failed with a property-specific error. Requires a " +
        "configured project.")]
    public async Task<string> CreateObject(
        [Description("AOT type name matching the XML root element, e.g. 'AxClass'.")] string axType,
        [Description("Full AOT XML for the object as a single UTF-8 string.")] string xml,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveProjectOrReport();
        if (gate != null) return gate;

        var req = new WriteObjectRequest
        {
            AxType = axType,
            Model = resolved!.Model,
            Xml = xml
        };
        WriteObjectResponse resp;
        try { resp = await _conn.Client.CreateObjectAsync(req, cancellationToken: ct); }
        catch (RpcException rx) { return FormatBridgeFailure(axType, "create", rx); }

        var sideEffects = await RecordPostWriteAsync(axType, resp.Name, createdHere: true, ct).ConfigureAwait(false);
        var warnings = sideEffects.Warnings.ToList();
        try
        {
            var scmWarning = await _project.ScmAddAsync(axType, resp.Name, ct).ConfigureAwait(false);
            if (scmWarning != null) warnings.Add($"scm: {scmWarning}");
        }
        catch (Exception ex) { warnings.Add($"scm op failed: {ex.Message}"); }

        return BuildWriteJson(resp, "created", sideEffects.AddedToProject, sideEffects.ChangesetUpdated, warnings);
    }

    [McpServerTool(Name = "xpp_update_object"), Description(
        "Overwrite an EXISTING AOT object in the current project's model. " +
        "The model comes from the active .dynamics-xpp project — no caller " +
        "override. The XML must be the FULL object representation, not a " +
        "patch — the canonical flow is xpp_get_object_xml -> edit locally " +
        "-> xpp_update_object. Prefer xpp_patch_{type} for partial updates " +
        "of supported types; drop to raw xpp_update_object only when the " +
        "typed patch surface can't express the change. Pre-flight: if the " +
        "target object lives in a different model (typically a Microsoft- " +
        "shipped one), the call is rejected with out_of_model_update and a " +
        "proposed_action pointing at the right extension shape (see " +
        "dynamics-xpp:xpp-extension skill). On success, the object is " +
        "auto-added to the project's .rnrproj <ItemGroup> if not already " +
        "referenced, and the changeset is refreshed. The bridge's metadata " +
        "deserializer is the validator; failures come back as " +
        "bridge_update_failed with property-specific detail. Requires a " +
        "configured project.")]
    public async Task<string> UpdateObject(
        [Description("AOT type name matching the XML root element, e.g. 'AxClass'.")] string axType,
        [Description("Full updated AOT XML for the object as a single UTF-8 string.")] string xml,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveProjectOrReport();
        if (gate != null) return gate;

        var name = TryExtractName(xml);
        if (!string.IsNullOrEmpty(name))
        {
            var rejection = await CheckTargetModelAsync(axType, name!, resolved!, ct).ConfigureAwait(false);
            if (rejection != null) return rejection;
        }

        // SCM pre-flight: checkout so the bridge can overwrite a
        // read-only TFVC file. Failures fall through as warnings — if
        // checkout truly failed, the bridge's own access-denied error
        // surfaces via the bridge_update_failed path.
        string? scmPreWarning = null;
        if (!string.IsNullOrEmpty(name))
        {
            try { scmPreWarning = await _project.ScmCheckoutAsync(axType, name!, ct).ConfigureAwait(false); }
            catch (Exception ex) { scmPreWarning = $"scm op failed: {ex.Message}"; }
        }

        var req = new WriteObjectRequest
        {
            AxType = axType,
            Model = resolved!.Model,
            Xml = xml
        };
        WriteObjectResponse resp;
        try { resp = await _conn.Client.UpdateObjectAsync(req, cancellationToken: ct); }
        catch (RpcException rx) { return FormatBridgeFailure(axType, "update", rx); }

        var sideEffects = await RecordPostWriteAsync(axType, resp.Name, createdHere: false, ct).ConfigureAwait(false);
        var updateWarnings = sideEffects.Warnings.ToList();
        if (scmPreWarning != null) updateWarnings.Insert(0, $"scm: {scmPreWarning}");
        return BuildWriteJson(resp, "updated", sideEffects.AddedToProject, sideEffects.ChangesetUpdated, updateWarnings);
    }

    /// <summary>
    /// Build the JSON response for a raw create / update. Echoes the identity
    /// envelope and folds any bridge-detected round-trip drops into both the
    /// human-readable <c>sideEffectWarnings</c> (with round-trip-specific
    /// wording — these are FromFile drops, not typed-mapper gaps) and a
    /// structured <c>droppedProperties</c> block. <paramref name="verb"/> is
    /// "created" or "updated".
    /// </summary>
    private static string BuildWriteJson(
        WriteObjectResponse resp, string verb, bool addedToProject, bool changesetUpdated, List<string> warnings)
    {
        foreach (var d in resp.Drift)
            warnings.Add(
                $"dropped on round-trip: '{d.RequestPath}' (value='{d.RequestValue}') was present in the " +
                "posted XML but did not survive the bridge's deserialize/serialize — most often an " +
                "out-of-order or unrecognized element (the on-disk element order is contract-significant, " +
                "and MS's deserializer silently skips elements it can't place). Diff your XML against a " +
                "clean xpp_get_object_xml of this object; for supported types the typed xpp_create_/xpp_patch_ " +
                "tools avoid this entirely.");

        var payload = new Dictionary<string, object?>
        {
            ["axType"] = resp.AxType,
            ["model"] = resp.Model,
            ["name"] = resp.Name,
            [verb] = true,
            ["addedToProject"] = addedToProject,
            ["changesetUpdated"] = changesetUpdated,
            ["sideEffectWarnings"] = warnings.ToArray(),
        };
        if (resp.Drift.Count > 0)
            payload["droppedProperties"] = resp.Drift.Select(d => new { path = d.RequestPath, value = d.RequestValue }).ToArray();
        return JsonSerializer.Serialize(payload);
    }

    // -- private helpers -----------------------------------------------------

    /// <summary>
    /// Resolve the project; if missing or invalid, return a structured
    /// error payload as the gate response. Successful resolution returns
    /// the config and a null gate.
    /// </summary>
    private (ResolvedConfig? resolved, string? gate) ResolveProjectOrReport()
    {
        try
        {
            var r = _project.Resolve();
            if (r == null)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    error = "project_not_configured",
                    message = "Write operations require a .dynamics-xpp/config.json in the launch directory. Load the dynamics-xpp:xpp-project skill and walk the user through first-time setup.",
                    cwd = Environment.CurrentDirectory,
                    skill = "dynamics-xpp:xpp-project"
                });
                return (null, payload);
            }
            return (r, null);
        }
        catch (ProjectConfigException pcx)
        {
            var payload = JsonSerializer.Serialize(new
            {
                error = "project_config_invalid",
                message = pcx.Message,
                skill = "dynamics-xpp:xpp-project"
            });
            return (null, payload);
        }
    }

    /// <summary>
    /// Pre-flight: ensure the target object's model matches the project's.
    /// Uses the existing FindObject gRPC streaming RPC; takes the first
    /// match. Returns null on success (proceed with update), or a JSON
    /// out_of_model_update payload on rejection.
    /// </summary>
    private async Task<string?> CheckTargetModelAsync(string axType, string name, ResolvedConfig resolved, CancellationToken ct)
    {
        var request = new FindObjectRequest
        {
            Name = name,
            AxType = axType
        };
        try
        {
            using var call = _conn.Client.FindObject(request);
            while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                var m = call.ResponseStream.Current;
                if (string.Equals(m.Ref.Model, resolved.Model, StringComparison.OrdinalIgnoreCase))
                    return null; // target sits in our model — proceed.

                return JsonSerializer.Serialize(new
                {
                    error = "out_of_model_update",
                    message = $"{name} is in '{m.Ref.Model}'. The dynamics-xpp write tools only modify objects in the current project's model ('{resolved.Model}'). Microsoft application models are sealed since release 8.0 — modifications go through extensions.",
                    target = new { axType = m.Ref.AxType, name = m.Ref.Name, in_model = m.Ref.Model, current_model = resolved.Model },
                    proposed_action = ProposeExtension(axType, name, resolved.ExtensionSuffix),
                    skill = "dynamics-xpp:xpp-extension"
                });
            }
        }
        catch
        {
            // FindObject is best-effort pre-flight. If it fails for an
            // infrastructure reason, fall through to letting the update
            // proceed; the bridge will reject if the object truly doesn't
            // exist or is in a foreign model.
        }
        return null;
    }

    /// <summary>
    /// Best-effort: extract the object's Name from the XML root. AOT objects
    /// shape this as &lt;Root&gt;&lt;Name&gt;X&lt;/Name&gt;...&lt;/Root&gt;.
    /// Returns null if not found — caller treats that as "skip pre-flight."
    /// </summary>
    private static string? TryExtractName(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
        }
        catch { return null; }
    }

    private static object ProposeExtension(string axType, string name, string extensionSuffix)
    {
        // Map base AOT type -> the canonical extension shape. For class-style
        // (CoC) extensions there's no metadata-extension type — they become
        // AxClass with [ExtensionOf] — so we propose AxClass with a
        // <prefix><Type><Target>_Extension naming hint and steer the agent
        // at the dynamics-xpp:xpp-class CoC section instead.
        return axType switch
        {
            "AxTable" => new
            {
                approach = $"Create a table extension in your current project.",
                tool = "xpp_create_object",
                axType = "AxTableExtension",
                suggested_name = $"{name}.{extensionSuffix}",
                name_pattern = "<original>.<extensionSuffix> — see dynamics-xpp:xpp-extension skill",
                constraints = new[]
                {
                    "AxTableExtension can add fields, indexes, relations, field groups, FieldGroupExtensions, and FieldModifications (label/help only). It cannot remove/rename/retype base fields.",
                    "Load the dynamics-xpp:xpp-extension skill for the structural shape."
                }
            },
            "AxForm" => new
            {
                approach = $"Create a form extension in your current project.",
                tool = "xpp_create_object",
                axType = "AxFormExtension",
                suggested_name = $"{name}.{extensionSuffix}",
                name_pattern = "<original>.<extensionSuffix> — see dynamics-xpp:xpp-extension skill",
                constraints = new[] { "Load the dynamics-xpp:xpp-extension skill for the structural shape." }
            },
            "AxEdt" => new
            {
                approach = $"Create an EDT extension in your current project.",
                tool = "xpp_create_object",
                axType = "AxEdtExtension",
                suggested_name = $"{name}.{extensionSuffix}",
                name_pattern = "<original>.<extensionSuffix> — see dynamics-xpp:xpp-extension skill",
                constraints = new[] { "Load the dynamics-xpp:xpp-extension skill for the structural shape." }
            },
            "AxEnum" => new
            {
                approach = $"Create an enum extension in your current project (base enum must be marked Extensible).",
                tool = "xpp_create_object",
                axType = "AxEnumExtension",
                suggested_name = $"{name}.{extensionSuffix}",
                name_pattern = "<original>.<extensionSuffix> — see dynamics-xpp:xpp-extension skill",
                constraints = new[] { "Load the dynamics-xpp:xpp-extension skill for the structural shape." }
            },
            "AxClass" => new
            {
                approach = "Create a Chain of Command (CoC) class extension in your current project.",
                tool = "xpp_create_object",
                axType = "AxClass",
                suggested_name = $"<prefix>{name}_Extension",
                name_pattern = "<prefix><Target>_Extension — see dynamics-xpp:xpp-class (CoC section)",
                constraints = new[]
                {
                    "Class must be marked 'final' and named with the '_Extension' suffix (BP-enforced).",
                    "Decorate with [ExtensionOf(classStr(<Target>))] and use 'next' to chain.",
                    "Load the dynamics-xpp:xpp-class skill (CoC section)."
                }
            },
            _ => new
            {
                approach = "Create an extension instead of updating the base.",
                tool = "xpp_create_object",
                axType = $"{axType}Extension",
                suggested_name = $"{name}.{extensionSuffix}",
                name_pattern = "<original>.<extensionSuffix> — see dynamics-xpp:xpp-extension skill",
                constraints = new[] { "Load the dynamics-xpp:xpp-extension skill for the structural shape." }
            }
        };
    }

    private async Task<SideEffectReport> RecordPostWriteAsync(string axType, string name, bool createdHere, CancellationToken ct)
    {
        var warnings = new List<string>();
        bool added = false;
        bool changeset = false;

        try { added = await _project.AddToRnprojAsync(axType, name, ct).ConfigureAwait(false); }
        catch (Exception ex) { warnings.Add($"failed to add to rnrproj: {ex.Message}"); }

        try { await _project.UpsertChangesetAsync(axType, name, createdHere, ct).ConfigureAwait(false); changeset = true; }
        catch (Exception ex) { warnings.Add($"failed to update changeset: {ex.Message}"); }

        return new SideEffectReport(added, changeset, warnings);
    }

    private sealed record SideEffectReport(bool AddedToProject, bool ChangesetUpdated, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Translate a downstream gRPC failure into a structured payload so
    /// callers get property-specific detail instead of the MCP SDK's
    /// generic "An error occurred" wrapper. The bridge's DataContract
    /// deserializer or metadata writer is the authoritative validator;
    /// its message is what callers should iterate against.
    /// </summary>
    private static string FormatBridgeFailure(string axType, string operation, RpcException rx)
    {
        return JsonSerializer.Serialize(new
        {
            error = "bridge_" + operation + "_failed",
            axType,
            code = rx.Status.StatusCode.ToString(),
            message = rx.Status.Detail,
            hint = "Failure originated in the bridge (DataContract deserialization, " +
                   "metadata writer, or file system). The message names the " +
                   "specific property or element; for raw-XML calls, compare the " +
                   "posted XML to a clean xpp_get_object_xml of the same object."
        });
    }
}
