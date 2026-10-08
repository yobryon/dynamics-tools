using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xpp.Service.Domain;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// A tools/call filter that refuses arguments the tool would otherwise
/// silently ignore.
///
/// Why: System.Text.Json drops unknown members by default, so a caller who
/// guessed <c>menuItemType</c> instead of <c>kind</c> (or <c>relation</c>
/// instead of <c>name</c> on a form link) got <c>created: true</c>, an empty
/// warning list, and an artifact built from the DEFAULT of the property they
/// thought they had set. The failure then surfaced at runtime in the browser.
/// A create tool that accepts a property name it does not understand is
/// indistinguishable, from the caller's side, from one that honored it.
///
/// What it checks, before the SDK binds anything:
///  1. every top-level argument name is a parameter of the tool method;
///  2. every argument whose parameter type is one of our domain records (or a
///     list of them) deserializes with unmapped members DISALLOWED, at any
///     nesting depth.
/// Keys starting with an underscore (<c>_doc</c> in the skill examples) are
/// stripped before the check so copied examples still work.
///
/// On a violation the result is an error-shaped tool result that names the
/// offending property, where it sits, and the valid property names of the
/// type -- the message the SDK's generic "An error occurred invoking" hides.
/// </summary>
internal static class StrictToolArguments
{
    private static readonly Assembly DomainAssembly = typeof(DomainJson).Assembly;
    private static readonly Dictionary<string, MethodInfo> Tools = DiscoverTools();
    private static readonly JsonSerializerOptions Strict = BuildStrictOptions();
    private static readonly Regex UnmappedRegex = new(
        @"property '(?<prop>[^']+)' could not be mapped to any \.NET member contained in type '(?<type>[^']+)'",
        RegexOptions.Compiled);

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (context, ct) =>
        {
            var problem = Validate(context.Params?.Name, context.Params?.Arguments);
            if (problem != null)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(problem) }],
                };
            }
            return await next(context, ct).ConfigureAwait(false);
        };

    /// <summary>Null when the arguments are acceptable; otherwise the error payload.</summary>
    internal static object? Validate(string? toolName, IDictionary<string, JsonElement>? args)
    {
        if (string.IsNullOrEmpty(toolName) || args == null || args.Count == 0) return null;
        if (!Tools.TryGetValue(toolName, out var method)) return null;

        var parameters = method.GetParameters()
            .Where(p => !IsInjected(p.ParameterType))
            .ToDictionary(p => p.Name!, p => p, StringComparer.OrdinalIgnoreCase);

        var unknown = args.Keys.Where(k => !k.StartsWith('_') && !parameters.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
        {
            return new
            {
                error = "unknown_argument",
                tool = toolName,
                unknownArguments = unknown,
                validArguments = parameters.Keys.ToArray(),
                message = $"'{toolName}' has no argument named {string.Join(", ", unknown.Select(u => $"'{u}'"))}. " +
                          "Nothing was written. Use the names listed in validArguments (re-read the tool schema with ToolSearch if unsure).",
            };
        }

        foreach (var (key, value) in args)
        {
            if (key.StartsWith('_') || !parameters.TryGetValue(key, out var p)) continue;
            if (!IsDomainShaped(p.ParameterType)) continue;
            if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;

            var cleaned = StripDocKeys(JsonNode.Parse(value.GetRawText()));
            try
            {
                JsonSerializer.Deserialize(cleaned?.ToJsonString() ?? "null", p.ParameterType, Strict);
            }
            catch (JsonException ex)
            {
                var m = UnmappedRegex.Match(ex.Message ?? string.Empty);
                if (m.Success)
                {
                    var typeName = m.Groups["type"].Value;
                    var shortType = typeName[(typeName.LastIndexOf('.') + 1)..];
                    return new
                    {
                        error = "unknown_property",
                        tool = toolName,
                        argument = key,
                        property = m.Groups["prop"].Value,
                        at = Rebase(ex.Path, key),
                        type = shortType,
                        validProperties = PropertyNames(typeName),
                        message = $"'{m.Groups["prop"].Value}' is not a property of {shortType} (at {Rebase(ex.Path, key)}). " +
                                  "Nothing was written: an unrecognised property used to be dropped silently and the value you meant to set fell back to its default. " +
                                  "Use one of validProperties.",
                    };
                }
                return new
                {
                    error = "invalid_argument",
                    tool = toolName,
                    argument = key,
                    at = Rebase(ex.Path, key),
                    message = $"argument '{key}' does not match the {p.ParameterType.Name} schema: {ex.Message}",
                };
            }
            catch (Exception)
            {
                // Validation must never block a call for a reason of its own
                // making; the SDK's binding will report anything real.
                return null;
            }
        }
        return null;
    }

    private static string Rebase(string? jsonPath, string argument)
        => string.IsNullOrEmpty(jsonPath) || jsonPath == "$" ? $"$.{argument}" : "$." + argument + jsonPath[1..];

    private static string[] PropertyNames(string clrTypeName)
    {
        var t = DomainAssembly.GetType(clrTypeName) ?? Type.GetType(clrTypeName);
        if (t == null) return Array.Empty<string>();
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(pr => pr.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .Select(pr => JsonNamingPolicy.CamelCase.ConvertName(pr.Name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    // Parameters the SDK injects rather than binds from the arguments.
    private static bool IsInjected(Type t)
        => t == typeof(CancellationToken)
           || t.Name is "IMcpServer" || t.Name.StartsWith("RequestContext", StringComparison.Ordinal)
           || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IProgress<>))
           || t.Name is "IServiceProvider";

    private static bool IsDomainShaped(Type t)
    {
        if (t.Assembly == DomainAssembly) return true;
        if (t.IsGenericType)
            return t.GetGenericArguments().Any(IsDomainShaped);
        return false;
    }

    private static JsonNode? StripDocKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(kv => kv.Key).Where(k => k.StartsWith('_')).ToList()) o.Remove(k);
                foreach (var kv in o) StripDocKeys(kv.Value);
                break;
            case JsonArray a:
                foreach (var item in a) StripDocKeys(item);
                break;
        }
        return node;
    }

    private static Dictionary<string, MethodInfo> DiscoverTools()
    {
        var map = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var type in typeof(StrictToolArguments).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null) continue;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;
                map[attr.Name ?? m.Name] = m;
            }
        }
        return map;
    }

    private static JsonSerializerOptions BuildStrictOptions()
    {
        var o = new JsonSerializerOptions(DomainJson.Options)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    ti =>
                    {
                        if (ti.Kind == JsonTypeInfoKind.Object && ti.Type.Assembly == DomainAssembly)
                            ti.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                    },
                },
            },
        };
        return o;
    }
}
