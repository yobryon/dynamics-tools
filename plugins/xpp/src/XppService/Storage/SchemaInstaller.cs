using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Xpp.Service.Storage;

/// <summary>
/// Applies the v2 schema to a freshly created database, and validates the
/// version of an existing one.
///
/// Migration policy: forward-only. <see cref="Migrations"/> lists the ordered
/// scripts; on startup we apply every one the stored version is behind on. Each
/// script must set schema_version.version to its own sequence number, which is
/// how we verify it actually ran.
///
/// To add a migration: drop ../Schema/00N-*.sql in (idempotent steps, ending in
/// the version bump), register it here, and raise
/// <see cref="CurrentVersion"/>.
///
/// The reverse direction is a hard stop. A cache written by a NEWER build can't
/// be understood by this one, so we throw <see cref="SchemaDowngradeException"/>
/// rather than run against it — see the guard in <see cref="EnsureSchema"/>.
/// </summary>
public sealed class SchemaInstaller
{
    public const int CurrentVersion = 7;

    // Migration scripts, in order. Each must update schema_version.version
    // to its sequence number when it succeeds.
    private static readonly (int Version, string Script)[] Migrations = new[]
    {
        (2, "002-field-refs.sql"),
        (3, "003-label-refs.sql"),
        (4, "004-phase2-marker.sql"),
        (5, "005-runtime-source.sql"),
        (6, "006-label-value-hash.sql"),
        (7, "007-embeddable-count.sql"),
    };

    private readonly ILogger<SchemaInstaller> _logger;

    public SchemaInstaller(ILogger<SchemaInstaller> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// If the database has no schema yet, run the initial creation script
    /// followed by all migrations in order. If it has a prior version,
    /// apply just the pending migrations. If already current, no-op.
    /// </summary>
    /// <summary>
    /// Read the stored schema version straight off a database file without
    /// going through <see cref="IndexDatabase"/>. Returns null when the file
    /// doesn't exist yet, has no schema, or can't be read.
    ///
    /// Exists so startup can make the downgrade decision ONCE, before the host
    /// is built. The guard inside <see cref="EnsureSchema"/> is correct but
    /// fires too late to be usable as a user-facing message: by then the
    /// lifecycle, embedder and initializer are all opening the DB
    /// concurrently, and the console fills with duplicate stack traces from
    /// whichever one lost the race.
    /// </summary>
    public static int? PeekStoredVersion(string databasePath)
    {
        if (!File.Exists(databasePath)) return null;

        try
        {
            // Read-only, and Mode=ReadOnly means we can't create or migrate
            // anything by accident on this path.
            using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            conn.Open();
            if (!TableExists(conn, "schema_version")) return null;
            return ReadStoredVersion(conn);
        }
        catch
        {
            // Unreadable/locked/corrupt: not our call to make here. Let the
            // normal open path produce the real error.
            return null;
        }
    }

    public void EnsureSchema(SqliteConnection connection)
    {
        var hasSchema = TableExists(connection, "schema_version");
        if (!hasSchema)
        {
            _logger.LogInformation("Fresh database; applying initial schema");
            ApplyEmbeddedScript(connection, "001-initial.sql");
        }

        var current = ReadStoredVersion(connection);

        // Downgrade guard. Migrations are forward-only, so a database written
        // by a NEWER build can hold columns, tables and semantics this build
        // knows nothing about. Running against it anyway is the one failure
        // mode that silently corrupts the user's index, so we refuse before
        // touching anything.
        //
        // Deliberately not self-healing: nuking the cache would be a
        // multi-hour re-index plus a real embedding bill, and the usual cause
        // is a stale session that the user can simply close. So we stop, name
        // both versions, and hand them the two choices. See
        // docs/versioning-and-servicing-design.md.
        if (current > CurrentVersion)
        {
            throw new SchemaDowngradeException(current, CurrentVersion);
        }

        foreach (var (version, script) in Migrations)
        {
            if (current >= version) continue;
            _logger.LogInformation("Applying schema migration v{Version} ({Script})", version, script);
            ApplyEmbeddedScript(connection, script);
            var newVersion = ReadStoredVersion(connection);
            if (newVersion != version)
            {
                throw new InvalidOperationException(
                    $"Migration {script} ran but stored version is {newVersion}, expected {version}. " +
                    $"The migration script must update schema_version.version to its sequence number.");
            }
            current = newVersion;
        }

        if (current != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Cache database is at schema version {current}; this build of XppService expects version " +
                $"{CurrentVersion} but no migration is registered to bridge the gap. Either add the migration " +
                $"or delete the cache file to rebuild from scratch.");
        }

        _logger.LogInformation("Schema v{Version} ready", current);
    }

    /// <summary>
    /// Repair/self-heal the full-text search indexes. Runs on every startup,
    /// independent of schema version.
    ///
    /// Why this exists: the FTS sync triggers (methods_ai/ad/au, labels_ai/ad/au)
    /// are base schema, but the indexer's fresh-bulk-load path DROPs them for
    /// speed and recreates them only at the very end (after a single-pass FTS
    /// 'rebuild'). If the process is killed or crashes in between — which has
    /// happened — the triggers are left dropped and the FTS index is never
    /// rebuilt. The damage is permanent and silent: incremental sweeps don't
    /// recreate triggers, write-through relies on them, so every xpp_search_code
    /// / label search returns 0 against an empty index that looks "ready."
    ///
    /// Two idempotent repairs, both cheap on a healthy DB:
    ///   1. CREATE TRIGGER IF NOT EXISTS for all six — guarantees the sync
    ///      triggers exist after startup no matter how a prior run died.
    ///   2. If a content table has rows but its FTS shadow has zero indexed
    ///      documents, run the one-pass 'rebuild' to repopulate it.
    /// </summary>
    public void EnsureSearchIndexHealth(SqliteConnection connection)
    {
        // (1) Triggers — idempotent. Mirrors 001-initial.sql; keep in sync.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS methods_ai AFTER INSERT ON methods BEGIN
                    INSERT INTO methods_fts(rowid, source_code) VALUES (new.id, new.source_code);
                END;
                CREATE TRIGGER IF NOT EXISTS methods_ad AFTER DELETE ON methods BEGIN
                    INSERT INTO methods_fts(methods_fts, rowid, source_code) VALUES ('delete', old.id, old.source_code);
                END;
                CREATE TRIGGER IF NOT EXISTS methods_au AFTER UPDATE ON methods BEGIN
                    INSERT INTO methods_fts(methods_fts, rowid, source_code) VALUES ('delete', old.id, old.source_code);
                    INSERT INTO methods_fts(rowid, source_code)              VALUES (new.id, new.source_code);
                END;
                CREATE TRIGGER IF NOT EXISTS labels_ai AFTER INSERT ON labels BEGIN
                    INSERT INTO labels_fts(rowid, value) VALUES (new.id, new.value);
                END;
                CREATE TRIGGER IF NOT EXISTS labels_ad AFTER DELETE ON labels BEGIN
                    INSERT INTO labels_fts(labels_fts, rowid, value) VALUES ('delete', old.id, old.value);
                END;
                CREATE TRIGGER IF NOT EXISTS labels_au AFTER UPDATE ON labels BEGIN
                    INSERT INTO labels_fts(labels_fts, rowid, value) VALUES ('delete', old.id, old.value);
                    INSERT INTO labels_fts(rowid, value)             VALUES (new.id, new.value);
                END;";
            cmd.ExecuteNonQuery();
        }

        // (2) Rebuild any FTS index that's empty-but-should-not-be.
        RebuildFtsIfEmpty(connection, contentTable: "methods", ftsTable: "methods_fts");
        RebuildFtsIfEmpty(connection, contentTable: "labels", ftsTable: "labels_fts");
    }

    /// <summary>
    /// If <paramref name="contentTable"/> has rows but the FTS index has zero
    /// indexed documents, repopulate it with a single-pass 'rebuild'. No-op
    /// (two cheap counts) when the index is already populated or the content
    /// table is empty.
    /// </summary>
    private void RebuildFtsIfEmpty(SqliteConnection connection, string contentTable, string ftsTable)
    {
        long contentRows, indexedDocs;
        using (var cmd = connection.CreateCommand())
        {
            // The _docsize shadow table has one row per indexed document; zero
            // while content rows exist is the unmistakable "never built" tell.
            cmd.CommandText = $"SELECT (SELECT count(*) FROM {contentTable}), (SELECT count(*) FROM {ftsTable}_docsize);";
            using var r = cmd.ExecuteReader();
            r.Read();
            contentRows = r.GetInt64(0);
            indexedDocs = r.GetInt64(1);
        }
        if (contentRows == 0 || indexedDocs > 0) return;

        _logger.LogWarning(
            "{Fts} is empty but {Content} has {Rows} rows — repairing search index with a one-pass rebuild (one-time)",
            ftsTable, contentTable, contentRows);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"INSERT INTO {ftsTable}({ftsTable}) VALUES ('rebuild');";
            cmd.ExecuteNonQuery();
        }
        _logger.LogInformation("{Fts} rebuild complete", ftsTable);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private static int ReadStoredVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version WHERE id=1";
        var raw = cmd.ExecuteScalar();
        return raw == null || raw is DBNull ? 0 : Convert.ToInt32(raw);
    }

    private static void ApplyEmbeddedScript(SqliteConnection connection, string scriptName)
    {
        var assembly = typeof(SchemaInstaller).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(scriptName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded schema script not found: {scriptName}");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded schema script stream null: {resourceName}");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        // SQLite's ExecuteNonQuery happily handles multi-statement scripts as
        // long as we don't use parameters. Our schema script doesn't.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Thrown when the cache database was written by a newer build than the one
/// trying to open it. Carries both versions so the startup path can print an
/// actionable message instead of a stack trace.
/// </summary>
public sealed class SchemaDowngradeException : Exception
{
    public int StoredVersion { get; }
    public int ExpectedVersion { get; }

    public SchemaDowngradeException(int storedVersion, int expectedVersion)
        : base($"The index cache is at schema version {storedVersion}, but this build of XppService " +
               $"only understands version {expectedVersion}. A newer version of the dynamics-xpp plugin " +
               $"has used this cache, and the schema is forward-only.")
    {
        StoredVersion = storedVersion;
        ExpectedVersion = expectedVersion;
    }
}
