using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xpp.Service.Domain;

/// <summary>
/// Shared JSON serialization conventions for the domain layer.
/// camelCase properties, enums as strings, null fields omitted on
/// serialize, case-insensitive on deserialize so agents can be a
/// little forgiving.
/// </summary>
public static class DomainJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
