using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XppDebugBridge.Debug
{
    /// <summary>
    /// Produces the .xpp text Visual Studio's D365 extension would generate for
    /// an AOT element, byte-for-byte.
    ///
    /// The X++ PDBs don't reference files on disk; they reference the virtual
    /// document "xppSource://Source/&lt;Model&gt;\&lt;AxType&gt;_&lt;Name&gt;.xpp", and
    /// the VS extension resolves that to
    /// "&lt;PackagesLocalDirectory&gt;\bin\XppSource\&lt;Model&gt;\&lt;AxType&gt;_&lt;Name&gt;.xpp",
    /// generating the file the first time a developer opens the code. A
    /// breakpoint only binds against that document, so to debug an element
    /// nobody has opened in VS we have to produce the identical file
    /// ourselves -- identical, because the PDB's line numbers are for THIS
    /// layout.
    ///
    /// The layout (validated against a VS-generated file: 751/751 lines equal):
    ///   - the class declaration block with leading/trailing blank lines
    ///     trimmed and its closing brace dropped,
    ///   - then, for each method in XML order: its source with blank edges
    ///     trimmed (the XML already carries the 4-space indentation), followed
    ///     by one blank line,
    ///   - then the closing brace.
    /// </summary>
    internal static class XppSourceGenerator
    {
        private static readonly string[] SupportedRoots = { "AxClass", "AxTable", "AxDataEntityView", "AxView", "AxQuery", "AxMap" };

        public sealed class Generated
        {
            public string Text = string.Empty;
            /// <summary>1-based line of each method's declaration (the line containing "name(").</summary>
            public Dictionary<string, int> MethodDeclarationLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            /// <summary>1-based first line of each method's source block (doc comments included).</summary>
            public Dictionary<string, int> MethodStartLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsSupported(string axType) => SupportedRoots.Contains(axType, StringComparer.OrdinalIgnoreCase);

        public static Generated Generate(string xmlPath)
        {
            var doc = XDocument.Load(xmlPath);
            var root = doc.Root ?? throw new InvalidDataException("empty AOT XML");
            if (!IsSupported(root.Name.LocalName))
                throw new NotSupportedException(
                    $"{root.Name.LocalName} source generation is not supported yet (forms and extensions have a different layout). " +
                    "Set the breakpoint in a class or table method on the call path instead.");

            var sourceCode = root.Element("SourceCode") ?? throw new InvalidDataException("AOT XML has no SourceCode element");
            var declaration = (string?)sourceCode.Element("Declaration") ?? string.Empty;

            var lines = new List<string>();
            var decl = TrimBlankEdges(SplitLines(declaration));
            if (decl.Count > 0 && decl[decl.Count - 1].Trim() == "}") decl.RemoveAt(decl.Count - 1);
            lines.AddRange(decl);

            var result = new Generated();
            var methods = sourceCode.Element("Methods")?.Elements("Method") ?? Enumerable.Empty<XElement>();
            foreach (var m in methods)
            {
                var name = (string?)m.Element("Name") ?? string.Empty;
                var src = TrimBlankEdges(SplitLines((string?)m.Element("Source") ?? string.Empty));
                var start = lines.Count + 1;
                result.MethodStartLines[name] = start;
                var declRegex = new Regex(@"\b" + Regex.Escape(name) + @"\s*\(", RegexOptions.IgnoreCase);
                var declLine = 0;
                for (var i = 0; i < src.Count; i++)
                {
                    var t = src[i].TrimStart();
                    if (t.StartsWith("//") || t.StartsWith("[")) continue;   // doc comments / attributes
                    if (declRegex.IsMatch(src[i])) { declLine = start + i; break; }
                }
                if (declLine == 0 && src.Count > 0) declLine = start;
                result.MethodDeclarationLines[name] = declLine;
                lines.AddRange(src);
                lines.Add(string.Empty);
            }
            lines.Add("}");

            result.Text = string.Join("\r\n", lines);
            return result;
        }

        /// <summary>Where VS expects the generated file for this element.</summary>
        public static string CachePath(string packagesDir, string model, string axType, string name)
            => Path.Combine(packagesDir, "bin", "XppSource", model, $"{axType}_{name}.xpp");

        /// <summary>
        /// Materialize the file at the cache path. Returns the generation
        /// result. Only rewrites when the content differs, so a file VS itself
        /// produced (and may have open) is left untouched.
        /// </summary>
        public static Generated Materialize(string xmlPath, string cachePath)
        {
            var gen = Generate(xmlPath);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var existing = File.Exists(cachePath) ? File.ReadAllText(cachePath) : null;
            if (existing == null || !string.Equals(existing, gen.Text, StringComparison.Ordinal))
                File.WriteAllText(cachePath, gen.Text, new UTF8Encoding(false));
            return gen;
        }

        private static List<string> SplitLines(string s)
            => s.Replace("\r\n", "\n").Split('\n').ToList();

        private static List<string> TrimBlankEdges(List<string> l)
        {
            var a = 0; var b = l.Count;
            while (a < b && string.IsNullOrWhiteSpace(l[a])) a++;
            while (b > a && string.IsNullOrWhiteSpace(l[b - 1])) b--;
            return l.GetRange(a, b - a);
        }
    }
}
