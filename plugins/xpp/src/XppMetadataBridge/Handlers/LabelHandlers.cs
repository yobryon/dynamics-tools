using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    // ============================================================================
    // Label resource file CRUD handlers. Each one resolves the AxLabelFile
    // metadata artifact to its on-disk .label.txt path via LabelOperations.Resolve,
    // operates on the parsed file, and (for mutations) writes it back atomically.
    //
    // Single read + single write per RPC, even for batch mutations — the
    // caller sends N entries in one request and we apply them in memory
    // before saving.
    // ============================================================================

    /// <summary>
    /// labelSearch — regex over one (label_file_id, language) at a time
    /// (caller fans out across multiple files in parallel on the service
    /// side). Always case-insensitive; we compile the regex once with a
    /// safety timeout so a pathological pattern can't hang the bridge.
    /// </summary>
    internal sealed class LabelSearchHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;
        public LabelSearchHandler(MetadataProviderHost providers) { _providers = providers; }
        public string Method => "labelSearch";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            if (@params is not JObject p)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "params must be an object");

            var labelFileId = p["labelFileId"]?.Value<string>();
            var language = p["language"]?.Value<string>();
            var pattern = p["pattern"]?.Value<string>();
            var matchDescription = p["matchDescription"]?.Value<bool>() ?? false;
            var limit = p["limit"]?.Value<int>() ?? 0;

            if (string.IsNullOrWhiteSpace(labelFileId))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labelFileId' is required");
            if (string.IsNullOrWhiteSpace(pattern))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'pattern' is required");

            Regex regex;
            try
            {
                regex = new Regex(pattern!,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InvalidParams,
                    $"Invalid regex: {ex.Message}");
            }

            var resolved = LabelOperations.Resolve(_providers, labelFileId!, language ?? "en-US");
            var file = LabelOperations.Load(resolved.AbsolutePath);

            var matches = new List<object>();
            foreach (var hit in LabelOperations.Search(file, regex, matchDescription, limit))
            {
                matches.Add(new
                {
                    labelFileId = resolved.LabelFileId,
                    language = resolved.Language,
                    labelId = hit.LabelId,
                    value = hit.Value,
                    description = hit.Description,
                    line = hit.Line,
                    matchedIn = hit.MatchedIn
                });
            }

            return Task.FromResult<object?>(new
            {
                labelFileId = resolved.LabelFileId,
                language = resolved.Language,
                resourcePath = resolved.AbsolutePath,
                matches
            });
        }
    }

    /// <summary>
    /// labelRead — fetch one label by (labelFileId, language, labelId).
    /// Cheap; loads the resource file, finds the entry, returns it.
    /// </summary>
    internal sealed class LabelReadHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;
        public LabelReadHandler(MetadataProviderHost providers) { _providers = providers; }
        public string Method => "labelRead";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            if (@params is not JObject p)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "params must be an object");

            var labelFileId = p["labelFileId"]?.Value<string>();
            var language = p["language"]?.Value<string>();
            var labelId = p["labelId"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(labelFileId))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labelFileId' is required");
            if (string.IsNullOrWhiteSpace(labelId))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labelId' is required");

            var resolved = LabelOperations.Resolve(_providers, labelFileId!, language ?? "en-US");
            var file = LabelOperations.Load(resolved.AbsolutePath);
            var entry = LabelOperations.Find(file, labelId!);
            if (entry == null)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.ObjectNotFound,
                    $"Label '{labelId}' not found in '{labelFileId}.{language ?? "en-US"}.label.txt'.");
            }

            return Task.FromResult<object?>(new
            {
                labelFileId = resolved.LabelFileId,
                language = resolved.Language,
                labelId = entry.LabelId,
                value = entry.Value ?? string.Empty,
                description = entry.Description ?? string.Empty,
                line = entry.LabelLineNumber,
                resourcePath = resolved.AbsolutePath
            });
        }
    }

    /// <summary>
    /// labelAdd — append one or many labels in a single file write. Fails
    /// the whole batch if any labelId already exists (use labelUpdate for
    /// overwrites). Batch semantics keep four-labels-at-once flows to one
    /// round-trip.
    /// </summary>
    internal sealed class LabelAddHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;
        public LabelAddHandler(MetadataProviderHost providers) { _providers = providers; }
        public string Method => "labelAdd";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var inputs = LabelMutationParsing.Parse(@params, out var labelFileId, out var language);
            var resolved = LabelOperations.Resolve(_providers, labelFileId, language);
            var file = LabelOperations.Load(resolved.AbsolutePath);

            // Build a quick set of existing label ids for duplicate detection.
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in file.Entries)
            {
                if (entry.Kind == LabelLineKind.Label && entry.LabelId != null) existing.Add(entry.LabelId);
            }

            // Also reject duplicates within the batch itself — adding the same
            // labelId twice would silently overwrite within our own request.
            var batchSeen = new HashSet<string>(StringComparer.Ordinal);
            var conflicts = new List<string>();
            foreach (var input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input.LabelId)) continue;
                if (!batchSeen.Add(input.LabelId)) conflicts.Add($"{input.LabelId} (duplicated in batch)");
                else if (existing.Contains(input.LabelId)) conflicts.Add(input.LabelId);
            }
            if (conflicts.Count > 0)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InvalidParams,
                    $"Cannot add: label id(s) already exist or are duplicated in the batch: {string.Join(", ", conflicts)}. Use labelUpdate to overwrite.");
            }

            // Append each new entry. The file's trailing blank handling is
            // taken care of inside Save based on what we leave in Entries.
            foreach (var input in inputs)
            {
                file.Entries.Add(new LabelLine
                {
                    Kind = LabelLineKind.Label,
                    LabelId = input.LabelId,
                    Value = input.Value ?? string.Empty,
                    Description = string.IsNullOrEmpty(input.Description) ? null : input.Description,
                    LabelLineNumber = -1
                });
            }

            LabelOperations.Save(file);

            return Task.FromResult<object?>(new
            {
                labelFileId = resolved.LabelFileId,
                language = resolved.Language,
                resourcePath = resolved.AbsolutePath,
                affected = inputs.Count
            });
        }
    }

    /// <summary>
    /// labelUpdate — change one or many existing labels in place. Fails the
    /// whole batch if any labelId is missing (use labelAdd for new entries).
    /// </summary>
    internal sealed class LabelUpdateHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;
        public LabelUpdateHandler(MetadataProviderHost providers) { _providers = providers; }
        public string Method => "labelUpdate";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var inputs = LabelMutationParsing.Parse(@params, out var labelFileId, out var language);
            var resolved = LabelOperations.Resolve(_providers, labelFileId, language);
            var file = LabelOperations.Load(resolved.AbsolutePath);

            // Index existing label rows by id for O(1) lookup.
            var byId = new Dictionary<string, LabelLine>(StringComparer.Ordinal);
            foreach (var entry in file.Entries)
            {
                if (entry.Kind == LabelLineKind.Label && entry.LabelId != null) byId[entry.LabelId] = entry;
            }

            var missing = inputs
                .Where(i => !string.IsNullOrEmpty(i.LabelId) && !byId.ContainsKey(i.LabelId))
                .Select(i => i.LabelId)
                .ToList();
            if (missing.Count > 0)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.ObjectNotFound,
                    $"Cannot update: label id(s) not found: {string.Join(", ", missing)}. Use labelAdd to create.");
            }

            foreach (var input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input.LabelId)) continue;
                var entry = byId[input.LabelId];
                entry.Value = input.Value ?? string.Empty;
                // Empty description on input means "clear the description";
                // null means "leave existing description alone" — but JSON
                // distinguishes those poorly, so we treat empty string as
                // explicit clear (matches the input shape of labelAdd).
                entry.Description = string.IsNullOrEmpty(input.Description) ? null : input.Description;
            }

            LabelOperations.Save(file);

            return Task.FromResult<object?>(new
            {
                labelFileId = resolved.LabelFileId,
                language = resolved.Language,
                resourcePath = resolved.AbsolutePath,
                affected = inputs.Count
            });
        }
    }

    /// <summary>
    /// labelDelete — remove one or many labels. Fails the batch if any id
    /// is missing (keeps the "no silent partial mutations" guarantee).
    /// </summary>
    internal sealed class LabelDeleteHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;
        public LabelDeleteHandler(MetadataProviderHost providers) { _providers = providers; }
        public string Method => "labelDelete";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            if (@params is not JObject p)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "params must be an object");

            var labelFileId = p["labelFileId"]?.Value<string>();
            var language = p["language"]?.Value<string>() ?? "en-US";
            var idsArr = p["labelIds"] as JArray
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labelIds' (array) is required");

            if (string.IsNullOrWhiteSpace(labelFileId))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labelFileId' is required");

            var ids = new List<string>(idsArr.Count);
            foreach (var item in idsArr)
            {
                var id = item?.Value<string>();
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
            }
            if (ids.Count == 0)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labelIds' must contain at least one id");

            var resolved = LabelOperations.Resolve(_providers, labelFileId!, language);
            var file = LabelOperations.Load(resolved.AbsolutePath);

            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in file.Entries)
            {
                if (entry.Kind == LabelLineKind.Label && entry.LabelId != null) existing.Add(entry.LabelId);
            }
            var missing = ids.Where(id => !existing.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.ObjectNotFound,
                    $"Cannot delete: label id(s) not found: {string.Join(", ", missing)}.");
            }

            var toRemove = new HashSet<string>(ids, StringComparer.Ordinal);
            int removed = file.Entries.RemoveAll(e =>
                e.Kind == LabelLineKind.Label && e.LabelId != null && toRemove.Contains(e.LabelId));

            LabelOperations.Save(file);

            return Task.FromResult<object?>(new
            {
                labelFileId = resolved.LabelFileId,
                language = resolved.Language,
                resourcePath = resolved.AbsolutePath,
                affected = removed
            });
        }
    }

    // ---------------------------------------------------------------------
    // Shared input parsing for add/update — both have the same envelope
    // shape (labelFileId, language, labels: [{labelId, value, description}])
    // and the same per-row validation rules.
    // ---------------------------------------------------------------------

    internal sealed class LabelMutationInput
    {
        public string LabelId { get; }
        public string Value { get; }
        public string? Description { get; }
        public LabelMutationInput(string labelId, string value, string? description)
        {
            LabelId = labelId;
            Value = value;
            Description = description;
        }
    }

    internal static class LabelMutationParsing
    {
        public static List<LabelMutationInput> Parse(JToken? @params, out string labelFileId, out string language)
        {
            if (@params is not JObject p)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "params must be an object");

            labelFileId = p["labelFileId"]?.Value<string>() ?? string.Empty;
            language = p["language"]?.Value<string>() ?? "en-US";
            if (string.IsNullOrWhiteSpace(labelFileId))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labelFileId' is required");

            var arr = p["labels"] as JArray
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labels' (array) is required");
            if (arr.Count == 0)
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'labels' must contain at least one entry");

            var inputs = new List<LabelMutationInput>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JObject row)
                    throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"labels[{i}] must be an object");
                var id = row["labelId"]?.Value<string>();
                var value = row["value"]?.Value<string>() ?? string.Empty;
                var description = row["description"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(id))
                    throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, $"labels[{i}].labelId is required");
                inputs.Add(new LabelMutationInput(id!, value, description));
            }
            return inputs;
        }
    }
}
