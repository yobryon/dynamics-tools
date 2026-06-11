using System;
using System.IO;
using System.Reflection;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// Shared helpers for the createObject / updateObject handlers. Both
    /// handlers do the same prep work (resolve typed accessor, deserialize
    /// XML, look up the target model) and differ only in which method they
    /// invoke on the provider's typed reader.
    ///
    /// We always write through the Custom provider. On Tier 1 dev VMs that's
    /// the same handle as Standard; on split-path setups it's the explicitly
    /// writable workspace. Writing to Standard would attempt to mutate the
    /// shipped Microsoft metadata directory, which is wrong on every setup.
    /// </summary>
    internal static class WriteOperations
    {
        public enum WriteKind
        {
            Create,
            Update
        }

        /// <summary>
        /// Resolves the typed accessor for an axType ("AxClass", "AxTable", ...)
        /// on the writable (Custom) provider. The returned <c>Accessor</c>
        /// object is whatever the property returns; in practice it's the
        /// concrete reader/writer (e.g. <c>SingleKeyedDiskMetadataProvider&lt;AxClass&gt;</c>)
        /// that implements both read and write methods.
        /// </summary>
        public sealed class TypedAccessor
        {
            public PropertyInfo Property { get; }
            public Type EntityType { get; }
            public object Accessor { get; }

            public TypedAccessor(PropertyInfo property, Type entityType, object accessor)
            {
                Property = property;
                EntityType = entityType;
                Accessor = accessor;
            }
        }

        public static TypedAccessor ResolveAccessor(MetadataProviderHost host, string axType)
        {
            var provider = host.Custom;
            var prop = TypeMap.ResolveProperty(provider, axType)
                       ?? throw new JsonRpcException(
                           JsonRpcErrorCodes.InvalidParams,
                           $"Unknown axType '{axType}'. Call listKnownTypes for the supported set.");
            var accessor = prop.GetValue(provider)
                           ?? throw new JsonRpcException(
                               JsonRpcErrorCodes.InternalError,
                               $"Provider returned a null accessor for {axType}.");

            // Extract the actual element type (T in IMetadataReader<T>) from
            // the property so the deserializer can target it. Shared with
            // TypeMap so the answer is consistent across read + write paths.
            var entityType = TypeMap.ExtractEntityType(prop.PropertyType)
                             ?? throw new JsonRpcException(
                                 JsonRpcErrorCodes.InternalError,
                                 $"Could not resolve the CLR type for {axType}.");
            return new TypedAccessor(prop, entityType, accessor);
        }

        /// <summary>
        /// Deserialize disk-shape XML back into the CLR metadata object.
        ///
        /// We can't use XmlSerializer here: the on-disk format is what the
        /// DataContract pipeline emits (SourceCode-nested Methods, CDATA
        /// source bodies, no flattened CompilerMetadata), and XmlSerializer
        /// silently drops the nested elements because the CLR object graph
        /// expects them at top level.
        ///
        /// Path: write the XML to a temp file and call the disk provider's
        /// public FromFile(string, out ISingleKeyedMetadata) method. That's
        /// the same entry point the on-disk Read() pipeline ultimately uses,
        /// so we get DataContract deserialization, post-deserialize hooks,
        /// and schema-version handling for free.
        /// </summary>
        public static object DeserializeXml(object accessor, string xml, Type entityType)
        {
            var fromFile = accessor.GetType().GetMethod(
                "FromFile",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (fromFile == null)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InternalError,
                    $"Provider accessor for {entityType.Name} has no public FromFile method.");
            }

            var tempPath = Path.Combine(Path.GetTempPath(),
                $"xpp_bridge_{Guid.NewGuid():N}_{entityType.Name}.xml");
            try
            {
                File.WriteAllText(tempPath, xml, System.Text.Encoding.UTF8);

                var args = new object[] { tempPath, null };
                bool ok;
                try
                {
                    ok = (bool)fromFile.Invoke(accessor, args);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    throw new JsonRpcException(
                        JsonRpcErrorCodes.InvalidParams,
                        $"XML is not a valid {entityType.Name}: {InnermostMessage(tie.InnerException)}",
                        new { detail = tie.InnerException.ToString() });
                }

                if (!ok || args[1] == null)
                {
                    throw new JsonRpcException(
                        JsonRpcErrorCodes.InvalidParams,
                        $"XML did not parse as a {entityType.Name}.");
                }
                if (!entityType.IsInstanceOfType(args[1]))
                {
                    throw new JsonRpcException(
                        JsonRpcErrorCodes.InvalidParams,
                        $"XML parsed to {args[1].GetType().Name}, expected {entityType.Name}.");
                }
                return args[1];
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
        }

        public static ModelSaveInfo ResolveModel(MetadataProviderHost host, string modelName)
        {
            // The model manifest takes the model's logical Name and returns a
            // ModelInfo populated with Layer/Id/SequenceId/Module, all of
            // which ModelSaveInfo needs to plant the file in the right
            // physical location. A null return means the model isn't loaded.
            var manifest = host.Custom.ModelManifest;
            var modelInfo = manifest.Read(modelName);
            if (modelInfo == null)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InvalidParams,
                    $"Model '{modelName}' not found. Call listModels to see what's loaded.");
            }
            return new ModelSaveInfo(modelInfo);
        }

        public static void Invoke(TypedAccessor accessor, WriteKind kind, object metadataObject, ModelSaveInfo saveInfo)
        {
            // The typed accessor exposes Create(T, ModelSaveInfo) and
            // Update(T, ModelSaveInfo). Both are instance methods with the
            // same arity; we discriminate by name and bind by the resolved
            // entity type.
            var methodName = kind == WriteKind.Create ? "Create" : "Update";
            var method = accessor.Accessor.GetType().GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { accessor.EntityType, typeof(ModelSaveInfo) },
                modifiers: null);
            if (method == null)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InternalError,
                    $"Provider accessor for {accessor.EntityType.Name} has no {methodName}({accessor.EntityType.Name}, ModelSaveInfo) method.");
            }

            try
            {
                method.Invoke(accessor.Accessor, new[] { metadataObject, saveInfo });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InternalError,
                    $"{methodName} failed: {InnermostMessage(tie.InnerException)}",
                    new { detail = tie.InnerException.ToString() });
            }
        }

        private static string InnermostMessage(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
