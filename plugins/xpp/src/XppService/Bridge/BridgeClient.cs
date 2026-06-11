using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xpp.Service.Bridge;

/// <summary>
/// Result of a typed metaclass Create/Patch: the canonical object name plus
/// the optional form-pattern conformance payload (raw JSON node, mapped to
/// the proto shape by the gRPC handler). Conformance is null for every type
/// except AxForm and for forms with no declared pattern.
/// </summary>
public sealed record BridgeDomainWriteResult(string Name, JsonNode? Conformance);

/// <summary>
/// Typed facade over BridgeProcess. Every bridge RPC gets a method here that
/// takes/returns concrete records, so callers (indexer, search, etc.) never
/// see JsonNode in their code paths.
///
/// Adding a new RPC follows the recipe:
///   1. Implement the handler in src/XppMetadataBridge/Handlers
///   2. Add a record to BridgeDtos.cs that mirrors its result shape
///   3. Add a Foo<Bar>Async method here that wraps InvokeAsync
///
/// Why we don't auto-generate this from a schema today: the bridge speaks
/// JSON-RPC with hand-shaped responses, not protobuf. A schema-first
/// approach for the bridge wire would mean dragging gRPC into a net48
/// child process, which is a known headache (Grpc.Core is deprecated and
/// Grpc.AspNetCore needs modern .NET). The hand-written facade is small
/// enough that the cost is much lower than the schema infrastructure.
/// </summary>
public sealed class BridgeClient
{
    private readonly BridgePool _pool;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Constructs the typed client over a worker pool. Every call acquires
    /// one worker round-robin from the pool. For pool-size 1 this is
    /// indistinguishable from the previous single-bridge shape.
    /// </summary>
    public BridgeClient(BridgePool pool)
    {
        _pool = pool;
    }

    public async Task<BridgePing> PingAsync(string echo, CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("ping", new JsonObject { ["echo"] = echo }, ct).ConfigureAwait(false);
        return Deserialize<BridgePing>(result);
    }

    public async Task<IReadOnlyList<string>> ListKnownTypesAsync(CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("listKnownTypes", null, ct).ConfigureAwait(false);
        var types = result?["types"]?.AsArray();
        if (types == null) return Array.Empty<string>();
        var list = new List<string>(types.Count);
        foreach (var t in types)
        {
            if (t is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
        }
        return list;
    }

    public async Task<IReadOnlyList<BridgeModel>> ListModelsAsync(CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("listModels", null, ct).ConfigureAwait(false);
        var models = result?["models"]?.AsArray();
        if (models == null) return Array.Empty<BridgeModel>();
        var list = new List<BridgeModel>(models.Count);
        foreach (var m in models)
        {
            // Some entries can be "error" placeholders (the bridge logs them
            // and includes them so the caller knows the model existed even
            // if we couldn't load its info). Skip those — they have no usable
            // properties for indexing.
            if (m?["error"] is not null) continue;
            var dto = m.Deserialize<BridgeModel>(JsonOpts);
            if (dto != null) list.Add(dto);
        }
        return list;
    }

    /// <summary>
    /// Enumerate (name, source) pairs for one (model, axType). The bridge
    /// returns parallel <c>names</c> and <c>sources</c> arrays; we zip them
    /// here. Source is "disk" or "runtime" — disk wins where both providers
    /// see the object, runtime fills the gap for binary-only modules.
    /// </summary>
    public async Task<IReadOnlyList<BridgeObjectListEntry>> ListObjectsAsync(string model, string axType, CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("listObjects",
            new JsonObject { ["model"] = model, ["axType"] = axType }, ct).ConfigureAwait(false);
        var names = result?["names"]?.AsArray();
        var sources = result?["sources"]?.AsArray();
        if (names == null) return Array.Empty<BridgeObjectListEntry>();
        var list = new List<BridgeObjectListEntry>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] is not JsonValue v || !v.TryGetValue<string>(out var s)) continue;
            string source = "disk";
            if (sources != null && i < sources.Count
                && sources[i] is JsonValue sv && sv.TryGetValue<string>(out var st))
            {
                source = st;
            }
            list.Add(new BridgeObjectListEntry(s, source));
        }
        return list;
    }

    public async Task<IReadOnlyList<BridgeMethodInfo>> GetObjectMethodsAsync(string model, string axType, string name, CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("getObjectMethods",
            new JsonObject { ["model"] = model, ["axType"] = axType, ["name"] = name }, ct).ConfigureAwait(false);
        var arr = result?["methods"]?.AsArray();
        if (arr == null) return Array.Empty<BridgeMethodInfo>();
        var list = new List<BridgeMethodInfo>(arr.Count);
        foreach (var node in arr)
        {
            var dto = node.Deserialize<BridgeMethodInfo>(JsonOpts);
            if (dto != null) list.Add(dto);
        }
        return list;
    }

    /// <summary>
    /// Indexer fast-path: load an object once and return both methods AND
    /// structural references in one round-trip. Halves the bridge-RPC count
    /// (and bridge-side provider.Read calls) during a full phase-2 walk.
    /// </summary>
    public async Task<BridgeObjectFull> GetObjectFullAsync(string model, string axType, string name, CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("getObjectFull",
            new JsonObject { ["model"] = model, ["axType"] = axType, ["name"] = name }, ct).ConfigureAwait(false);

        var methodsArr = result?["methods"]?.AsArray();
        var refsArr = result?["references"]?.AsArray();
        var fieldRefsArr = result?["fieldReferences"]?.AsArray();
        var labelRefsArr = result?["labelReferences"]?.AsArray();

        var methods = new List<BridgeMethodInfo>(methodsArr?.Count ?? 0);
        if (methodsArr != null)
        {
            foreach (var node in methodsArr)
            {
                var dto = node.Deserialize<BridgeMethodInfo>(JsonOpts);
                if (dto != null) methods.Add(dto);
            }
        }

        var refs = new List<BridgeReferenceEdge>(refsArr?.Count ?? 0);
        if (refsArr != null)
        {
            foreach (var node in refsArr)
            {
                var dto = node.Deserialize<BridgeReferenceEdge>(JsonOpts);
                if (dto != null) refs.Add(dto);
            }
        }

        var fieldRefs = new List<BridgeFieldReferenceEdge>(fieldRefsArr?.Count ?? 0);
        if (fieldRefsArr != null)
        {
            foreach (var node in fieldRefsArr)
            {
                var dto = node.Deserialize<BridgeFieldReferenceEdge>(JsonOpts);
                if (dto != null) fieldRefs.Add(dto);
            }
        }

        var labelRefs = new List<BridgeLabelReferenceEdge>(labelRefsArr?.Count ?? 0);
        if (labelRefsArr != null)
        {
            foreach (var node in labelRefsArr)
            {
                var dto = node.Deserialize<BridgeLabelReferenceEdge>(JsonOpts);
                if (dto != null) labelRefs.Add(dto);
            }
        }

        var source = result?["source"]?.GetValue<string>() ?? "disk";
        var binary = result?["binaryModule"]?.GetValue<bool>() ?? false;
        return new BridgeObjectFull(methods, refs, fieldRefs, labelRefs, source, binary);
    }

    /// <summary>
    /// Batched form of <see cref="GetObjectFullAsync"/>. Sends N requests
    /// over one pipe round-trip and returns one item per input request.
    /// Per-item failures arrive as items with a non-null Error rather
    /// than throwing.
    /// </summary>
    public async Task<IReadOnlyList<BridgeObjectsFullItem>> GetObjectsFullAsync(
        IReadOnlyList<(string Model, string AxType, string Name)> requests,
        IReadOnlyList<string> labelLanguages,
        CancellationToken ct)
    {
        var arr = new JsonArray();
        foreach (var (model, axType, name) in requests)
        {
            arr.Add(new JsonObject
            {
                ["model"] = model,
                ["axType"] = axType,
                ["name"] = name
            });
        }
        var langs = new JsonArray();
        foreach (var l in labelLanguages) langs.Add(l);
        var result = await _pool.Acquire().InvokeAsync("getObjectsFull",
            new JsonObject { ["requests"] = arr, ["languages"] = langs }, ct).ConfigureAwait(false);

        var results = result?["results"]?.AsArray();
        if (results == null) return Array.Empty<BridgeObjectsFullItem>();
        var list = new List<BridgeObjectsFullItem>(results.Count);
        foreach (var node in results)
        {
            var dto = node.Deserialize<BridgeObjectsFullItem>(JsonOpts);
            if (dto != null) list.Add(dto);
        }
        return list;
    }

    /// <summary>
    /// Read an AOT object as its full on-disk XML. Foundation for the write
    /// surface — clients GetObjectXml, edit, then UpdateObjectAsync (or
    /// CreateObjectAsync for new objects).
    /// </summary>
    public async Task<string> GetObjectXmlAsync(string axType, string name, string? model, CancellationToken ct)
    {
        var payload = new JsonObject { ["axType"] = axType, ["name"] = name };
        if (!string.IsNullOrWhiteSpace(model)) payload["model"] = model;
        var result = await _pool.Acquire().InvokeAsync("getObjectXml", payload, ct).ConfigureAwait(false);
        return result?["xml"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>
    /// Write a new AOT object from caller-supplied XML. Fails if it already
    /// exists. Returns the Name resolved from the deserialized XML so the
    /// caller can verify identity.
    /// </summary>
    public async Task<string> CreateObjectAsync(string axType, string model, string xml, CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("createObject",
            new JsonObject { ["axType"] = axType, ["model"] = model, ["xml"] = xml }, ct).ConfigureAwait(false);
        return result?["name"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>
    /// Overwrite an existing AOT object's XML. Caller is expected to have
    /// pulled the current XML via GetObjectXmlAsync and edited it locally.
    /// </summary>
    public async Task<string> UpdateObjectAsync(string axType, string model, string xml, CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("updateObject",
            new JsonObject { ["axType"] = axType, ["model"] = model, ["xml"] = xml }, ct).ConfigureAwait(false);
        return result?["name"]?.GetValue<string>() ?? string.Empty;
    }

    // -------------------------------------------------------------------
    // Metaclass-routed domain RPCs. Bridge constructs the MS metaclass
    // directly from typed JSON and lets MS's provider serialize it. The
    // axType must be registered in the bridge's DomainMapperRegistry.
    // -------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> ListDomainMappedTypesAsync(CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("listDomainMappedTypes", null, ct).ConfigureAwait(false);
        var arr = result?["axTypes"]?.AsArray();
        if (arr == null) return Array.Empty<string>();
        var list = new List<string>(arr.Count);
        foreach (var t in arr)
            if (t is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
        return list;
    }

    public async Task<BridgeDomainWriteResult> CreateDomainObjectViaMetaclassAsync(string axType, string model, string domainJson, CancellationToken ct)
    {
        var jo = JsonNode.Parse(domainJson) as JsonObject
            ?? throw new InvalidOperationException("domainJson must be a JSON object");
        var result = await _pool.Acquire().InvokeAsync("createDomainObject",
            new JsonObject { ["axType"] = axType, ["model"] = model, ["domainJson"] = jo }, ct).ConfigureAwait(false);
        return ToDomainWriteResult(result);
    }

    public async Task<BridgeDomainWriteResult> PatchDomainObjectViaMetaclassAsync(string axType, string model, string name, string patchJson, CancellationToken ct)
    {
        var jo = JsonNode.Parse(patchJson) as JsonObject
            ?? throw new InvalidOperationException("patchJson must be a JSON object");
        var result = await _pool.Acquire().InvokeAsync("patchDomainObject",
            new JsonObject { ["axType"] = axType, ["model"] = model, ["name"] = name, ["patchJson"] = jo }, ct).ConfigureAwait(false);
        return ToDomainWriteResult(result);
    }

    private static BridgeDomainWriteResult ToDomainWriteResult(JsonNode? result)
    {
        var name = result?["name"]?.GetValue<string>() ?? string.Empty;
        // patternConformance is present only for AxForm writes that declared a
        // known pattern; absent otherwise (bridge omits null keys). Detach via
        // DeepClone so it outlives the parent response node.
        var conformance = result?["patternConformance"]?.DeepClone();
        return new BridgeDomainWriteResult(name, conformance);
    }

    public async Task<string> GetDomainObjectViaMetaclassAsync(
        string axType, string name, CancellationToken ct, bool includeDefaults = false)
    {
        var p = new JsonObject { ["axType"] = axType, ["name"] = name };
        // Drift detection passes true so default-valued properties are emitted
        // (not suppressed) and therefore don't look "dropped" in the diff.
        if (includeDefaults) p["includeDefaults"] = true;
        var result = await _pool.Acquire().InvokeAsync("getDomainObject", p, ct).ConfigureAwait(false);
        return result?["domainJson"]?.ToJsonString() ?? "{}";
    }

    /// <summary>
    /// Search a single label resource file via regex (case-insensitive). The
    /// service-side gRPC handler fans this out across multiple files when the
    /// caller passes several label_file_ids.
    /// </summary>
    public async Task<BridgeLabelSearchResult> LabelSearchAsync(
        string labelFileId, string language, string pattern, bool matchDescription, int limit, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["labelFileId"] = labelFileId,
            ["language"] = language,
            ["pattern"] = pattern,
            ["matchDescription"] = matchDescription,
            ["limit"] = limit
        };
        var result = await _pool.Acquire().InvokeAsync("labelSearch", payload, ct).ConfigureAwait(false);
        return Deserialize<BridgeLabelSearchResult>(result);
    }

    public async Task<BridgeLabelReadResult> LabelReadAsync(string labelFileId, string language, string labelId, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["labelFileId"] = labelFileId,
            ["language"] = language,
            ["labelId"] = labelId
        };
        var result = await _pool.Acquire().InvokeAsync("labelRead", payload, ct).ConfigureAwait(false);
        return Deserialize<BridgeLabelReadResult>(result);
    }

    public Task<BridgeLabelMutationResult> LabelAddAsync(string labelFileId, string language, IReadOnlyList<BridgeLabelMutationInput> labels, CancellationToken ct)
        => InvokeLabelMutationAsync("labelAdd", labelFileId, language, labels, ct);

    public Task<BridgeLabelMutationResult> LabelUpdateAsync(string labelFileId, string language, IReadOnlyList<BridgeLabelMutationInput> labels, CancellationToken ct)
        => InvokeLabelMutationAsync("labelUpdate", labelFileId, language, labels, ct);

    public async Task<BridgeLabelMutationResult> LabelDeleteAsync(string labelFileId, string language, IReadOnlyList<string> labelIds, CancellationToken ct)
    {
        var idsArr = new JsonArray();
        foreach (var id in labelIds) idsArr.Add(id);
        var payload = new JsonObject
        {
            ["labelFileId"] = labelFileId,
            ["language"] = language,
            ["labelIds"] = idsArr
        };
        var result = await _pool.Acquire().InvokeAsync("labelDelete", payload, ct).ConfigureAwait(false);
        return Deserialize<BridgeLabelMutationResult>(result);
    }

    private async Task<BridgeLabelMutationResult> InvokeLabelMutationAsync(
        string method, string labelFileId, string language, IReadOnlyList<BridgeLabelMutationInput> labels, CancellationToken ct)
    {
        var arr = new JsonArray();
        foreach (var l in labels)
        {
            var obj = new JsonObject
            {
                ["labelId"] = l.LabelId,
                ["value"] = l.Value
            };
            if (l.Description != null) obj["description"] = l.Description;
            arr.Add(obj);
        }
        var payload = new JsonObject
        {
            ["labelFileId"] = labelFileId,
            ["language"] = language,
            ["labels"] = arr
        };
        var result = await _pool.Acquire().InvokeAsync(method, payload, ct).ConfigureAwait(false);
        return Deserialize<BridgeLabelMutationResult>(result);
    }

    public async Task<IReadOnlyList<BridgeReferenceEdge>> GetStructuralReferencesAsync(string model, string axType, string name, CancellationToken ct)
    {
        var result = await _pool.Acquire().InvokeAsync("getStructuralReferences",
            new JsonObject { ["model"] = model, ["axType"] = axType, ["name"] = name }, ct).ConfigureAwait(false);
        var arr = result?["references"]?.AsArray();
        if (arr == null) return Array.Empty<BridgeReferenceEdge>();
        var list = new List<BridgeReferenceEdge>(arr.Count);
        foreach (var node in arr)
        {
            var dto = node.Deserialize<BridgeReferenceEdge>(JsonOpts);
            if (dto != null) list.Add(dto);
        }
        return list;
    }

    private static T Deserialize<T>(JsonNode? node)
    {
        if (node == null) throw new InvalidOperationException("bridge returned null result");
        return node.Deserialize<T>(JsonOpts)
            ?? throw new InvalidOperationException($"bridge result did not deserialize to {typeof(T).Name}");
    }
}
