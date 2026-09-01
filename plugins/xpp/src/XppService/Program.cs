// XppService — modern .NET orchestrator for the v2 stack.
//
// One long-running instance per box. Hosts gRPC over a named pipe (Kestrel +
// built-in named-pipe transport, .NET 8+), spawns the XppMetadataBridge as
// a child process for legacy net48 metadata access, and (eventually) owns
// the SQLite cache, indexer, and embedding model.
//
// Startup flow:
//   1. Acquire the cross-process single-instance mutex. Bail cleanly if
//      another XppService is already running.
//   2. Build the host with logging, gRPC, and named-pipe Kestrel.
//   3. Spawn the bridge as a hosted service, prove it answers a ping at
//      startup, and keep it owned for the lifetime of the host.
//   4. Map gRPC services and start listening.
//
// Shutdown is the reverse: stop accepting requests, drain in-flight calls,
// dispose the bridge (closes stdin, waits for the child, kills on timeout),
// release the mutex.

using System.Text.Json.Nodes;
using Xpp.Service.Bridge;
using Xpp.Service.Indexing;
using Xpp.Service.Lifecycle;
using Xpp.Service.Services;
using Xpp.Service.Storage;

// === Acquire single-instance lock BEFORE anything else =====================
// If we can't get it, exit code 75 (EX_TEMPFAIL) is a conventional signal
// for "service unavailable due to existing peer". Callers (the MCP stub
// auto-spawn path) interpret this as "service is already up; connect to it"
// rather than "real failure".

const int ExitCodeAlreadyRunning = 75;

// Exit code 78 (EX_CONFIG) — the box is configured in a way this build can't
// work with, and no amount of retrying fixes it. Used for the schema-downgrade
// refusal below, so callers can tell "wrong build for this cache" apart from
// a crash.
const int ExitCodeSchemaDowngrade = 78;

SingleInstanceLock? singleInstanceLock;
try
{
    singleInstanceLock = new SingleInstanceLock("xpp-service-v2");
}
catch (SingleInstanceAlreadyRunningException ex)
{
    Console.Error.WriteLine(ex.Message);
    return ExitCodeAlreadyRunning;
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    // === User-global config overlay ======================================
    // A per-machine config.json next to the index DB (%LOCALAPPDATA%\
    // dynamics-xpp\config.json) lets the user override pool sizing and other
    // knobs without touching the shipped appsettings.json. Layered last so it
    // wins, but still below env vars / command line. Optional: absent on a
    // fresh box.
    var globalConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dynamics-xpp", "config.json");
    builder.Configuration.AddJsonFile(globalConfigPath, optional: true, reloadOnChange: false);

    // === Bridge options ==================================================
    // Resolve XppMetadataBridge.exe with this priority:
    //   1. XppService:BridgeExecutable config (explicit override; absolute
    //      or relative to AppContext.BaseDirectory)
    //   2. XPP_BRIDGE_EXE env var
    //   3. Sibling: same directory as XppService.exe (defensive — not the
    //      normal layout since the bridge is net48 and the service net9)
    //   4. Walk up from AppContext.BaseDirectory looking for
    //      src/XppMetadataBridge/bin/{Release,Debug}/net48/XppMetadataBridge.exe
    //
    // Release is preferred over Debug since that's our shipping config; the
    // Debug fallback exists for local dev where someone built Debug only.
    //
    // On failure, the error lists every path we checked so the user can
    // see exactly what's missing.
    var bridgePath = BridgeExeResolver.Resolve(builder.Configuration, out var bridgeSearchTrace);
    if (bridgePath == null)
    {
        throw new FileNotFoundException(
            "XppMetadataBridge.exe could not be located.\nSearched:\n" +
            string.Join("\n", bridgeSearchTrace.Select(p => "  - " + p)) +
            "\nBuild the bridge or set XppService:BridgeExecutable / XPP_BRIDGE_EXE.");
    }

    // === D365 metadata store =============================================
    // Normally dt setup discovers this and records it in the user-global
    // config.json that we overlaid above. If it is missing or stale, fall
    // back to discovering it ourselves rather than failing: a hardcoded
    // drive letter is wrong on any box whose LCS deployment chose a
    // different drive, which is exactly how this used to break.
    var configuredPackages = builder.Configuration["D365:PackagesLocalDirectory"] ?? string.Empty;
    var packagesPath = D365Locator.Resolve(configuredPackages, out var packagesTrace) ?? string.Empty;

    var customPath = builder.Configuration["D365:CustomMetadataPath"] ?? string.Empty;
    // The custom-metadata path defaults to the packages directory, and a
    // configured value that no longer exists is worse than that default.
    if (string.IsNullOrWhiteSpace(customPath) || !Directory.Exists(customPath))
    {
        customPath = packagesPath;
    }

    // Dynamic pool bounds. Defaults: Min=2 (kept warm so queries never pay
    // cold-start), Max=max(2, ProcessorCount-1) (leave a core for the service
    // + writer). A legacy single XppService:BridgePoolSize, if set, pins both
    // bounds (static pool) for back-compat. All overridable via the global
    // config.json (XppService:BridgePoolMin / BridgePoolMax / BridgePoolIdleSeconds).
    var legacyFixed = builder.Configuration.GetValue<int?>("XppService:BridgePoolSize");
    var poolMin = builder.Configuration.GetValue<int?>("XppService:BridgePoolMin")
                  ?? legacyFixed ?? 2;
    var poolMax = builder.Configuration.GetValue<int?>("XppService:BridgePoolMax")
                  ?? legacyFixed ?? Math.Max(2, Environment.ProcessorCount - 1);
    var idleSeconds = builder.Configuration.GetValue<int?>("XppService:BridgePoolIdleSeconds") ?? 60;
    if (poolMin < 1) poolMin = 1;
    if (poolMax < poolMin) poolMax = poolMin;

    builder.Services.AddSingleton(new BridgeOptions
    {
        ExecutablePath = bridgePath,
        PackagesLocalDirectory = packagesPath,
        CustomMetadataPath = customPath,
        Min = poolMin,
        Max = poolMax,
        IdleTimeout = TimeSpan.FromSeconds(Math.Max(5, idleSeconds))
    });

    // Bridge pool: dynamically sized [Min, Max] set of worker processes. The
    // pool owns a worker factory (so it can spawn on scale-up); the scaler
    // hosted service drives sizing.
    builder.Services.AddSingleton<BridgePool>(sp =>
    {
        var opts = sp.GetRequiredService<BridgeOptions>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var nextId = 0;
        Func<BridgeProcess> factory = () => new BridgeProcess(
            loggerFactory.CreateLogger<BridgeProcess>(), opts)
        { WorkerId = Interlocked.Increment(ref nextId) };
        return new BridgePool(factory, opts, loggerFactory.CreateLogger<BridgePool>());
    });
    builder.Services.AddHostedService<BridgeLifecycle>();
    builder.Services.AddHostedService<BridgePoolScaler>();

    // === Index database ==================================================
    // Default lives in %LOCALAPPDATA%\XppService\ on Windows. Survives builds,
    // branch switches, and cleans of the repo - the cache is hard-earned
    // (full rebuild on a real F&O codebase is multi-minutes to hours) and
    // belongs in user-local app data, not the build output. Overridable via
    // appsettings.json or the XppService__DataDirectory env var; tests set
    // the env var to a temp dir so they don't pollute the real cache.
    var dataDir = builder.Configuration["XppService:DataDirectory"];
    if (string.IsNullOrWhiteSpace(dataDir))
    {
        // Project-name aligned ("dynamics-xpp") rather than component-name
        // ("XppService") so future v2 artifacts (embeddings, logs, exports)
        // naturally share the same folder.
        dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "dynamics-xpp");
    }
    if (!Path.IsPathRooted(dataDir))
    {
        dataDir = Path.GetFullPath(dataDir, AppContext.BaseDirectory);
    }
    var dbPath = Path.Combine(dataDir, "v2-index.db");

    // === Downgrade refusal ==============================================
    // Migrations are forward-only, so a cache written by a NEWER plugin build
    // is unreadable to this one, and running against it anyway is the one
    // failure mode that silently corrupts the user's index.
    //
    // Checked HERE, before the host is built, because it's the only place the
    // answer can be delivered as a single clear message: once the host starts,
    // the lifecycle, embedder and DB initializer all open the cache
    // concurrently and each throws its own copy. See
    // docs/versioning-and-servicing-design.md.
    var storedSchema = SchemaInstaller.PeekStoredVersion(dbPath);
    if (storedSchema > SchemaInstaller.CurrentVersion)
    {
        WriteDowngradeRefusal(storedSchema.Value, SchemaInstaller.CurrentVersion, dbPath);
        return ExitCodeSchemaDowngrade;
    }

    builder.Services.AddSingleton(new IndexDatabaseOptions { DatabasePath = dbPath });

    // === Embeddings / semantic search ====================================
    // Cloud inference via an Azure OpenAI embeddings deployment, behind the
    // Microsoft.Extensions.AI IEmbeddingProvider seam, serving vector search
    // through sqlite-vec. The local CPU ONNX path (QwenEmbeddingGenerator /
    // ModelAcquisition) is retained in the codebase but inactive — it was too
    // slow to embed the full corpus. Config under "Embedding:*" (+ the global
    // config.json overlay); endpoint + key come ONLY from the environment.
    var embeddingOptions = builder.Configuration.GetSection("Embedding").Get<Xpp.Service.Embeddings.EmbeddingOptions>()
                           ?? new Xpp.Service.Embeddings.EmbeddingOptions();

    // Credential resolution: prefer the conventional Azure env vars; fall back
    // to the ANTHROPIC_FOUNDRY_* pair Claude already uses on this box. The
    // endpoint can be given outright (AZURE_OPENAI_ENDPOINT) or built from a
    // bare Foundry resource name. NOTHING is defaulted in code — if neither key
    // nor endpoint resolves, the embedding subsystem simply stays off.
    var azureKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
                   ?? Environment.GetEnvironmentVariable("ANTHROPIC_FOUNDRY_API_KEY");
    var azureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
    if (string.IsNullOrWhiteSpace(azureEndpoint))
    {
        var resource = Environment.GetEnvironmentVariable("ANTHROPIC_FOUNDRY_RESOURCE");
        if (!string.IsNullOrWhiteSpace(resource))
            azureEndpoint = $"https://{resource}.services.ai.azure.com/";
    }
    var deployment = builder.Configuration["Embedding:Deployment"]
                     ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT")
                     ?? embeddingOptions.Deployment;

    var azureConfigured = !string.IsNullOrWhiteSpace(azureKey) && !string.IsNullOrWhiteSpace(azureEndpoint);
    embeddingOptions.Enabled = azureConfigured;
    if (azureConfigured)
    {
        // Stamp a backend-specific model version so any vectors from a previous
        // backend (e.g. the local Qwen run) are treated as stale and re-embedded
        // — different models live in different vector spaces and must not mix.
        embeddingOptions.ModelVersion = $"azure-{deployment}-d{embeddingOptions.Dim}";
    }

    builder.Services.AddSingleton(embeddingOptions);
    // EmbeddingPaths is still needed even with the cloud backend: it resolves
    // the vendored sqlite-vec dll path (VecReady / VecDllPath) used by the index.
    builder.Services.AddSingleton<Xpp.Service.Embeddings.EmbeddingPaths>();
    builder.Services.AddSingleton<Xpp.Service.Embeddings.EmbeddingWorkSignal>();

    if (azureConfigured)
    {
        builder.Services.AddSingleton<Xpp.Service.Embeddings.IEmbeddingProvider>(sp =>
            new Xpp.Service.Embeddings.AzureOpenAIEmbeddingGenerator(
                azureEndpoint!, azureKey!, deployment!, embeddingOptions.Dim, embeddingOptions.MaxInputChars,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Xpp.Service.Embeddings.AzureOpenAIEmbeddingGenerator>()));
        // The Embedder background service drains pending method/label rows into
        // the vec0 store whenever content lands (full rebuild, sweep, or
        // write-through), nudged via EmbeddingWorkSignal from the lifecycle.
        builder.Services.AddHostedService<Xpp.Service.Embeddings.Embedder>();
    }
    else
    {
        builder.Services.AddSingleton<Xpp.Service.Embeddings.IEmbeddingProvider>(
            _ => new Xpp.Service.Embeddings.DisabledEmbeddingProvider(embeddingOptions.Dim));
    }

    // Expose the provider through the standard M.E.AI seam too.
    builder.Services.AddSingleton<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(
        sp => sp.GetRequiredService<Xpp.Service.Embeddings.IEmbeddingProvider>());

    builder.Services.AddSingleton<SchemaInstaller>();
    builder.Services.AddSingleton<IndexDatabase>();
    builder.Services.AddHostedService<IndexDatabaseInitializer>();

    // Writer task — hosted-service so its start/stop is owned by the host;
    // also registered as a singleton service so the indexer can inject it.
    builder.Services.AddSingleton<IndexWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexWriter>());

    // Typed bridge facade for indexer / search code. Singleton because the
    // underlying BridgeProcess is.
    builder.Services.AddSingleton<BridgeClient>();
    // Languages to extract from AxLabelFile entries. en-US default keeps
    // the labels table small; widen via XppService:LabelLanguages
    // (comma-separated list) when working on a localization.
    var labelLanguages = (builder.Configuration["XppService:LabelLanguages"] ?? "en-US")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    builder.Services.AddSingleton(new IndexerOptions
    {
        LabelLanguages = labelLanguages,
        PackagesLocalDirectory = packagesPath,
    });
    builder.Services.AddSingleton<Indexer>();

    // Service-managed indexing lifecycle. Owns bootstrap-on-startup, lazy
    // delta sweep (kicked from search RPCs), and write-through after
    // domain mutations. The agent doesn't see any of this — there is no
    // MCP-exposed rebuild tool by design.
    builder.Services.AddSingleton<IndexLifecycle>();
    builder.Services.AddHostedService<IndexLifecycleStarter>();

    // === Kestrel named-pipe transport ====================================
    var pipeName = builder.Configuration["XppService:PipeName"] ?? "xpp-service-v2";
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenNamedPipe(pipeName, listenOptions =>
        {
            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
        });
    });

    builder.Services.AddGrpc(options =>
    {
        // Surface real exception messages to the client. Acceptable here
        // because the service is local-only and the calling MCP stub /
        // probe is on the same machine. We're not exposing to untrusted
        // network callers.
        options.EnableDetailedErrors = true;
    });

    var app = builder.Build();

    app.MapGrpcService<PingGrpcService>();

    app.Logger.LogInformation("XppService starting; pipe={Pipe}, bridge={Bridge}", pipeName, bridgePath);

    // Say which metadata store we landed on and how we decided. When this is
    // wrong, it is the single most useful line in the log.
    if (string.IsNullOrEmpty(packagesPath))
    {
        app.Logger.LogError(
            "No D365 PackagesLocalDirectory found - metadata calls will fail. Tried:\n{Trace}\n" +
            "Run 'dt setup', or set D365:PackagesLocalDirectory in {Config}.",
            string.Join(Environment.NewLine, packagesTrace.Select(t => "  - " + t)), globalConfigPath);
    }
    else
    {
        app.Logger.LogInformation("D365 metadata store: {Packages}", packagesPath);
        foreach (var t in packagesTrace) app.Logger.LogDebug("  packages discovery: {Step}", t);
    }

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (FindDowngrade(ex) is { } downgrade)
{
    // Backstop for the race the pre-host probe can't cover: the cache being
    // replaced by a newer build between our peek and the first open. Same
    // message, so the user can't tell which path caught it.
    WriteDowngradeRefusal(downgrade.StoredVersion, downgrade.ExpectedVersion, null);
    return ExitCodeSchemaDowngrade;
}
finally
{
    singleInstanceLock.Dispose();
}

// A downgrade is a user-actionable situation, not a crash: print the choices
// plainly and exit clean. A stack trace here would bury the one line that
// matters, and this path is reached on a perfectly normal launch of an older
// plugin build against a newer cache.
//
// Deliberately NOT self-healing. Clearing the cache costs a full re-index and
// a real embedding bill, and the usual cause is a stale session the user can
// just close — so we name the situation and let them choose.
static void WriteDowngradeRefusal(int stored, int expected, string? dbPath)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("XppService refused to start: the index cache is newer than this build.");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  cache schema version : {stored}");
    Console.Error.WriteLine($"  this build understands: {expected}");
    if (dbPath != null)
        Console.Error.WriteLine($"  cache file           : {dbPath}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  A newer dynamics-xpp plugin has used this cache. Migrations are forward-only,");
    Console.Error.WriteLine("  so running this build against it would corrupt the index. Nothing was touched.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Two ways forward:");
    Console.Error.WriteLine("    1. Run the newer build - usually just close the stale session, or 'dt update'.");
    Console.Error.WriteLine("    2. Discard the cache and re-index from scratch: 'dt cache clear'.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Option 2 costs a full re-index and re-embedding, so prefer option 1.");
    Console.Error.WriteLine();
}

// The backstop's exception reaches us wrapped (AggregateException, or a
// host-startup wrapper), so walk the chain rather than matching the top type.
static SchemaDowngradeException? FindDowngrade(Exception? ex)
{
    while (ex != null)
    {
        if (ex is SchemaDowngradeException d) return d;
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                var found = FindDowngrade(inner);
                if (found != null) return found;
            }
            return null;
        }
        ex = ex.InnerException;
    }
    return null;
}

// =============================================================================
// Hosted service that owns the bridge's process lifetime. Starting/stopping
// the host starts/stops the bridge.
// =============================================================================

/// <summary>
/// Forces the index database to open (and apply the schema on first run)
/// at startup, rather than lazily on the first gRPC request that touches
/// it. Failing here is preferable to discovering the failure mid-request.
/// </summary>
/// <summary>
/// Drives bootstrap-on-startup for the indexing lifecycle. Registered as
/// a hosted service so the host's StartAsync chain kicks the initial
/// sweep without blocking gRPC dispatch.
/// </summary>
file sealed class IndexLifecycleStarter : IHostedService
{
    private readonly IndexLifecycle _lifecycle;
    private CancellationTokenSource? _cts;

    public IndexLifecycleStarter(IndexLifecycle lifecycle) { _lifecycle = lifecycle; }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = new CancellationTokenSource();
        _lifecycle.EnsureBootstrap(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return Task.CompletedTask;
    }
}

file sealed class IndexDatabaseInitializer : IHostedService
{
    private readonly IndexDatabase _db;
    private readonly ILogger<IndexDatabaseInitializer> _logger;

    public IndexDatabaseInitializer(IndexDatabase db, ILogger<IndexDatabaseInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Open and discard; the side effect is the one-time init.
        using var conn = _db.Open();
        _logger.LogInformation("Index database ready at {Path}", _db.DatabasePath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

file sealed class BridgeLifecycle : IHostedService
{
    private readonly BridgePool _pool;
    private readonly ILogger<BridgeLifecycle> _logger;

    public BridgeLifecycle(BridgePool pool, ILogger<BridgeLifecycle> logger)
    {
        _pool = pool;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting bridge pool with {Count} workers", _pool.Size);
        await _pool.StartAllAsync(ct).ConfigureAwait(false);

        // Probe each worker in parallel. If any one of them can't answer a
        // ping in 5 seconds the whole startup fails loud rather than
        // accepting traffic that will silently route to a broken worker.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Each bridge worker spawns a net48 child process and the OS pays
        // disk-load cost on each one. With 8 starting concurrently a 5s
        // budget gets squeezed; 30s leaves comfortable margin without
        // letting a truly broken bridge hold up the service.
        probeCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await Task.WhenAll(_pool.Workers.Select(async (worker, idx) =>
            {
                var probe = new JsonObject { ["echo"] = $"startup-probe-{idx}" };
                var result = await worker.InvokeAsync("ping", probe, probeCts.Token).ConfigureAwait(false);
                var version = result?["bridgeVersion"]?.GetValue<string>() ?? "unknown";
                _logger.LogInformation("Bridge worker {Idx} alive; reported version {Version}", idx, version);
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Bridge pool startup probe failed; aborting service");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _pool.DisposeAsync().ConfigureAwait(false);
    }
}

file static class BridgeExeResolver
{
    /// <summary>
    /// Find XppMetadataBridge.exe. Returns the resolved absolute path or null;
    /// on null, <paramref name="searchTrace"/> lists every candidate checked
    /// so the caller can produce a useful error.
    ///
    /// Priority:
    ///   1. XppService:BridgeExecutable config (explicit override)
    ///   2. XPP_BRIDGE_EXE env var
    ///   3. Sibling: same directory as XppService.exe (defensive)
    ///   4. Walk up from AppContext.BaseDirectory looking for
    ///      src/XppMetadataBridge/bin/{Release,Debug}/net48/XppMetadataBridge.exe
    /// </summary>
    public static string? Resolve(Microsoft.Extensions.Configuration.IConfiguration config, out IReadOnlyList<string> searchTrace)
    {
        var trace = new List<string>();

        // 1. Explicit config override.
        var configured = config["XppService:BridgeExecutable"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(configured, AppContext.BaseDirectory);
            trace.Add($"config XppService:BridgeExecutable -> {resolved}");
            if (File.Exists(resolved))
            {
                searchTrace = trace;
                return resolved;
            }
        }

        // 2. Env var.
        var envPath = Environment.GetEnvironmentVariable("XPP_BRIDGE_EXE");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            trace.Add($"env XPP_BRIDGE_EXE -> {envPath}");
            if (File.Exists(envPath))
            {
                searchTrace = trace;
                return envPath;
            }
        }

        // 3. Sibling (defensive; net48 vs net9 makes this an unusual layout).
        var svcDir = AppContext.BaseDirectory;
        var sibling = Path.Combine(svcDir, "XppMetadataBridge.exe");
        trace.Add($"sibling -> {sibling}");
        if (File.Exists(sibling))
        {
            searchTrace = trace;
            return sibling;
        }

        // 4. Dev-tree walk: start at the service's bin dir, walk up. At each
        // level, try the typical csproj-relative path for both build configs
        // with Release first (our shipping default).
        var dir = new DirectoryInfo(svcDir);
        while (dir != null)
        {
            foreach (var configName in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    "src", "XppMetadataBridge", "bin", configName, "net48",
                    "XppMetadataBridge.exe");
                trace.Add($"walk -> {candidate}");
                if (File.Exists(candidate))
                {
                    searchTrace = trace;
                    return candidate;
                }
            }
            dir = dir.Parent;
        }

        searchTrace = trace;
        return null;
    }
}
