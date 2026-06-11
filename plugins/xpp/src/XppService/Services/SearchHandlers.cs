using Grpc.Core;
using Microsoft.Data.Sqlite;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Services;

/// <summary>
/// Search RPCs. Lives in a partial extension of PingGrpcService so the
/// gRPC dispatch table sees one implementation type but the code is split
/// by responsibility.
///
/// All four handlers follow the same shape:
///   1. validate / normalize input
///   2. open a read connection (WAL means readers don't fight the writer)
///   3. issue ONE SQL query that returns the projection the response needs
///   4. stream rows back as proto messages, observing the client's
///      cancellation token at every iteration
///
/// We deliberately keep the SQL inline rather than abstracting to a
/// repository. The queries are small, the table shape is local to this
/// project, and one-step-removed indirection would only obscure what's
/// happening on the wire.
/// </summary>
public sealed partial class PingGrpcService
{
    public override async Task FindObject(
        FindObjectRequest request,
        IServerStreamWriter<ObjectMatch> responseStream,
        ServerCallContext context)
    {
        _lifecycle.MaybeTriggerSweep(context.CancellationToken);
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "name is required"));
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // COLLATE NOCASE on objects.name means '=' matches case-insensitively
        // already; the filter clauses fall through when their arguments are
        // empty strings so callers don't need to omit them.
        cmd.CommandText = @"
            SELECT name, ax_type, model, source
            FROM objects
            WHERE name = $name
              AND ($axType = '' OR ax_type = $axType)
              AND ($model  = '' OR model   = $model);
        ";
        cmd.Parameters.AddWithValue("$name",   request.Name);
        cmd.Parameters.AddWithValue("$axType", request.AxType ?? string.Empty);
        cmd.Parameters.AddWithValue("$model",  request.Model ?? string.Empty);

        using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(new ObjectMatch
            {
                Ref = new ObjectRef
                {
                    Name = reader.GetString(0),
                    AxType = reader.GetString(1),
                    Model = reader.GetString(2),
                    Source = reader.IsDBNull(3) ? "disk" : reader.GetString(3)
                }
            }).ConfigureAwait(false);
        }
    }

    public override async Task SearchByPattern(
        PatternRequest request,
        IServerStreamWriter<ObjectMatch> responseStream,
        ServerCallContext context)
    {
        _lifecycle.MaybeTriggerSweep(context.CancellationToken);
        // Translate v1-style * and ? wildcards to SQL LIKE %/_. Empty pattern
        // means "match everything" — useful for browsing a model entirely.
        var pattern = string.IsNullOrEmpty(request.Pattern)
            ? "%"
            : request.Pattern.Replace('*', '%').Replace('?', '_');

        var limit = request.Limit > 0 ? request.Limit : int.MaxValue;

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name, ax_type, model, source
            FROM objects
            WHERE name LIKE $pattern
              AND ($axType = '' OR ax_type = $axType)
              AND ($model  = '' OR model   = $model)
            ORDER BY name, ax_type, model
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$pattern", pattern);
        cmd.Parameters.AddWithValue("$axType",  request.AxType ?? string.Empty);
        cmd.Parameters.AddWithValue("$model",   request.Model ?? string.Empty);
        cmd.Parameters.AddWithValue("$limit",   limit);

        using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(new ObjectMatch
            {
                Ref = new ObjectRef
                {
                    Name = reader.GetString(0),
                    AxType = reader.GetString(1),
                    Model = reader.GetString(2),
                    Source = reader.IsDBNull(3) ? "disk" : reader.GetString(3)
                }
            }).ConfigureAwait(false);
        }
    }

    public override async Task SearchCode(
        CodeSearchRequest request,
        IServerStreamWriter<CodeSearchHit> responseStream,
        ServerCallContext context)
    {
        _lifecycle.MaybeTriggerSweep(context.CancellationToken);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "query is required"));
        }

        var limit = request.Limit > 0 ? request.Limit : 200;

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        // FTS5's MATCH operator handles the actual lexer. We let through whatever
        // syntax the client sends; malformed expressions surface as a SQLite
        // error which we map back to an RpcException below.
        //
        // snippet() args: column index (0 = source_code), open/close tags,
        // ellipsis text, max tokens (32). Ranked by FTS5's bm25-style score.
        cmd.CommandText = @"
            SELECT o.name, o.ax_type, o.model, m.name,
                   snippet(methods_fts, 0, '<mark>', '</mark>', '…', 32) AS snip
            FROM methods_fts
            JOIN methods m ON m.id = methods_fts.rowid
            JOIN objects o ON o.id = m.object_id
            WHERE methods_fts MATCH $q
            ORDER BY rank
            LIMIT $limit;
        ";
        cmd.Parameters.AddWithValue("$q",     request.Query);
        cmd.Parameters.AddWithValue("$limit", limit);

        SqliteDataReader reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            // FTS5 throws on bad MATCH syntax; surface as InvalidArgument so
            // the caller knows it's a request shape problem, not a server fault.
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"bad FTS query: {ex.Message}"));
        }

        using (reader)
        {
            while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new CodeSearchHit
                {
                    Object = new ObjectRef
                    {
                        Name = reader.GetString(0),
                        AxType = reader.GetString(1),
                        Model = reader.GetString(2)
                    },
                    MethodName = reader.GetString(3),
                    Snippet = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                }).ConfigureAwait(false);
            }
        }
    }

    public override async Task FindReferences(
        ReferenceQuery request,
        IServerStreamWriter<ReferenceHit> responseStream,
        ServerCallContext context)
    {
        _lifecycle.MaybeTriggerSweep(context.CancellationToken);
        // target_label takes precedence and doesn't need target_name.
        // Otherwise target_name is mandatory.
        if (string.IsNullOrWhiteSpace(request.TargetName) && string.IsNullOrWhiteSpace(request.TargetLabel))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "target_name or target_label is required"));
        }

        var limit = request.Limit > 0 ? request.Limit : 500;
        var emitted = 0;

        using var conn = _db.Open();

        // ---- label reverse-lookup path (Tier C) -------------------------
        // When target_label is set, we query label_refs. Object/field paths
        // and source-mention search are skipped; the label key takes
        // precedence over the other filters.
        //
        // Label parsing is fuzzy because labels can appear in two forms on
        // disk (and either form may be how the user thinks of it):
        //   (A) explicit:    '@SYS:343903'   -> (file='SYS', key='343903')
        //   (B) concatenated: '@SYS343903'   -> (file='',    key='SYS343903')
        // The bridge stores whatever is literally on disk. To match both
        // forms regardless of which form the user supplied, we generate
        // all plausible (file, key) candidates and OR them.
        if (!string.IsNullOrWhiteSpace(request.TargetLabel))
        {
            var rawLabel = request.TargetLabel.TrimStart('@');
            var candidates = new HashSet<(string File, string Key)>();
            candidates.Add((string.Empty, rawLabel)); // raw form (legacy concatenated or truly unprefixed)
            var colonIdx = rawLabel.IndexOf(':');
            if (colonIdx > 0)
            {
                var f = rawLabel.Substring(0, colonIdx);
                var k = rawLabel.Substring(colonIdx + 1);
                candidates.Add((f, k));                       // explicit-form match
                candidates.Add((string.Empty, f + k));        // alt: legacy concatenated of the same name
            }
            else
            {
                // Try splitting an unprefixed token like 'SYS343903' into
                // 'SYS' + '343903' — the AX-2012 concat heuristic. Works
                // when the label file uses a short uppercase prefix.
                var m = System.Text.RegularExpressions.Regex.Match(
                    rawLabel, @"^([A-Z][A-Z0-9_]*?)(\d+)$");
                if (m.Success)
                {
                    candidates.Add((m.Groups[1].Value, m.Groups[2].Value));
                }
            }

            // Build a UNION-ish IN clause across candidate pairs.
            var sb = new System.Text.StringBuilder();
            sb.Append(@"
                SELECT o.name, o.ax_type, o.model, lr.reference_kind, lr.context, lr.source_member
                FROM label_refs lr
                JOIN objects o ON o.id = lr.source_object_id
                WHERE (");
            var i = 0;
            using var lcmd = conn.CreateCommand();
            foreach (var (f, k) in candidates)
            {
                if (i > 0) sb.Append(" OR ");
                sb.Append($"(lr.label_file = $f{i} AND lr.label_key = $k{i})");
                lcmd.Parameters.AddWithValue($"$f{i}", f);
                lcmd.Parameters.AddWithValue($"$k{i}", k);
                i++;
            }
            sb.Append(@")
                ORDER BY o.model, o.ax_type, o.name
                LIMIT $limit;
            ");
            lcmd.CommandText = sb.ToString();
            lcmd.Parameters.AddWithValue("$limit", limit);

            using var lreader = await lcmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            while (await lreader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                if (emitted >= limit) return;
                await responseStream.WriteAsync(new ReferenceHit
                {
                    Source = new ObjectRef
                    {
                        Name = lreader.GetString(0),
                        AxType = lreader.GetString(1),
                        Model = lreader.GetString(2)
                    },
                    Kind = lreader.GetString(3),
                    Context = lreader.IsDBNull(4) ? string.Empty : lreader.GetString(4),
                    SourceMember = lreader.IsDBNull(5) ? string.Empty : lreader.GetString(5),
                }).ConfigureAwait(false);
                emitted++;
            }
            return;
        }

        // ---- field-level path (Tier B) ----------------------------------
        // When target_field is set, target_name is the table name and we
        // query field_refs. Object-level edges and source-mention search
        // are skipped — the agent that needs both should call twice.
        if (!string.IsNullOrWhiteSpace(request.TargetField))
        {
            using var fcmd = conn.CreateCommand();
            fcmd.CommandText = @"
                SELECT o.name, o.ax_type, o.model, fr.reference_kind, fr.context, fr.source_member
                FROM field_refs fr
                JOIN objects o ON o.id = fr.source_object_id
                WHERE fr.target_table_name = $table
                  AND fr.target_field_name = $field
                ORDER BY o.model, o.ax_type, o.name
                LIMIT $limit;
            ";
            fcmd.Parameters.AddWithValue("$table", request.TargetName);
            fcmd.Parameters.AddWithValue("$field", request.TargetField);
            fcmd.Parameters.AddWithValue("$limit", limit);

            using var freader = await fcmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            while (await freader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                if (emitted >= limit) return;
                await responseStream.WriteAsync(new ReferenceHit
                {
                    Source = new ObjectRef
                    {
                        Name = freader.GetString(0),
                        AxType = freader.GetString(1),
                        Model = freader.GetString(2)
                    },
                    Kind = freader.GetString(3),
                    Context = freader.IsDBNull(4) ? string.Empty : freader.GetString(4),
                    SourceMember = freader.IsDBNull(5) ? string.Empty : freader.GetString(5),
                }).ConfigureAwait(false);
                emitted++;
            }
            return;
        }

        // ---- structural edges from the refs table -----------------------
        // This is the canonical "X is referenced by Y" graph. Filter by
        // target_type when given; the column is nullable so we also have to
        // let nulls through when no type filter is requested.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT o.name, o.ax_type, o.model, r.reference_kind, r.context
                FROM refs r
                JOIN objects o ON o.id = r.source_object_id
                WHERE r.target_object_name = $name
                  AND ($type = '' OR r.target_object_type = $type)
                ORDER BY o.model, o.ax_type, o.name
                LIMIT $limit;
            ";
            cmd.Parameters.AddWithValue("$name",  request.TargetName);
            cmd.Parameters.AddWithValue("$type",  request.TargetType ?? string.Empty);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                if (emitted >= limit) return;
                await responseStream.WriteAsync(new ReferenceHit
                {
                    Source = new ObjectRef
                    {
                        Name = reader.GetString(0),
                        AxType = reader.GetString(1),
                        Model = reader.GetString(2)
                    },
                    Kind = reader.GetString(3),
                    Context = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                }).ConfigureAwait(false);
                emitted++;
            }
        }

        if (!request.IncludeSourceMentions) return;
        if (emitted >= limit) return;

        // ---- tokenized source mentions via FTS5 -------------------------
        // Use FTS5 to narrow to methods that contain the identifier as a
        // token, then verify with a precise LIKE filter (FTS5 matches the
        // token but the wider regex of "is this an actual reference" is
        // beyond our schema today). The LIKE filter is case-insensitive
        // because methods.source_code follows SQLite default collation;
        // we approximate via LOWER() on both sides since we didn't apply
        // NOCASE to the source column (it'd bloat the FTS index).
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT o.name, o.ax_type, o.model, m.name
                FROM methods_fts
                JOIN methods m ON m.id = methods_fts.rowid
                JOIN objects o ON o.id = m.object_id
                WHERE methods_fts MATCH $tok
                  AND LOWER(m.source_code) LIKE '%' || LOWER($tok) || '%'
                ORDER BY rank
                LIMIT $remaining;
            ";
            cmd.Parameters.AddWithValue("$tok",       request.TargetName);
            cmd.Parameters.AddWithValue("$remaining", Math.Max(0, limit - emitted));

            try
            {
                using var reader = await cmd.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    if (emitted >= limit) return;
                    await responseStream.WriteAsync(new ReferenceHit
                    {
                        Source = new ObjectRef
                        {
                            Name = reader.GetString(0),
                            AxType = reader.GetString(1),
                            Model = reader.GetString(2)
                        },
                        Kind = "source-mention",
                        Context = reader.GetString(3) // method name
                    }).ConfigureAwait(false);
                    emitted++;
                }
            }
            catch (SqliteException)
            {
                // If the target name happens to be FTS5 syntax (very unlikely
                // for a D365 identifier but possible) we silently skip the
                // source-mentions phase rather than failing the whole call.
            }
        }
    }
}
