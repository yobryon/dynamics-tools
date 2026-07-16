using System.Linq;
using System.Text.Json;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Shared shape for the JSON each typed Create/Patch tool returns.
/// Consolidates three concerns that every tool used to write inline:
///
///  - the response identity envelope (axType, model, name, created/updated)
///  - side-effect bookkeeping (addedToProject, changesetUpdated,
///    sideEffectWarnings)
///  - mapper-drift reporting (drift array + auto-feedback file write +
///    drift entries merged into sideEffectWarnings)
///
/// Tools that don't compute one of these signals pass null/empty and
/// the corresponding key is omitted from the response. Keeps the wire
/// shape compact without forcing every tool to know about every field.
/// </summary>
internal static class WriteResponseSerializer
{
    /// <summary>
    /// Build the JSON response for a successful typed create or patch.
    /// Triggers an implicit feedback-file write when any drift entries
    /// were returned, so the maintainer accumulates a record without
    /// requiring the agent to manually log it.
    /// </summary>
    /// <param name="resp">The gRPC response from CreateDomainObject / PatchDomainObject.</param>
    /// <param name="op">"create" or "patch" — used for the response field name and the feedback-file frontmatter.</param>
    /// <param name="addedToProject">From the tool's post-write bookkeeping; null when the tool doesn't track it.</param>
    /// <param name="changesetUpdated">Whether the changeset upsert succeeded.</param>
    /// <param name="sideEffectWarnings">Pre-existing warnings (rnrproj add failures, SCM op failures, etc.). Drift entries are appended.</param>
    public static string Serialize(
        WriteObjectResponse resp,
        string op,
        bool? addedToProject,
        bool changesetUpdated,
        IEnumerable<string>? sideEffectWarnings)
    {
        DriftReporting.TryWriteFeedbackAsync(resp.AxType, resp.Name, op, resp.Drift);

        var payload = new Dictionary<string, object?>
        {
            ["axType"] = resp.AxType,
            ["model"] = resp.Model,
            ["name"] = resp.Name,
        };
        if (string.Equals(op, "create", StringComparison.OrdinalIgnoreCase))
            payload["created"] = true;
        else
            payload["updated"] = true;
        if (addedToProject.HasValue) payload["addedToProject"] = addedToProject.Value;
        payload["changesetUpdated"] = changesetUpdated;

        // Form-pattern conformance (AxForm only). Auto-stamped property fixes
        // are already on disk; here we surface what the stamp couldn't fix —
        // structural `missing` controls and residual `mismatches` — both as a
        // structured block AND as high-signal sideEffectWarnings so the agent
        // can't miss them. Author `overrides` ride in the block (informational).
        var warnings = (sideEffectWarnings ?? Array.Empty<string>()).ToList();
        var pc = resp.PatternConformance;
        if (pc != null && !string.IsNullOrEmpty(pc.Pattern))
        {
            payload["patternConformance"] = ConformancePayload(pc);
            if (!pc.Ok) warnings.AddRange(ConformanceWarnings(pc));
        }

        payload["sideEffectWarnings"] = DriftReporting.MergedWarnings(warnings, resp.Drift);
        if (resp.Drift.Count > 0)
            payload["drift"] = DriftReporting.AsStructuredArray(resp.Drift);

        return JsonSerializer.Serialize(payload);
    }

    private static Dictionary<string, object?> ConformancePayload(PatternConformance pc)
    {
        var obj = new Dictionary<string, object?>
        {
            ["pattern"] = pc.Pattern,
            ["version"] = pc.Version,
            ["ok"] = pc.Ok,
        };
        if (!string.IsNullOrEmpty(pc.Note)) obj["note"] = pc.Note;
        if (!pc.VersionActive)
        {
            obj["versionActive"] = false;
            if (pc.ActiveVersions.Count > 0) obj["activeVersions"] = pc.ActiveVersions.ToArray();
            if (!string.IsNullOrEmpty(pc.VersionNote)) obj["versionNote"] = pc.VersionNote;
        }
        if (pc.Missing.Count > 0)
            obj["missing"] = pc.Missing.Select(m => new { path = m.Path, expected = m.Expected }).ToArray();
        if (pc.Overrides.Count > 0)
            obj["overrides"] = pc.Overrides.Select(o => new
            {
                path = o.Path, control = o.Control, property = o.Property,
                requested = o.Requested, patternValue = o.PatternValue,
            }).ToArray();
        if (pc.Mismatches.Count > 0)
            obj["mismatches"] = pc.Mismatches.Select(m => new
            {
                path = m.Path, control = m.Control, property = m.Property,
                actual = m.Actual, patternValue = m.PatternValue, op = m.Op,
            }).ToArray();
        return obj;
    }

    private static IEnumerable<string> ConformanceWarnings(PatternConformance pc)
    {
        // Lead with a retired pattern version: everything else in this report
        // was analyzed against the OBSOLETE pattern, so the missing/mismatch
        // entries below it (and any pattern errors the next compile emits) may
        // be artifacts of the stale version rather than real authoring gaps.
        if (!pc.VersionActive)
            yield return string.IsNullOrEmpty(pc.VersionNote)
                ? $"pattern {pc.Pattern}: declared version '{pc.Version}' is not active" +
                  (pc.ActiveVersions.Count > 0 ? $" (active: {string.Join(", ", pc.ActiveVersions)})" : "")
                : $"pattern {pc.Pattern}: {pc.VersionNote}";

        foreach (var m in pc.Missing)
            yield return string.IsNullOrEmpty(m.Path)
                ? $"pattern {pc.Pattern}: missing required control '{m.Expected}'"
                : $"pattern {pc.Pattern}: missing required control '{m.Expected}' under '{m.Path}'";
        foreach (var mm in pc.Mismatches)
            // The invalid-declared-pattern case carries its guidance ("valid
            // here: ...") in PatternValue, so read it as prose rather than the
            // generic "must <op> <value>" template (which produced the awkward
            // "must MustBeOneOf 'valid here: ...'").
            yield return string.Equals(mm.Property, "pattern", StringComparison.OrdinalIgnoreCase)
                ? $"pattern {pc.Pattern}: control '{mm.Control}' declares an invalid pattern '{mm.Actual}' — {mm.PatternValue}"
                : $"pattern {pc.Pattern}: {mm.Property} on '{mm.Control}' must {mm.Op} '{mm.PatternValue}' (is '{mm.Actual}')";
    }
}
