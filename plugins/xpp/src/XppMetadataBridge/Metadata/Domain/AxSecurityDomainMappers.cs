using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    // Shared helpers for the AxSecurity* family. Grants are the AccessGrant
    // struct (via MetaclassMap.Apply/EmitAccessGrant). The XML-era element-name
    // bugs (e.g. AxSecurityEntryPointReferenceForm) dissolve here.
    internal static class SecurityHelpers
    {
        public static void BuildDataEntityRefs(object parent, string collProp, string elemType, JArray? arr)
        {
            if (arr == null) return;
            var coll = parent.GetType().GetProperty(collProp)?.GetValue(parent);
            if (coll == null) return;
            if (coll is IList ilc) ilc.Clear();   // replace-wholesale on patch (see MetaclassMap.BuildRefs)
            MetaclassJson.AllowDuplicates(coll);
            var add = MetaclassMap.AddMethodFor(coll, elemType);
            // DataEntityReference uses *FieldReference (no methods);
            // DataEntityPermission uses *FieldPermission + *MethodPermission.
            var isPermission = elemType.EndsWith("Permission");
            var fieldType = isPermission ? "AxSecurityDataEntityFieldPermission" : "AxSecurityDataEntityFieldReference";
            var methodType = isPermission ? "AxSecurityDataEntityMethodPermission" : null;
            foreach (var rj in arr.OfType<JObject>())
            {
                var r = MetaclassMap.Instantiate(elemType);
                MetaclassMap.SetName(r, (string?)rj["name"] ?? string.Empty);
                if (rj["grant"] is JObject g) MetaclassMap.ApplyAccessGrant(r, "Grant", g);
                BuildGrantRefs(r, "Fields", fieldType, rj["fields"] as JArray);
                if (methodType != null) BuildGrantRefs(r, "Methods", methodType, rj["methods"] as JArray);
                MetaclassMap.AddTo(add, coll, r);
            }
        }

        public static JArray EmitDataEntityRefs(object parent, string collProp)
        {
            var arr = new JArray();
            var coll = parent.GetType().GetProperty(collProp)?.GetValue(parent);
            if (coll is not IEnumerable en) return arr;
            foreach (var r in en)
            {
                var o = new JObject { ["name"] = MetaclassMap.GetName(r) };
                var g = MetaclassMap.EmitAccessGrant(r, "Grant");
                if (g.Count > 0) o["grant"] = g;
                var f = EmitGrantRefs(r, "Fields");
                if (f.Count > 0) o["fields"] = f;
                var m = EmitGrantRefs(r, "Methods");
                if (m.Count > 0) o["methods"] = m;
                arr.Add(o);
            }
            return arr;
        }

        // {name, grant} reference items (field / method grants).
        public static void BuildGrantRefs(object parent, string collProp, string elemType, JArray? arr)
        {
            if (arr == null) return;
            var coll = parent.GetType().GetProperty(collProp)?.GetValue(parent);
            if (coll == null) return;
            if (coll is IList ilg) ilg.Clear();   // replace-wholesale on patch (see MetaclassMap.BuildRefs)
            MetaclassJson.AllowDuplicates(coll);
            var add = MetaclassMap.AddMethodFor(coll, elemType);
            foreach (var rj in arr.OfType<JObject>())
            {
                var r = MetaclassMap.Instantiate(elemType);
                MetaclassMap.SetName(r, (string?)rj["name"] ?? string.Empty);
                if (rj["grant"] is JObject g) MetaclassMap.ApplyAccessGrant(r, "Grant", g);
                MetaclassMap.AddTo(add, coll, r);
            }
        }

        public static JArray EmitGrantRefs(object parent, string collProp)
        {
            var arr = new JArray();
            var coll = parent.GetType().GetProperty(collProp)?.GetValue(parent);
            if (coll is not IEnumerable en) return arr;
            foreach (var r in en)
            {
                var o = new JObject { ["name"] = MetaclassMap.GetName(r) };
                var g = MetaclassMap.EmitAccessGrant(r, "Grant");
                if (g.Count > 0) o["grant"] = g;
                arr.Add(o);
            }
            return arr;
        }
    }

    internal sealed class AxSecurityDutyDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxSecurityDuty";
        protected override string AccessorProperty => "SecurityDuties";

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxSecurityDuty");
            MetaclassMap.SetName(ax, (string?)json["name"] ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "name required"));
            Apply(ax, json); return ax;
        }
        protected override object ApplyPatch(object current, JObject patch) { Apply(current, patch); return current; }
        private static void Apply(object ax, JObject json)
        {
            MetaclassJson.Assign(ax, "Label", json["label"]);
            MetaclassJson.Assign(ax, "Description", json["description"]);
            MetaclassMap.BuildRefs(ax, "Privileges", "AxSecurityPrivilegeReference", json["privileges"] as JArray);
        }
        protected override JObject ReadToJson(object ax)
        {
            var r = MetaclassMap.Reference(ax.GetType());
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            MetaclassJson.EmitDefaulted(jo, ax, r, "Label", "label", EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, r, "Description", "description", EmitAs.Raw);
            var p = MetaclassMap.EmitRefs(ax, "Privileges");
            if (p.Count > 0) jo["privileges"] = p;
            return jo;
        }
    }

    internal sealed class AxSecurityRoleDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxSecurityRole";
        protected override string AccessorProperty => "SecurityRoles";

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxSecurityRole");
            MetaclassMap.SetName(ax, (string?)json["name"] ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "name required"));
            Apply(ax, json); return ax;
        }
        protected override object ApplyPatch(object current, JObject patch) { Apply(current, patch); return current; }
        private static void Apply(object ax, JObject json)
        {
            MetaclassJson.Assign(ax, "Label", json["label"]);
            MetaclassJson.Assign(ax, "Description", json["description"]);
            MetaclassJson.Assign(ax, "ContextString", json["contextString"]);
            MetaclassJson.Assign(ax, "CanBeDeletedFromUI", json["canBeDeletedFromUI"]);
            SecurityHelpers.BuildDataEntityRefs(ax, "DirectAccessPermissions", "AxSecurityDataEntityReference", json["directAccessPermissions"] as JArray);
            MetaclassMap.BuildRefs(ax, "Duties", "AxSecurityDutyReference", json["duties"] as JArray);
            MetaclassMap.BuildRefs(ax, "Privileges", "AxSecurityPrivilegeReference", json["privileges"] as JArray);
            MetaclassMap.BuildRefs(ax, "SubRoles", "AxSecurityRoleReference", json["subRoles"] as JArray);
        }
        protected override JObject ReadToJson(object ax)
        {
            var r = MetaclassMap.Reference(ax.GetType());
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            MetaclassJson.EmitDefaulted(jo, ax, r, "Label", "label", EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, r, "Description", "description", EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, r, "ContextString", "contextString", EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, r, "CanBeDeletedFromUI", "canBeDeletedFromUI", EmitAs.Bool);
            var dap = SecurityHelpers.EmitDataEntityRefs(ax, "DirectAccessPermissions");
            if (dap.Count > 0) jo["directAccessPermissions"] = dap;
            var d = MetaclassMap.EmitRefs(ax, "Duties"); if (d.Count > 0) jo["duties"] = d;
            var p = MetaclassMap.EmitRefs(ax, "Privileges"); if (p.Count > 0) jo["privileges"] = p;
            var s = MetaclassMap.EmitRefs(ax, "SubRoles"); if (s.Count > 0) jo["subRoles"] = s;
            return jo;
        }
    }
}
