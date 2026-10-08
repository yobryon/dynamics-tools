using System;
using System.Collections.Generic;
using System.Text;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// Normalizes the leading indentation of caller-supplied X++ method source
    /// so it lands at the canonical on-disk level.
    ///
    /// D365 stores method bodies verbatim in <c>AxMethod.Source</c>, and the
    /// shipped convention indents a method one class-body level in: the
    /// signature at 4 spaces, its body at 8, etc. Agents naturally author a
    /// method as standalone text with the signature at column 0, so it persists
    /// under-indented and the compiler's Best Practice pass flags every line
    /// (<c>BPIndentationError</c>: "element should start in column N"). This
    /// shifts the whole block to the canonical level WITHOUT reflowing it: a
    /// single uniform add/remove of leading whitespace that preserves the
    /// author's relative indentation (body stays deeper than signature).
    ///
    /// Deliberately minimal (not a formatter):
    ///  - measures the block's minimum indent across non-blank lines (tabs
    ///    expanded to 4) and shifts every line by (target - min);
    ///  - a no-op when the block is already at the target (so correctly-indented
    ///    input is untouched);
    ///  - SKIPS a method that contains a multi-line verbatim string (@"...")
    ///    entirely, since shifting would alter the string's literal content;
    ///  - emits spaces (the on-disk convention), preserving the source's
    ///    newline style.
    /// </summary>
    internal static class MethodSource
    {
        private const int TabWidth = 4;

        /// <summary>
        /// Re-indent every method body the CALLER supplied in a create/patch
        /// JSON: any object inside an array named "methods" (at any depth)
        /// whose "source" is a string. Run once on the request, before the
        /// mapper merges it with on-disk content, so pre-existing methods are
        /// never touched (a 20-line append must not become a 400-line diff).
        /// </summary>
        public static void NormalizeAuthoredSources(Newtonsoft.Json.Linq.JToken? node, HashSet<string>? unchanged = null)
        {
            switch (node)
            {
                case Newtonsoft.Json.Linq.JObject o:
                    foreach (var p in o.Properties())
                    {
                        if (string.Equals(p.Name, "methods", StringComparison.OrdinalIgnoreCase) && p.Value is Newtonsoft.Json.Linq.JArray methods)
                        {
                            foreach (var m in methods)
                            {
                                if (m is Newtonsoft.Json.Linq.JObject mo && mo["source"] is Newtonsoft.Json.Linq.JToken s && s.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                {
                                    var src = (string)s!;
                                    if (unchanged == null || !unchanged.Contains(src)) mo["source"] = NormalizeIndent(src);
                                }
                                NormalizeAuthoredSources(m, unchanged);
                            }
                        }
                        else NormalizeAuthoredSources(p.Value, unchanged);
                    }
                    break;
                case Newtonsoft.Json.Linq.JArray a:
                    foreach (var item in a) NormalizeAuthoredSources(item, unchanged);
                    break;
            }
        }

        /// <summary>Every method source text in a domain JSON (any "methods"
        /// array at any depth), for the "did this request change it" check.</summary>
        public static HashSet<string> CollectSources(Newtonsoft.Json.Linq.JToken? node)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            Collect(node, set);
            return set;
        }

        private static void Collect(Newtonsoft.Json.Linq.JToken? node, HashSet<string> set)
        {
            switch (node)
            {
                case Newtonsoft.Json.Linq.JObject o:
                    foreach (var p in o.Properties())
                    {
                        if (string.Equals(p.Name, "methods", StringComparison.OrdinalIgnoreCase) && p.Value is Newtonsoft.Json.Linq.JArray methods)
                            foreach (var m in methods)
                            {
                                if (m is Newtonsoft.Json.Linq.JObject mo && mo["source"] is Newtonsoft.Json.Linq.JToken s && s.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                    set.Add((string)s!);
                                Collect(m, set);
                            }
                        else Collect(p.Value, set);
                    }
                    break;
                case Newtonsoft.Json.Linq.JArray a:
                    foreach (var item in a) Collect(item, set);
                    break;
            }
        }

        /// <summary>Re-indent <paramref name="source"/> so its least-indented
        /// non-blank line sits at <paramref name="targetColumn"/> (default 4,
        /// the class-body level for a method). Returns the input unchanged when
        /// it's empty, already at target, or contains a multi-line verbatim
        /// string.</summary>
        public static string NormalizeIndent(string? source, int targetColumn = 4)
        {
            if (string.IsNullOrEmpty(source)) return source ?? string.Empty;
            if (HasMultilineVerbatimString(source)) return source;

            var newline = source.Contains("\r\n") ? "\r\n" : "\n";
            var lines = source.Replace("\r\n", "\n").Split('\n');

            int min = int.MaxValue;
            foreach (var line in lines)
            {
                if (IsBlank(line)) continue;
                var indent = LeadingWidth(line);
                if (indent < min) min = indent;
            }
            if (min == int.MaxValue) return source;       // all blank
            var delta = targetColumn - min;
            if (delta == 0) return source;                // already canonical

            var sb = new StringBuilder(source.Length + (delta > 0 ? delta * lines.Length : 0));
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append(newline);
                var line = lines[i];
                if (IsBlank(line)) { sb.Append(line.TrimEnd('\r', ' ', '\t')); continue; }
                var width = LeadingWidth(line);
                var content = line.Substring(CountLeadingWhitespaceChars(line));
                sb.Append(new string(' ', Math.Max(0, width + delta)));
                sb.Append(content);
            }
            return sb.ToString();
        }

        private static bool IsBlank(string line) => line.Trim().Length == 0;

        /// <summary>Leading-whitespace width in columns, tabs expanded.</summary>
        private static int LeadingWidth(string line)
        {
            int w = 0;
            foreach (var c in line)
            {
                if (c == ' ') w++;
                else if (c == '\t') w += TabWidth - (w % TabWidth);
                else break;
            }
            return w;
        }

        private static int CountLeadingWhitespaceChars(string line)
        {
            int n = 0;
            foreach (var c in line)
            {
                if (c == ' ' || c == '\t') n++;
                else break;
            }
            return n;
        }

        /// <summary>True if the source contains a verbatim string literal
        /// (@"...") that spans a newline. Re-indenting would inject whitespace
        /// into the literal, so such a method is left exactly as authored.
        /// Conservative scan: ignores the // and /* */ comment cases (harmless
        /// to re-indent) and only tracks @"..." with "" as the escaped quote.</summary>
        private static bool HasMultilineVerbatimString(string s)
        {
            for (int i = 0; i + 1 < s.Length; i++)
            {
                if (s[i] == '@' && s[i + 1] == '"')
                {
                    int j = i + 2;
                    while (j < s.Length)
                    {
                        if (s[j] == '"')
                        {
                            if (j + 1 < s.Length && s[j + 1] == '"') { j += 2; continue; } // escaped ""
                            break;                                                          // closing quote
                        }
                        if (s[j] == '\n') return true;                                      // spans a line
                        j++;
                    }
                    i = j;
                }
            }
            return false;
        }
    }
}
