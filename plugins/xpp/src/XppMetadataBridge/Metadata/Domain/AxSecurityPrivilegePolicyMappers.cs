using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    internal sealed class AxSecurityPrivilegeDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxSecurityPrivilege";
        protected override string AccessorProperty => "SecurityPrivileges";

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxSecurityPrivilege");
            MetaclassMap.SetName(ax, (string?)json["name"] ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "name required"));
            Apply(ax, json); return ax;
        }
        protected override object ApplyPatch(object current, JObject patch) { Apply(current, patch); return current; }

        private static void Apply(object ax, JObject json)
        {
            MetaclassJson.Assign(ax, "Label", json["label"]);
            MetaclassJson.Assign(ax, "Description", json["description"]);
            // DataEntityPermissions = AxSecurityDataEntityPermission (Grant + Fields + Methods).
            SecurityHelpers.BuildDataEntityRefs(ax, "DataEntityPermissions", "AxSecurityDataEntityPermission", json["dataEntityPermissions"] as JArray);
            SecurityHelpers.BuildDataEntityRefs(ax, "DirectAccessPermissions", "AxSecurityDataEntityReference", json["directAccessPermissions"] as JArray);
            BuildEntryPoints(ax, json["entryPoints"] as JArray);
            BuildFormControlOverrides(ax, json["formControlOverrides"] as JArray);
        }

        protected override JObject ReadToJson(object ax)
        {
            var r = MetaclassMap.Reference(ax.GetType());
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            MetaclassJson.EmitDefaulted(jo, ax, r, "Label", "label", EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, r, "Description", "description", EmitAs.Raw);
            var dep = SecurityHelpers.EmitDataEntityRefs(ax, "DataEntityPermissions"); if (dep.Count > 0) jo["dataEntityPermissions"] = dep;
            var dap = SecurityHelpers.EmitDataEntityRefs(ax, "DirectAccessPermissions"); if (dap.Count > 0) jo["directAccessPermissions"] = dap;
            var eps = EmitEntryPoints(ax); if (eps.Count > 0) jo["entryPoints"] = eps;
            var fco = EmitFormControlOverrides(ax); if (fco.Count > 0) jo["formControlOverrides"] = fco;
            return jo;
        }

        // EntryPoints = AxSecurityEntryPointReference. The XML-era
        // AxSecurityEntryPointReferenceForm element-name bug dissolves here.
        private static void BuildEntryPoints(object parent, JArray? arr)
        {
            if (arr == null) return;
            var coll = parent.GetType().GetProperty("EntryPoints")?.GetValue(parent);
            if (coll == null) return;
            MetaclassJson.AllowDuplicates(coll);
            var add = MetaclassMap.AddMethodFor(coll, "AxSecurityEntryPointReference");
            foreach (var ej in arr.OfType<JObject>())
            {
                var ep = MetaclassMap.Instantiate("AxSecurityEntryPointReference");
                MetaclassMap.SetName(ep, (string?)ej["name"] ?? string.Empty);
                MetaclassJson.Assign(ep, "ObjectName", ej["objectName"]);
                MetaclassJson.Assign(ep, "ObjectType", ej["objectType"]);
                MetaclassJson.Assign(ep, "ObjectChildName", ej["objectChildName"]);
                if (ej["grant"] is JObject g) MetaclassMap.ApplyAccessGrant(ep, "Grant", g);
                if (ej["forms"] is JArray forms)
                {
                    var fc = ep.GetType().GetProperty("Forms")?.GetValue(ep);
                    if (fc != null)
                    {
                        MetaclassJson.AllowDuplicates(fc);
                        var fadd = MetaclassMap.AddMethodFor(fc, "AxSecurityEntryPointReferenceForm");
                        foreach (var fn in forms)
                        {
                            var fo = MetaclassMap.Instantiate("AxSecurityEntryPointReferenceForm");
                            MetaclassMap.SetName(fo, fn.Type == JTokenType.String ? (string)fn! : (string?)(fn as JObject)?["name"] ?? string.Empty);
                            MetaclassMap.AddTo(fadd, fc, fo);
                        }
                    }
                }
                MetaclassMap.AddTo(add, coll, ep);
            }
        }

        private static JArray EmitEntryPoints(object parent)
        {
            var arr = new JArray();
            if (parent.GetType().GetProperty("EntryPoints")?.GetValue(parent) is not IEnumerable en) return arr;
            foreach (var ep in en)
            {
                var er = MetaclassMap.Reference(ep.GetType());
                var o = new JObject { ["name"] = MetaclassMap.GetName(ep) };
                MetaclassJson.EmitDefaulted(o, ep, er, "ObjectName", "objectName", EmitAs.Raw);
                MetaclassJson.EmitDefaulted(o, ep, er, "ObjectType", "objectType", EmitAs.EnumCamel);
                MetaclassJson.EmitDefaulted(o, ep, er, "ObjectChildName", "objectChildName", EmitAs.Raw);
                var g = MetaclassMap.EmitAccessGrant(ep, "Grant");
                if (g.Count > 0) o["grant"] = g;
                if (ep.GetType().GetProperty("Forms")?.GetValue(ep) is IEnumerable fe)
                {
                    var forms = new JArray();
                    foreach (var f in fe) forms.Add(MetaclassMap.GetName(f));
                    if (forms.Count > 0) o["forms"] = forms;
                }
                arr.Add(o);
            }
            return arr;
        }

        // FormControlOverrides = AxSecurityFormControlReferenceCollection {Name, Controls=AxSecurityFormControlReference{Name,Grant}}.
        private static void BuildFormControlOverrides(object parent, JArray? arr)
        {
            if (arr == null) return;
            var coll = parent.GetType().GetProperty("FormControlOverrides")?.GetValue(parent);
            if (coll == null) return;
            MetaclassJson.AllowDuplicates(coll);
            var add = MetaclassMap.AddMethodFor(coll, "AxSecurityFormControlReferenceCollection");
            foreach (var cj in arr.OfType<JObject>())
            {
                var c = MetaclassMap.Instantiate("AxSecurityFormControlReferenceCollection");
                MetaclassMap.SetName(c, (string?)cj["name"] ?? string.Empty);
                SecurityHelpers.BuildGrantRefs(c, "Controls", "AxSecurityFormControlReference", cj["controls"] as JArray);
                MetaclassMap.AddTo(add, coll, c);
            }
        }

        private static JArray EmitFormControlOverrides(object parent)
        {
            var arr = new JArray();
            if (parent.GetType().GetProperty("FormControlOverrides")?.GetValue(parent) is not IEnumerable en) return arr;
            foreach (var c in en)
            {
                var o = new JObject { ["name"] = MetaclassMap.GetName(c) };
                var ctrls = SecurityHelpers.EmitGrantRefs(c, "Controls");
                if (ctrls.Count > 0) o["controls"] = ctrls;
                arr.Add(o);
            }
            return arr;
        }
    }

    internal sealed class AxSecurityPolicyDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxSecurityPolicy";
        protected override string AccessorProperty => "SecurityPolicies";

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxSecurityPolicy");
            MetaclassMap.SetName(ax, (string?)json["name"] ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "name required"));
            Apply(ax, json); return ax;
        }
        protected override object ApplyPatch(object current, JObject patch) { Apply(current, patch); return current; }

        private static readonly (string Key, string Prop, EmitAs Kind)[] Scalars =
        {
            ("label","Label",EmitAs.Raw), ("description","HelpText",EmitAs.Raw),
            ("constrainedTable","ConstrainedTable",EmitAs.Bool), ("enabled","Enabled",EmitAs.Bool),
            ("primaryTable","PrimaryTable",EmitAs.Raw), ("query","Query",EmitAs.Raw),
            ("contextType","ContextType",EmitAs.EnumCamel), ("contextString","ContextString",EmitAs.Raw),
            ("operation","Operation",EmitAs.EnumCamel), ("roleName","RoleName",EmitAs.Raw),
            ("useNotExistJoin","UseNotExistJoin",EmitAs.Bool),
        };

        private static void Apply(object ax, JObject json)
        {
            foreach (var (key, prop, _) in Scalars) MetaclassJson.Assign(ax, prop, json[key]);
            BuildConstrained(ax, json["constrainedTables"] as JArray);
        }

        protected override JObject ReadToJson(object ax)
        {
            var r = MetaclassMap.Reference(ax.GetType());
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            foreach (var (key, prop, kind) in Scalars)
                MetaclassJson.EmitDefaulted(jo, ax, r, prop, key, kind);
            var ct = EmitConstrained(ax);
            if (ct.Count > 0) jo["constrainedTables"] = ct;
            return jo;
        }

        // Recursive: AxSecurityPolicyConstrainedTable / ConstrainedExpression.
        private static void BuildConstrained(object parent, JArray? arr)
        {
            if (arr == null) return;
            var coll = parent.GetType().GetProperty("ConstrainedTables")?.GetValue(parent);
            if (coll == null) return;
            MetaclassJson.AllowDuplicates(coll);
            var add = coll.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
            foreach (var cj in arr.OfType<JObject>())
            {
                var kind = (string?)cj["kind"] ?? "Table";
                var typeName = kind.Equals("Expression", StringComparison.OrdinalIgnoreCase)
                    ? "AxSecurityPolicyConstrainedExpression" : "AxSecurityPolicyConstrainedTable";
                var c = MetaclassMap.Instantiate(typeName);
                MetaclassMap.SetName(c, (string?)cj["name"] ?? string.Empty);
                MetaclassJson.Assign(c, "Constrained", cj["constrained"]);
                MetaclassJson.Assign(c, "TableRelation", cj["tableRelation"]); // no-op on Expression
                MetaclassJson.Assign(c, "Value", cj["value"]);                 // no-op on Table
                MetaclassJson.Assign(c, "Tags", cj["tags"]);
                BuildConstrained(c, cj["constrainedTables"] as JArray);
                add?.Invoke(coll, new[] { c });
            }
        }

        private static JArray EmitConstrained(object parent)
        {
            var arr = new JArray();
            if (parent.GetType().GetProperty("ConstrainedTables")?.GetValue(parent) is not IEnumerable en) return arr;
            foreach (var c in en)
            {
                var tn = c.GetType().Name;
                var rc = MetaclassMap.Reference(c.GetType());
                var o = new JObject
                {
                    ["kind"] = tn.Contains("Expression") ? "Expression" : "Table",
                    ["name"] = MetaclassMap.GetName(c),
                };
                MetaclassJson.EmitDefaulted(o, c, rc, "Constrained", "constrained", EmitAs.Bool);
                var tr = c.GetType().GetProperty("TableRelation")?.GetValue(c) as string;
                if (!string.IsNullOrEmpty(tr)) o["tableRelation"] = tr;
                var val = c.GetType().GetProperty("Value")?.GetValue(c) as string;
                if (!string.IsNullOrEmpty(val)) o["value"] = val;
                var tags = c.GetType().GetProperty("Tags")?.GetValue(c) as string;
                if (!string.IsNullOrEmpty(tags)) o["tags"] = tags;
                var nested = EmitConstrained(c);
                if (nested.Count > 0) o["constrainedTables"] = nested;
                arr.Add(o);
            }
            return arr;
        }
    }
}
