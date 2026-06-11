using System.Collections.Generic;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// Bridge-side registry of metaclass-routed domain mappers, keyed by
    /// axType. The bridge's domain-routed RPCs (createDomainObject etc.)
    /// dispatch through here.
    ///
    /// We expect this set to grow over the AOT-type migration (tranche by
    /// tranche). Anything not registered here is unsupported — callers
    /// (the service) keep using the legacy XML path until each type is
    /// migrated.
    /// </summary>
    internal sealed class DomainMapperRegistry
    {
        private readonly Dictionary<string, IDomainBridgeMapper> _mappers;

        public DomainMapperRegistry()
        {
            _mappers = new Dictionary<string, IDomainBridgeMapper>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["AxEnum"] = new AxEnumDomainMapper(),
                ["AxClass"] = new AxClassDomainMapper(),
                ["AxEdt"] = new AxEdtDomainMapper(),
                ["AxTable"] = new AxTableDomainMapper(),
                ["AxQuery"] = new AxQueryDomainMapper(),
                ["AxForm"] = new AxFormDomainMapper(),
                ["AxMenuItemDisplay"] = new AxMenuItemDomainMapper("Display"),
                ["AxMenuItemAction"] = new AxMenuItemDomainMapper("Action"),
                ["AxMenuItemOutput"] = new AxMenuItemDomainMapper("Output"),
                ["AxResource"] = new AxResourceDomainMapper(),
                ["AxService"] = new AxServiceDomainMapper(),
                ["AxServiceGroup"] = new AxServiceGroupDomainMapper(),
                ["AxTile"] = new AxTileDomainMapper(),
                ["AxSecurityDuty"] = new AxSecurityDutyDomainMapper(),
                ["AxSecurityRole"] = new AxSecurityRoleDomainMapper(),
                ["AxSecurityPrivilege"] = new AxSecurityPrivilegeDomainMapper(),
                ["AxSecurityPolicy"] = new AxSecurityPolicyDomainMapper(),
                ["AxMenu"] = new AxMenuDomainMapper(),
                ["AxView"] = new AxViewDomainMapper(),
                ["AxDataEntityView"] = new AxDataEntityViewDomainMapper(),
                ["AxEnumExtension"] = new AxEnumExtensionDomainMapper(),
                ["AxEdtExtension"] = new AxEdtExtensionDomainMapper(),
                ["AxTableExtension"] = new AxTableExtensionDomainMapper(),
                ["AxViewExtension"] = new AxViewExtensionDomainMapper(),
                ["AxDataEntityViewExtension"] = new AxDataEntityViewExtensionDomainMapper(),
                ["AxMenuExtension"] = new AxMenuExtensionDomainMapper(),
                ["AxFormExtension"] = new AxFormExtensionDomainMapper(),
            };
        }

        public IDomainBridgeMapper Resolve(string axType)
        {
            if (string.IsNullOrWhiteSpace(axType))
                throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "axType is required.");
            if (!_mappers.TryGetValue(axType, out var m))
                throw new JsonRpcException(
                    JsonRpcErrorCodes.InvalidParams,
                    $"No bridge-side domain mapper for axType '{axType}'. " +
                    "This type still uses the legacy XML route.");
            return m;
        }

        public IEnumerable<string> SupportedTypes => _mappers.Keys;
    }
}
