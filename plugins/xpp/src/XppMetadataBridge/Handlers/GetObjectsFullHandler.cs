using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// getObjectsFull - batched form of getObjectFull. Accepts an array of
    /// {model, axType, name} requests and returns a parallel array of
    /// results. Indexer fast-path: one pipe round-trip per N objects
    /// instead of one per object.
    ///
    /// Per-item failures (ObjectNotFound, mis-typed metadata) do NOT abort
    /// the batch; the result item carries an `error` field instead of the
    /// methods/references arrays. This keeps the bridge from punishing the
    /// indexer for a few bad apples in a 50-object batch.
    /// </summary>
    internal sealed class GetObjectsFullHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public GetObjectsFullHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "getObjectsFull";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            if (@params is not JObject p)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "params must be an object");

            var requests = p["requests"] as JArray;
            if (requests == null)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'requests' must be an array");

            // Optional language filter for label extraction. Defaults to en-US.
            // Unknown to non-AxLabelFile requests (no effect).
            var languages = new List<string>();
            if (p["languages"] is JArray langArr)
            {
                foreach (var l in langArr)
                {
                    if (l?.Type == JTokenType.String)
                    {
                        var s = l.Value<string>();
                        if (!string.IsNullOrWhiteSpace(s)) languages.Add(s!);
                    }
                }
            }
            if (languages.Count == 0) languages.Add("en-US");

            var results = new List<object>(requests.Count);
            foreach (var req in requests)
            {
                ct.ThrowIfCancellationRequested();
                if (req is not JObject ro)
                {
                    results.Add(new { error = "request item must be an object" });
                    continue;
                }

                var model = ro["model"]?.Value<string>();
                var axType = ro["axType"]?.Value<string>();
                var name = ro["name"]?.Value<string>();

                if (string.IsNullOrWhiteSpace(model) ||
                    string.IsNullOrWhiteSpace(axType) ||
                    string.IsNullOrWhiteSpace(name))
                {
                    results.Add(new { model, axType, name, error = "missing model/axType/name" });
                    continue;
                }

                try
                {
                    var hit = ObjectProjection.ReadObjectWithSource(_providers, axType!, name!);
                    if (hit == null)
                    {
                        results.Add(new { model, axType, name, error = "not found" });
                        continue;
                    }

                    var obj = hit.Value.Value;
                    var methods = ObjectProjection.ProjectMethods(obj);
                    var references = ObjectProjection.ProjectReferences(obj, axType!);
                    var fieldReferences = ObjectProjection.ProjectFieldReferences(obj, axType!);
                    var labelReferences = ObjectProjection.ProjectLabelReferences(obj, axType!);

                    // AxLabelFile: also harvest label entries for the
                    // requested languages. Other axTypes always emit an
                    // empty labels array so the wire shape is uniform.
                    var labels = string.Equals(axType, "AxLabelFile", StringComparison.OrdinalIgnoreCase)
                        ? ObjectProjection.ProjectLabels(_providers, obj, name!, model!, languages)
                        : new System.Collections.Generic.List<object>();

                    results.Add(new
                    {
                        model,
                        axType,
                        name,
                        methods,
                        references,
                        fieldReferences,
                        labelReferences,
                        labels,
                        methodCount = methods.Count,
                        referenceCount = references.Count,
                        fieldReferenceCount = fieldReferences.Count,
                        labelReferenceCount = labelReferences.Count,
                        labelCount = labels.Count,
                        source = SourceWire.From(hit.Value.Source),
                        binaryModule = hit.Value.Source == Metadata.ProviderSource.Runtime
                    });
                }
                catch (System.Exception ex)
                {
                    // Catch broadly so one bad object can't poison a whole
                    // batch. The indexer counts these as failures via the
                    // `error` field and moves on.
                    results.Add(new { model, axType, name, error = ex.Message });
                }
            }

            return Task.FromResult<object?>(new { results });
        }
    }
}
