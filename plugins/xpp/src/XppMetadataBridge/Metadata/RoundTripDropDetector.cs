using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// One element the caller's posted XML carried with a meaningful value
    /// that did not survive the bridge's deserialize (FromFile) -> serialize
    /// round-trip into the on-disk shape.
    /// </summary>
    internal readonly struct XmlDrop
    {
        public XmlDrop(string path, string value) { Path = path; Value = value; }
        public string Path { get; }
        public string Value { get; }
    }

    /// <summary>
    /// Presence-only, conservative round-trip drop detection for the raw XML
    /// write surface (createObject / updateObject). Compares the caller's
    /// posted XML against what comes back out after FromFile ->
    /// MetadataSerializer.Serialize. Any leaf the caller set with a non-empty
    /// value that is absent (or i:nil / empty) after the round-trip is flagged.
    ///
    /// Why this exists: MS's DataContract deserializer silently skips elements
    /// it can't place -- notably out-of-order ones, since the on-disk element
    /// order is contract-significant -- so a "successful" raw write can quietly
    /// lose content. The handler returns these as advisory warnings WITHOUT
    /// failing the write (the object may be legitimately normalized).
    ///
    /// This is the raw-XML analog of the typed path's DriftDetector and shares
    /// its conservative posture:
    ///  - presence-only: a surviving-but-changed value is NOT flagged (enum /
    ///    case normalization is common and would be noisy);
    ///  - meaningful-leaf-only: empty / whitespace / i:nil input values are
    ///    never flagged;
    ///  - within an element, scalar property children are matched by name
    ///    (order-insensitive -- the serializer canonicalizes property order);
    ///  - repeated same-named children (collection items) are matched by
    ///    identity (their Name / DataField / Field child) when present, else
    ///    positionally.
    ///
    /// A default the caller stated explicitly that the serializer then elides
    /// can produce a benign false positive; the warning is advisory, never
    /// fatal. For the canonical read-edit-write flow both sides are
    /// MetadataSerializer output, so defaults match and no false positive
    /// arises.
    /// </summary>
    internal static class RoundTripDropDetector
    {
        private const string XsiNil = "{http://www.w3.org/2001/XMLSchema-instance}nil";
        private static readonly string[] IdentityChildNames = { "Name", "DataField", "Field" };

        public static IReadOnlyList<XmlDrop> Detect(string postedXml, string roundTripXml)
        {
            if (string.IsNullOrWhiteSpace(postedXml) || string.IsNullOrWhiteSpace(roundTripXml))
                return Array.Empty<XmlDrop>();

            XElement? posted, roundTrip;
            try
            {
                posted = XDocument.Parse(postedXml).Root;
                roundTrip = XDocument.Parse(roundTripXml).Root;
            }
            catch
            {
                // Unparseable on either side -- the real failure surfaces via
                // the normal bridge error path; don't invent drift.
                return Array.Empty<XmlDrop>();
            }
            if (posted == null || roundTrip == null) return Array.Empty<XmlDrop>();

            var drops = new List<XmlDrop>();
            WalkElement(posted, roundTrip, string.Empty, drops);
            return drops;
        }

        private static void WalkElement(XElement input, XElement? output, string path, List<XmlDrop> sink)
        {
            foreach (var group in input.Elements().GroupBy(e => e.Name.LocalName))
            {
                var localName = group.Key;
                var inputItems = group.ToList();
                var outputItems = output?.Elements().Where(e => e.Name.LocalName == localName).ToList()
                                  ?? new List<XElement>();
                var childPath = Combine(path, localName);

                if (inputItems.Count > 1 || outputItems.Count > 1)
                {
                    MatchCollection(inputItems, outputItems, childPath, sink);
                    continue;
                }

                var inEl = inputItems[0];
                var outEl = outputItems.Count > 0 ? outputItems[0] : null;
                if (HasElementChildren(inEl))
                {
                    if (outEl == null) EmitMeaningful(inEl, childPath, sink);
                    else WalkElement(inEl, outEl, childPath, sink);
                }
                else
                {
                    // Leaf. Presence-only: flag only when the caller's value is
                    // meaningful and the round-trip side lost it entirely.
                    var v = LeafValue(inEl);
                    if (!string.IsNullOrEmpty(v) && string.IsNullOrEmpty(outEl == null ? null : LeafValue(outEl)))
                        sink.Add(new XmlDrop(childPath, v!));
                }
            }
        }

        private static void MatchCollection(List<XElement> inputItems, List<XElement> outputItems, string basePath, List<XmlDrop> sink)
        {
            // Identity-keyed match so collection reordering (the serializer may
            // canonicalize) doesn't read as a drop. Items without an identity
            // child fall back to positional alignment.
            var outByKey = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < outputItems.Count; i++)
            {
                var k = IdentityOf(outputItems[i]);
                if (k != null && !outByKey.ContainsKey(k)) outByKey[k] = outputItems[i];
            }

            for (int i = 0; i < inputItems.Count; i++)
            {
                var inEl = inputItems[i];
                var key = IdentityOf(inEl);
                var itemPath = key != null ? $"{basePath}[{key}]" : $"{basePath}[{i}]";

                XElement? outEl = null;
                if (key != null) outByKey.TryGetValue(key, out outEl);
                else if (i < outputItems.Count) outEl = outputItems[i];

                if (outEl == null) EmitMeaningful(inEl, itemPath, sink);
                else WalkElement(inEl, outEl, itemPath, sink);
            }
        }

        private static string? IdentityOf(XElement el)
        {
            foreach (var idName in IdentityChildNames)
            {
                var c = el.Elements().FirstOrDefault(e => e.Name.LocalName == idName);
                var v = c == null ? null : LeafValue(c);
                if (!string.IsNullOrEmpty(v)) return idName + ":" + v;
            }
            return null;
        }

        private static void EmitMeaningful(XElement el, string path, List<XmlDrop> sink)
        {
            if (HasElementChildren(el))
            {
                foreach (var group in el.Elements().GroupBy(e => e.Name.LocalName))
                {
                    var items = group.ToList();
                    var groupPath = Combine(path, group.Key);
                    if (items.Count == 1)
                        EmitMeaningful(items[0], groupPath, sink);
                    else
                        for (int i = 0; i < items.Count; i++)
                        {
                            var key = IdentityOf(items[i]);
                            EmitMeaningful(items[i], key != null ? $"{groupPath}[{key}]" : $"{groupPath}[{i}]", sink);
                        }
                }
            }
            else
            {
                var v = LeafValue(el);
                if (!string.IsNullOrEmpty(v)) sink.Add(new XmlDrop(path, v!));
            }
        }

        private static bool HasElementChildren(XElement el) => el.Elements().Any();

        private static string? LeafValue(XElement el)
        {
            if (string.Equals(el.Attribute(XsiNil)?.Value, "true", StringComparison.OrdinalIgnoreCase))
                return null;
            var v = el.Value;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private static string Combine(string path, string name)
            => string.IsNullOrEmpty(path) ? "/" + name : path + "/" + name;
    }
}
