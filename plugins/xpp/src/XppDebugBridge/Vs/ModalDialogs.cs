using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace XppDebugBridge.Vs
{
    /// <summary>
    /// Finds and dismisses modal dialogs owned by the hidden devenv.
    ///
    /// Why: a hidden Visual Studio still raises modal dialogs, and nobody is
    /// there to click them. While one is up, every DTE automation call blocks
    /// (CurrentMode included) and the bridge reports "automation not
    /// responding"; the only release was killing VS, which kills the debuggee.
    /// Measured 2026-10-08: a breakpoint whose condition cannot evaluate
    /// ("noSuchVariable == 1") produces "The condition for a breakpoint failed
    /// to execute ... Click OK to stop at this breakpoint" the moment the
    /// breakpoint is reached, and the session is wedged from then on.
    ///
    /// What the dialog looks like from outside (window dump while wedged): a
    /// VISIBLE top-level WPF window of the devenv process, class
    /// "HwndWrapper[DefaultDomain;;...]", title "Microsoft Visual Studio", no
    /// Win32 children -- VS's modern message box, not a #32770 dialog. So the
    /// text and the OK button are reached through UI Automation, which sees
    /// WPF content; a classic #32770 dialog is handled the same way.
    ///
    /// The bridge's watchdog calls <see cref="DismissAll"/> every second: each
    /// such window has its text captured and its OK (else Yes / Break / the
    /// first button) invoked, WM_CLOSE as a last resort. The text is surfaced
    /// to the agent so a bad condition is reported, not hidden.
    /// </summary>
    internal static class ModalDialogs
    {
        private const int WM_CLOSE = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private const uint GW_OWNER = 4;
        private static readonly string[] PreferredButtons = { "OK", "Yes", "Break", "Continue", "Close", "Cancel" };

        /// <summary>Dismiss every visible dialog owned by <paramref name="pid"/>;
        /// returns one "title: text [pressed X]" line per dialog dismissed.</summary>
        public static List<string> DismissAll(int pid)
        {
            var dismissed = new List<string>();
            if (pid <= 0) return dismissed;
            var candidates = new List<IntPtr>();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var wpid);
                if (wpid != (uint)pid || !IsWindowVisible(h)) return true;
                var cls = ClassOf(h);
                var title = TextOf(h);
                // VS's WPF message box: a visible top-level HwndWrapper titled
                // "Microsoft Visual Studio" (the main window is hidden and has
                // the document title). Classic dialogs are #32770.
                var isWpfBox = cls.StartsWith("HwndWrapper[", StringComparison.Ordinal)
                               && string.Equals(title, "Microsoft Visual Studio", StringComparison.Ordinal);
                if (isWpfBox || cls == "#32770") candidates.Add(h);
                return true;
            }, IntPtr.Zero);

            foreach (var h in candidates)
            {
                string text = string.Empty, pressed = "none";
                try
                {
                    var root = AutomationElement.FromHandle(h);
                    var sb = new StringBuilder();
                    // VS's WPF message box exposes its message through whatever
                    // element type it likes (Text, Document, a Pane's Name); take
                    // every named descendant except the buttons, deduplicated.
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (AutomationElement t in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    {
                        if (t.Current.ControlType == ControlType.Button) continue;
                        var n = (t.Current.Name ?? string.Empty).Trim();
                        if (n.Length == 0 || IsChrome(n) || !seen.Add(n)) continue;
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(n);
                    }
                    text = sb.ToString();
                    if (text.Length > 700) text = text.Substring(0, 700) + "...";

                    var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    AutomationElement? choice = null;
                    foreach (var want in PreferredButtons)
                    {
                        foreach (AutomationElement b in buttons)
                            if (string.Equals((b.Current.Name ?? string.Empty).Trim().TrimEnd('.'), want, StringComparison.OrdinalIgnoreCase)) { choice = b; break; }
                        if (choice != null) break;
                    }
                    if (choice == null && buttons.Count > 0) choice = buttons[0];
                    if (choice != null && choice.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                    {
                        ((InvokePattern)p).Invoke();
                        pressed = choice.Current.Name;
                    }
                }
                catch (Exception ex) { text += " (uia: " + ex.Message + ")"; }

                if (pressed == "none") { PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); pressed = "WM_CLOSE"; }
                dismissed.Add($"{TextOf(h)}: {text} [pressed {pressed}]");
            }
            return dismissed;
        }

        // Title-bar furniture UI Automation names alongside the message.
        private static bool IsChrome(string n)
            => n is "System" or "Menu Bar" or "System Menu Bar" or "Microsoft Visual Studio" or "Close" or "Minimize" or "Maximize" or "Restore";

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TextOf(IntPtr h)
        {
            var len = GetWindowTextLength(h);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
