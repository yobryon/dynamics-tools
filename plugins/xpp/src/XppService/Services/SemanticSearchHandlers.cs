using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Grpc.Core;
using Microsoft.Data.Sqlite;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Services;

/// <summary>
/// Semantic (vector) search RPC. Embeds the query locally, runs a vec0
/// nearest-neighbour lookup over the method/label embeddings, and — in hybrid
/// mode — fuses that ranking with the FTS5 bm25 ranking via Reciprocal Rank
/// Fusion (RRF) so conceptual matches and exact-token matches both surface.
///
/// Graceful degradation is the rule: if the model is still downloading or
/// sqlite-vec is unavailable, hybrid mode silently falls back to FTS-only
/// (still useful), and pure-semantic mode returns a FailedPrecondition that
/// names the current embedding state and points at the FTS tool. Full-text
/// search is never affected by the embedding subsystem's readiness.
/// </summary>
public sealed partial class PingGrpcService
{
    // RRF constant; 60 is the value from the original Cormack et al. paper and
    // the de-facto default. Dampens the contribution of lower-ranked items.
    private const double RrfK = 60.0;

    public override async Task SearchSemantic(
        SemanticSearchRequest request,
        IServerStreamWriter<SemanticSearchHit> responseStream,
        ServerCallContext context)
    {
        _lifecycle.MaybeTriggerSweep(context.CancellationToken);
        var ct = context.CancellationToken;

        if (string.IsNullOrWhiteSpace(request.Query))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "query is required"));

        var kind = string.Equals(request.Kind?.Trim(), "label", StringComparison.OrdinalIgnoreCase)
            ? "label" : "method";
        var mode = string.Equals(request.Mode?.Trim(), "semantic", StringComparison.OrdinalIgnoreCase)
            ? "semantic" : "hybrid";
        var limit = request.Limit > 0 ? request.Limit : 20;

        var vectorReady = _db.VecEnabled && _embeddings.IsReady;
        if (!vectorReady && mode == "semantic")
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"semantic search unavailable (embedding state: {DescribeEmbeddingState()}). " +
                "Full-text search (xpp_search_code) works now; retry semantic search once the model is ready."));
        }

        // Over-fetch per arm so RRF has signal beyond the final cut.
        var poolK = Math.Max(limit, 50);

        // --- vector arm: embed the query, KNN over the vec0 table -----------
        var vec = new List<(long Id, double Distance)>();
        if (vectorReady)
        {
            var formatted = _embeddings.FormatQuery(request.Query);
            var embeddings = await _embeddings.GenerateAsync(new[] { formatted }, cancellationToken: ct).ConfigureAwait(false);
            var blob = ToBlob(embeddings[0].Vector.ToArray());
            vec = QueryVector(kind, blob, poolK);
        }

        // --- fts arm (hybrid only) ------------------------------------------
        var fts = mode == "hybrid" ? QueryFts(kind, request.Query, poolK) : new List<long>();

        // --- fuse -----------------------------------------------------------
        var ranked = Fuse(mode, vec, fts, limit);
        if (ranked.Count == 0) return;

        // --- hydrate + stream -----------------------------------------------
        using var conn = _db.Open();
        foreach (var r in ranked)
        {
            ct.ThrowIfCancellationRequested();
            var hit = Hydrate(conn, kind, r.Id, r.Score, r.Distance);
            if (hit != null) await responseStream.WriteAsync(hit).ConfigureAwait(false);
        }
    }

    /// <summary>vec0 KNN. Uses the join-friendly "k = ?" constraint form. Rows
    /// come back ordered by ascending cosine distance (0 = identical).</summary>
    private List<(long Id, double Distance)> QueryVector(string kind, byte[] queryBlob, int k)
    {
        var table = kind == "label" ? "label_vec" : "method_vec";
        var results = new List<(long, double)>(k);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT rowid, distance
            FROM {table}
            WHERE embedding MATCH $blob AND k = $k
            ORDER BY distance;";
        cmd.Parameters.Add("$blob", SqliteType.Blob).Value = queryBlob;
        cmd.Parameters.AddWithValue("$k", k);
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add((reader.GetInt64(0), reader.GetDouble(1)));
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Vector KNN failed for {Kind}; returning no vector hits", kind);
        }
        return results;
    }

    /// <summary>FTS5 bm25 ranking, used as the lexical arm of hybrid search.
    /// The raw query is reduced to a safe OR-of-tokens expression so arbitrary
    /// user text can't trip FTS5 syntax. Returns ids in best-rank-first order.</summary>
    private List<long> QueryFts(string kind, string rawQuery, int k)
    {
        var ftsQuery = BuildFtsQuery(rawQuery);
        if (ftsQuery == null) return new List<long>();

        var (ftsTable, baseTable) = kind == "label"
            ? ("labels_fts", "labels")
            : ("methods_fts", "methods");

        var ids = new List<long>(k);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT b.id
            FROM {ftsTable}
            JOIN {baseTable} b ON b.id = {ftsTable}.rowid
            WHERE {ftsTable} MATCH $q
            ORDER BY rank
            LIMIT $k;";
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        cmd.Parameters.AddWithValue("$k", k);
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "FTS arm failed for {Kind}; hybrid degrades to vector-only", kind);
        }
        return ids;
    }

    /// <summary>
    /// Combine the two ranked lists. mode=semantic returns the vector list
    /// scored by cosine similarity (1 - distance). mode=hybrid applies
    /// Reciprocal Rank Fusion across both lists so an item ranked highly by
    /// either arm rises; the vector distance is carried through for display.
    /// </summary>
    private static List<(long Id, double Score, double Distance)> Fuse(
        string mode, List<(long Id, double Distance)> vec, List<long> fts, int limit)
    {
        if (mode == "semantic")
        {
            return vec
                .OrderBy(v => v.Distance)
                .Take(limit)
                .Select(v => (v.Id, 1.0 - v.Distance, v.Distance))
                .ToList();
        }

        var score = new Dictionary<long, double>();
        var distance = new Dictionary<long, double>();

        for (var i = 0; i < vec.Count; i++)
        {
            var (id, dist) = vec[i];
            score[id] = score.GetValueOrDefault(id) + 1.0 / (RrfK + i + 1);
            distance[id] = dist;
        }
        for (var i = 0; i < fts.Count; i++)
        {
            var id = fts[i];
            score[id] = score.GetValueOrDefault(id) + 1.0 / (RrfK + i + 1);
        }

        return score
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => (kv.Key, kv.Value, distance.GetValueOrDefault(kv.Key, 0.0)))
            .ToList();
    }

    /// <summary>Look up the display fields for a hit id; returns null if the
    /// row vanished (id churn between query and hydrate — rare).</summary>
    private SemanticSearchHit? Hydrate(SqliteConnection conn, string kind, long id, double score, double distance)
    {
        using var cmd = conn.CreateCommand();
        if (kind == "label")
        {
            cmd.CommandText = @"
                SELECT o.name, o.ax_type, o.model, o.source, l.key, l.value
                FROM labels l JOIN objects o ON o.id = l.label_file_id
                WHERE l.id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new SemanticSearchHit
            {
                Kind = "label",
                Object = new ObjectRef
                {
                    Name = r.GetString(0),
                    AxType = r.GetString(1),
                    Model = r.GetString(2),
                    Source = r.IsDBNull(3) ? "disk" : r.GetString(3),
                },
                LabelKey = r.GetString(4),
                Text = Excerpt(r.IsDBNull(5) ? string.Empty : r.GetString(5)),
                Score = score,
                Distance = distance,
            };
        }

        cmd.CommandText = @"
            SELECT o.name, o.ax_type, o.model, o.source, m.name, m.source_code
            FROM methods m JOIN objects o ON o.id = m.object_id
            WHERE m.id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        return new SemanticSearchHit
        {
            Kind = "method",
            Object = new ObjectRef
            {
                Name = rd.GetString(0),
                AxType = rd.GetString(1),
                Model = rd.GetString(2),
                Source = rd.IsDBNull(3) ? "disk" : rd.GetString(3),
            },
            MethodName = rd.GetString(4),
            Text = Excerpt(rd.IsDBNull(5) ? string.Empty : rd.GetString(5)),
            Score = score,
            Distance = distance,
        };
    }

    private static string Excerpt(string text)
    {
        text = text.Trim();
        return text.Length <= 400 ? text : text.Substring(0, 400) + "…";
    }

    /// <summary>Reduce arbitrary query text to a safe FTS5 expression: the
    /// alphanumeric tokens OR'd together. Returns null when there's nothing to
    /// match on, so the caller skips the FTS arm entirely.</summary>
    private static string? BuildFtsQuery(string raw)
    {
        var tokens = Regex.Matches(raw, "[A-Za-z0-9_]+")
            .Select(m => m.Value)
            .Where(t => t.Length > 1)
            .Distinct()
            .ToList();
        return tokens.Count == 0 ? null : string.Join(" OR ", tokens);
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        MemoryMarshal.AsBytes(vector.AsSpan()).CopyTo(bytes);
        return bytes;
    }
}
