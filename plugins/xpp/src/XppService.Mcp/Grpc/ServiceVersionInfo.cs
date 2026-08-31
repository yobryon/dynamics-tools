using System.Reflection;

namespace Xpp.Service.Mcp.Grpc;

/// <summary>
/// This MCP build's own plugin version and the comparison used for the
/// newest-wins service negotiation. The version is the AssemblyVersion, which
/// Directory.Build.props stamps from .claude-plugin/plugin.json — so the MCP and
/// the service (built from the same plugin folder) agree on it, and it matches
/// the installed plugin version.
/// </summary>
internal static class ServiceVersionInfo
{
    /// <summary>This MCP build's plugin semver, e.g. "0.1.0".</summary>
    public static string PluginVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Compare two plugin-version strings. Returns &lt;0 when <paramref name="a"/>
    /// is older than <paramref name="b"/>, 0 when equal, &gt;0 when newer. An
    /// empty or unparseable version sorts as the oldest possible (a
    /// pre-versioning service reports an empty plugin_version).
    /// </summary>
    public static int Compare(string? a, string? b) => Parse(a).CompareTo(Parse(b));

    private static Version Parse(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
}
