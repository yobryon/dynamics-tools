using System.Text.Json;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Maps a form's Design.Pattern (and HeaderPattern) onto the matching
/// dynamics-xpp:xpp-pattern-* skill names so get_form / get_form_extension
/// can hint the agent toward the authoring skill that covers the host
/// pattern. Skills are easiest to load at the *response* moment — once
/// the agent has the object in hand — so we attach the hint here.
/// </summary>
internal static class FormPatternHints
{
    private static readonly Dictionary<string, string> PatternSkill = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SimpleListDetails"]      = "dynamics-xpp:xpp-pattern-simple-list-details",
        ["SimpleList"]             = "dynamics-xpp:xpp-pattern-simple-list",
        ["ListPage"]               = "dynamics-xpp:xpp-pattern-list-page",
        ["DetailsFormMaster"]      = "dynamics-xpp:xpp-pattern-details-master",
        ["DetailsFormTransaction"] = "dynamics-xpp:xpp-pattern-details-transaction",
        ["TableOfContents"]        = "dynamics-xpp:xpp-pattern-table-of-contents",
        ["Task"]                   = "dynamics-xpp:xpp-pattern-task",
        ["TaskDoubleInDocument"]   = "dynamics-xpp:xpp-pattern-task-parent-child",
        ["TaskWithParentChild"]    = "dynamics-xpp:xpp-pattern-task-parent-child",
        ["Workspace"]              = "dynamics-xpp:xpp-pattern-workspace-operational",
        ["WorkspaceOperational"]   = "dynamics-xpp:xpp-pattern-workspace-operational",
        ["Wizard"]                 = "dynamics-xpp:xpp-pattern-wizard",
    };

    /// <summary>For a form's typed domain JSON, returns the skills worth loading.</summary>
    public static string[] ForForm(JsonElement domain)
    {
        var hints = new List<string>();
        // Look up via camelCase first (DomainJson's emitted shape), then
        // fall through to PascalCase for any caller still serialising the
        // older convention.
        if ((TryReadString(domain, "design", "pattern", out var p)
                || TryReadString(domain, "Design", "Pattern", out p))
            && PatternSkill.TryGetValue(p, out var skill))
            hints.Add(skill);
        // Form-subpatterns covers the slot-by-slot conventions inside
        // whatever top-level pattern was matched; it's almost always
        // relevant alongside the per-pattern skill.
        hints.Add("dynamics-xpp:xpp-form-subpatterns");
        hints.Add("dynamics-xpp:xpp-form");
        return hints.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// For a form extension's typed domain JSON. We don't have the host
    /// form's pattern at hand here, so we emit the always-relevant trio
    /// for extension work: extension conventions, sub-patterns (which
    /// drive where controls go), and the form skill for control shapes.
    /// </summary>
    public static string[] ForFormExtension(JsonElement domain)
    {
        _ = domain;
        return new[]
        {
            "dynamics-xpp:xpp-extension",
            "dynamics-xpp:xpp-form-subpatterns",
            "dynamics-xpp:xpp-form",
        };
    }

    private static bool TryReadString(JsonElement root, string a, string b, out string value)
    {
        value = "";
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty(a, out var first) || first.ValueKind != JsonValueKind.Object) return false;
        if (!first.TryGetProperty(b, out var second) || second.ValueKind != JsonValueKind.String) return false;
        var s = second.GetString();
        if (string.IsNullOrWhiteSpace(s)) return false;
        value = s;
        return true;
    }
}
