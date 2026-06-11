using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Dynamics.AX.Metadata.Providers;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// Maps D365 AxType names ("AxClass", "AxTable", ...) to the
    /// corresponding property on <c>IMetadataProvider</c> that returns the
    /// typed reader for that entity (provider.Classes, provider.Tables, ...).
    ///
    /// Built once via reflection on the first request. We could ship a static
    /// hardcoded table — there are ~80 types and the names don't change
    /// often — but reflection gives us forward-compat with new types
    /// Microsoft adds without us noticing, and it costs nothing after the
    /// initial scan.
    ///
    /// Cached per <c>IMetadataProvider</c> instance; the bridge's host has
    /// at most two providers (standard + custom) so the cache stays tiny.
    /// </summary>
    internal static class TypeMap
    {
        private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> _cache
            = new ConcurrentDictionary<Type, IReadOnlyDictionary<string, PropertyInfo>>();

        public static IReadOnlyDictionary<string, PropertyInfo> For(IMetadataProvider provider)
        {
            return _cache.GetOrAdd(provider.GetType(), Build);
        }

        public static PropertyInfo? ResolveProperty(IMetadataProvider provider, string axType)
        {
            var map = For(provider);
            return map.TryGetValue(axType, out var prop) ? prop : null;
        }

        private static IReadOnlyDictionary<string, PropertyInfo> Build(Type providerType)
        {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in providerType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var entityType = ExtractEntityType(prop.PropertyType);
                if (entityType == null) continue;

                if (entityType.Namespace == null) continue;
                if (!entityType.Namespace.StartsWith("Microsoft.Dynamics.AX.Metadata", StringComparison.Ordinal)) continue;
                if (!entityType.Name.StartsWith("Ax", StringComparison.Ordinal)) continue;

                map[entityType.Name] = prop;
            }

            // Log what we found at INFO level on stderr so we can debug
            // type-discovery mismatches without poking at the protocol stream.
            Console.Error.WriteLine($"[bridge] TypeMap built for {providerType.Name}: {map.Count} types");
            return map;
        }

        /// <summary>
        /// Walks up the property's declared type chain looking for a
        /// generic argument that names an Ax* entity. Concrete provider
        /// properties may expose IMetadataReader&lt;T&gt; directly, OR a
        /// subclass / wrapper whose own generic chain holds T further up.
        /// We probe both the declared type and its base types' generics.
        /// Public because WriteOperations needs the same walk to bind
        /// FromFile/Create/Update against the right T.
        /// </summary>
        public static Type? ExtractEntityType(Type propType)
        {
            // Walk: propType, base types, AND all interfaces. The provider's
            // properties often return implementation types that implement
            // IMetadataReader<T> as an interface rather than declare it
            // directly.
            var candidates = new List<Type>();
            for (var t = propType; t != null && t != typeof(object); t = t.BaseType)
            {
                candidates.Add(t);
            }
            if (propType.GetInterfaces() is { } ifaces)
            {
                candidates.AddRange(ifaces);
            }

            foreach (var t in candidates)
            {
                if (!t.IsGenericType) continue;
                var args = t.GetGenericArguments();
                if (args.Length != 1) continue;
                var arg = args[0];
                if (arg.Name.StartsWith("Ax", StringComparison.Ordinal)) return arg;
            }
            return null;
        }
    }
}
