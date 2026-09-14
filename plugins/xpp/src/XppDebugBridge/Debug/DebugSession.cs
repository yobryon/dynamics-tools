using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using EnvDTE90a;
using XppDebugBridge.Vs;

namespace XppDebugBridge.Debug
{
    /// <summary>
    /// The one debug session this bridge owns: a hidden VS attached to one
    /// F&amp;O process, a set of breakpoints, and whatever the debuggee is doing
    /// right now. Every VS call goes through the STA worker.
    ///
    /// Safety posture: a paused AOS freezes every client session on the box,
    /// so nothing here leaves the target paused unattended -- a watchdog
    /// resumes it after <see cref="MaxPauseSeconds"/>, breakpoints are deleted
    /// on detach, and detach always resumes first.
    /// </summary>
    internal sealed class DebugSession : IDisposable
    {
        private const string EngineName = "Managed (.NET Framework 4.x)";
        public int MaxPauseSeconds = 300;

        private readonly StaWorker _sta;
        private readonly VsHost _vs;
        private readonly string _packagesDir;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private readonly Timer _watchdog;
        private readonly List<BreakpointRecord> _breakpoints = new List<BreakpointRecord>();
        private DateTime? _pausedSince;
        private bool _packageLoaded;
        private int _nextBpId = 1;

        public TargetResolver.Target? Target { get; private set; }
        public bool Attached => Target != null && _vs.IsAlive;
        public bool AutoResumedSinceLastReport { get; private set; }

        public sealed class BreakpointRecord
        {
            public int Id;
            public string AxType = string.Empty;
            public string Name = string.Empty;
            public string Method = string.Empty;
            public string File = string.Empty;
            public int RequestedLine;
            public int BoundLine;
            public bool Bound;
            public string? Condition;
        }

        public DebugSession(StaWorker sta, VsHost vs, string packagesDir, Action<string> log)
        {
            _sta = sta; _vs = vs; _packagesDir = packagesDir; _log = log;
            _watchdog = new Timer(_ => Watchdog(), null, 5000, 5000);
        }

        private DTE2 Dte => _vs.Dte ?? throw new InvalidOperationException("Visual Studio is not running");
        private EnvDTE.Debugger Dbg => Dte.Debugger;

        // ---- attach / detach ---------------------------------------------------

        public object Attach(string targetName)
        {
            lock (_gate)
            {
                if (Attached)
                    throw new InvalidOperationException($"already attached to {Target!.ProcessName} (pid {Target.Pid}); detach first");

                var target = TargetResolver.Resolve(targetName);
                var sw = Stopwatch.StartNew();
                _vs.EnsureStarted();
                EnsurePackageLoaded();

                // LocalProcesses is unreliable after its first enumeration in a
                // given instance; take one snapshot and use it.
                EnvDTE.Process? proc = null;
                var seen = 0;
                _sta.Invoke(() =>
                {
                    foreach (EnvDTE.Process p in Dbg.LocalProcesses) { seen++; if (p.ProcessID == target.Pid) proc = p; }
                }, 120_000);
                if (proc == null)
                    throw new InvalidOperationException(
                        $"Visual Studio cannot see pid {target.Pid} ({seen} processes visible). " +
                        "The debugger must run elevated to attach to a NETWORK SERVICE process -- start Claude Code as administrator and restart the plugin service (dt service restart).");

                _sta.Invoke(() =>
                {
                    var d2 = (Debugger2)Dbg;
                    var engines = new List<string>();
                    foreach (Engine e in d2.Transports.Item("Default").Engines) engines.Add(e.Name);
                    if (!engines.Contains(EngineName))
                        throw new InvalidOperationException($"engine '{EngineName}' not offered by this VS; have: {string.Join(" | ", engines)}");
                    ((Process2)proc).Attach2(EngineName);
                }, 240_000);

                // Attach2 returns before the session is fully up.
                var ready = false;
                for (var i = 0; i < 120 && !ready; i++)
                {
                    ready = _sta.Invoke(() => { var n = 0; foreach (EnvDTE.Process p in Dbg.DebuggedProcesses) n++; return n > 0; });
                    if (!ready) System.Threading.Thread.Sleep(500);
                }
                if (!ready) throw new InvalidOperationException("attach did not complete (no debugged process reported)");

                Target = target;
                _pausedSince = null;
                _log($"attached to {target.ProcessName} pid {target.Pid} in {sw.Elapsed.TotalSeconds:N1}s");
                return new
                {
                    attached = true,
                    target = target.Kind,
                    pid = target.Pid,
                    processName = target.ProcessName,
                    engine = EngineName,
                    elapsedMs = sw.ElapsedMilliseconds,
                    vsPid = _vs.Pid,
                };
            }
        }

        public object Detach()
        {
            lock (_gate)
            {
                if (!Attached) { return new { attached = false, note = "not attached" }; }
                var resumed = false;
                try
                {
                    _sta.Invoke(() =>
                    {
                        if (Dbg.CurrentMode == dbgDebugMode.dbgBreakMode) { Dbg.Go(false); resumed = true; }
                    });
                }
                catch { }
                try { ClearBreakpointsCore(); } catch { }
                _sta.Invoke(() => Dbg.DetachAll(), 60_000);
                var t = Target; Target = null; _pausedSince = null;
                _log($"detached from {t!.ProcessName} pid {t.Pid}");
                return new { attached = false, detachedFrom = t.Kind, pid = t.Pid, resumedBeforeDetach = resumed };
            }
        }

        /// <summary>
        /// The D365 package provides the xppSource:// document resolver. It
        /// loads when an .xpp document is opened; nothing binds until it has.
        /// Global is in ApplicationPlatform and exists on every box.
        /// </summary>
        private void EnsurePackageLoaded()
        {
            if (_packageLoaded) return;
            var xml = Path.Combine(_packagesDir, "ApplicationPlatform", "ApplicationPlatform", "AxClass", "Global.xml");
            if (!File.Exists(xml)) throw new FileNotFoundException("ApplicationPlatform Global.xml not found under the packages directory", xml);
            var cache = XppSourceGenerator.CachePath(_packagesDir, "ApplicationPlatform", "AxClass", "Global");
            XppSourceGenerator.Materialize(xml, cache);
            var sw = Stopwatch.StartNew();
            _vs.OpenFile(cache, closeAfter: false);
            _packageLoaded = true;
            _log($"D365 package loaded via {Path.GetFileName(cache)} in {sw.Elapsed.TotalSeconds:N1}s");
        }

        // ---- breakpoints ---------------------------------------------------------

        public object SetBreakpoint(string axType, string name, string method, string model, string xmlPath, int offset, string? condition)
        {
            lock (_gate)
            {
                RequireAttached();
                var cache = XppSourceGenerator.CachePath(_packagesDir, model, axType, name);
                var gen = XppSourceGenerator.Materialize(xmlPath, cache);
                if (!gen.MethodDeclarationLines.TryGetValue(method, out var declLine) || declLine == 0)
                    throw new ArgumentException($"method '{method}' not found on {axType} {name} (have: {string.Join(", ", gen.MethodDeclarationLines.Keys.Take(30))}{(gen.MethodDeclarationLines.Count > 30 ? ", ..." : "")})");

                // offset 0 = the declaration line; VS moves the breakpoint to the
                // first statement at or after the requested line anyway.
                var line = declLine + Math.Max(0, offset);
                _vs.OpenFile(cache, closeAfter: true);

                var rec = new BreakpointRecord { Id = _nextBpId++, AxType = axType, Name = name, Method = method, File = cache, RequestedLine = line, Condition = condition };
                _sta.Invoke(() =>
                {
                    var before = Dbg.Breakpoints.Count;
                    Dbg.Breakpoints.Add("", cache, line, 1, condition ?? "",
                        dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue, "", "", 1, "", 1, dbgHitCountType.dbgHitCountTypeNone);
                    var bp = Dbg.Breakpoints.Item(before + 1);
                    ((Breakpoint2)bp).Tag = "xpp:" + rec.Id;
                });

                // Binding is quick once symbols are loaded; give it a few seconds.
                for (var i = 0; i < 20 && !rec.Bound; i++)
                {
                    RefreshBinding(rec);
                    if (!rec.Bound) System.Threading.Thread.Sleep(500);
                }
                _breakpoints.Add(rec);
                return Describe(rec, gen);
            }
        }

        private object Describe(BreakpointRecord rec, XppSourceGenerator.Generated? gen)
        {
            string? sourceLine = null;
            try
            {
                var l = rec.Bound ? rec.BoundLine : rec.RequestedLine;
                var lines = File.ReadAllLines(rec.File);
                if (l >= 1 && l <= lines.Length) sourceLine = lines[l - 1].Trim();
            }
            catch { }
            return new
            {
                id = rec.Id,
                axType = rec.AxType,
                name = rec.Name,
                method = rec.Method,
                file = rec.File,
                requestedLine = rec.RequestedLine,
                bound = rec.Bound,
                boundLine = rec.Bound ? rec.BoundLine : (int?)null,
                sourceLine,
                condition = rec.Condition,
                note = rec.Bound ? null :
                    "not bound yet: the module may not be loaded in the target (it binds when it loads), or this code path lives in a form/extension whose source we cannot generate.",
            };
        }

        private void RefreshBinding(BreakpointRecord rec)
        {
            _sta.Invoke(() =>
            {
                foreach (Breakpoint b in Dbg.Breakpoints)
                {
                    if (((Breakpoint2)b).Tag != "xpp:" + rec.Id) continue;
                    var kids = 0; try { kids = b.Children.Count; } catch { }
                    if (kids > 0)
                    {
                        rec.Bound = true;
                        try { rec.BoundLine = b.Children.Item(1).FileLine; } catch { rec.BoundLine = b.FileLine; }
                    }
                    return;
                }
            });
        }

        public object ListBreakpoints()
        {
            lock (_gate)
            {
                foreach (var r in _breakpoints) { if (!r.Bound && Attached) { try { RefreshBinding(r); } catch { } } }
                return _breakpoints.Select(r => Describe(r, null)).ToArray();
            }
        }

        public object ClearBreakpoints()
        {
            lock (_gate) { var n = ClearBreakpointsCore(); return new { cleared = n }; }
        }

        private int ClearBreakpointsCore()
        {
            var n = _breakpoints.Count;
            if (_vs.IsAlive)
            {
                _sta.Invoke(() =>
                {
                    var all = new List<Breakpoint>();
                    foreach (Breakpoint b in Dbg.Breakpoints) all.Add(b);
                    foreach (var b in all) { try { b.Delete(); } catch { } }
                });
            }
            _breakpoints.Clear();
            return n;
        }

        // ---- run control -----------------------------------------------------------

        /// <summary>Block until a breakpoint hits (or the timeout). On a hit the target stays PAUSED.</summary>
        public object Wait(int timeoutSec, string[] watch)
        {
            RequireAttached();
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSec));
            while (DateTime.UtcNow < deadline)
            {
                if (IsBroken())
                {
                    lock (_gate) { _pausedSince = _pausedSince ?? DateTime.UtcNow; return Capture(watch, hit: true); }
                }
                System.Threading.Thread.Sleep(200);
            }
            return new { hit = false, timedOut = true, timeoutSec, autoResumed = TakeAutoResumed() };
        }

        public object Continue()
        {
            lock (_gate)
            {
                RequireAttached();
                var was = IsBroken();
                if (was) _sta.Invoke(() => Dbg.Go(false));
                _pausedSince = null;
                return new { resumed = was, wasPaused = was };
            }
        }

        public object Step(string kind, string[] watch)
        {
            lock (_gate)
            {
                RequireAttached();
                if (!IsBroken()) throw new InvalidOperationException("not paused at a breakpoint; step only applies while paused");
                _sta.Invoke(() =>
                {
                    switch (kind.ToLowerInvariant())
                    {
                        case "into": Dbg.StepInto(false); break;
                        case "out": Dbg.StepOut(false); break;
                        default: Dbg.StepOver(false); break;
                    }
                });
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && !IsBroken()) System.Threading.Thread.Sleep(100);
                if (!IsBroken())
                {
                    _pausedSince = null;
                    return new { hit = false, note = "the step did not stop within 20s (the code ran on past all breakpoints); the target is running" };
                }
                _pausedSince = DateTime.UtcNow;
                return Capture(watch, hit: true);
            }
        }

        public object Eval(string expression, bool expand)
        {
            lock (_gate)
            {
                RequireAttached();
                if (!IsBroken()) throw new InvalidOperationException("not paused; expressions can only be evaluated while stopped at a breakpoint");
                return _sta.Invoke<object>(() =>
                {
                    var e = Dbg.GetExpression(expression, true, 5000);
                    return new
                    {
                        expression,
                        valid = e.IsValidValue,
                        type = e.Type,
                        value = Trunc(e.Value, 2000),
                        members = expand ? Members(e, 60) : null,
                    };
                }, 60_000);
            }
        }

        public object Status()
        {
            var attached = Attached;
            var mode = attached ? _sta.Invoke(() => Dbg.CurrentMode.ToString()) : "detached";
            return new
            {
                attached,
                target = Target?.Kind,
                pid = Target?.Pid,
                processName = Target?.ProcessName,
                mode,
                paused = attached && mode == "dbgBreakMode",
                pausedSeconds = _pausedSince.HasValue ? (int)(DateTime.UtcNow - _pausedSince.Value).TotalSeconds : 0,
                maxPauseSeconds = MaxPauseSeconds,
                breakpoints = _breakpoints.Count,
                vsPid = _vs.Pid,
                vsAlive = _vs.IsAlive,
                autoResumed = TakeAutoResumed(),
            };
        }

        // ---- capture ---------------------------------------------------------------

        private object Capture(string[] watch, bool hit)
        {
            return _sta.Invoke<object>(() =>
            {
                var th = Dbg.CurrentThread;
                var frames = new List<object>();
                if (th != null)
                {
                    var k = 0;
                    foreach (EnvDTE.StackFrame f in th.StackFrames)
                    {
                        if (++k > 30) break;
                        frames.Add(DescribeFrame(f, k));
                    }
                }
                var fr = Dbg.CurrentStackFrame;
                object? thisValue = null; var args = new List<object>(); var locals = new List<object>();
                if (fr != null)
                {
                    try { var e = Dbg.GetExpression("this", true, 5000); if (e.IsValidValue) thisValue = new { type = e.Type, value = Trunc(e.Value, 300) }; } catch { }
                    try { var n = 0; foreach (Expression a in fr.Arguments) { if (++n > 20) break; args.Add(new { name = a.Name, type = a.Type, value = Trunc(a.Value, 300) }); } } catch { }
                    try { var n = 0; foreach (Expression l in fr.Locals) { if (++n > 40) break; locals.Add(new { name = l.Name, type = l.Type, value = Trunc(l.Value, 300) }); } } catch { }
                }
                var watches = new List<object>();
                foreach (var w in watch ?? new string[0])
                {
                    try { var e = Dbg.GetExpression(w, true, 5000); watches.Add(new { expression = w, valid = e.IsValidValue, type = e.Type, value = Trunc(e.Value, 1000) }); }
                    catch (Exception ex) { watches.Add(new { expression = w, valid = false, error = ex.Message }); }
                }
                object? hitBp = null;
                try
                {
                    var b = Dbg.BreakpointLastHit;
                    if (b != null) hitBp = new { file = NormalizeFile(b.File), line = b.FileLine, function = b.FunctionName };
                }
                catch { }

                return new
                {
                    hit,
                    paused = true,
                    threadId = th?.ID,
                    threadName = th?.Name,
                    breakpoint = hitBp,
                    location = frames.Count > 0 ? frames[0] : null,
                    @this = thisValue,
                    arguments = args,
                    locals,
                    watches,
                    stack = frames,
                    maxPauseSeconds = MaxPauseSeconds,
                    note = "the target is PAUSED (all AOS sessions wait). Use debug.eval / debug.step, then debug.continue. It auto-resumes after maxPauseSeconds.",
                };
            }, 120_000);
        }

        private object DescribeFrame(EnvDTE.StackFrame f, int index)
        {
            string fn = "", lang = "", file = ""; var line = 0;
            try { fn = f.FunctionName; } catch { }
            try { lang = f.Language; } catch { }
            try { if (f is StackFrame2 f2) { file = f2.FileName ?? ""; line = (int)f2.LineNumber; } } catch { }
            var norm = NormalizeFile(file);
            string? sourceLine = null;
            if (norm != null && line > 0)
            {
                try
                {
                    var p = Path.Combine(_packagesDir, "bin", "XppSource", norm);
                    if (File.Exists(p)) { var lines = File.ReadAllLines(p); if (line <= lines.Length) sourceLine = lines[line - 1].Trim(); }
                }
                catch { }
            }
            var isXpp = lang == "X++" || fn.StartsWith("Dynamics.AX.Application.", StringComparison.Ordinal);
            return new { index, function = fn, language = lang, xpp = isXpp, file = norm, line = line > 0 ? line : (int?)null, sourceLine };
        }

        /// <summary>"xppSource://Source/Model\AxClass_X.xpp" and "...\bin\XppSource\Model\AxClass_X.xpp" both become "Model\AxClass_X.xpp".</summary>
        private static string? NormalizeFile(string? file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var m = Regex.Match(file!, @"(?:xppSource://Source/|\\bin\\XppSource\\)(?<rel>.+)$", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["rel"].Value : file;
        }

        private static List<object> Members(Expression e, int max)
        {
            var list = new List<object>();
            try
            {
                var n = 0;
                foreach (Expression m in e.DataMembers)
                {
                    if (++n > max) break;
                    list.Add(new { name = m.Name, type = m.Type, value = Trunc(m.Value, 300) });
                }
            }
            catch { }
            return list;
        }

        private static string Trunc(string? s, int n)
        {
            if (s == null) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > n ? s.Substring(0, n) + "..." : s;
        }

        private bool IsBroken()
        {
            if (!Attached) return false;
            try { return _sta.Invoke(() => Dbg.CurrentMode == dbgDebugMode.dbgBreakMode, 30_000); }
            catch { return false; }
        }

        private void RequireAttached()
        {
            if (!Attached) throw new InvalidOperationException("not attached: call debug.attach first (target: aos | batch)");
        }

        private bool TakeAutoResumed()
        {
            var v = AutoResumedSinceLastReport; AutoResumedSinceLastReport = false; return v;
        }

        /// <summary>Never leave the AOS frozen: resume a pause that outlived its budget.</summary>
        private void Watchdog()
        {
            try
            {
                if (!Monitor.TryEnter(_gate, 0)) return;
                try
                {
                    if (!Attached || !_pausedSince.HasValue) return;
                    if ((DateTime.UtcNow - _pausedSince.Value).TotalSeconds < MaxPauseSeconds) return;
                    if (IsBroken())
                    {
                        _sta.Invoke(() => Dbg.Go(false));
                        AutoResumedSinceLastReport = true;
                        _log($"watchdog: target was paused for more than {MaxPauseSeconds}s -- resumed");
                    }
                    _pausedSince = null;
                }
                finally { Monitor.Exit(_gate); }
            }
            catch (Exception ex) { _log("watchdog error: " + ex.Message); }
        }

        public void Dispose()
        {
            try { _watchdog.Dispose(); } catch { }
            try { if (Attached) Detach(); } catch { }
        }
    }
}
