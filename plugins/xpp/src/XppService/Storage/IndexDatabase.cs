using Microsoft.Data.Sqlite;
using Xpp.Service.Embeddings;

namespace Xpp.Service.Storage;

/// <summary>
/// Owns the cache database file: ensures its directory exists, applies the
/// schema on first open, and hands out connections.
///
/// Connections are opened on demand and disposed by the caller — we don't
/// pool here because Microsoft.Data.Sqlite already has its own connection
/// pool (gated by the connection string). Repeated open/close calls reuse
/// pooled handles transparently.
///
/// All connections share the same PRAGMA configuration:
///   - journal_mode=WAL    readers don't block writers; writers don't block
///                         readers. Required by the single-writer + many-
///                         reader design.
///   - synchronous=NORMAL  faster commits while still WAL-safe; durability
///                         is good enough for a derivable cache.
///   - foreign_keys=ON     so the ON DELETE CASCADE rules in the schema
///                         actually fire.
///   - busy_timeout=5000   wait up to 5s when a competing writer holds the
///                         lock before failing the read. The single-writer
///                         design means real contention should be rare.
/// </summary>
public sealed class IndexDatabase
{
    private readonly ILogger<IndexDatabase> _logger;
    private readonly SchemaInstaller _schema;
    private readonly EmbeddingPaths _embeddingPaths;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly bool _vecEnabled;
    private readonly int _vecDim;
    private bool _initialized;

    public IndexDatabase(
        ILogger<IndexDatabase> logger, SchemaInstaller schema, IndexDatabaseOptions options,
        EmbeddingPaths embeddingPaths, EmbeddingOptions embeddingOptions)
    {
        _logger = logger;
        _schema = schema;
        _embeddingPaths = embeddingPaths;
        _databasePath = options.DatabasePath;

        // Semantic search is available only when the user hasn't disabled it and
        // the vendored sqlite-vec extension is present next to the build output.
        // The model still has to self-download before vectors can be produced,
        // but the vec0 tables / extension load don't depend on that — they make
        // the storage side ready so the embedder can fill it once the model lands.
        _vecEnabled = embeddingOptions.Enabled && embeddingPaths.VecReady;
        _vecDim = embeddingOptions.Dim;

        // Private cache (the default), NOT shared. WAL already gives us
        // concurrent readers + a single writer without shared cache, and
        // shared-cache across many connections/threads has documented native
        // fault hazards under concurrent read-during-write load (the suspected
        // cause of an unexplained hard crash in sqlite3_step during a heavy
        // rebuild + concurrent read storm). Dropping it removes that fault class.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        _connectionString = builder.ToString();
    }

    public string DatabasePath => _databasePath;

    /// <summary>True when semantic search is configured on and the sqlite-vec
    /// native extension is available, so connections from <see cref="Open"/>
    /// carry the vec0 module and the method_vec / label_vec tables exist. When
    /// false, full-text search is entirely unaffected; only vector search is
    /// dark.</summary>
    public bool VecEnabled => _vecEnabled;

    /// <summary>Stored vector width of the vec0 tables (Matryoshka dim).</summary>
    public int VecDim => _vecDim;

    /// <summary>
    /// Opens a connection, applying schema setup on the first call. Subsequent
    /// calls go through the Microsoft.Data.Sqlite connection pool.
    /// </summary>
    public SqliteConnection Open()
    {
        if (!_initialized)
        {
            InitializeOnce();
        }

        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        ApplyPragmas(conn);
        EnsureVecLoaded(conn);
        return conn;
    }

    private void InitializeOnce()
    {
        lock (this)
        {
            if (_initialized) return;

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger.LogInformation("Created cache directory {Dir}", directory);
            }

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            ApplyPragmas(conn);
            _schema.EnsureSchema(conn);
            // Self-heal the FTS indexes: recreate the sync triggers if a prior
            // crash stranded them, and rebuild any FTS that's empty-but-populated.
            // Cheap on a healthy DB; repairs a silently-broken search index.
            _schema.EnsureSearchIndexHealth(conn);
            EnsureVecLoaded(conn);
            EnsureVecTables(conn);

            _initialized = true;
            _logger.LogInformation("Index database ready at {Path} (semantic={Vec})", _databasePath, _vecEnabled);
        }
    }

    /// <summary>
    /// Loads the sqlite-vec loadable extension onto a connection, once. The
    /// Microsoft.Data.Sqlite pool reuses native handles, so a reused connection
    /// may already have vec0 registered — re-running the init would fail on the
    /// duplicate module registration. We probe for vec_version() first and only
    /// load when it's absent, which is robust against both fresh and pooled
    /// handles. A load failure is logged and swallowed: full-text search keeps
    /// working; only vector queries (guarded on <see cref="VecEnabled"/>) go dark.
    /// </summary>
    private void EnsureVecLoaded(SqliteConnection connection)
    {
        if (!_vecEnabled) return;
        if (VecAlreadyLoaded(connection)) return;
        try
        {
            connection.EnableExtensions(true);
            // Explicit entry point: the vendored DLL is named vec0.dll, so
            // SQLite's filename-derived default would look for sqlite3_vec0_init,
            // which isn't exported. The actual export is sqlite3_vec_init.
            connection.LoadExtension(_embeddingPaths.VecDllPath, "sqlite3_vec_init");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to load sqlite-vec ({Path}); semantic search disabled on this connection (FTS unaffected)",
                _embeddingPaths.VecDllPath);
        }
    }

    private static bool VecAlreadyLoaded(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT vec_version();";
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the vec0 virtual tables that hold the actual vectors, keyed by
    /// rowid == methods.id / labels.id (single-chunk-per-row for v1). Idempotent
    /// (IF NOT EXISTS). Only meaningful after the extension has loaded; guarded
    /// so a missing/failed extension is a clean no-op.
    /// </summary>
    private void EnsureVecTables(SqliteConnection connection)
    {
        if (!_vecEnabled) return;
        if (!VecAlreadyLoaded(connection)) return; // load failed above
        try
        {
            using var cmd = connection.CreateCommand();
            // Dim is an int from config, not user text — safe to interpolate.
            // Cosine metric: our vectors are L2-normalized, so cosine is the
            // natural similarity and makes score = 1 - distance trivially.
            cmd.CommandText =
                $"CREATE VIRTUAL TABLE IF NOT EXISTS method_vec USING vec0(embedding float[{_vecDim}] distance_metric=cosine);" +
                $"CREATE VIRTUAL TABLE IF NOT EXISTS label_vec USING vec0(embedding float[{_vecDim}] distance_metric=cosine);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create vec0 tables; semantic search will stay dark");
        }
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        // Setting WAL mode is idempotent and must happen on every connection
        // (it's per-connection-then-stored-on-disk for SQLite). The others
        // are per-connection settings.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
        ";
        cmd.ExecuteNonQuery();
    }
}

public sealed class IndexDatabaseOptions
{
    public required string DatabasePath { get; init; }
}
