using System.Text;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Centralises how typed Create/Patch tools surface drift entries
/// returned by the service. Three responsibilities:
///
///  1. Merge drift entries into the existing sideEffectWarnings
///     string list (so a single skim catches both rnrproj / SCM
///     warnings AND mapper drops).
///  2. Expose drift as a structured array on the response (so a
///     scripting consumer can parse without regex'ing the
///     human-readable strings).
///  3. Implicitly write a feedback note to the plugin's drop path
///     for every drift event, so the maintainer accumulates a
///     concrete record of mapper gaps even when the agent doesn't
///     write a manual feedback artifact.
///
/// Best-effort throughout. A failure to write the feedback file
/// (filesystem busy, env var unset, etc.) must never escalate —
/// drift detection is a soft signal layered on top of a successful
/// write.
/// </summary>
internal static class DriftReporting
{
    /// <summary>
    /// Combine pre-existing side-effect warnings (rnrproj / changeset /
    /// SCM) with human-readable drift lines. Order: side-effect
    /// warnings first (more immediately actionable), drift lines after.
    /// </summary>
    public static string[] MergedWarnings(IEnumerable<string> existing, IReadOnlyList<DriftWarning> drift)
    {
        var list = existing == null ? new List<string>() : existing.ToList();
        foreach (var d in drift)
        {
            list.Add($"drift: '{d.RequestPath}' (value={d.RequestValue}) was specified in the request but did not survive the mapper round-trip. File as a typed-mapper gap.");
        }
        return list.ToArray();
    }

    /// <summary>
    /// Structured-array projection of the drift entries for callers
    /// that want to consume without parsing strings.
    /// </summary>
    public static object[] AsStructuredArray(IReadOnlyList<DriftWarning> drift)
    {
        var arr = new object[drift.Count];
        for (int i = 0; i < drift.Count; i++)
            arr[i] = new { path = drift[i].RequestPath, value = drift[i].RequestValue };
        return arr;
    }

    /// <summary>
    /// If drift is non-empty, append a feedback note to the plugin's
    /// drop path (%LOCALAPPDATA%\dynamics-xpp\feedback\). One file per
    /// drift event so the maintainer can browse them by timestamp.
    /// Failures are swallowed — feedback drop is auxiliary.
    /// </summary>
    public static void TryWriteFeedbackAsync(
        string axType, string name, string operation, IReadOnlyList<DriftWarning> drift)
    {
        if (drift.Count == 0) return;
        try
        {
            var feedbackDir = ResolveFeedbackDir();
            if (feedbackDir == null) return;
            Directory.CreateDirectory(feedbackDir);

            var ts = DateTime.UtcNow;
            var stamp = ts.ToString("yyyy-MM-dd_HH-mm-ss");
            // Sanitise name for the filename — same convention agents use.
            var safeName = Sanitise(name);
            var safeType = Sanitise(axType);
            var path = Path.Combine(feedbackDir, $"feedback_{stamp}_mapper-drop_{safeType}_{safeName}.md");

            var body = FormatFeedbackBody(ts, axType, name, operation, drift);
            File.WriteAllText(path, body, Encoding.UTF8);
        }
        catch
        {
            // Swallow — feedback drop is not load-bearing.
        }
    }

    private static string FormatFeedbackBody(DateTime ts, string axType, string name, string operation, IReadOnlyList<DriftWarning> drift)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append($"date: {ts:yyyy-MM-ddTHH:mm:ssZ}\n");
        sb.Append($"topic: typed-mapper drop on {axType} {operation} for '{name}'\n");
        sb.Append("severity: moderate\n");
        sb.Append("auto_generated: true\n");
        sb.Append($"tools_touched: [xpp_{operation}_{LowerFirst(axType)}]\n");
        sb.Append("skills_loaded: []\n");
        sb.Append("---\n\n");
        sb.Append($"# Mapper drop on {axType} {operation} ('{name}')\n\n");
        sb.Append("The drift detector found request properties that did not survive the\n");
        sb.Append("mapper round-trip into on-disk XML. This file was written automatically\n");
        sb.Append($"by `{operation}` on `{axType}` for '{name}' so the maintainer accumulates a\n");
        sb.Append("concrete record of every drop without requiring the agent to articulate it.\n\n");
        sb.Append("## Dropped properties\n\n");
        foreach (var d in drift)
        {
            sb.Append($"- `{d.RequestPath}` (value: `{d.RequestValue}`)\n");
        }
        sb.Append("\n## Diagnostic notes\n\n");
        sb.Append("Same drift list was returned to the calling agent via `sideEffectWarnings` and\n");
        sb.Append("`drift` on the tool response. If the agent still produced a usable artifact,\n");
        sb.Append("that means the dropped properties were either not load-bearing for this case\n");
        sb.Append("or were applied manually elsewhere (raw `xpp_update_object`, etc.). Each\n");
        sb.Append("dropped property indicates an unaddressed gap in the typed mapper.\n");
        return sb.ToString();
    }

    private static string? ResolveFeedbackDir()
    {
        // Mirror what dynamics-xpp:feedback uses: %LOCALAPPDATA%\dynamics-xpp\feedback.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) return null;
        return Path.Combine(localAppData, "dynamics-xpp", "feedback");
    }

    private static string Sanitise(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Where(c => !invalid.Contains(c) && c != ' ').ToArray();
        var clean = new string(chars);
        return clean.Length == 0 ? "unknown" : clean;
    }

    private static string LowerFirst(string axType)
    {
        // "AxTableExtension" -> "tableextension". Best-effort hint at the
        // tool name in the feedback frontmatter; not load-bearing.
        if (string.IsNullOrEmpty(axType)) return "unknown";
        var trimmed = axType.StartsWith("Ax", StringComparison.OrdinalIgnoreCase) ? axType.Substring(2) : axType;
        return trimmed.ToLowerInvariant();
    }
}
