using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Dynamics.AX.Metadata.Providers;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// Shared helpers for the label CRUD handlers. Owns:
    ///   - Resolving (labelFileId, language) -> on-disk .label.txt path via
    ///     the AxLabelFile metadata object's RelativeUriInModelStore.
    ///   - Parsing the resource file into a list of typed entries that
    ///     preserves original ordering, comments, blank lines, and BOM.
    ///   - Writing the entries back, restoring the encoding the file was
    ///     read with so re-serialization is round-trip stable.
    ///
    /// All five label RPCs go through these helpers so the IO/parsing
    /// behavior is identical across read, search, and mutation paths.
    /// </summary>
    internal static class LabelOperations
    {
        // -------------------------------------------------------------------
        // Resolution
        // -------------------------------------------------------------------

        /// <summary>
        /// Locate the AxLabelFile metadata object for (labelFileId, language)
        /// across both providers (custom first). Returns the resolved file
        /// object, the absolute on-disk path of its .label.txt resource file,
        /// and the model that owns it. Throws JsonRpcException(ObjectNotFound)
        /// if no matching artifact exists in either provider.
        /// </summary>
        public static ResolvedLabelFile Resolve(MetadataProviderHost host, string labelFileId, string language)
        {
            if (string.IsNullOrWhiteSpace(labelFileId))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "'label_file_id' is required");
            if (string.IsNullOrWhiteSpace(language))
                language = "en-US";

            var artifactName = $"{labelFileId}_{language}";

            var providers = host.CustomDistinctFromStandard
                ? new[] { host.Custom, host.Standard }
                : new[] { host.Standard };

            foreach (var provider in providers)
            {
                var labelFilesProp = provider.GetType().GetProperty("LabelFiles");
                if (labelFilesProp == null) continue;
                var labelFilesProvider = labelFilesProp.GetValue(provider);
                if (labelFilesProvider == null) continue;

                // ReadByName / Read(string) returns the AxLabelFile for the
                // requested artifact name (LabelFileId + "_" + Language).
                var readMethod = labelFilesProvider.GetType().GetMethod(
                    "Read",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);
                if (readMethod == null) continue;

                object? labelFileObj;
                try { labelFileObj = readMethod.Invoke(labelFilesProvider, new object[] { artifactName }); }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    // Read can throw when the artifact doesn't exist in this
                    // provider; that's normal — fall through to the next one.
                    continue;
                }
                if (labelFileObj == null) continue;

                var relUri = labelFileObj.GetType().GetProperty("RelativeUriInModelStore")?.GetValue(labelFileObj) as string;
                if (string.IsNullOrWhiteSpace(relUri))
                {
                    throw new JsonRpcException(
                        JsonRpcErrorCodes.InternalError,
                        $"AxLabelFile '{artifactName}' has no RelativeUriInModelStore — metadata is incomplete.");
                }

                // The relative URI is rooted in the metadata store; absolute
                // path is the provider's metadata root + relUri. For the
                // custom provider on Tier-1 setups that's the same physical
                // path as standard, so trying both in order finds the file
                // either way.
                var roots = host.CustomDistinctFromStandard
                    ? new[] { host.Config.CustomMetadataPath, host.Config.PackagesLocalDirectory }
                    : new[] { host.Config.PackagesLocalDirectory };

                foreach (var root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    var candidate = Path.Combine(root, relUri.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        return new ResolvedLabelFile(labelFileObj, candidate, labelFileId, language);
                    }
                }

                // We found the AxLabelFile artifact but the resource file
                // isn't there — surface the path we expected so the user
                // can see the disconnect.
                throw new JsonRpcException(
                    JsonRpcErrorCodes.ObjectNotFound,
                    $"AxLabelFile '{artifactName}' resolves to '{relUri}' but the resource file does not exist on disk.");
            }

            throw new JsonRpcException(
                JsonRpcErrorCodes.ObjectNotFound,
                $"Label file '{labelFileId}' for language '{language}' (artifact '{artifactName}') was not found in any loaded model.");
        }

        // -------------------------------------------------------------------
        // Parsing
        // -------------------------------------------------------------------

        /// <summary>
        /// Read a label resource file as raw bytes (to detect and preserve
        /// the BOM) and return the parsed entries plus the encoding info
        /// needed to write the file back unchanged when no entry changes.
        /// </summary>
        public static ParsedLabelFile Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            int startIndex = hasBom ? 3 : 0;
            string content = Encoding.UTF8.GetString(bytes, startIndex, bytes.Length - startIndex);

            // Detect newline style for round-trip stability. Default to CRLF
            // on Windows boxes which is what VS writes; fall back to LF if
            // we see no CRLF anywhere in the file.
            string newline = content.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";

            var entries = new List<LabelLine>();
            var rawLines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int i = 0; i < rawLines.Length; i++)
            {
                var line = rawLines[i];
                if (line.Length == 0)
                {
                    entries.Add(LabelLine.Blank(i + 1));
                    continue;
                }

                // A line starting with ' ;' is a description for the most
                // recently emitted label-bearing line, NOT a top-level
                // comment. Attach it to the prior entry if one exists.
                if (line.StartsWith(" ;", StringComparison.Ordinal))
                {
                    var desc = line.Substring(2);
                    var lastEntry = entries.LastOrDefault(e => e.Kind == LabelLineKind.Label);
                    if (lastEntry != null && lastEntry.Description == null)
                    {
                        lastEntry.Description = desc;
                        lastEntry.DescriptionLineNumber = i + 1;
                        continue;
                    }
                    // Orphan description (no preceding label). Treat as a
                    // comment to preserve the file intact on round-trip.
                    entries.Add(LabelLine.Comment(line, i + 1));
                    continue;
                }

                if (line[0] == ';' || line[0] == '#')
                {
                    entries.Add(LabelLine.Comment(line, i + 1));
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    entries.Add(LabelLine.Comment(line, i + 1));
                    continue;
                }

                var labelId = line.Substring(0, eq);
                var value = line.Substring(eq + 1);
                entries.Add(new LabelLine
                {
                    Kind = LabelLineKind.Label,
                    LabelId = labelId,
                    Value = value,
                    LabelLineNumber = i + 1,
                });
            }

            // Strip the trailing synthetic blank from the final split if the
            // file ended in a newline — otherwise we'd emit an extra blank
            // on rewrite.
            if (entries.Count > 0
                && entries[entries.Count - 1].Kind == LabelLineKind.Blank
                && (content.EndsWith("\n", StringComparison.Ordinal) || content.EndsWith("\r\n", StringComparison.Ordinal)))
            {
                entries.RemoveAt(entries.Count - 1);
            }

            return new ParsedLabelFile(path, entries, hasBom, newline);
        }

        /// <summary>
        /// Write a parsed file back to disk, preserving original encoding
        /// (BOM + newline style). The file is written via a temp-then-rename
        /// dance so a crash mid-write doesn't truncate the original.
        /// </summary>
        public static void Save(ParsedLabelFile file)
        {
            var sb = new StringBuilder(file.Entries.Count * 64);
            for (int i = 0; i < file.Entries.Count; i++)
            {
                var e = file.Entries[i];
                switch (e.Kind)
                {
                    case LabelLineKind.Blank:
                        sb.Append(file.Newline);
                        break;
                    case LabelLineKind.Comment:
                        sb.Append(e.RawLine).Append(file.Newline);
                        break;
                    case LabelLineKind.Label:
                        sb.Append(e.LabelId).Append('=').Append(e.Value).Append(file.Newline);
                        if (e.Description != null)
                        {
                            sb.Append(" ;").Append(e.Description).Append(file.Newline);
                        }
                        break;
                }
            }

            // Encoding: UTF-8 with BOM if the original had one, no BOM
            // otherwise. The Encoding(false/true) ctor controls BOM emission
            // when WriteAllText is used; we drive it explicitly.
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: file.HasBom, throwOnInvalidBytes: false);
            byte[] body = encoding.GetBytes(sb.ToString());
            byte[] bytes;
            if (file.HasBom)
            {
                // UTF8Encoding(true).GetBytes does NOT prepend the BOM to the
                // output array — it only does so via stream-writing APIs that
                // check the preamble. Prepend it manually for parity with the
                // input we read.
                var preamble = encoding.GetPreamble();
                bytes = new byte[preamble.Length + body.Length];
                Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
                Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            }
            else
            {
                bytes = body;
            }

            var tempPath = file.Path + ".tmp";
            File.WriteAllBytes(tempPath, bytes);
            // File.Replace is atomic on NTFS when source/dest are on the same
            // volume. Falls back to a delete+move on rare filesystem corners.
            try
            {
                File.Replace(tempPath, file.Path, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                // Original gone (shouldn't happen in our flow, but harmless
                // to handle): just move into place.
                File.Move(tempPath, file.Path);
            }
        }

        // -------------------------------------------------------------------
        // Search / find
        // -------------------------------------------------------------------

        public static IEnumerable<LabelSearchHit> Search(ParsedLabelFile file, Regex regex, bool matchDescription, int limit)
        {
            int yielded = 0;
            foreach (var entry in file.Entries)
            {
                if (entry.Kind != LabelLineKind.Label) continue;
                bool valueHit = regex.IsMatch(entry.Value ?? string.Empty);
                bool descHit = matchDescription && entry.Description != null && regex.IsMatch(entry.Description);
                if (!valueHit && !descHit) continue;
                yield return new LabelSearchHit(
                    labelId: entry.LabelId!,
                    value: entry.Value ?? string.Empty,
                    description: entry.Description ?? string.Empty,
                    line: entry.LabelLineNumber,
                    matchedIn: valueHit ? "value" : "description");
                yielded++;
                if (limit > 0 && yielded >= limit) yield break;
            }
        }

        public static LabelLine? Find(ParsedLabelFile file, string labelId)
        {
            foreach (var entry in file.Entries)
            {
                if (entry.Kind != LabelLineKind.Label) continue;
                if (string.Equals(entry.LabelId, labelId, StringComparison.Ordinal)) return entry;
            }
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Records / helper types
    // ---------------------------------------------------------------------

    internal enum LabelLineKind { Blank, Comment, Label }

    /// <summary>
    /// One logical line in a parsed label file. Three kinds:
    ///   Blank   — empty line, preserved on rewrite
    ///   Comment — line that's not a label entry (preserved verbatim
    ///             in RawLine)
    ///   Label   — LabelId + Value + optional Description (each backed by
    ///             its source line number for diagnostics)
    /// </summary>
    internal sealed class LabelLine
    {
        public LabelLineKind Kind;
        public string? LabelId;
        public string? Value;
        public string? Description;
        public string? RawLine;     // For Comment kind
        public int LabelLineNumber;
        public int DescriptionLineNumber;

        public static LabelLine Blank(int line) => new LabelLine { Kind = LabelLineKind.Blank, LabelLineNumber = line };
        public static LabelLine Comment(string raw, int line) => new LabelLine
        {
            Kind = LabelLineKind.Comment,
            RawLine = raw,
            LabelLineNumber = line,
        };
    }

    internal sealed class ParsedLabelFile
    {
        public string Path { get; }
        public List<LabelLine> Entries { get; }
        public bool HasBom { get; }
        public string Newline { get; }

        public ParsedLabelFile(string path, List<LabelLine> entries, bool hasBom, string newline)
        {
            Path = path;
            Entries = entries;
            HasBom = hasBom;
            Newline = newline;
        }
    }

    internal sealed class ResolvedLabelFile
    {
        public object LabelFileObj { get; }
        public string AbsolutePath { get; }
        public string LabelFileId { get; }
        public string Language { get; }

        public ResolvedLabelFile(object labelFileObj, string absolutePath, string labelFileId, string language)
        {
            LabelFileObj = labelFileObj;
            AbsolutePath = absolutePath;
            LabelFileId = labelFileId;
            Language = language;
        }
    }

    internal sealed class LabelSearchHit
    {
        public string LabelId { get; }
        public string Value { get; }
        public string Description { get; }
        public int Line { get; }
        public string MatchedIn { get; }

        public LabelSearchHit(string labelId, string value, string description, int line, string matchedIn)
        {
            LabelId = labelId;
            Value = value;
            Description = description;
            Line = line;
            MatchedIn = matchedIn;
        }
    }
}
