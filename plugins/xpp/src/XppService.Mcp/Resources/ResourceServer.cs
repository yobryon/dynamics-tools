using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Xpp.Service.Mcp.Resources;

/// <summary>
/// MCP resources exposed alongside the tools. Currently a single URI family:
///
///   xpp://schema/{type}      - authoritative XSDs for AOT object types
///
/// Instructive / authoring guidance lives in the dynamics-xpp Claude Code
/// plugin's skill fleet (plugins/xpp/skills/*), not here. Skills are the
/// single source of truth for "how to author X"; MCP resources are reserved
/// for live, structural data (the XSDs today; future reflection-derived
/// metadata sidecars next).
///
/// Content is loaded from embedded resources so a published single-file
/// build still carries everything it needs without a sidecar folder.
/// </summary>
[McpServerResourceType]
public sealed class ResourceServer
{
    private static readonly Assembly Self = typeof(ResourceServer).Assembly;

    // Embedded resource names follow MSBuild's default convention:
    //   <RootNamespace>.<RelativePath with / => .>
    // e.g. Resources/Schemas/AxClass.xsd -> Xpp.Service.Mcp.Resources.Schemas.AxClass.xsd
    private const string SchemaPrefix = "Xpp.Service.Mcp.Resources.Schemas.";

    [McpServerResource(
        UriTemplate = "xpp://schema/{type}",
        Name = "AOT type XSD",
        Title = "Authoritative XML schema for an AOT object type",
        MimeType = "application/xml"),
     Description(
        "Returns the XSD that defines the valid XML shape for an AOT type " +
        "(AxClass, AxTable, AxForm, AxEdt, AxEnum, AxLabelFile, and the " +
        "matching *Extension variants). Use this before constructing XML " +
        "for a create/update call - it lists every legal element and " +
        "property name. Extracted from the official VS2022 D365 extension.")]
    public string GetSchema(
        [Description("AOT type name, e.g. 'AxClass', 'AxTable', 'AxTableExtension'.")] string type)
    {
        var resourceName = SchemaPrefix + Normalize(type) + ".xsd";
        return ReadEmbeddedOrThrow(resourceName,
            $"No schema known for type '{type}'. Known: AxClass, AxTable, AxTableExtension, " +
            "AxForm, AxFormExtension, AxEdt, AxEdtExtension, AxEnum, AxEnumExtension, AxLabelFile.");
    }

    private static string ReadEmbeddedOrThrow(string resourceName, string notFoundMessage)
    {
        using var stream = Self.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException(notFoundMessage);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Strip prefixes/casing so callers can pass 'class', 'AxClass', 'AXCLASS'
    // and land on the same resource.
    private static string Normalize(string type)
    {
        var t = type.Trim();
        if (t.StartsWith("Ax", StringComparison.OrdinalIgnoreCase))
            return "Ax" + char.ToUpperInvariant(t[2]) + t[3..];
        // Treat short slugs (class, table, ...) as the AOT type without prefix.
        return char.ToUpperInvariant(t[0]) + t[1..];
    }
}
