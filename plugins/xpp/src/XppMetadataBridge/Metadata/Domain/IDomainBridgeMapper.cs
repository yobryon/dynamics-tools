using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.Storage;
using Microsoft.Dynamics.AX.Metadata.Providers;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// Result of a typed Create/Patch. Carries the canonical object name plus
    /// an optional <see cref="Conformance"/> payload — currently only AxForm
    /// produces one (form-pattern conformance from MS's pattern engine). Null
    /// for every other type.
    /// </summary>
    internal readonly struct DomainWriteResult
    {
        public string Name { get; }
        public JObject? Conformance { get; }
        public DomainWriteResult(string name, JObject? conformance)
        {
            Name = name;
            Conformance = conformance;
        }
    }

    /// <summary>
    /// Bridge-side domain mapper. Owns the JSON ↔ MS metaclass translation
    /// for one AOT type. Replaces the service-side XML-emission mapper —
    /// we now stage a populated metaclass instance and let MS's provider
    /// serialize it canonically.
    ///
    /// Why this lives in the bridge: the metaclass DLLs (net48) only load
    /// here. Everything north of the bridge (service, MCP) sees only the
    /// transport JSON. The bridge becomes the only place that knows about
    /// MS's type system; the service becomes a dumb forwarder for any
    /// AxType that has a registered <see cref="IDomainBridgeMapper"/>.
    /// </summary>
    internal interface IDomainBridgeMapper
    {
        /// <summary>The axType this mapper handles (e.g. "AxEnum").</summary>
        string AxType { get; }

        /// <summary>
        /// Create a new object. <paramref name="domainJson"/> is the
        /// service-side typed-request shape (camelCase keys).
        /// Implementations construct the appropriate MS metaclass, call
        /// the typed accessor's <c>Create</c>, and return the canonical
        /// object name (whatever the metaclass settled on after defaults
        /// were applied).
        /// </summary>
        DomainWriteResult Create(JObject domainJson, MetadataProviderHost providers, string model);

        /// <summary>
        /// Patch an existing object with merge-patch semantics. The
        /// <paramref name="patchJson"/> carries only the fields that should
        /// override the current state. Collections replace wholesale when
        /// non-null on the patch (same semantics as the legacy mappers).
        /// </summary>
        DomainWriteResult Patch(JObject patchJson, MetadataProviderHost providers, string model, string name);

        /// <summary>
        /// Read an object and return it as service-side response JSON
        /// (camelCase keys, shape matches the GetXxxResponse record on the
        /// service side). Used by both the agent-facing read tools and by
        /// the drift detector after a write.
        /// </summary>
        JObject Read(MetadataProviderHost providers, string name);
    }
}
