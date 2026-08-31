using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Xpp.Service.Contracts.V1;

namespace Xpp.Service.Mcp.Grpc;

/// <summary>
/// Newest-wins service negotiation, client half. See
/// docs/versioning-and-servicing-design.md.
///
/// One XppService owns the box (global mutex + well-known pipe + the SQLite
/// index). Sessions come and go asynchronously, so a user who updates the
/// plugin can easily end up with a NEW MCP talking to an OLD service left
/// running by a session started before the update — silently running last
/// week's code. The rule: the newest build wins. When this MCP finds an older
/// service on the pipe, it asks that service to stand down and lets the
/// connection factory spawn its own.
///
/// Cooperative first, forceful second:
///   1. <c>RequestShutdown</c> — the service answers, then drains in-flight
///      calls, checkpoints the DB, and releases the pipe + mutex.
///   2. If the process hasn't exited within the grace period, kill it by the
///      pid it reported. Guarded by a process-name check so a recycled pid
///      can't get an innocent process killed.
///   3. Wait for the pipe to disappear, so the next dial takes the spawn path
///      rather than reconnecting to a listener that's on its way out.
///
/// Everything here is best-effort. A takeover that fails logs loudly and
/// leaves the old service running — a stale-but-working service beats a
/// broken one, and the user still has <c>dt service restart</c>.
/// </summary>
internal static class ServiceTakeover
{
    /// <summary>How long we let a superseded service exit on its own before killing it.</summary>
    private static readonly TimeSpan CooperativeGrace = TimeSpan.FromSeconds(20);

    /// <summary>How long we wait for the pipe to go away after the process exits.</summary>
    private static readonly TimeSpan PipeReleaseGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Supersede the running service. Returns true when the box is clear (old
    /// service gone, pipe released) and the caller should re-dial to bring up
    /// its own build.
    /// </summary>
    public static async Task<bool> SupersedeAsync(
        XppService.XppServiceClient client,
        string pipeName,
        string runningVersion,
        string myVersion,
        ILogger logger,
        CancellationToken ct)
    {
        int pid;
        try
        {
            var rsp = await client.RequestShutdownAsync(
                new ShutdownRequest { Reason = $"superseded by plugin {myVersion} (running {Describe(runningVersion)})" },
                deadline: DateTime.UtcNow.AddSeconds(10),
                cancellationToken: ct);

            if (!rsp.Accepted)
            {
                logger.LogWarning("Running XppService declined the shutdown request; continuing against it.");
                return false;
            }
            pid = rsp.ProcessId;
            logger.LogInformation("Superseded XppService (plugin {Old}, pid {Pid}) accepted shutdown.", Describe(rsp.PluginVersion), pid);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            // A pre-increment-2 service has no RequestShutdown. Nothing
            // cooperative to do, and we have no pid to escalate to (that field
            // arrived in the same contract bump), so leave it be and say so.
            logger.LogWarning(
                "Running XppService (plugin {Old}) predates the takeover protocol and can't be superseded automatically. " +
                "Stop it manually (or run 'dt service restart') to pick up plugin {Mine}.",
                Describe(runningVersion), myVersion);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not ask the running XppService to shut down; continuing against it.");
            return false;
        }

        if (!await WaitForExitAsync(pid, logger, ct).ConfigureAwait(false))
            return false;

        return await WaitForPipeReleaseAsync(pipeName, logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for the superseded process to exit, escalating to a kill after the
    /// grace period. Returns false only if it's still alive after the kill
    /// attempt (in which case taking over would just race it).
    /// </summary>
    private static async Task<bool> WaitForExitAsync(int pid, ILogger logger, CancellationToken ct)
    {
        Process? proc;
        try
        {
            proc = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // Already gone — it exited between answering us and our lookup.
            return true;
        }

        using (proc)
        {
            // Capture the name now, while the handle is live: after exit,
            // ProcessName throws, and we need it for the recycled-pid guard.
            string name;
            try { name = proc.ProcessName; }
            catch { return true; }

            try
            {
                using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                graceCts.CancelAfter(CooperativeGrace);
                await proc.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                logger.LogInformation("Superseded XppService (pid {Pid}) exited cleanly.", pid);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Grace expired. Escalate — but only against something that
                // still looks like the service we asked to stop.
                if (!string.Equals(name, "XppService", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "Pid {Pid} is '{Name}', not XppService — refusing to kill it. Takeover abandoned.", pid, name);
                    return false;
                }

                logger.LogWarning(
                    "Superseded XppService (pid {Pid}) did not exit within {Grace}s; killing it.",
                    pid, CooperativeGrace.TotalSeconds);
                try
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to kill the superseded XppService (pid {Pid}); takeover abandoned.", pid);
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// The listener can outlive the last gRPC response by a beat. Wait until
    /// the named pipe is really gone so our next dial takes the spawn path
    /// instead of attaching to a dying listener.
    /// </summary>
    private static async Task<bool> WaitForPipeReleaseAsync(string pipeName, ILogger logger, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + PipeReleaseGrace;
        while (DateTime.UtcNow < deadline)
        {
            if (!PipeExists(pipeName))
                return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        logger.LogWarning("Pipe '{Pipe}' still present {Grace}s after the old service exited; takeover abandoned.",
            pipeName, PipeReleaseGrace.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Named pipes are enumerable as files under the pipe filesystem root.
    /// Cheaper and less ambiguous than dialing (a dial can succeed against a
    /// listener that is mid-teardown).
    /// </summary>
    private static bool PipeExists(string pipeName)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\")
                .Any(p => string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // If we can't enumerate, don't block the takeover on it.
            return false;
        }
    }

    private static string Describe(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "pre-versioning" : version;
}
