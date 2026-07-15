using System.Security.Cryptography;

namespace Xpp.Service.Indexing;

/// <summary>
/// Locates an indexed object's authoritative on-disk content file and hashes
/// it, so a startup reconcile can re-index only what actually changed since the
/// last pass — an MS platform update applied by LCS while we were down, a TFS
/// GET LATEST that pulled other developers' checkins, an ISV package refresh —
/// instead of trusting a coarse signal or forcing a full rebuild.
///
/// The on-disk layout is uniform across every AOT type:
///   &lt;PackagesLocalDirectory&gt;\&lt;Package&gt;\&lt;Model&gt;\&lt;AxType&gt;\&lt;Name&gt;.xml
/// Extensions are a single file named &lt;Base&gt;.&lt;Suffix&gt;.xml; label files,
/// resources, forms, etc. all follow the same &lt;AxType&gt;\&lt;Name&gt;.xml shape. The
/// parallel &lt;Package&gt;\XppMetadata\... tree is a derived signature sidecar we
/// never read for content, so it is irrelevant here.
///
/// Change signal is file mtime as a cheap gate — a real write always bumps it,
/// so it never under-triggers — confirmed by a content hash so a mere
/// re-extract with identical bytes doesn't force a needless re-read/re-embed.
/// mtime can over-trigger; the hash removes the over-triggers.
/// </summary>
internal static class DiskReconciler
{
    /// <summary>
    /// Build a case-insensitive model-name -> on-disk model-root map by matching
    /// each package's direct subdirectories against the known model set. A model
    /// belongs to exactly one package, so there are no name collisions; non-model
    /// dirs (bin, XppMetadata, Resources, ...) simply never match a model name.
    /// </summary>
    public static Dictionary<string, string> BuildModelRoots(string packagesDir, IEnumerable<string> knownModels)
    {
        var known = new HashSet<string>(knownModels, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(packagesDir) || !Directory.Exists(packagesDir)) return map;

        foreach (var package in Directory.EnumerateDirectories(packagesDir))
        {
            foreach (var modelDir in Directory.EnumerateDirectories(package))
            {
                var modelName = Path.GetFileName(modelDir);
                if (known.Contains(modelName) && !map.ContainsKey(modelName))
                {
                    map[modelName] = modelDir;
                }
            }
        }
        return map;
    }

    /// <summary>
    /// The authoritative content file for an object, or null if the model's root
    /// isn't on disk (e.g. a binary/runtime-only model). Existence isn't checked.
    /// </summary>
    public static string? ContentFilePath(
        IReadOnlyDictionary<string, string> modelRoots, string model, string axType, string name)
    {
        if (!modelRoots.TryGetValue(model, out var root)) return null;
        return Path.Combine(root, axType, name + ".xml");
    }

    /// <summary>SHA-256 of the file's bytes, hex-encoded — the same encoding the
    /// method/label source hashes use, so content_hash reads uniformly.</summary>
    public static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
