using System;
using System.IO;
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
            MethodSource.NormalizeAuthoredSources(json);
            var ax = BuildFromJson(json);
            var conformance = Conform(ax, json, isPatch: false);
            var saveInfo = WriteOperations.ResolveModel(providers, model);
            var accessor = AccessorOf(providers.Custom);
            using (var guard = new WriteGuard(DiskPath(providers, model, MetaclassMap.GetName(ax))))
            {
                InvokeWrite(accessor, "Create", ax, saveInfo);
                guard.Commit();
            }
            return new DomainWriteResult(MetaclassMap.GetName(ax), conformance);
        }

        /// <summary>
        /// On-disk XML of an object in a model, by the metadata store's layout
        /// (packages\module\model\AxType\Name.xml). Null when it cannot be
        /// determined; the guard then does nothing.
        /// </summary>
        private string? DiskPath(MetadataProviderHost providers, string model, string name)
        {
            try
            {
                var info = providers.Custom.ModelManifest.Read(model);
                var root = providers.Config.PackagesLocalDirectory;
                if (info == null || string.IsNullOrEmpty(root) || string.IsNullOrEmpty(name)) return null;
                return System.IO.Path.Combine(root, info.Module, info.Name, AxType, name + ".xml");
            }
            catch { return null; }
        }

        public DomainWriteResult Patch(JObject patch, MetadataProviderHost providers, string model, string name)
        {
            var accessor = AccessorOf(providers.Custom);
            var current = ReadVia(accessor, name)
                ?? throw new JsonRpcException(JsonRpcErrorCodes.ObjectNotFound,
                    $"{AxType} '{name}' not found in the writable workspace.");
            ValidateRead(current);
            // Only the methods THIS request authored get re-indented. Mappers
            // that patch by "emit current -> overlay -> rebuild" (forms) used to
            // push every pre-existing method through the normalizer too,
            // turning a one-method append into a whole-file re-indent diff.
            // ... and among those, only the ones that differ from what is on
            // disk: patch-by-path sends a whole top-level branch (a class's
            // entire methods array) as the patch, so "supplied" is not the
            // same as "changed". A byte-identical source is left exactly as
            // it was, whatever its indentation.
            MethodSource.NormalizeAuthoredSources(patch, MethodSource.CollectSources(ReadToJson(current)));
            var updated = ApplyPatch(current, patch);
            var conformance = Conform(updated, patch, isPatch: true);
            var saveInfo = WriteOperations.ResolveModel(providers, model);
            using (var guard = new WriteGuard(DiskPath(providers, model, name)))
            {
                InvokeWrite(accessor, "Update", updated, saveInfo);
                guard.Commit();
            }
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

    /// <summary>
    /// Keeps a rejected or botched metadata write from destroying the file.
    ///
    /// Measured 2026-10-23: Microsoft's typed-accessor Update truncated a
    /// form's XML to an empty root element and THEN threw ("Element named
    /// 'init' already exists"); the whole design was gone and only an earlier
    /// read in the agent's context saved it. So: snapshot the bytes before
    /// the write, and put them back when the write throws or leaves behind a
    /// file that is empty or far smaller than what was there.
    /// </summary>
    internal sealed class WriteGuard : IDisposable
    {
        private readonly string? _path;
        private readonly byte[]? _before;
        private bool _committed;

        public WriteGuard(string? path)
        {
            _path = path;
            try { if (path != null && File.Exists(path)) _before = File.ReadAllBytes(path); } catch { _before = null; }
        }

        /// <summary>Call after the write returned normally. Still restores when
        /// the accessor "succeeded" but left a hollow file behind.</summary>
        public void Commit()
        {
            _committed = true;
            if (_path == null || _before == null) return;
            try
            {
                var after = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                // An AxForm root alone is ~140 bytes; anything that shrank to
                // under a tenth of its former size did not get written properly.
                if (after < 200 && _before.Length > 400 || after * 10 < _before.Length)
                {
                    File.WriteAllBytes(_path, _before);
                    throw new JsonRpcException(JsonRpcErrorCodes.InternalError,
                        $"the metadata write left {System.IO.Path.GetFileName(_path)} at {after} bytes (was {_before.Length}); the previous content was restored and nothing was changed.");
                }
            }
            catch (JsonRpcException) { throw; }
            catch { /* best effort */ }
        }

        public void Dispose()
        {
            if (_committed || _path == null || _before == null) return;
            // The write threw. Whatever the accessor did to the file on the way
            // out, the object the caller had is the object they keep.
            try { File.WriteAllBytes(_path, _before); } catch { }
        }
    }
}
