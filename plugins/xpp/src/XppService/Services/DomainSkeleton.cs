using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xpp.Service.Services;

/// <summary>
/// Path-addressable navigation over an object's bridge-produced domain JSON.
///
/// The structural skeleton is derived purely from the JSON value SHAPE — NOT
/// from the typed domain records, which are an input-only, incomplete subset
/// (GetDomainObject is a raw passthrough of the bridge JSON; e.g. AxTable
/// emits `mappings`/`createdBy` that GetTableResponse never models). Driving
/// off the JSON is both more correct (sees everything the bridge emits) and
/// fully generic (zero per-type code, works for every AxType forever).
///
/// Classification by shape (see plugins/xpp/docs/path-addressable-navigation-design.md):
///   primitive / array-of-primitives        -> scalar leaf            (elided)
///   object, all values primitive           -> leaf property-group    (elided)
///   array of objects                        -> COLLECTION node
///   object with >=1 array/object child      -> structural SINGLETON node
///
/// Addressing: /&lt;singletonProp&gt; and /&lt;collectionProp&gt;/&lt;identity&gt;, where
/// identity = first present of name|dataField|field|mapField, else ordinal #n.
/// Depth: depth=0 = collection COUNTS only (the compact, always-bounded orient —
/// good even on a 1.5 MB form or a 200-field table); depth=1 = list members one
/// level; depth=2 = members + their sub-counts. Structural singletons (design /
/// sourceCode) are transparent to depth, so depth=0 still surfaces their counts
/// inline ("design has 3 controls"); depth only bounds the collection-member
/// expansion that actually explodes. The agent descends with a higher depth or
/// by atPath into the one subtree it cares about.
/// </summary>
internal static class DomainSkeleton
{
    private static readonly string[] IdentityKeys = { "name", "dataField", "field", "mapField" };
    private static readonly string[] DiscriminatorKeys =
        { "kind", "fieldType", "type", "relationshipType", "indexType" };
    private static readonly string[] BodyFields = { "source", "declaration" };
    // High-value scalars surfaced inline on an outline node (when present), so a
    // structural outline doubles as a recon read. Kept small: bindings + EDT +
    // related-table, not a general scalar dump.
    private static readonly string[] SummaryScalars =
        { "extendedDataType", "dataField", "dataSource", "relatedTable", "table" };

    private enum Shape { Scalar, ScalarList, LeafGroup, Collection, Singleton }

    private static Shape Classify(JsonNode? node) => node switch
    {
        JsonObject o => o.Any(kv => kv.Value is JsonObject or JsonArray) ? Shape.Singleton : Shape.LeafGroup,
        JsonArray a => a.Any(e => e is JsonObject) ? Shape.Collection : Shape.ScalarList,
        _ => Shape.Scalar,
    };

    private static string IdentityOf(JsonNode? elem, int ordinal)
    {
        if (elem is JsonObject o)
            foreach (var key in IdentityKeys)
                if (o.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
                    jv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    return s;
        return $"#{ordinal}";
    }

    private static string? DiscriminatorOf(JsonObject o)
    {
        foreach (var key in DiscriminatorKeys)
            if (o.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
                jv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                return s;
        return null;
    }

    private static string? StringProp(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is JsonValue jv &&
        jv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;

    /// <summary>First non-blank line of a method-like node's body, body elided.</summary>
    private static string? SignatureOf(JsonObject o)
    {
        foreach (var key in BodyFields)
        {
            var src = StringProp(o, key);
            if (src == null) continue;
            var line = src.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (!string.IsNullOrEmpty(line))
                return line!.Length > 100 ? line[..100] : line;
        }
        return null;
    }

    // ---- Resolve (zoom / patch target) -------------------------------------

    /// <summary>Navigate "/a/b/c" to its subtree. Returns null if the path
    /// does not resolve. Empty / "/" returns the root.</summary>
    public static JsonNode? Resolve(JsonNode root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return root;
        JsonNode? cur = root;
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (cur)
            {
                case JsonObject o when o.TryGetPropertyValue(seg, out var child):
                    cur = child;
                    break;
                case JsonArray arr:
                    cur = null;
                    for (var i = 0; i < arr.Count; i++)
                        if (IdentityOf(arr[i], i) == seg) { cur = arr[i]; break; }
                    if (cur == null) return null;
                    break;
                default:
                    return null;
            }
        }
        return cur;
    }

    // ---- Outline (orient) --------------------------------------------------

    /// <summary>Build the depth-bounded structural skeleton rooted at the
    /// given node. Returns a node tree: {path, name?, kind?, sig?, children?,
    /// childCounts?}.</summary>
    public static JsonObject BuildOutline(JsonNode root, string rootPath, string? rootName, int depth)
    {
        if (depth < 0) depth = 0;
        // depth=0 = collection counts only (the compact, always-bounded orient).
        // depth=1 = list members one level. Transparent singletons mean depth=0
        // still surfaces design/sourceCode counts inline. EXCEPTION: an atPath
        // rooted directly at a collection is an explicit "show me these members",
        // so list them even at depth 0.
        if (root is JsonArray && depth == 0) depth = 1;
        return BuildNode(root, NormalizeRoot(rootPath), rootName, depth);
    }

    private static string NormalizeRoot(string? p) =>
        string.IsNullOrWhiteSpace(p) || p == "/" ? "" : p!.TrimEnd('/');

    private static JsonObject BuildNode(JsonNode? value, string path, string? name, int remainingDepth)
    {
        var node = new JsonObject { ["path"] = path.Length == 0 ? "/" : path };
        if (!string.IsNullOrEmpty(name)) node["name"] = name;

        // atPath rooted directly at a collection (e.g. "/relations"): list its
        // members as children rather than returning a bare node.
        if (value is JsonArray rootArr)
        {
            if (rootArr.Any(e => e is JsonObject))
            {
                if (remainingDepth >= 1)
                {
                    var kids = new JsonArray();
                    for (var i = 0; i < rootArr.Count; i++)
                    {
                        var ident = IdentityOf(rootArr[i], i);
                        kids.Add(BuildNode(rootArr[i], path + "/" + ident, ident, remainingDepth - 1));
                    }
                    node["children"] = kids;
                }
                else node["childCounts"] = new JsonObject { ["items"] = rootArr.Count };
            }
            return node;
        }

        if (value is not JsonObject obj)
            return node;  // a scalar landed here (e.g. atPath into a leaf) — just the path/name

        var disc = DiscriminatorOf(obj);
        if (disc != null) node["kind"] = disc;
        var sig = SignatureOf(obj);
        if (sig != null) node["sig"] = sig;

        // Surface a few high-value identity/binding scalars inline so an outline
        // doubles as a recon read — e.g. a table field shows its EDT, a form
        // control shows what it's bound to — without a per-member zoom. Still
        // compact: only these allowlisted keys, only when present.
        foreach (var sk in SummaryScalars)
        {
            var v = StringProp(obj, sk);
            if (v != null) node[sk] = v;
        }

        JsonArray? children = null;
        JsonObject? childCounts = null;

        foreach (var (key, child) in obj)
        {
            var segPath = path + "/" + key;
            switch (Classify(child))
            {
                case Shape.Singleton:
                    // A structural singleton (design / sourceCode / formControlExtension)
                    // is TRANSPARENT to depth — it costs no level, but its own
                    // collections respect remainingDepth. So even depth=0 shows the
                    // useful "design has 3 controls / sourceCode has 175 methods"
                    // counts inline, while depth bounds only the collection-member
                    // expansion that actually explodes on a 1.5 MB form.
                    (children ??= new JsonArray())
                        .Add(BuildNode(child, segPath, key, remainingDepth));
                    break;

                case Shape.Collection:
                    var arr = (JsonArray)child!;
                    if (remainingDepth >= 1)
                    {
                        for (var i = 0; i < arr.Count; i++)
                        {
                            var ident = IdentityOf(arr[i], i);
                            (children ??= new JsonArray())
                                .Add(BuildNode(arr[i], segPath + "/" + ident, ident, remainingDepth - 1));
                        }
                    }
                    else
                    {
                        (childCounts ??= new JsonObject())[key] = arr.Count;
                    }
                    break;

                case Shape.ScalarList:
                    (childCounts ??= new JsonObject())[key] = ((JsonArray)child!).Count;
                    break;

                // Scalar / LeafGroup -> elided.
            }
        }

        if (children != null) node["children"] = children;
        if (childCounts != null) node["childCounts"] = childCounts;
        return node;
    }

    // ---- Splice (edit) -----------------------------------------------------

    /// <summary>Outcome of an in-place splice: the top-level property key that
    /// changed (used to build the minimal branch patch), the edited subtree
    /// itself (for the dry-run preview — a live reference into the mutated tree,
    /// NOT a re-resolve by path, so it survives an op that changes a member's
    /// identity key, e.g. a rename via merge/set), and the value keys the edit
    /// INTRODUCED that the target node didn't previously have (so the caller can
    /// suppress a false "mapper gap" drift when one of those is a property the
    /// node type simply doesn't carry).</summary>
    public sealed record SpliceResult(string TopSegment, JsonNode? Preview, IReadOnlyList<string> AddedKeys);

    public sealed class SpliceException : Exception
    {
        public SpliceException(string message) : base(message) { }
    }

    /// <summary>
    /// Apply a path-scoped op to <paramref name="root"/> in place.
    ///   set    — replace the node at atPath with value.
    ///   merge  — overlay value's top-level properties onto the object at atPath.
    ///   append — add value (an object) to the collection at atPath (auto-creates
    ///            the collection if it's currently empty/absent on disk).
    ///   remove — delete the node at atPath.
    /// Throws <see cref="SpliceException"/> with a caller-facing reason (incl. a
    /// valid-keys hint on a bad path). Returns the top-level segment that changed
    /// plus the edited subtree for preview.
    /// </summary>
    public static SpliceResult ApplyOp(JsonNode root, string atPath, string op, JsonNode? value)
    {
        var segs = (atPath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0)
            throw new SpliceException("at_path must point at a node (e.g. '/design/controls/Grid'), not the root.");
        op = (op ?? "").Trim().ToLowerInvariant();

        if (op == "append")
        {
            var target = Resolve(root, atPath);
            if (target == null)
            {
                // The collection is empty/absent — an empty array is omitted on
                // disk, so its path doesn't resolve. Append to a known collection
                // shouldn't depend on it already having members: materialize the
                // array at the path (resolve the parent + create the key).
                var parentForArr = segs.Length == 1 ? root : Resolve(root, "/" + string.Join('/', segs[..^1]));
                if (parentForArr is JsonObject ppo)
                {
                    var fresh = new JsonArray();
                    ppo[segs[^1]] = fresh;
                    target = fresh;
                }
                else
                    throw new SpliceException($"at_path '{atPath}' does not resolve. {ResolveHint(root, atPath)}");
            }
            if (target is not JsonArray arr)
                throw new SpliceException($"append requires at_path to be a collection; '{atPath}' is a {Kind(target)}.");
            if (value is not JsonObject)
                throw new SpliceException("append requires value to be a JSON object (a new collection member).");
            arr.Add(value.DeepClone());
            return new SpliceResult(segs[0], arr, Array.Empty<string>());  // preview the collection incl. the new member
        }

        // set / merge / remove operate on the node via its parent + last segment.
        var parentPath = "/" + string.Join('/', segs[..^1]);
        var last = segs[^1];
        var parent = Resolve(root, parentPath)
            ?? throw new SpliceException($"parent of '{atPath}' does not resolve. {ResolveHint(root, parentPath)}");

        if (op is "set" or "merge" && value == null)
            throw new SpliceException($"{op} requires a value.");

        JsonNode? preview;
        IReadOnlyList<string> addedKeys = Array.Empty<string>();
        switch (parent)
        {
            case JsonObject po:
                if (op == "remove")
                {
                    if (!po.Remove(last))
                        throw new SpliceException($"property '{last}' not found at '{parentPath}'. {ResolveHint(root, atPath)}");
                    preview = po;
                }
                else if (op == "set") { addedKeys = AddedKeysOf(po[last] as JsonObject, value!); po[last] = value!.DeepClone(); preview = po[last]; }
                else if (op == "merge") { var t = GetObjOrThrow(po[last], atPath); addedKeys = AddedKeysOf(t, value!); MergeInto(t, value!); preview = t; }
                else throw new SpliceException($"unknown op '{op}' (set|merge|append|remove).");
                break;

            case JsonArray pa:
                var idx = IndexOfMember(pa, last);
                if (idx < 0)
                    throw new SpliceException($"no member '{last}' in collection at '{parentPath}'. {ResolveHint(root, atPath)}");
                if (op == "remove") { pa.RemoveAt(idx); preview = pa; }
                else if (op == "set") { addedKeys = AddedKeysOf(pa[idx] as JsonObject, value!); pa[idx] = value!.DeepClone(); preview = pa[idx]; }
                else if (op == "merge") { var t = GetObjOrThrow(pa[idx], atPath); addedKeys = AddedKeysOf(t, value!); MergeInto(t, value!); preview = t; }
                else throw new SpliceException($"unknown op '{op}' (set|merge|append|remove).");
                break;

            default:
                throw new SpliceException($"cannot edit '{atPath}': parent is a {Kind(parent)}.");
        }
        return new SpliceResult(segs[0], preview, addedKeys);
    }

    /// <summary>Keys present in <paramref name="value"/> (an object) that the
    /// <paramref name="oldNode"/> did NOT already have — i.e. properties the edit
    /// is introducing. If one of these fails to round-trip, it's the node type
    /// not carrying that property, not a mapper regression.</summary>
    private static IReadOnlyList<string> AddedKeysOf(JsonObject? oldNode, JsonNode value)
    {
        if (value is not JsonObject vo) return Array.Empty<string>();
        var existing = oldNode is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(oldNode.Select(kv => kv.Key), StringComparer.Ordinal);
        return vo.Select(kv => kv.Key).Where(k => !existing.Contains(k)).ToArray();
    }

    /// <summary>
    /// Walk <paramref name="path"/> as far as it resolves and describe the first
    /// segment that fails, listing the valid child keys at the deepest node that
    /// DID resolve — turns a bare "does not resolve" into a self-correcting hint.
    /// </summary>
    public static string ResolveHint(JsonNode root, string? path)
    {
        JsonNode? cur = root;
        var resolved = "";
        foreach (var seg in (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonNode? next = null;
            if (cur is JsonObject o && o.TryGetPropertyValue(seg, out var c)) next = c;
            else if (cur is JsonArray a)
                for (var i = 0; i < a.Count; i++)
                    if (IdentityOf(a[i], i) == seg) { next = a[i]; break; }
            if (next == null)
            {
                var where = resolved.Length == 0 ? "/" : resolved;
                var keys = ChildKeys(cur);
                return keys.Length == 0
                    ? $"'{seg}' not found at '{where}' (no addressable children there)."
                    : $"'{seg}' not found at '{where}'. Valid keys here: {string.Join(", ", keys)}.";
            }
            cur = next;
            resolved += "/" + seg;
        }
        return "path resolved.";
    }

    /// <summary>Addressable child keys of a node: property names for an object,
    /// member identities for a collection (capped). Used for resolve hints.</summary>
    private static string[] ChildKeys(JsonNode? node) => node switch
    {
        JsonObject o => o.Select(kv => kv.Key).ToArray(),
        JsonArray a => a.Select((e, i) => IdentityOf(e, i)).Take(30).ToArray(),
        _ => Array.Empty<string>(),
    };

    private static JsonObject GetObjOrThrow(JsonNode? node, string atPath) =>
        node as JsonObject ?? throw new SpliceException($"merge requires the node at '{atPath}' to be an object.");

    /// <summary>Shallow overlay: each top-level property of <paramref name="value"/>
    /// replaces the same property on <paramref name="target"/>. To change something
    /// nested, target that deeper path instead.</summary>
    private static void MergeInto(JsonObject target, JsonNode value)
    {
        if (value is not JsonObject vo)
            throw new SpliceException("merge requires value to be a JSON object.");
        foreach (var (k, v) in vo)
            target[k] = v?.DeepClone();
    }

    private static int IndexOfMember(JsonArray arr, string identity)
    {
        for (var i = 0; i < arr.Count; i++)
            if (IdentityOf(arr[i], i) == identity) return i;
        return -1;
    }

    private static string Kind(JsonNode? n) => n switch
    {
        JsonArray => "collection", JsonObject => "object", null => "absent", _ => "scalar",
    };

    // ---- Find (locate) -----------------------------------------------------

    public sealed record FindFilter(string? Query, string? Kind, string? DataSource, string? DataField);

    /// <summary>Walk every structural node, return matches as a JSON array of
    /// { path, kind?, name?, dataSource?, dataField?, caption? }.</summary>
    public static JsonArray Find(JsonNode root, FindFilter filter)
    {
        var hits = new JsonArray();
        Walk(root, "", filter, hits);
        return hits;
    }

    private static void Walk(JsonNode? value, string path, FindFilter filter, JsonArray hits)
    {
        if (value is not JsonObject obj) return;
        foreach (var (key, child) in obj)
        {
            var segPath = path + "/" + key;
            switch (Classify(child))
            {
                case Shape.Singleton:
                    Walk(child, segPath, filter, hits);
                    break;
                case Shape.Collection:
                    var arr = (JsonArray)child!;
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var ident = IdentityOf(arr[i], i);
                        var epath = segPath + "/" + ident;
                        if (arr[i] is JsonObject eo && Matches(eo, ident, filter))
                            hits.Add(MatchEntry(eo, epath, ident));
                        Walk(arr[i], epath, filter, hits);
                    }
                    break;
            }
        }
    }

    private static bool Matches(JsonObject o, string identity, FindFilter f)
    {
        if (!string.IsNullOrEmpty(f.Query) &&
            identity.IndexOf(f.Query!, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        if (!string.IsNullOrEmpty(f.Kind) &&
            !string.Equals(DiscriminatorOf(o), f.Kind, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(f.DataSource) &&
            !string.Equals(StringProp(o, "dataSource"), f.DataSource, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(f.DataField) &&
            !string.Equals(StringProp(o, "dataField"), f.DataField, StringComparison.OrdinalIgnoreCase))
            return false;
        // A find with no criteria at all matches nothing (avoid dumping the tree).
        return !(string.IsNullOrEmpty(f.Query) && string.IsNullOrEmpty(f.Kind) &&
                 string.IsNullOrEmpty(f.DataSource) && string.IsNullOrEmpty(f.DataField));
    }

    private static JsonObject MatchEntry(JsonObject o, string path, string identity)
    {
        var e = new JsonObject { ["path"] = path };
        var disc = DiscriminatorOf(o);
        if (disc != null) e["kind"] = disc;
        // Only surface `name` when the node actually HAS one. Synthesizing
        // name=identity for a nameless node (e.g. a FormDataSourceField, keyed
        // by dataField) misleads the caller into sending `name` back on a patch
        // — a property that node type doesn't carry. The path already conveys
        // identity; dataField is surfaced below.
        var nm = StringProp(o, "name");
        if (nm != null) e["name"] = nm;
        foreach (var key in new[] { "dataSource", "dataField", "caption", "label", "table" })
        {
            var v = StringProp(o, key);
            if (v != null) e[key] = v;
        }
        return e;
    }
}
