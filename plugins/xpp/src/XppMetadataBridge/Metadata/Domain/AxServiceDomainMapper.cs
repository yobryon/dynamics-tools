using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using XppMetadataBridge.Rpc;
using EmitAs = XppMetadataBridge.Metadata.Domain.MetaclassJson.EmitAs;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>AxService — endpoint backed by an X++ class, with ServiceOperations
    /// (each names a Method; per-op SubscriberAccessLevel is an AccessGrant struct).</summary>
    internal sealed class AxServiceDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxService";
        protected override string AccessorProperty => "Services";

        private static readonly (string Key, string Prop, EmitAs Kind)[] Scalars =
        {
            ("class","Class",EmitAs.Raw), ("description","Description",EmitAs.Raw),
            ("externalName","ExternalName",EmitAs.Raw), ("namespace","Namespace",EmitAs.Raw),
            ("isObsolete","IsObsolete",EmitAs.Bool),
        };

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxService");
            MetaclassMap.SetName(ax, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxService name is required."));
            Apply(ax, json);
            return ax;
        }

        protected override object ApplyPatch(object current, JObject patch) { Apply(current, patch); return current; }

        private static void Apply(object ax, JObject json)
        {
            foreach (var (key, prop, _) in Scalars) MetaclassJson.Assign(ax, prop, json[key]);
            var ops = ax.GetType().GetProperty("ServiceOperations")?.GetValue(ax);
            if (ops != null && json["serviceOperations"] is JArray arr)
            {
                var coll = ops; var add = MetaclassMap.AddMethodFor(coll, "AxServiceOperation");
                MetaclassJson.AllowDuplicates(coll);
                foreach (var oj in arr.OfType<JObject>())
                {
                    var op = MetaclassMap.Instantiate("AxServiceOperation");
                    MetaclassMap.SetName(op, (string?)oj["name"] ?? string.Empty);
                    MetaclassJson.Assign(op, "Method", oj["method"]);
                    MetaclassJson.Assign(op, "EnableIdempotence", oj["enableIdempotence"]);
                    if (oj["subscriberAccessLevel"] is JObject sal) MetaclassMap.ApplySubscriberAccess(op, sal);
                    MetaclassMap.AddTo(add, coll, op);
                }
            }
        }

        protected override JObject ReadToJson(object ax)
        {
            var reference = MetaclassMap.Reference(ax.GetType());
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            foreach (var (key, prop, kind) in Scalars)
                MetaclassJson.EmitDefaulted(jo, ax, reference, prop, key, kind);
            var ops = ax.GetType().GetProperty("ServiceOperations")?.GetValue(ax);
            if (ops is IEnumerable en)
            {
                var arr = new JArray();
                foreach (var op in en)
                {
                    var or = MetaclassMap.Reference(op.GetType());
                    var oo = new JObject { ["name"] = MetaclassMap.GetName(op) };
                    MetaclassJson.EmitDefaulted(oo, op, or, "Method", "method", EmitAs.Raw);
                    MetaclassJson.EmitDefaulted(oo, op, or, "EnableIdempotence", "enableIdempotence", EmitAs.Bool);
                    var sal = MetaclassMap.EmitSubscriberAccess(op);
                    if (sal.Count > 0) oo["subscriberAccessLevel"] = sal;
                    arr.Add(oo);
                }
                if (arr.Count > 0) jo["serviceOperations"] = arr;
            }
            return jo;
        }
    }

    /// <summary>AxServiceGroup — deployment bundle of services.</summary>
    internal sealed class AxServiceGroupDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxServiceGroup";
        protected override string AccessorProperty => "ServiceGroups";

        protected override object BuildFromJson(JObject json)
        {
            var ax = MetaclassMap.Instantiate("AxServiceGroup");
            MetaclassMap.SetName(ax, (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxServiceGroup name is required."));
            Apply(ax, json);
            return ax;
        }

        protected override object ApplyPatch(object current, JObject patch) { Apply(current, patch); return current; }

        private static void Apply(object ax, JObject json)
        {
            MetaclassJson.Assign(ax, "AutoDeploy", json["autoDeploy"]);
            MetaclassJson.Assign(ax, "Description", json["description"]);
            MetaclassJson.Assign(ax, "IsObsolete", json["isObsolete"]);
            var coll = ax.GetType().GetProperty("Services")?.GetValue(ax);
            if (coll != null && json["services"] is JArray arr)
            {
                var add = MetaclassMap.AddMethodFor(coll, "AxServiceGroupService");
                MetaclassJson.AllowDuplicates(coll);
                foreach (var sj in arr.OfType<JObject>())
                {
                    var m = MetaclassMap.Instantiate("AxServiceGroupService");
                    MetaclassMap.SetName(m, (string?)sj["name"] ?? string.Empty);
                    MetaclassJson.Assign(m, "Service", sj["service"]);
                    MetaclassMap.AddTo(add, coll, m);
                }
            }
        }

        protected override JObject ReadToJson(object ax)
        {
            var reference = MetaclassMap.Reference(ax.GetType());
            var jo = new JObject { ["name"] = MetaclassMap.GetName(ax) };
            MetaclassJson.EmitDefaulted(jo, ax, reference, "AutoDeploy", "autoDeploy", EmitAs.Bool);
            MetaclassJson.EmitDefaulted(jo, ax, reference, "Description", "description", EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, reference, "IsObsolete", "isObsolete", EmitAs.Bool);
            var coll = ax.GetType().GetProperty("Services")?.GetValue(ax);
            if (coll is IEnumerable en)
            {
                var arr = new JArray();
                foreach (var m in en)
                {
                    var oo = new JObject { ["name"] = MetaclassMap.GetName(m) };
                    var svc = m.GetType().GetProperty("Service")?.GetValue(m) as string;
                    if (!string.IsNullOrEmpty(svc)) oo["service"] = svc;
                    arr.Add(oo);
                }
                if (arr.Count > 0) jo["services"] = arr;
            }
            return jo;
        }
    }
}
