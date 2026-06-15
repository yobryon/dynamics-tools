using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Dynamics.AX.Metadata.Core.IO;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// getObjectXml - read an AOT object and return its on-disk XML
    /// representation as a single string. Foundation for the write surface:
    /// the agent reads the XML, edits it locally, and posts it back through
    /// updateObject.
    ///
    /// Why echo XML rather than a JSON projection: AOT XML is the contract
    /// Microsoft picked for AI-driven authoring (see misc/d365_extension_notes).
    /// One self-contained representation per object - declaration + methods +
    /// fields + indexes + relations + properties together - validatable
    /// against an XSD. Re-serializing the loaded object guarantees the round
    /// trip is shape-correct even when the on-disk file omits optional
    /// elements that have moved into the type's defaults.
    ///
    /// Serializer choice: Microsoft.Dynamics.AX.Metadata.Core.IO.MetadataSerializer.
    /// XmlSerializer would dump the CLR object graph (flattened CompilerMetadata,
    /// top-level Methods/AxMethod, duplicated declarations) which fails the
    /// MS-authored AxClass.xsd. MetadataSerializer wraps the same DataContract
    /// pipeline the disk providers use, so the result is byte-for-byte the
    /// on-disk shape (validated against the bundled XSDs).
    ///
    /// Runtime-sourced reads: when the object resolved from a binary-only
    /// module, the serializer still produces valid XML but X++ method
    /// bodies are empty. The response carries source="runtime" so the
    /// caller can warn the agent before they try to operate on missing
    /// source.
    /// </summary>
    internal sealed class GetObjectXmlHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;

        public GetObjectXmlHandler(MetadataProviderHost providers)
        {
            _providers = providers;
        }

        public string Method => "getObjectXml";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Require(@params);
            var axType = Params.RequireString(p, "axType");
            var name = Params.RequireString(p, "name");
            var model = Params.OptionalString(p, "model"); // accepted for parity, not used for disambiguation

            var hit = ObjectProjection.ReadObjectWithSource(_providers, axType, name)
                ?? throw new JsonRpcException(
                    JsonRpcErrorCodes.ObjectNotFound,
                    $"{axType}:{name} not found in any provider");

            string xml;
            try
            {
                xml = SerializeToXml(hit.Value);
            }
            catch (Exception ex)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InternalError,
                    $"Failed to serialize {axType}:{name} to XML: {ex.Message}",
                    new { detail = ex.ToString() });
            }

            return Task.FromResult<object?>(new
            {
                model,
                axType,
                name,
                xml,
                source = SourceWire.From(hit.Source),
                binaryModule = hit.Source == ProviderSource.Runtime
            });
        }

        private static readonly MetadataSerializer _serializer = new MetadataSerializer();

        // Some AOT families are polymorphic at the file root: the on-disk
        // XML wraps every concrete subtype in <Family xsi:type="Subtype">.
        // MS's DataContract pipeline both emits and consumes this shape;
        // MetadataSerializer, however, dispatches on the runtime CLR type
        // and emits <Subtype> as the root with no discriminator. The agent's
        // round trip breaks because the bridge's WRITE path (FromFile) only
        // accepts the family-rooted-with-xsi:type shape. The fix is to
        // rewrite the root at serialization time so the round trip is
        // symmetric.
        //
        // Map keys: concrete subtype name (the runtime CLR class).
        // Map values: family root name (what FromFile expects on read).
        // Most AOT families (AxClass, AxTable, AxForm, ...) aren't
        // polymorphic at the file root and aren't in this map.
        private static readonly System.Collections.Generic.Dictionary<string, string> PolymorphicRootMap =
            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                // AxEdt family
                ["AxEdtString"] = "AxEdt",
                ["AxEdtInt"] = "AxEdt",
                ["AxEdtInt64"] = "AxEdt",
                ["AxEdtReal"] = "AxEdt",
                ["AxEdtEnum"] = "AxEdt",
                ["AxEdtDate"] = "AxEdt",
                ["AxEdtTime"] = "AxEdt",
                ["AxEdtUtcDateTime"] = "AxEdt",
                ["AxEdtContainer"] = "AxEdt",
                ["AxEdtGuid"] = "AxEdt",
                ["AxEdtDateTime"] = "AxEdt",
                // AxQuery family
                ["AxQuerySimple"] = "AxQuery",
                ["AxQueryComposite"] = "AxQuery",
            };

        internal static string SerializeToXml(object metadataObject)
        {
            // Every AOT type implements INamedObject (it's what carries the
            // Name primary key). Cast there directly; if the caller hands us
            // anything else the InvalidCastException is the right diagnostic.
            var xml = _serializer.Serialize((INamedObject)metadataObject);
            return NormalizePolymorphicRoot(xml);
        }

        /// <summary>
        /// When the root element of <paramref name="xml"/> matches a known
        /// polymorphic subtype (e.g. AxEdtString, AxQuerySimple), rewrite
        /// the open tag to the family root with an xsi:type discriminator
        /// (&lt;AxEdt xmlns="" i:type="AxEdtString"&gt;) and the close tag to
        /// match. No-op otherwise. Conservative string rewrite —
        /// MetadataSerializer's output is stable enough to pattern-match
        /// the open tag and replace the matching close tag.
        /// </summary>
        internal static string NormalizePolymorphicRoot(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return xml;
            // Find the root element opening. Skip the XML declaration and any
            // whitespace before '<'.
            var rootOpen = xml.IndexOf('<');
            while (rootOpen >= 0 && rootOpen + 1 < xml.Length && xml[rootOpen + 1] == '?')
            {
                var declEnd = xml.IndexOf("?>", rootOpen + 2, StringComparison.Ordinal);
                if (declEnd < 0) return xml;
                rootOpen = xml.IndexOf('<', declEnd + 2);
            }
            if (rootOpen < 0) return xml;

            // Extract the root element name.
            var nameEnd = rootOpen + 1;
            while (nameEnd < xml.Length && !char.IsWhiteSpace(xml[nameEnd]) && xml[nameEnd] != '>' && xml[nameEnd] != '/')
                nameEnd++;
            var rootName = xml.Substring(rootOpen + 1, nameEnd - rootOpen - 1);
            if (!PolymorphicRootMap.TryGetValue(rootName, out var familyRoot)) return xml;

            // Replace `<AxEdtString` -> `<AxEdt xmlns="" i:type="AxEdtString"`.
            // The serializer already writes xmlns:i for us, so we just inject
            // the empty default namespace + the xsi:type discriminator.
            var rewritten = xml
                .Remove(rootOpen + 1, rootName.Length)
                .Insert(rootOpen + 1, familyRoot + " xmlns=\"\" i:type=\"" + rootName + "\"");

            // Replace `</AxEdtString>` with `</AxEdt>`. Should be exactly one
            // (the document root close); tolerate multiple defensively.
            rewritten = rewritten.Replace("</" + rootName + ">", "</" + familyRoot + ">");
            return rewritten;
        }
    }
}
