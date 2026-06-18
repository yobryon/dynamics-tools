using System.Text.Json;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Write-time sanity check for the silent-empty-column trap: a value-bearing
/// leaf control inside a Grid that has neither <c>dataField</c> nor
/// <c>dataMethod</c> has no value source, so it renders an empty cell at
/// runtime — and nothing else flags it (compile, BP, and patternConformance
/// are all green; a typed read just omits the unset binding). The only symptom
/// is a blank column you find by clicking the form.
///
/// Emits a warn-only <c>sideEffectWarning</c> per such column — never blocks
/// the write. Deliberately tight to avoid crying wolf: only the leaf kinds that
/// render a per-row value, only when BOTH bindings are absent, and the message
/// hedges for the legitimate case (a column populated in X++ at runtime).
/// </summary>
internal static class FormBindingCheck
{
    // Leaf control kinds that render a per-row value and therefore need a
    // dataField (bound) or dataMethod (display/edit method) to show anything.
    // Containers (Group/Tab/Grid/...), buttons, and static text are excluded.
    private static readonly HashSet<string> ValueBearingKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "String", "Integer", "Int64", "Real", "RealEdit", "Date", "Time",
        "UtcDateTime", "DateTime", "CheckBox", "ComboBox", "Image",
    };

    /// <summary>Walk a form's domain JSON and return one warning per
    /// value-bearing Grid column missing both bindings. Empty list when the JSON
    /// has no design/controls (e.g. a patch that doesn't touch the design).</summary>
    public static List<string> UnboundGridColumns(string? domainJson)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(domainJson)) return warnings;
        try
        {
            using var doc = JsonDocument.Parse(domainJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return warnings;
            if (!doc.RootElement.TryGetProperty("design", out var design)
                || design.ValueKind != JsonValueKind.Object) return warnings;
            if (!design.TryGetProperty("controls", out var controls)
                || controls.ValueKind != JsonValueKind.Array) return warnings;
            foreach (var c in controls.EnumerateArray())
                Walk(c, inGrid: false, warnings);
        }
        catch (JsonException)
        {
            // Malformed JSON — the normal write path surfaces the real error;
            // don't manufacture a warning here.
        }
        return warnings;
    }

    private static void Walk(JsonElement control, bool inGrid, List<string> warnings)
    {
        if (control.ValueKind != JsonValueKind.Object) return;
        var kind = GetStr(control, "kind");

        if (inGrid && kind != null && ValueBearingKinds.Contains(kind)
            && string.IsNullOrEmpty(GetStr(control, "dataField"))
            && string.IsNullOrEmpty(GetStr(control, "dataMethod")))
        {
            var name = GetStr(control, "name") ?? "(unnamed)";
            warnings.Add(
                $"control '{name}' ({kind}) sits in a Grid with neither dataField nor dataMethod — "
                + "it has no value source and will render an empty column unless it's populated in code. "
                + "Set dataField (bound column) or dataMethod (display/edit method).");
        }

        // Once inside a Grid, every descendant column counts (a Grid column can
        // itself be a Group of value controls).
        var childInGrid = inGrid || string.Equals(kind, "Grid", StringComparison.OrdinalIgnoreCase);
        if (control.TryGetProperty("controls", out var kids) && kids.ValueKind == JsonValueKind.Array)
            foreach (var k in kids.EnumerateArray())
                Walk(k, childInGrid, warnings);
    }

    private static string? GetStr(JsonElement o, string key)
        => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
