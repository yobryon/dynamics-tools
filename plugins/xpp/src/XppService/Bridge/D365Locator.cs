using System.Xml.Linq;

namespace Xpp.Service.Bridge;

/// <summary>
/// Finds the D365 PackagesLocalDirectory when it isn't configured.
///
/// The metadata store is not always on J:. An LCS-deployed Tier 1 VM puts it on
/// whichever drive the deployment chose, so a hardcoded drive letter works on
/// exactly one machine — which is precisely how this used to break for anyone
/// whose box had it on K:.
///
/// Normally <c>dt setup</c> discovers the path (tools/D365Discovery.ps1) and
/// records it in <c>%LOCALAPPDATA%\dynamics-xpp\config.json</c>, which the
/// service overlays. This is the safety net for when that hasn't happened —
/// the config was hand-edited, the file was lost, or the service was started
/// without setup ever running. Keep the ladder here in step with the
/// PowerShell one.
///
/// Every candidate is validated before it is accepted, so a stale entry in any
/// source falls through to the next rung rather than poisoning the result.
/// </summary>
public static class D365Locator
{
    /// <summary>
    /// The assembly the bridge must load. Its presence is what makes a
    /// directory a usable packages directory, so we test for the real
    /// requirement rather than merely "the folder exists".
    /// </summary>
    private const string ProbeAssembly = @"bin\Microsoft.Dynamics.AX.Metadata.dll";

    public static bool IsPackagesDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return Directory.Exists(path) && File.Exists(Path.Combine(path, ProbeAssembly));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve the packages directory, preferring <paramref name="configured"/>
    /// when it validates. Returns null when nothing usable is found.
    /// <paramref name="trace"/> records each rung tried, for the startup log.
    /// </summary>
    public static string? Resolve(string? configured, out IReadOnlyList<string> trace)
    {
        var log = new List<string>();
        trace = log;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (IsPackagesDirectory(configured))
            {
                log.Add($"configured -> {configured} [OK]");
                return configured;
            }
            log.Add($"configured -> {configured} [rejected: no {ProbeAssembly}]");
        }

        var devConfig = ReadDevConfig();

        // 1. The AOS's own answer to "where is my metadata".
        foreach (var webConfig in WebConfigCandidates(devConfig.WebRoot))
        {
            var candidate = ReadMetadataDirectory(webConfig);
            if (candidate == null) continue;
            if (IsPackagesDirectory(candidate))
            {
                log.Add($"web.config ({webConfig}) -> {candidate} [OK]");
                return candidate;
            }
            log.Add($"web.config ({webConfig}) -> {candidate} [rejected]");
        }

        // 2. The VS dev tooling's own config.
        if (IsPackagesDirectory(devConfig.PackagesFromHostConfig))
        {
            log.Add($"DynamicsDevConfig ApplicationHostConfigFile -> {devConfig.PackagesFromHostConfig} [OK]");
            return devConfig.PackagesFromHostConfig;
        }
        if (devConfig.WebRoot != null)
        {
            var sibling = SafeCombine(Path.GetDirectoryName(devConfig.WebRoot), "PackagesLocalDirectory");
            if (IsPackagesDirectory(sibling))
            {
                log.Add($"DynamicsDevConfig WebRoleDeploymentFolder sibling -> {sibling} [OK]");
                return sibling;
            }
        }

        // 3. Last resort: look on every fixed drive.
        foreach (var root in FixedDriveRoots())
        {
            var candidate = SafeCombine(root, @"AosService\PackagesLocalDirectory");
            if (IsPackagesDirectory(candidate))
            {
                log.Add($"drive scan -> {candidate} [OK]");
                return candidate;
            }
        }

        log.Add("no PackagesLocalDirectory found on this machine");
        return null;
    }

    private sealed record DevConfig(string? PackagesFromHostConfig, string? WebRoot);

    /// <summary>
    /// Parse DynamicsDevConfig.xml. ApplicationHostConfigFile points at
    /// &lt;packages&gt;\bin\applicationHost.config, so two levels up is the
    /// packages directory; WebRoleDeploymentFolder gives the AosService
    /// webroot, whose sibling is the packages directory.
    /// </summary>
    private static DevConfig ReadDevConfig()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"Documents\Visual Studio Dynamics 365\DynamicsDevConfig.xml");
            if (!File.Exists(path)) return new DevConfig(null, null);

            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null) return new DevConfig(null, null);

            // The file carries a default namespace; match on local names so we
            // don't have to hardcode (and track) the schema URI.
            string? Value(string name) => root.Elements()
                .FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

            string? packages = null;
            var hostConfig = Value("ApplicationHostConfigFile");
            if (!string.IsNullOrWhiteSpace(hostConfig))
            {
                var binDir = Path.GetDirectoryName(hostConfig);
                if (!string.IsNullOrEmpty(binDir)) packages = Path.GetDirectoryName(binDir);
            }

            var webRoot = Value("WebRoleDeploymentFolder");
            return new DevConfig(packages, string.IsNullOrWhiteSpace(webRoot) ? null : webRoot);
        }
        catch
        {
            return new DevConfig(null, null);
        }
    }

    /// <summary>
    /// Where an AOS web.config might live: whatever IIS says it is hosting,
    /// then the dev config's webroot, then the conventional location on each
    /// fixed drive.
    /// </summary>
    private static IEnumerable<string> WebConfigCandidates(string? devConfigWebRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in IisSitePaths())
        {
            var wc = SafeCombine(p, "web.config");
            if (wc != null && seen.Add(wc)) yield return wc;
        }

        if (devConfigWebRoot != null)
        {
            var wc = SafeCombine(devConfigWebRoot, "web.config");
            if (wc != null && seen.Add(wc)) yield return wc;
        }

        foreach (var root in FixedDriveRoots())
        {
            var wc = SafeCombine(root, @"AosService\webroot\web.config");
            if (wc != null && seen.Add(wc)) yield return wc;
        }
    }

    private static IEnumerable<string> IisSitePaths()
    {
        var results = new List<string>();
        try
        {
            var iis = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"system32\inetsrv\config\applicationHost.config");
            if (!File.Exists(iis)) return results;

            var doc = XDocument.Load(iis);
            foreach (var vdir in doc.Descendants("virtualDirectory"))
            {
                var physical = vdir.Attribute("physicalPath")?.Value;
                if (!string.IsNullOrWhiteSpace(physical)) results.Add(physical);
            }
        }
        catch
        {
            // Reading IIS config can fail on permissions. Not fatal — the
            // other rungs still apply.
        }
        return results;
    }

    /// <summary>Read Aos.MetadataDirectory (or an equivalent) from an AOS web.config.</summary>
    private static string? ReadMetadataDirectory(string webConfigPath)
    {
        try
        {
            if (!File.Exists(webConfigPath)) return null;
            var doc = XDocument.Load(webConfigPath);
            var adds = doc.Root?
                .Element("appSettings")?
                .Elements("add")
                .ToList();
            if (adds == null) return null;

            foreach (var key in new[] { "Aos.MetadataDirectory", "Aos.PackageDirectory", "Common.BinDir" })
            {
                var value = adds.FirstOrDefault(a =>
                    string.Equals(a.Attribute("key")?.Value, key, StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("value")?.Value;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch
        {
            // Unreadable or malformed: fall through to the next candidate.
        }
        return null;
    }

    private static IEnumerable<string> FixedDriveRoots()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var d in drives)
        {
            bool usable;
            try { usable = d.DriveType == DriveType.Fixed && d.IsReady; }
            catch { usable = false; }
            if (usable) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>Path.Combine throws on invalid characters; a bad candidate should just not match.</summary>
    private static string? SafeCombine(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return null;
        try { return Path.Combine(a, b); }
        catch { return null; }
    }
}
