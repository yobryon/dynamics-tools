using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Metadata;
using XppMetadataBridge.Metadata.Domain;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Handlers
{
    /// <summary>
    /// createDomainObject — write a new AOT object from typed-domain JSON.
    /// Bridge constructs the MS metaclass directly + invokes the provider's
    /// typed Create. Replaces the createObject (XML) flow for axTypes that
    /// have a registered <see cref="IDomainBridgeMapper"/>.
    /// </summary>
    internal sealed class CreateDomainObjectHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;
        private readonly DomainMapperRegistry _registry;

        public CreateDomainObjectHandler(MetadataProviderHost providers, DomainMapperRegistry registry)
        {
            _providers = providers;
            _registry = registry;
        }

        public string Method => "createDomainObject";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Require(@params);
            var axType = Params.RequireString(p, "axType");
            var model = Params.RequireString(p, "model");
            var json = Params.RequireObject(p, "domainJson");

            var mapper = _registry.Resolve(axType);
            var result = mapper.Create(json, _providers, model);
            return Task.FromResult<object?>(new
            {
                axType, model, name = result.Name, created = true,
                patternConformance = result.Conformance,
            });
        }
    }

    internal sealed class PatchDomainObjectHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;
        private readonly DomainMapperRegistry _registry;

        public PatchDomainObjectHandler(MetadataProviderHost providers, DomainMapperRegistry registry)
        {
            _providers = providers;
            _registry = registry;
        }

        public string Method => "patchDomainObject";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Require(@params);
            var axType = Params.RequireString(p, "axType");
            var model = Params.RequireString(p, "model");
            var name = Params.RequireString(p, "name");
            var json = Params.RequireObject(p, "patchJson");

            var mapper = _registry.Resolve(axType);
            var result = mapper.Patch(json, _providers, model, name);
            return Task.FromResult<object?>(new
            {
                axType, model, name = result.Name, patched = true,
                patternConformance = result.Conformance,
            });
        }
    }

    internal sealed class GetDomainObjectHandler : IRpcHandler
    {
        private readonly MetadataProviderHost _providers;
        private readonly DomainMapperRegistry _registry;

        public GetDomainObjectHandler(MetadataProviderHost providers, DomainMapperRegistry registry)
        {
            _providers = providers;
            _registry = registry;
        }

        public string Method => "getDomainObject";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
        {
            var p = Params.Require(@params);
            var axType = Params.RequireString(p, "axType");
            var name = Params.RequireString(p, "name");
            // Drift detection asks for a non-suppressed read so a property the
            // caller set to its metaclass default doesn't look dropped.
            var includeDefaults = p["includeDefaults"]?.Value<bool>() ?? false;

            var mapper = _registry.Resolve(axType);
            MetaclassJson.IncludeDefaults = includeDefaults;
            JObject json;
            try { json = mapper.Read(_providers, name); }
            finally { MetaclassJson.IncludeDefaults = false; }
            return Task.FromResult<object?>(new { axType, name, domainJson = json });
        }
    }

    internal sealed class ListDomainMappedTypesHandler : IRpcHandler
    {
        private readonly DomainMapperRegistry _registry;

        public ListDomainMappedTypesHandler(DomainMapperRegistry registry) { _registry = registry; }

        public string Method => "listDomainMappedTypes";

        public Task<object?> HandleAsync(JToken? @params, CancellationToken ct)
            => Task.FromResult<object?>(new { axTypes = _registry.SupportedTypes });
    }
}
