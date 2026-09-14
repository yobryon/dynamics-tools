using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EnvDTE;
using EnvDTE80;

namespace XppDebugBridge.Vs
{
    /// <summary>
    /// Owns one hidden Visual Studio instance.
    ///
    /// Why VS at all: X++ compiles to plain .NET IL, but the pieces that make
    /// debugging it usable -- the X++ expression evaluator and the resolver
    /// that maps the PDBs' virtual "xppSource://" documents to generated .xpp
    /// files -- live in the Dynamics 365 VS extension. Hosting VS invisibly
    /// and driving its debugger through the automation model gets us all of
    /// that without a UI.
    ///
    /// Hard-won rules, each of which cost a failed experiment:
    ///  - Spawn devenv OURSELVES with "-Embedding" (never CreateInstance via
    ///    COM activation): activation launches it unelevated, and the D365
    ///    package then blocks on a "must run as administrator" modal.
    ///  - Find the DTE in the ROT by pid, not GetActiveObject (that is the
    ///    user's VS).
    ///  - Suppress the "Attach Security Warning" -- a WPF modal that leaves
    ///    the attach pending forever in a hidden instance.
    ///  - Load the D365 package (by opening an .xpp document) BEFORE the
    ///    debugger is asked to bind anything; without it no X++ breakpoint
    ///    binds.
    ///  - Killing this devenv is the guaranteed release: it detaches the
    ///    debugger and the target runs on. It is what <see cref="Kill"/> does
    ///    when automation stops answering, and it never touches the user's own
    ///    Visual Studio because we only ever kill the pid we started.
    /// </summary>
    internal sealed class VsHost : IDisposable
    {
        private readonly StaWorker _sta;
        private readonly Action<string> _log;
        private System.Diagnostics.Process? _process;

        public DTE2? Dte { get; private set; }
        public int Pid => _process is { HasExited: false } ? _process.Id : 0;
        public bool IsAlive => _process is { HasExited: false } && Dte != null;

        public VsHost(StaWorker sta, Action<string> log)
        {
            _sta = sta;
            _log = log;
        }

        /// <summary>Spawn a hidden VS and wait until its automation model answers. Idempotent.</summary>
        public void EnsureStarted()
        {
            if (IsAlive) return;
            Dte = null; _process = null;
            var exe = DevenvLocator.Find()
                ?? throw new InvalidOperationException("Visual Studio 2022 (devenv.exe) was not found on this machine; the X++ debugger needs it.");

            var sw = Stopwatch.StartNew();
            _process = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-Embedding",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("Failed to start devenv.exe");

            object? dte = null;
            for (var i = 0; i < 240 && dte == null; i++)
            {
                dte = _sta.Invoke(() => RotHelper.FindDte(_process.Id), 10_000, "rot-lookup");
                if (dte == null)
                {
                    if (_process.HasExited) throw new InvalidOperationException($"devenv exited during startup (code {_process.ExitCode})");
                    System.Threading.Thread.Sleep(500);
                }
            }
            if (dte == null) throw new TimeoutException("devenv never registered its automation object");

            Dte = (DTE2)dte;
            _sta.Invoke(() => { Dte.UserControl = false; }, 30_000, "user-control");
            // Readiness: the debugger object answers. Then settle -- attaching
            // in the first seconds after that is rejected for a long while.
            _sta.Invoke(() => { var _ = Dte.Debugger.CurrentMode; }, 240_000, "debugger-ready");
            System.Threading.Thread.Sleep(6000);
            _log($"devenv {_process.Id} ready in {sw.Elapsed.TotalSeconds:N1}s");

            ImportSettings();
        }

        /// <summary>
        /// Disable the Attach Security Warning through a settings import --
        /// the only automation-reachable way to set it. Note this lands in the
        /// user's shared VS settings store; it is the standard D365 dev-box
        /// tweak, but it IS a side effect and the skill says so.
        /// </summary>
        private void ImportSettings()
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "dynamics-xpp-no-attach-warning.vssettings");
                File.WriteAllText(path,
                    "<UserSettings><ApplicationIdentity version=\"17.0\"/><ToolsOptions/>" +
                    "<Category name=\"Debugger\" Category=\"{EEDBF29A-5C8B-4E01-827C-263382C18CFE}\" Package=\"{C9DD4A57-47FB-11D2-83E7-00C04F9902C1}\" RegisteredName=\"Debugger\" PackageName=\"Visual Studio Debugger\">" +
                    "<PropertyValue name=\"DisableAttachSecurityWarning\">1</PropertyValue></Category></UserSettings>");
                _sta.Invoke(() => Dte!.ExecuteCommand("Tools.ImportandExportSettings", $"/import:\"{path}\""), 60_000, "import-settings");
                System.Threading.Thread.Sleep(1500);
            }
            catch (Exception ex)
            {
                _log($"settings import failed (attach may hang on the security warning): {ex.Message}");
            }
        }

        /// <summary>
        /// Open a document in the hidden VS. Opening any .xpp loads the D365
        /// package. Only used while the debuggee is RUNNING: opening a document
        /// while it is paused is the call that once wedged automation for
        /// minutes, so breakpoints are set without opening their file.
        /// </summary>
        public void OpenFile(string path, bool closeAfter, int timeoutMs)
        {
            _sta.Invoke(() =>
            {
                var w = Dte!.ItemOperations.OpenFile(path);
                if (closeAfter && w != null) { try { w.Close(vsSaveChanges.vsSaveChangesNo); } catch { } }
            }, timeoutMs, "open-file");
        }

        /// <summary>
        /// The escape hatch: terminate OUR devenv. Killing the debugger process
        /// detaches it and the target resumes immediately; nothing here needs
        /// the automation model, so it works precisely when automation does not.
        /// A thread blocked inside a COM call into that VS gets an RPC failure
        /// and unwinds, which also clears the STA worker's wedge.
        /// </summary>
        public bool Kill(string reason)
        {
            var p = _process;
            Dte = null;
            if (p == null) return false;
            var killed = false;
            try
            {
                if (!p.HasExited)
                {
                    _log($"killing hidden devenv {p.Id}: {reason}");
                    p.Kill();
                    p.WaitForExit(10_000);
                    killed = true;
                }
            }
            catch (Exception ex) { _log($"kill devenv failed: {ex.Message}"); }
            finally { try { p.Dispose(); } catch { } _process = null; }
            return killed;
        }

        public void Dispose()
        {
            try
            {
                if (Dte != null && !_sta.IsWedged)
                {
                    try { _sta.Invoke(() => { try { Dte.Debugger.DetachAll(); } catch { } }, 20_000, "detach-all"); } catch { }
                    try { _sta.Invoke(() => Dte.Quit(), 10_000, "quit"); } catch { }
                }
            }
            finally
            {
                Dte = null;
                if (_process != null)
                {
                    try { if (!_process.WaitForExit(5000)) _process.Kill(); } catch { }
                    try { _process.Dispose(); } catch { }
                    _process = null;
                }
            }
        }
    }

    /// <summary>
    /// Locate the devenv.exe to host. Candidates come from vswhere and the
    /// conventional 2022 paths; only a real devenv.exe qualifies (vswhere's
    /// "-products *" also lists VS-shell products such as SQL Server
    /// Management Studio, and launching SSMS.exe -Embedding never yields a
    /// DTE -- that cost a 400s hang), and an instance that carries the
    /// Dynamics 365 tools extension wins, because that extension IS the X++
    /// debugging support.
    /// </summary>
    internal static class DevenvLocator
    {
        public static string? Find()
        {
            var candidates = new System.Collections.Generic.List<string>();
            try
            {
                var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (File.Exists(vswhere))
                {
                    var psi = new ProcessStartInfo(vswhere, "-all -prerelease -products * -property productPath")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = System.Diagnostics.Process.Start(psi);
                    var output = p?.StandardOutput.ReadToEnd() ?? string.Empty;
                    p?.WaitForExit(10000);
                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        candidates.Add(line.Trim());
                }
            }
            catch { }

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var edition in new[] { "Enterprise", "Professional", "Community", "Preview" })
                candidates.Add(Path.Combine(pf, "Microsoft Visual Studio", "2022", edition, "Common7", "IDE", "devenv.exe"));

            var real = candidates
                .Where(c => c.EndsWith("devenv.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (real.Count == 0) return null;
            return real.FirstOrDefault(HasD365Tools) ?? real[0];
        }

        /// <summary>Does this VS install carry the Dynamics 365 tools extension (its DLLs sit under Common7\IDE\Extensions\&lt;random&gt;\)?</summary>
        public static bool HasD365Tools(string devenvPath)
        {
            try
            {
                var ext = Path.Combine(Path.GetDirectoryName(devenvPath)!, "Extensions");
                return Directory.Exists(ext) && Directory.EnumerateDirectories(ext)
                    .Any(d => File.Exists(Path.Combine(d, "Microsoft.Dynamics.AX.Metadata.dll")));
            }
            catch { return false; }
        }
    }
}
