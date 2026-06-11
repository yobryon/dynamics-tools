using System.Text.Json;

namespace Xpp.Service.Domain;

/// <summary>
/// One dropped property from a typed Create/Patch round-trip.
/// </summary>
/// <param name="RequestPath">
/// Dotted/indexed JSON-pointer-style path to the property in the
/// caller's request (e.g. <c>fields[2].label</c>,
/// <c>design.controls[0].controls[1].name</c>).
/// </param>
/// <param name="RequestValue">
/// JSON-encoded leaf value the caller sent (so the agent can
/// confirm the drift wasn't a spurious-empty-string emission).
/// </param>
public readonly record struct DriftEntry(string RequestPath, string RequestValue);

/// <summary>
/// Generic drift detection for the typed Create/Patch round-trip.
/// Both halves of the diff are domain JSON: the caller's original
/// request, and the round-tripped JSON we recover by running the
/// on-disk XML back through the same mapper's <c>FromAotXml</c>.
///
/// Because both sides are the mapper's own canonical shape, no
/// per-mapper drift logic is needed. The diff is structural: any
/// leaf the caller set with a non-null, non-empty value that isn't
/// present (or is null) after round-trip is flagged.
///
/// What we deliberately DON'T flag:
///  - properties the caller set to null/missing (intentional omission)
///  - properties the caller set to empty arrays (no items to assert)
///  - value mismatches — only existence is checked. Enum/case
///    normalization is common and would produce noisy false positives.
/// </summary>
public static class DriftDetector
{
    /// <summary>
    /// Compare the caller's original domain JSON against the
    /// mapper's round-tripped domain JSON. Returns drift entries
    /// for every non-null leaf in <paramref name="originalJson"/>
    /// that's missing (or null) in <paramref name="roundTrippedJson"/>.
    /// Both arguments are JSON strings — typically the same UTF-8
    /// payload the mapper consumed and re-emitted.
    /// </summary>
    public static IReadOnlyList<DriftEntry> Detect(string originalJson, string roundTrippedJson)
    {
        if (string.IsNullOrWhiteSpace(originalJson) || string.IsNullOrWhiteSpace(roundTrippedJson))
            return Array.Empty<DriftEntry>();

        JsonElement original, roundTripped;
        try
        {
            using var origDoc = JsonDocument.Parse(originalJson);
            using var rtDoc = JsonDocument.Parse(roundTrippedJson);
            // Clone both elements so we can outlive the using-scope.
            original = origDoc.RootElement.Clone();
            roundTripped = rtDoc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Malformed JSON on either side — nothing we can compare.
            // Don't synthesize a fake "drift" entry; let the regular
            // bridge error path surface the real failure.
            return Array.Empty<DriftEntry>();
        }

        var drift = new List<DriftEntry>();
        Walk(original, roundTripped, "", drift);
        return drift;
    }

    private static void Walk(JsonElement original, JsonElement roundTripped, string path, List<DriftEntry> sink)
    {
        switch (original.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in original.EnumerateObject())
                {
                    var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";

                    // The `otherProperties` bag is a catch-all: a caller may park a
                    // key in it that the mapper actually models as a TYPED property
                    // (e.g. a control's HeightMode). On round-trip that value is
                    // emitted in its typed slot on the PARENT, not back in the bag —
                    // so a naive diff flags otherProperties.<Key> as dropped even
                    // though it was honored. Treat such a key as satisfied when the
                    // round-trip parent carries the same-named typed sibling.
                    if (string.Equals(prop.Name, "otherProperties", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        TryGetPropertyCaseInsensitive(roundTripped, prop.Name, out var rtBag);
                        foreach (var bagProp in prop.Value.EnumerateObject())
                        {
                            var bagPath = $"{childPath}.{bagProp.Name}";
                            if (rtBag.ValueKind == JsonValueKind.Object
                                && TryGetPropertyCaseInsensitive(rtBag, bagProp.Name, out var rtBagChild))
                                Walk(bagProp.Value, rtBagChild, bagPath, sink);          // still in the bag
                            else if (TryGetPropertyCaseInsensitive(roundTripped, bagProp.Name, out _))
                                { /* promoted to a typed sibling on the parent — honored, not a drop */ }
                            else
                                EmitIfMeaningful(bagProp.Value, bagPath, sink);          // genuinely dropped
                        }
                        continue;
                    }

                    if (TryGetPropertyCaseInsensitive(roundTripped, prop.Name, out var rtChild))
                    {
                        Walk(prop.Value, rtChild, childPath, sink);
                    }
                    else
                    {
                        // Property absent in round-trip. Only flag if the
                        // original side carries a meaningful value.
                        EmitIfMeaningful(prop.Value, childPath, sink);
                    }
                }
                break;

            case JsonValueKind.Array:
                if (roundTripped.ValueKind != JsonValueKind.Array)
                {
                    // Array on the request, scalar/null/object/missing on
                    // the round-trip side. Whole array effectively dropped.
                    EmitIfMeaningful(original, path, sink);
                    return;
                }
                // Element-wise walk. We compare positionally — mappers
                // preserve order for the collections we care about
                // (Controls, Fields, Methods, etc.). If the round-trip
                // is shorter than the request, the missing tail elements
                // get flagged as drops.
                var origLen = original.GetArrayLength();
                var rtLen = roundTripped.GetArrayLength();
                for (int i = 0; i < origLen; i++)
                {
                    var origItem = original[i];
                    var itemPath = $"{path}[{i}]";
                    if (i < rtLen)
                    {
                        Walk(origItem, roundTripped[i], itemPath, sink);
                    }
                    else
                    {
                        EmitIfMeaningful(origItem, itemPath, sink);
                    }
                }
                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                // Leaf-vs-leaf. We're not checking value equality (would
                // create noise on enum/case normalization). Only check
                // presence: if the round-trip is null/missing, flag.
                if (roundTripped.ValueKind == JsonValueKind.Null
                    || roundTripped.ValueKind == JsonValueKind.Undefined)
                {
                    sink.Add(new DriftEntry(path, LeafToString(original)));
                }
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                // Caller didn't actually assert anything — intentional
                // omission, no drift to report.
                break;
        }
    }

    /// <summary>
    /// Emit a drift entry only when the leaf has a meaningful value
    /// (non-null, non-empty string). Recursively descends into objects
    /// and arrays so a dropped sub-tree produces a per-leaf list of
    /// drops — agents can pinpoint exactly what didn't make it.
    /// </summary>
    private static void EmitIfMeaningful(JsonElement value, string path, List<DriftEntry> sink)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                {
                    var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                    EmitIfMeaningful(prop.Value, childPath, sink);
                }
                break;
            case JsonValueKind.Array:
                var len = value.GetArrayLength();
                for (int i = 0; i < len; i++)
                {
                    EmitIfMeaningful(value[i], $"{path}[{i}]", sink);
                }
                break;
            case JsonValueKind.String:
                var s = value.GetString();
                if (!string.IsNullOrEmpty(s))
                    sink.Add(new DriftEntry(path, JsonSerializer.Serialize(s)));
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                sink.Add(new DriftEntry(path, LeafToString(value)));
                break;
        }
    }

    private static string LeafToString(JsonElement leaf)
    {
        return leaf.ValueKind switch
        {
            JsonValueKind.String => JsonSerializer.Serialize(leaf.GetString()),
            JsonValueKind.Number => leaf.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => leaf.GetRawText(),
        };
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        // First try exact match (camelCase = mapper's emitted shape).
        if (obj.TryGetProperty(name, out value)) return true;
        // Fall back to case-insensitive — DomainJson uses CamelCase
        // by default but PropertyNameCaseInsensitive=true on deserialize,
        // so a caller-supplied PascalCase property still binds. We honor
        // the same forgiveness here so we don't false-positive flag a
        // case-difference as a drop.
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }
}
