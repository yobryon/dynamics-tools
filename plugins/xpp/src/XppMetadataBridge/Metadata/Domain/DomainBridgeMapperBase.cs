using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.Providers;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// Template implementation of the Create / Patch / Read triad shared by
    /// every metaclass domain mapper. Subclasses supply only the typed
    /// accessor name (the IMetadataProvider property — "Enums", "Tables",
    /// "Forms", ...) and three delegates that do the genuinely per-type work:
    /// build-from-json, apply-patch, read-to-json.
    ///
    /// The base owns: model resolution, accessor resolution, the reflective
    /// Create/Update/Read calls, JsonRpcException wrapping, and the
    /// Custom -> Standard -> Runtime read fallback.
    /// </summary>
    internal abstract class DomainBridgeMapperBase : IDomainBridgeMapper
    {
        public abstract string AxType { get; }

        /// <summary>The IMetadataProvider property that exposes this type's
        /// typed accessor (e.g. "Enums" for AxEnum, "Tables" for AxTable).</summary>
        protected abstract string AccessorProperty { get; }

        protected abstract object BuildFromJson(JObject json);

        /// <summary>Apply a merge-patch onto the current metaclass instance and
        /// return the object to Update (usually <paramref name="current"/>
        /// mutated in place; some mappers rebuild and return a fresh object).</summary>
        protected abstract object ApplyPatch(object current, JObject patch);

        protected abstract JObject ReadToJson(object meta);

        /// <summary>Optional hook to reject metaclass shapes outside this
        /// mapper's scope (e.g. AxQueryComposite). Throw to reject.</summary>
        protected virtual void ValidateRead(object meta) { }

        /// <summary>
        /// Optional hook invoked on the freshly built/patched metaclass object
        /// BEFORE it is written. May mutate <paramref name="meta"/> in place
        /// (e.g. stamp pattern-prescribed properties) and return a payload to
        /// surface back to the caller. Default: no-op, returns null.
        /// <paramref name="requestJson"/> is the caller's create/patch JSON,
        /// so the hook can tell author-set values from defaults.
        /// </summary>
        protected virtual JObject? Conform(object meta, JObject requestJson, bool isPatch) => null;

        // ===================================================================
        public DomainWriteResult Create(JObject json, MetadataProviderHost providers, string model)
        {
            var ax = BuildFromJson(json);
            var conformance = Conform(ax, json, isPatch: false);
            var saveInfo = WriteOperations.ResolveModel(providers, model);
            var accessor = AccessorOf(providers.Custom);
            InvokeWrite(accessor, "Create", ax, saveInfo);
            return new DomainWriteResult(MetaclassMap.GetName(ax), conformance);
        }

        public DomainWriteResult Patch(JObject patch, MetadataProviderHost providers, string model, string name)
        {
            var accessor = AccessorOf(providers.Custom);
            var current = ReadVia(accessor, name)
                ?? throw new JsonRpcException(JsonRpcErrorCodes.ObjectNotFound,
                    $"{AxType} '{name}' not found in the writable workspace.");
            ValidateRead(current);
            var updated = ApplyPatch(current, patch);
            var conformance = Conform(updated, patch, isPatch: true);
            var saveInfo = WriteOperations.ResolveModel(providers, model);
            InvokeWrite(accessor, "Update", updated, saveInfo);
            return new DomainWriteResult(MetaclassMap.GetName(updated), conformance);
        }

        public JObject Read(MetadataProviderHost providers, string name)
        {
            foreach (var (provider, _) in providers.ReadOrder())
            {
                var meta = ReadVia(AccessorOf(provider), name);
                if (meta != null)
                {
                    ValidateRead(meta);
                    return ReadToJson(meta);
                }
            }
            throw new JsonRpcException(JsonRpcErrorCodes.ObjectNotFound,
                $"{AxType} '{name}' not found in any provider.");
        }

        // ===================================================================
        private object AccessorOf(IMetadataProvider provider)
        {
            var prop = typeof(IMetadataProvider).GetProperty(AccessorProperty)
                       ?? provider.GetType().GetProperty(AccessorProperty)
                       ?? throw new JsonRpcException(JsonRpcErrorCodes.InternalError,
                           $"IMetadataProvider has no '{AccessorProperty}' accessor for {AxType}.");
            return prop.GetValue(provider)
                   ?? throw new JsonRpcException(JsonRpcErrorCodes.InternalError,
                       $"Null accessor '{AccessorProperty}' for {AxType}.");
        }

        private static object? ReadVia(object accessor, string name)
        {
            var read = accessor.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "Read"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(string));
            return read?.Invoke(accessor, new object[] { name });
        }

        private void InvokeWrite(object accessor, string methodName, object meta, object saveInfo)
        {
            // Create(T, ModelSaveInfo) / Update(T, ModelSaveInfo) — bind by the
            // 2-arg shape so a subtype instance (e.g. AxEdtString) still matches
            // the base-typed parameter (AxEdt).
            var method = accessor.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 2)
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InternalError,
                    $"Accessor for {AxType} has no {methodName}(T, ModelSaveInfo).");
            try
            {
                method.Invoke(accessor, new[] { meta, saveInfo });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams,
                    $"{methodName} {AxType} failed: {MetaclassMap.Innermost(tie.InnerException)}",
                    new { detail = tie.InnerException.ToString() });
            }
        }
    }
}
