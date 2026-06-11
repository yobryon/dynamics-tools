using System.ComponentModel;
using System.Text.Json;
using Grpc.Core;
using ModelContextProtocol.Server;
using Xpp.Service.Contracts.V1;
using Xpp.Service.Mcp.Grpc;
using Xpp.Service.Mcp.Project;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Mapper drift discovery / fuzz tool. Plugin-maintainer instrumentation.
///
/// Walks a configurable sample of the indexed AOT corpus per type:
///   - Pulls the typed domain JSON via xpp_get_&lt;type&gt;.
///   - Renames it to a probe name.
///   - Pushes the renamed copy back through xpp_create_&lt;type&gt; — the
///     same path agents use. The service-side drift detector runs as
///     usual; we collect every drift entry the response carries.
///   - Cleans up the probe object (rnrproj + on-disk delete).
///
/// Aggregates findings by (axType, drift.path) so a pattern like "AxView
/// drops `query` 5/5 times" jumps out, instead of having to read each
/// individual feedback note.
///
/// Cost: each probe writes a real object to the active project's model,
/// then deletes it. The TFVC pending change set churns through
/// (sample * 2) adds and (sample * 2) deletes. Use sparingly; the
/// expected user is the plugin maintainer driving sweep before a
/// release, not the agent in a tight authoring loop.
/// </summary>
[McpServerToolType]
public sealed class FuzzTools
{
    private readonly XppServiceConnection _conn;
    private readonly ProjectContext _project;

    public FuzzTools(XppServiceConnection conn, ProjectContext project)
    {
        _conn = conn;
        _project = project;
    }

    /// <summary>
    /// Types we have typed Create/Get/Patch tools for. Order roughly
    /// follows complexity — simple types first so a fast pass tells us
    /// the typed surface is healthy before we hit the heavy types.
    /// </summary>
    private static readonly string[] SupportedAxTypes = new[]
    {
        "AxEnum",
        "AxEdt",
        "AxTable",
        "AxClass",
        "AxQuery",
        "AxView",
        "AxDataEntityView",
        "AxMenu",
        "AxMenuItemDisplay",
        "AxMenuItemAction",
        "AxMenuItemOutput",
        "AxTile",
        "AxService",
        "AxServiceGroup",
        "AxResource",
        "AxSecurityPrivilege",
        "AxSecurityDuty",
        "AxSecurityRole",
        "AxSecurityPolicy",
        "AxForm",
    };

    [McpServerTool(Name = "xpp_fuzz_mapper"), Description(
        "Plugin-maintainer instrumentation: walk a sample of the indexed " +
        "AOT corpus per typed AxType, push each object back through the " +
        "typed Create path under a probe name, and aggregate any drift " +
        "the mapper produces. Surfaces mapper coverage gaps the existing " +
        "drift detector would catch one-at-a-time — instead you get a " +
        "table of (axType, property path) -> hit count and can prioritize " +
        "fixes. " +
        "Cost: each probe writes + deletes a real object in the active " +
        "model. Sample size defaults to 3/type for a fast pass; bump to " +
        "20+ for a thorough sweep. Each probe object's lifecycle: read " +
        "source -> rename to probePrefix+source -> create -> collect " +
        "drift -> delete. Total writes is roughly (#types * sampleSize). " +
        "Requires a configured project with an active model the maintainer " +
        "is comfortable polluting with probe objects (they're cleaned up " +
        "immediately, but the TFVC pending change set churns through " +
        "add+delete pairs).")]
    public async Task<string> FuzzMapper(
        [Description("AxTypes to sweep. Empty array = all supported. Example: ['AxEnum', 'AxTable'].")] string[]? axTypes = null,
        [Description("Sample size per type. Default 3. Pass 0 for unlimited (every object of that type in the filtered scope).")] int sampleSizePerType = 3,
        [Description("Prefix for probe object names. Default '_FuzzProbe_'.")] string? probePrefix = null,
        [Description("Whether to attempt cleanup of probes even when create fails. Default true.")] bool cleanupOnFailure = true,
        [Description("Optional model filter. When set, sample only objects in that model (e.g. 'ContosoRetail'). Default null = all models.")] string? model = null,
        [Description("Optional absolute file path. When set, the detailed per-probe results (drift entries, errors) are written to this file as JSON, and only the summary aggregate is returned via MCP. Useful for high-sample sweeps where the detailed output would dominate the MCP response.")] string? outputFile = null,
        [Description("Optional explicit object names to probe instead of sampling the index. When set, these exact names are used as the source for EVERY axType in the sweep (so pair with a single-element axTypes). Lets you target a specific large object for an XML-level fidelity check.")] string[]? targetNames = null,
        [Description("When true, successful probes are NOT deleted — the written XML stays on disk in the active model so it can be diffed against the original. Each kept probe's written path is reported in the detailed output (phase='written'). Default false (probes are cleaned up immediately). Remember to delete kept probes afterward (xpp_delete_object).")] bool keepProbes = false,
        CancellationToken ct = default)
    {
        var (resolved, gate) = ResolveOrGate();
        if (gate != null) return gate;

        var prefix = string.IsNullOrWhiteSpace(probePrefix) ? "_FuzzProbe_" : probePrefix;
        var types = (axTypes != null && axTypes.Length > 0) ? axTypes : SupportedAxTypes;
        // perType 0 = unlimited (consume the full pattern stream for each type).
        var perType = sampleSizePerType <= 0 ? 0 : sampleSizePerType;
        var modelFilter = string.IsNullOrWhiteSpace(model) ? null : model;

        // Aggregations.
        var byTypePropertyHits = new Dictionary<(string axType, string path), int>();
        var byTypeTotals = new Dictionary<string, (int probesTested, int probesWithDrift, int totalDriftEntries, int probesErrored)>();
        var detailed = new List<object>();
        var skipped = new List<object>();

        foreach (var axType in types)
        {
            ct.ThrowIfCancellationRequested();
            byTypeTotals[axType] = (0, 0, 0, 0);

            // Sample existing objects of this type via pattern search, or use
            // the explicit target list when provided.
            var sources = (targetNames != null && targetNames.Length > 0)
                ? targetNames.ToList()
                : await SampleObjectsAsync(axType, perType, modelFilter, ct).ConfigureAwait(false);
            if (sources.Count == 0)
            {
                skipped.Add(new { axType, reason = "no_index_hits", message = "Index has no objects of this type — re-run xpp_status / wait for sweep, or this type isn't represented in the active corpus." });
                continue;
            }

            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                var probeName = prefix + source;
                var tally = byTypeTotals[axType];
                tally.probesTested++;

                // 1. Read source via typed get.
                string sourceJson;
                try
                {
                    var getResp = await _conn.Client.GetDomainObjectAsync(new GetDomainObjectRequest
                    {
                        AxType = axType,
                        Name = source,
                    }, cancellationToken: ct).ConfigureAwait(false);
                    sourceJson = getResp.DomainJson;
                }
                catch (RpcException rx)
                {
                    detailed.Add(new { axType, source, probeName, phase = "get", error = rx.Status.Detail });
                    tally.probesErrored++;
                    byTypeTotals[axType] = tally;
                    continue;
                }

                // 2. Patch the name to the probe name. Domain JSON's top-level
                //    "name" property is the AOT object identity for every type.
                string probeJson;
                try { probeJson = ReplaceTopLevelName(sourceJson, probeName); }
                catch (Exception ex)
                {
                    detailed.Add(new { axType, source, probeName, phase = "rename", error = ex.Message });
                    tally.probesErrored++;
                    byTypeTotals[axType] = tally;
                    continue;
                }

                // 3. Create via typed path. Drift detection runs server-side
                //    automatically; the response carries the drift list.
                WriteObjectResponse createResp;
                try
                {
                    createResp = await _conn.Client.CreateDomainObjectAsync(new CreateDomainObjectRequest
                    {
                        AxType = axType,
                        Model = resolved!.Model,
                        DomainJson = probeJson,
                    }, cancellationToken: ct).ConfigureAwait(false);
                }
                catch (RpcException rx)
                {
                    detailed.Add(new { axType, source, probeName, phase = "create", error = rx.Status.Detail });
                    tally.probesErrored++;
                    byTypeTotals[axType] = tally;
                    if (cleanupOnFailure) await TryCleanupAsync(axType, probeName, ct).ConfigureAwait(false);
                    continue;
                }

                if (createResp.Drift.Count > 0)
                {
                    tally.probesWithDrift++;
                    tally.totalDriftEntries += createResp.Drift.Count;
                    foreach (var d in createResp.Drift)
                    {
                        var key = (axType, d.RequestPath);
                        byTypePropertyHits[key] = byTypePropertyHits.GetValueOrDefault(key) + 1;
                    }
                    detailed.Add(new
                    {
                        axType,
                        source,
                        probeName,
                        phase = "drift",
                        drift = createResp.Drift.Select(d => new { path = d.RequestPath, value = d.RequestValue }).ToArray(),
                    });
                }
                byTypeTotals[axType] = tally;

                // 4. Clean up successful creates — unless keepProbes is set, in
                //    which case leave the written XML on disk and report its path
                //    so it can be diffed against the original.
                if (keepProbes)
                {
                    var writtenPath = _project.ResolveMetadataFilePath(axType, probeName);
                    detailed.Add(new { axType, source, probeName, phase = "written", writtenPath });
                }
                else
                {
                    await TryCleanupAsync(axType, probeName, ct).ConfigureAwait(false);
                }
            }
        }

        // Format the aggregate.
        var hotProperties = byTypePropertyHits
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new { axType = kv.Key.axType, requestPath = kv.Key.path, dropCount = kv.Value })
            .ToArray();

        var perTypeSummary = byTypeTotals
            .Select(kv => new
            {
                axType = kv.Key,
                probesTested = kv.Value.probesTested,
                probesWithDrift = kv.Value.probesWithDrift,
                totalDriftEntries = kv.Value.totalDriftEntries,
                probesErrored = kv.Value.probesErrored,
                cleanRate = kv.Value.probesTested == 0
                    ? 1.0
                    : 1.0 - ((double)kv.Value.probesWithDrift / kv.Value.probesTested),
            })
            .OrderBy(t => t.cleanRate)
            .ToArray();

        var totalProbesTested = byTypeTotals.Values.Sum(v => v.probesTested);
        var totalDriftEntries = byTypeTotals.Values.Sum(v => v.totalDriftEntries);
        var totalErrored = byTypeTotals.Values.Sum(v => v.probesErrored);

        if (!string.IsNullOrWhiteSpace(outputFile))
        {
            try
            {
                var dir = Path.GetDirectoryName(outputFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var fullPayload = JsonSerializer.Serialize(new
                {
                    probePrefix = prefix,
                    sampleSizePerType = perType,
                    typesAttempted = types,
                    perTypeSummary,
                    hotProperties,
                    skipped,
                    detailed,
                }, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outputFile!, fullPayload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "output_file_write_failed",
                    outputFile,
                    message = ex.Message,
                });
            }

            // Return a compact summary only; detailed lives on disk.
            return JsonSerializer.Serialize(new
            {
                probePrefix = prefix,
                sampleSizePerType = perType,
                totalProbesTested,
                totalDriftEntries,
                totalErrored,
                perTypeSummary,
                hotProperties,
                outputFile,
                note = "Detailed per-probe drift/error entries written to outputFile.",
            });
        }

        return JsonSerializer.Serialize(new
        {
            probePrefix = prefix,
            sampleSizePerType = perType,
            typesAttempted = types,
            totalProbesTested,
            totalDriftEntries,
            totalErrored,
            perTypeSummary,
            hotProperties,
            skipped,
            detailed,
        });
    }

    /// <summary>
    /// Sample N object names of a given AxType from the index. Returns
    /// an empty list when none are indexed yet. Uses xpp_search_pattern's
    /// wildcard mode; sampling is the first N hits by index order rather
    /// than truly random — fast and deterministic.
    /// </summary>
    private async Task<List<string>> SampleObjectsAsync(string axType, int sampleSize, string? model, CancellationToken ct)
    {
        var names = new List<string>();
        var unlimited = sampleSize <= 0;
        var req = new PatternRequest
        {
            Pattern = "*",
            AxType = axType,
            // 0 = no server-side cap; consume the entire stream.
            Limit = unlimited ? 0 : sampleSize,
        };
        if (model != null) req.Model = model;
        using var call = _conn.Client.SearchByPattern(req);
        while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
        {
            var m = call.ResponseStream.Current;
            // Skip our own probes if the sweep is re-run before a previous
            // run finished cleaning up.
            if (m.Ref.Name.StartsWith("_FuzzProbe_", StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(m.Ref.Name);
            if (!unlimited && names.Count >= sampleSize) break;
        }
        return names;
    }

    /// <summary>
    /// Replace the top-level "name" property in a domain JSON payload.
    /// Every typed CreateRequest has the AOT object identity at the
    /// top level under this key (camelCase per DomainJson conventions).
    /// </summary>
    private static string ReplaceTopLevelName(string json, string newName)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("root is not an object");

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            var replaced = false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase) && !replaced)
                {
                    writer.WriteString("name", newName);
                    replaced = true;
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            if (!replaced) writer.WriteString("name", newName);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Best-effort cleanup of a probe. Removes from rnrproj, removes from
    /// changeset, deletes the on-disk file. Doesn't fail the sweep on
    /// cleanup error — leftover probes can be cleaned up by re-running
    /// with a more aggressive cleanup mode, or manually by the maintainer.
    /// </summary>
    private async Task TryCleanupAsync(string axType, string name, CancellationToken ct)
    {
        try { await _project.RemoveFromRnprojAsync(axType, name, ct).ConfigureAwait(false); } catch { }
        try { await _project.RemoveFromChangesetAsync(axType, name, ct).ConfigureAwait(false); } catch { }
        try
        {
            var path = _project.ResolveMetadataFilePath(axType, name);
            if (path != null && File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private (ResolvedConfig? resolved, string? gate) ResolveOrGate()
    {
        try
        {
            var r = _project.Resolve();
            if (r == null)
            {
                return (null, JsonSerializer.Serialize(new
                {
                    configured = false,
                    message = "xpp_fuzz_mapper requires a .dynamics-xpp/config.json so probes can be written to (and cleaned up from) an active model.",
                    skill = "dynamics-xpp:xpp-project",
                }));
            }
            return (r, null);
        }
        catch (ProjectConfigException pcx)
        {
            return (null, JsonSerializer.Serialize(new
            {
                configured = false,
                error = "project_config_invalid",
                message = pcx.Message,
            }));
        }
    }
}
