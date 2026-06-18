using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain;

/// <summary>
/// Deserializes a JSON array whose elements are EITHER bare strings ("Name")
/// OR single-value wrapper objects ({"dataField":"Name"} / {"field":...} /
/// {"name":...}), normalizing every element to its string. Serializes back as
/// a plain string array.
///
/// Field-group members are bare field-name strings on the wire, but agents
/// naturally author them as objects — mirroring fields[]/relations[], which ARE
/// objects. Bound to a <c>List&lt;string&gt;</c> property, the object form fails
/// System.Text.Json arg-binding inside the MCP SDK and surfaces as the
/// contentless "An error occurred invoking '&lt;tool&gt;'." envelope (a pre-method
/// bind failure the per-tool catch can't reach). Normalizing here makes the
/// object form just work, and crucially keeps the value a plain string
/// downstream — so the service/bridge round-trip stays string-symmetric and no
/// spurious drift is produced.
/// </summary>
public sealed class LenientStringListConverter : JsonConverter<List<string>>
{
    private static readonly string[] WrapperKeys = { "dataField", "field", "name", "value" };

    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected an array of field names (strings, or {dataField:...} objects).");

        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    list.Add(reader.GetString()!);
                    break;
                case JsonTokenType.StartObject:
                    var val = ReadWrapper(ref reader);
                    if (!string.IsNullOrEmpty(val)) list.Add(val!);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        return list;
    }

    // reader positioned at StartObject; consume through the matching EndObject,
    // returning the first wrapper-key string value found.
    private static string? ReadWrapper(ref Utf8JsonReader reader)
    {
        string? found = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            reader.Read(); // advance to the value
            if (found == null
                && reader.TokenType == JsonTokenType.String
                && Array.Exists(WrapperKeys, k => string.Equals(k, prop, StringComparison.OrdinalIgnoreCase)))
            {
                found = reader.GetString();
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip(); // skip nested container values we don't care about
            }
            // scalar non-match values are already consumed by the Read() above
        }
        return found;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}
