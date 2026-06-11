using System;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// AxMenuItem(Display|Action|Output) domain mapper. One class, one instance
    /// per kind (the registry registers three). Flat scalar surface: the
    /// metaclass property names match the domain field names 1:1, so most of
    /// the work is a typed-field table. Image is nested in the domain shape but
    /// flat on the metaclass; SubscriberAccessLevel is the AccessGrant struct.
    /// </summary>
    internal sealed class AxMenuItemDomainMapper : DomainBridgeMapperBase
    {
        private readonly string _kind; // Display / Action / Output

        public AxMenuItemDomainMapper(string kind) => _kind = kind;

        public override string AxType => "AxMenuItem" + _kind;
        protected override string AccessorProperty => "MenuItem" + _kind + "s";

        // Flat scalar fields shared by all 3 kinds (Action adds the StateMachine
        // trio — they're harmless no-ops on Display/Output via Assign/EmitDefaulted).
        private static readonly (string Key, string Prop, EmitAs Kind)[] Fields =
        {
            ("object","Object",EmitAs.Raw), ("objectType","ObjectType",EmitAs.Raw),
            ("label","Label",EmitAs.Raw), ("helpText","HelpText",EmitAs.Raw),
            ("parameters","Parameters",EmitAs.Raw), ("enumTypeParameter","EnumTypeParameter",EmitAs.Raw),
            ("enumParameter","EnumParameter",EmitAs.Raw), ("query","Query",EmitAs.Raw),
            ("reportDesign","ReportDesign",EmitAs.Raw), ("needsRecord","NeedsRecord",EmitAs.Bool),
            ("multiSelect","MultiSelect",EmitAs.Bool), ("openMode","OpenMode",EmitAs.Raw),
            ("formViewOption","FormViewOption",EmitAs.Raw), ("copyCallerQuery","CopyCallerQuery",EmitAs.Bool),
            ("allowRootNavigation","AllowRootNavigation",EmitAs.Bool), ("configurationKey","ConfigurationKey",EmitAs.Raw),
            ("countryConfigurationKey","CountryConfigurationKey",EmitAs.Raw), ("countryRegionCodes","CountryRegionCodes",EmitAs.Raw),
            ("operationalDomain","OperationalDomain",EmitAs.Raw), ("isObsolete","IsObsolete",EmitAs.Bool),
            ("featureClass","FeatureClass",EmitAs.Raw), ("tags","Tags",EmitAs.Raw),
            ("maintainUserLicense","MaintainUserLicense",EmitAs.Raw), ("viewUserLicense","ViewUserLicense",EmitAs.Raw),
            ("linkedPermissionType","LinkedPermissionType",EmitAs.Raw), ("linkedPermissionObject","LinkedPermissionObject",EmitAs.Raw),
            ("linkedPermissionObjectChild","LinkedPermissionObjectChild",EmitAs.Raw), ("extendedDataSecurity","ExtendedDataSecurity",EmitAs.Raw),
            ("createPermissions","CreatePermissions",EmitAs.Raw), ("readPermissions","ReadPermissions",EmitAs.Raw),
            ("updatePermissions","UpdatePermissions",EmitAs.Raw), ("deletePermissions","DeletePermissions",EmitAs.Raw),
            ("correctPermissions","CorrectPermissions",EmitAs.Raw),
            ("stateMachine","StateMachine",EmitAs.Raw), ("stateMachineDataSource","StateMachineDataSource",EmitAs.Raw),
            ("stateMachineTransitionTo","StateMachineTransitionTo",EmitAs.Raw),
        };

        private static readonly (string Key, string Prop)[] ImageFields =
        {
            ("normalImage","NormalImage"), ("disabledImage","DisabledImage"),
            ("imageLocation","ImageLocation"), ("disabledImageLocation","DisabledImageLocation"),
            ("normalResource","NormalResource"), ("disabledResource","DisabledResource"),
        };

        protected override object BuildFromJson(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxMenuItem name is required.");
            var mi = MetaclassMap.Instantiate("AxMenuItem" + _kind);
            MetaclassMap.SetName(mi, name);
            ApplyAll(mi, json);
            return mi;
        }

        protected override object ApplyPatch(object current, JObject patch)
        {
            ApplyAll(current, patch);
            return current;
        }

        private static void ApplyAll(object mi, JObject json)
        {
            foreach (var (key, prop, _) in Fields) MetaclassJson.Assign(mi, prop, json[key]);
            if (json["image"] is JObject img)
                foreach (var (key, prop) in ImageFields) MetaclassJson.Assign(mi, prop, img[key]);
            if (json["subscriberAccessLevel"] is JObject sal) MetaclassMap.ApplySubscriberAccess(mi, sal);
            if (json["advanced"] is JObject adv) MetaclassJson.Assign(mi, "Visibility", adv["visibility"]);
        }

        protected override JObject ReadToJson(object mi)
        {
            var reference = MetaclassMap.Reference(mi.GetType());
            var jo = new JObject
            {
                ["name"] = MetaclassMap.GetName(mi),
                ["kind"] = _kind,
            };
            foreach (var (key, prop, kind) in Fields)
                MetaclassJson.EmitDefaulted(jo, mi, reference, prop, key, kind);

            var img = new JObject();
            foreach (var (key, prop) in ImageFields)
                MetaclassJson.EmitDefaulted(img, mi, reference, prop, key, EmitAs.Raw);
            if (img.Count > 0) jo["image"] = img;

            var sal = MetaclassMap.EmitSubscriberAccess(mi);
            if (sal.Count > 0) jo["subscriberAccessLevel"] = sal;

            var vis = MetaclassJson.ReadEnumCamel(mi, "Visibility");
            if (vis != null && vis != "public") jo["advanced"] = new JObject { ["visibility"] = vis };

            return jo;
        }
    }
}
