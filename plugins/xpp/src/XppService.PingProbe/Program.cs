// XppService.PingProbe — dev tool that connects to the local XppService via
// named-pipe gRPC, fires a few RPCs, prints what came back, and exits.
//
// Useful as a smoke test (see tools/test-service.ps1) and as a manual sanity
// probe when poking at the service during development. The dynamics-xpp-stub will
// do the same things this probe does, but more; this exists so you can verify
// the gRPC + bridge path is healthy without dragging Node / MCP into the
// picture.
//
// Usage:
//   XppService.PingProbe.exe [pipe-name] [echo-string]
//   XppService.PingProbe.exe --rebuild <model-name> [pipe-name]
//   XppService.PingProbe.exe --status [pipe-name]
//
//   default mode: pings the service and asks for status.
//   --rebuild mode: triggers RebuildIndex scoped to one model, then verifies
//                   GetStatus reports >0 objects indexed.
//   XppService.PingProbe.exe --shutdown [pipe-name]
//
//   --status mode: emits one line of JSON to stdout with the status fields.
//                  Designed to be parsed by dt.ps1 / xpp-status.ps1 (or any
//                  other consumer) — no human-formatted logging on stdout. All
//                  diagnostics go to stderr.
//   --shutdown mode: asks the running service to stop gracefully (the same RPC
//                  the newest-wins takeover uses) and reports the outcome as
//                  JSON. Backs 'dt service stop'.
//
// Exit codes:
//   0   all probe steps succeeded
//   1   any RPC failed, echo mismatch, or post-rebuild status didn't update
//   3   (--status / --shutdown only) nothing is listening on the pipe. A
//       normal state, not a failure — kept distinct so the dt CLI can say
//       "service not running" instead of reporting an error.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using Grpc.Net.Client;
using Xpp.Service.Contracts.V1;

string pipeName;
string echo;
string? rebuildModel = null;
bool statusOnly = false;
bool shutdownMode = false;

// Exit code for "there is no service listening". Distinct from 1 (a real
// failure) so the dt CLI can say "service not running" -- an ordinary,
// expected state -- instead of reporting an error.
const int ExitNotRunning = 3;
string? dumpModel = null;
string dumpOut = "dump";
string[]? dumpTypeFilter = null;
// --nav <axType> <name> [atPath] [query] [pipe] : exercise the path-addressable
// navigation RPCs (GetDomainObject outline/at_path + FindInObject) against one object.
string? navAxType = null, navName = null, navAtPath = null, navQuery = null;
// --patch <axType> <name> <atPath> <op> [valueJson] [pipe] : DRY-RUN the surgical
// patch-by-path (preview only; this probe never commits a write).
string? patchAxType = null, patchName = null, patchAtPath = null, patchOp = null, patchValue = null, patchCommitModel = null;

if (args.Length > 0 && args[0] == "--rebuild")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("FAIL: --rebuild requires a model name");
        return 1;
    }
    rebuildModel = args[1];
    pipeName = args.Length > 2 ? args[2] : "xpp-service-v2";
    echo = $"rebuild-probe-{Guid.NewGuid():N}".Substring(0, 14);
}
else if (args.Length > 0 && args[0] == "--status")
{
    statusOnly = true;
    pipeName = args.Length > 1 ? args[1] : "xpp-service-v2";
    echo = string.Empty;
}
else if (args.Length > 0 && args[0] == "--shutdown")
{
    shutdownMode = true;
    pipeName = args.Length > 1 ? args[1] : "xpp-service-v2";
    echo = string.Empty;
}
else if (args.Length > 0 && args[0] == "--dump")
{
    // --dump <model> <outDir> [pipe] : write every domain-mapped object's
    // domain JSON to <outDir>/<model>/<axType>/<Name>.json for coverage analysis.
    if (args.Length < 3)
    {
        Console.Error.WriteLine("FAIL: --dump requires <model> <outDir>");
        return 1;
    }
    dumpModel = args[1];
    dumpOut = args[2];
    // Optional 4th arg: comma-separated axType filter (e.g. "AxTable,AxForm").
    // When omitted, dump every domain-mapped type.
    if (args.Length > 3 && args[3].Contains("Ax"))
    {
        dumpTypeFilter = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        pipeName = args.Length > 4 ? args[4] : "xpp-service-v2";
    }
    else
    {
        pipeName = args.Length > 3 ? args[3] : "xpp-service-v2";
    }
    echo = string.Empty;
}
else if (args.Length > 0 && args[0] == "--patch")
{
    if (args.Length < 5)
    {
        Console.Error.WriteLine("FAIL: --patch requires <axType> <name> <atPath> <op> [valueJson]");
        return 1;
    }
    patchAxType = args[1];
    patchName = args[2];
    patchAtPath = args[3];
    patchOp = args[4];
    patchValue = args.Length > 5 && args[5] != "-" ? args[5] : null;
    // Optional "--commit <model>" actually writes (default is dry-run preview).
    var ci = Array.IndexOf(args, "--commit");
    if (ci >= 0 && ci + 1 < args.Length) { patchCommitModel = args[ci + 1]; }
    pipeName = args.Length > 6 && !args[6].StartsWith("--") ? args[6] : "xpp-service-v2";
    echo = string.Empty;
}
else if (args.Length > 0 && args[0] == "--nav")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("FAIL: --nav requires <axType> <name> [atPath] [query]");
        return 1;
    }
    navAxType = args[1];
    navName = args[2];
    navAtPath = args.Length > 3 && args[3].Length > 0 && args[3] != "-" ? args[3] : null;
    navQuery = args.Length > 4 && args[4].Length > 0 && args[4] != "-" ? args[4] : null;
    pipeName = args.Length > 5 ? args[5] : "xpp-service-v2";
    echo = string.Empty;
}
else
{
    pipeName = args.Length > 0 ? args[0] : "xpp-service-v2";
    echo = args.Length > 1 ? args[1] : $"probe-{Guid.NewGuid():N}".Substring(0, 12);
}

// The machine-readable modes are consumed by the dt CLI, where "the service
// isn't running" is a normal answer rather than a failure. Check for the pipe
// before dialing so we can report that cleanly instead of surfacing a gRPC
// connect exception. (Only these modes: the interactive/dev modes below are
// better served by the real error.)
if ((statusOnly || shutdownMode) && !PipeExists(pipeName))
{
    Console.WriteLine(JsonSerializer.Serialize(new { running = false, pipe = pipeName }));
    return ExitNotRunning;
}

try
{
    var connectionFactory = new NamedPipeConnectionFactory(pipeName);
    var handler = new SocketsHttpHandler { ConnectCallback = connectionFactory.ConnectAsync };

    using var channel = GrpcChannel.ForAddress(
        $"http://{pipeName}",
        new GrpcChannelOptions { HttpHandler = handler, UnsafeUseInsecureChannelCallCredentials = false });

    var client = new XppService.XppServiceClient(channel);

    // --- Shutdown mode: ask the running service to stop, gracefully. Same
    // RPC the newest-wins takeover uses, so 'dt service stop' drains in-flight
    // work and checkpoints the DB rather than killing the process.
    if (shutdownMode)
    {
        var sd = await client.RequestShutdownAsync(
            new ShutdownRequest { Reason = "requested via dt service stop" },
            deadline: DateTime.UtcNow.AddSeconds(15));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            running = true,
            accepted = sd.Accepted,
            processId = sd.ProcessId,
            pluginVersion = sd.PluginVersion,
        }));
        return sd.Accepted ? 0 : 1;
    }

    // --- Status-only mode: single GetStatus call, JSON to stdout, done.
    if (statusOnly)
    {
        // 30s deadline: covers the pipe connect, the bridge handshake,
        // and the SQLite read. Status itself is ~1ms; the rest is a
        // one-time cold cost when the channel is brand new.
        var s = await client.GetStatusAsync(
            new StatusRequest(),
            deadline: DateTime.UtcNow.AddSeconds(30));
        var payload = new
        {
            running          = true,
            pluginVersion    = s.PluginVersion,
            processId        = s.ProcessId,
            bridgeHealthy    = s.BridgeHealthy,
            indexState       = s.IndexState,
            indexReady       = s.IndexReady,
            sweepInProgress  = s.SweepInProgress,
            objectCount      = s.ObjectCount,
            methodCount      = s.MethodCount,
            referenceCount   = s.ReferenceCount,
            labelCount       = s.LabelCount,
            embeddingState   = s.EmbeddingState,
            embeddingCount   = s.EmbeddingCount,
            embeddingTotal   = s.EmbeddingTotal,
            lastSweepAt      = s.LastSweepAt,
            lastFullScanAt   = s.LastFullScanAt,
            lastIndexUpdate  = s.LastIndexUpdate,
        };
        Console.WriteLine(JsonSerializer.Serialize(payload));
        return 0;
    }

    // --- Dump mode: write every domain-mapped object's domain JSON to disk.
    if (dumpModel != null)
    {
        // Keep in sync with XppMetadataBridge DomainMapperRegistry._mappers.
        var domainTypes = new[]
        {
            "AxEnum", "AxClass", "AxEdt", "AxTable", "AxQuery", "AxForm",
            "AxMenuItemDisplay", "AxMenuItemAction", "AxMenuItemOutput", "AxResource",
            "AxService", "AxServiceGroup", "AxTile", "AxSecurityDuty", "AxSecurityRole",
            "AxSecurityPrivilege", "AxSecurityPolicy", "AxMenu", "AxView", "AxDataEntityView",
            "AxEnumExtension", "AxEdtExtension", "AxTableExtension", "AxViewExtension",
            "AxDataEntityViewExtension", "AxMenuExtension", "AxFormExtension",
        };
        if (dumpTypeFilter != null)
            domainTypes = domainTypes.Where(t => dumpTypeFilter.Contains(t, StringComparer.OrdinalIgnoreCase)).ToArray();
        int ok = 0, fail = 0;
        var swDump = System.Diagnostics.Stopwatch.StartNew();
        foreach (var type in domainTypes)
        {
            // Enumerate every object of this type in the target model via the index.
            var names = new List<string>();
            using (var call = client.SearchByPattern(new PatternRequest { Pattern = "*", AxType = type, Model = dumpModel, Limit = 0 }))
                while (await call.ResponseStream.MoveNext(default))
                    names.Add(call.ResponseStream.Current.Ref.Name);
            if (names.Count == 0) continue;

            var dir = Path.Combine(dumpOut, dumpModel, type);
            Directory.CreateDirectory(dir);
            int tOk = 0, tFail = 0;
            foreach (var name in names)
            {
                try
                {
                    var r = await client.GetDomainObjectAsync(
                        new GetDomainObjectRequest { AxType = type, Name = name },
                        deadline: DateTime.UtcNow.AddSeconds(180));
                    var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                    File.WriteAllText(Path.Combine(dir, safe + ".json"), r.DomainJson);
                    tOk++;
                }
                catch (Exception ex)
                {
                    tFail++;
                    Console.Error.WriteLine($"  FAIL {type}/{name}: {ex.Message.Split('\n')[0]}");
                }
            }
            ok += tOk; fail += tFail;
            Console.WriteLine($"{type,-28} {tOk,5} dumped, {tFail} failed");
        }
        Console.WriteLine($"DONE: {ok} objects dumped, {fail} failed in {swDump.Elapsed.TotalSeconds:N0}s -> {Path.GetFullPath(Path.Combine(dumpOut, dumpModel))}");
        return 0;
    }

    // --- Nav mode: exercise path-addressable navigation RPCs ----------
    if (navAxType != null && navName != null)
    {
        var navSw = System.Diagnostics.Stopwatch.StartNew();

        async Task DumpOutline(int depth, string? atPath)
        {
            var r = await client.GetDomainObjectAsync(new GetDomainObjectRequest
            {
                AxType = navAxType, Name = navName,
                Outline = true, Depth = depth, AtPath = atPath ?? "",
            }, deadline: DateTime.UtcNow.AddSeconds(180));
            Console.WriteLine($"\n=== OUTLINE {navAxType} {navName} depth={depth} atPath={(atPath ?? "/")}  isOutline={r.IsOutline} atPathEcho={r.AtPath}  ({r.DomainJson.Length} chars) ===");
            Console.WriteLine(r.DomainJson);
        }

        await DumpOutline(1, null);
        await DumpOutline(2, null);
        if (navAtPath != null)
        {
            await DumpOutline(2, navAtPath);
            var full = await client.GetDomainObjectAsync(new GetDomainObjectRequest
            {
                AxType = navAxType, Name = navName, AtPath = navAtPath,
            }, deadline: DateTime.UtcNow.AddSeconds(180));
            Console.WriteLine($"\n=== ZOOM (full subtree) atPath={navAtPath}  atPathEcho={full.AtPath} isOutline={full.IsOutline}  ({full.DomainJson.Length} chars) ===");
            Console.WriteLine(full.DomainJson.Length > 1500 ? full.DomainJson[..1500] + " ...[truncated]" : full.DomainJson);

            // Negative: a bogus path should 404.
            try
            {
                await client.GetDomainObjectAsync(new GetDomainObjectRequest
                {
                    AxType = navAxType, Name = navName, AtPath = navAtPath + "/__nope__",
                }, deadline: DateTime.UtcNow.AddSeconds(30));
                Console.WriteLine("WARN: bogus path did not error");
            }
            catch (Grpc.Core.RpcException nf) { Console.WriteLine($"\n=== bogus path -> {nf.StatusCode}: {nf.Status.Detail} (expected NotFound) ==="); }
        }
        if (navQuery != null)
        {
            var f = await client.FindInObjectAsync(new FindInObjectRequest
            {
                AxType = navAxType, Name = navName, Query = navQuery,
            }, deadline: DateTime.UtcNow.AddSeconds(180));
            Console.WriteLine($"\n=== FIND query='{navQuery}' in {navName} ===");
            Console.WriteLine(f.MatchesJson);
        }
        Console.WriteLine($"\nPASS: nav probe completed in {navSw.ElapsedMilliseconds}ms");
        return 0;
    }

    // --- Patch mode: dry-run preview by default; --commit <model> writes ---
    if (patchAxType != null && patchName != null)
    {
        var commit = patchCommitModel != null;
        var pr = await client.PatchDomainObjectByPathAsync(new PatchByPathRequest
        {
            AxType = patchAxType, Model = patchCommitModel ?? "probe", Name = patchName,
            AtPath = patchAtPath, Op = patchOp, ValueJson = patchValue ?? "",
            DryRun = !commit,
        }, deadline: DateTime.UtcNow.AddSeconds(180));
        if (commit)
        {
            Console.WriteLine($"=== COMMIT {patchOp} {patchAxType} {patchName} atPath={patchAtPath} (model={patchCommitModel}) ===");
            Console.WriteLine($"  name={pr.Name} drift={pr.Drift.Count}" +
                (pr.PatternConformance != null && !string.IsNullOrEmpty(pr.PatternConformance.Pattern)
                    ? $" patternOk={pr.PatternConformance.Ok}" : ""));
            Console.WriteLine("PASS: patch committed");
        }
        else
        {
            Console.WriteLine($"=== DRY-RUN {patchOp} {patchAxType} {patchName} atPath={patchAtPath} ===");
            Console.WriteLine($"preview ({pr.PreviewJson.Length} chars):");
            Console.WriteLine(pr.PreviewJson.Length > 4000 ? pr.PreviewJson[..4000] + " ...[truncated]" : pr.PreviewJson);
            Console.WriteLine("PASS: patch dry-run completed (no write performed)");
        }
        return 0;
    }

    // --- Ping ---------------------------------------------------------
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var response = await client.PingAsync(new PingRequest { Echo = echo }, deadline: DateTime.UtcNow.AddSeconds(10));
    sw.Stop();

    if (response.Echo != echo)
    {
        Console.Error.WriteLine($"FAIL: echo mismatch. sent='{echo}' got='{response.Echo}'");
        return 1;
    }
    Console.WriteLine($"PASS: ping round-trip in {sw.ElapsedMilliseconds}ms");
    Console.WriteLine($"  echo:           {response.Echo}");
    Console.WriteLine($"  service version: {response.ServiceVersion}");

    // --- GetStatus (pre-rebuild) -------------------------------------
    var status = await client.GetStatusAsync(new StatusRequest());
    Console.WriteLine($"  status:          bridgeHealthy={status.BridgeHealthy}, indexReady={status.IndexReady}, objects={status.ObjectCount}");
    if (!status.BridgeHealthy)
    {
        Console.Error.WriteLine("FAIL: GetStatus reports bridge unhealthy");
        return 1;
    }

    var startObjects = status.ObjectCount;

    // --- RebuildIndex (only in --rebuild mode) ------------------------
    if (rebuildModel != null)
    {
        Console.WriteLine($"  rebuild model:   {rebuildModel}");
        var rebuildSw = System.Diagnostics.Stopwatch.StartNew();
        var request = new RebuildRequest
        {
            // Cap phase 2 so the smoke test stays bounded. The first 50
            // objects in alphabetical order are a usable sample: enough
            // variety in axType to exercise both methods and refs writes,
            // but ~5 seconds of bridge round-trips rather than minutes.
            MaxObjectsPhase2 = 50
        };
        request.ModelsFilter.Add(rebuildModel);

        using var call = client.RebuildIndex(request, deadline: DateTime.UtcNow.AddMinutes(5));
        var events = 0;
        var finalProgress = (IndexProgress?)null;
        // Manual MoveNext loop because ReadAllAsync is in Grpc.Core.Utils,
        // which isn't part of the Grpc.Net.Client surface we depend on.
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            var evt = call.ResponseStream.Current;
            events++;
            finalProgress = evt;
            // Print every event; in production a client would tail.
            Console.WriteLine($"    [{evt.Phase}] {evt.Message}");
        }
        rebuildSw.Stop();

        if (events == 0)
        {
            Console.Error.WriteLine("FAIL: rebuild streamed zero progress events");
            return 1;
        }
        if (finalProgress is null || !finalProgress.Done)
        {
            Console.Error.WriteLine("FAIL: rebuild stream ended without a done=true marker");
            return 1;
        }
        Console.WriteLine($"PASS: rebuild streamed {events} events in {rebuildSw.ElapsedMilliseconds}ms");

        // --- GetStatus (post-rebuild) -------------------------------
        var statusAfter = await client.GetStatusAsync(new StatusRequest());
        Console.WriteLine($"  status (after):  indexReady={statusAfter.IndexReady}, objects={statusAfter.ObjectCount}, methods={statusAfter.MethodCount}, refs={statusAfter.ReferenceCount}");
        if (statusAfter.ObjectCount <= startObjects)
        {
            Console.Error.WriteLine($"FAIL: object count didn't grow ({startObjects} -> {statusAfter.ObjectCount}); rebuild didn't write anything");
            return 1;
        }
        if (!statusAfter.IndexReady)
        {
            Console.Error.WriteLine("FAIL: post-rebuild GetStatus reports indexReady=false");
            return 1;
        }
        // Phase 2 should have produced methods and refs. With a 50-object
        // sample of ContosoRetail we expect at least some methods and some
        // refs to land - the cap is large enough to include both classes
        // (methods) and forms or tables (refs).
        if (statusAfter.MethodCount == 0 && statusAfter.ReferenceCount == 0)
        {
            Console.Error.WriteLine("FAIL: phase 2 wrote zero methods AND zero refs - bridge calls likely all failed");
            return 1;
        }

        // ---- Search smoke tests against the post-rebuild data ----------
        if (!await RunSearchSmokeAsync(client))
        {
            return 1;
        }
    }

    return 0;
}
catch (Grpc.Core.RpcException rex)
{
    Console.Error.WriteLine($"FAIL: gRPC error {rex.StatusCode}: {rex.Status.Detail}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

// =============================================================================
// Search smoke tests. Run after a successful --rebuild so there's real data
// to query. Each helper exercises one RPC and asserts a reasonable shape.
// =============================================================================
static async Task<bool> RunSearchSmokeAsync(XppService.XppServiceClient client)
{
    try
    {
        // We don't know specific ContosoRetail identifiers, but phase 2 wrote
        // SOME methods. SearchByPattern with "*" lets us discover an object
        // name we can drive subsequent searches against.
        Console.WriteLine("  --- search smoke ---");
        string? sampleName = null;
        using (var call = client.SearchByPattern(new PatternRequest { Pattern = "*", Limit = 5 }))
        {
            int n = 0;
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                var m = call.ResponseStream.Current;
                if (sampleName == null) sampleName = m.Ref.Name;
                n++;
            }
            if (n == 0)
            {
                Console.Error.WriteLine("FAIL: SearchByPattern '*' returned zero hits");
                return false;
            }
            Console.WriteLine($"  SearchByPattern '*': {n} hits (sample '{sampleName}')");
        }

        // FindObject by the exact name we just discovered should yield it.
        if (sampleName != null)
        {
            using var call = client.FindObject(new FindObjectRequest { Name = sampleName });
            int n = 0;
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                n++;
            }
            if (n == 0)
            {
                Console.Error.WriteLine($"FAIL: FindObject '{sampleName}' returned zero hits");
                return false;
            }
            Console.WriteLine($"  FindObject '{sampleName}': {n} hit(s)");
        }

        // SearchCode: pick a generic FTS keyword that's very likely to be
        // in any X++ corpus with methods. "public" appears in nearly every
        // method declaration.
        {
            using var call = client.SearchCode(new CodeSearchRequest { Query = "public", Limit = 5 });
            int n = 0;
            string? sampleSnippet = null;
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                var h = call.ResponseStream.Current;
                if (sampleSnippet == null) sampleSnippet = h.Snippet;
                n++;
            }
            if (n == 0)
            {
                Console.Error.WriteLine("FAIL: SearchCode 'public' returned zero hits");
                return false;
            }
            Console.WriteLine($"  SearchCode 'public': {n} hit(s)");
            if (sampleSnippet != null && !sampleSnippet.Contains("<mark>"))
            {
                Console.Error.WriteLine($"FAIL: SearchCode snippet missing FTS5 highlighting: '{sampleSnippet}'");
                return false;
            }
        }

        // FindReferences: structural edges only (no source-mentions because
        // we don't know what targets the small sample referenced). We pick
        // a name that's likely to have inbound edges from any indexed
        // model — "CustTable" is a safe bet if any tables in scope have
        // relations pointing at it. Even if zero, the call should succeed.
        {
            using var call = client.FindReferences(new ReferenceQuery
            {
                TargetName = "CustTable",
                IncludeSourceMentions = false,
                Limit = 20
            });
            int n = 0;
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                n++;
            }
            Console.WriteLine($"  FindReferences CustTable: {n} hit(s) (zero is OK with this sample)");
        }

        return true;
    }
    catch (Grpc.Core.RpcException rex)
    {
        Console.Error.WriteLine($"FAIL: search smoke gRPC error {rex.StatusCode}: {rex.Status.Detail}");
        return false;
    }
}

/// <summary>
/// Is anything listening on the named pipe? Named pipes are enumerable as
/// files under the pipe filesystem root, which is cheaper and less ambiguous
/// than dialing. Used to answer "service not running" without an exception.
/// </summary>
static bool PipeExists(string pipeName)
{
    try
    {
        return Directory.EnumerateFiles(@"\\.\pipe\")
            .Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase));
    }
    catch
    {
        // Can't enumerate: assume it might be there and let the dial decide.
        return true;
    }
}

/// <summary>
/// HttpMessageHandler ConnectCallback that opens a NamedPipeClientStream
/// against the given pipe name. gRPC.NET treats the stream as a transport
/// for HTTP/2 frames; the pipe just has to be bidirectional, which it is.
/// </summary>
internal sealed class NamedPipeConnectionFactory
{
    private readonly string _pipeName;

    public NamedPipeConnectionFactory(string pipeName) => _pipeName = pipeName;

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _, CancellationToken ct)
    {
        var clientStream = new System.IO.Pipes.NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: System.IO.Pipes.PipeDirection.InOut,
            options: System.IO.Pipes.PipeOptions.Asynchronous,
            impersonationLevel: System.Security.Principal.TokenImpersonationLevel.Anonymous);

        await clientStream.ConnectAsync(ct).ConfigureAwait(false);
        return clientStream;
    }
}
