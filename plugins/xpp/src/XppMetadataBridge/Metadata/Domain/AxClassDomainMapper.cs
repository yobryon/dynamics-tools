using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using NoYes = Microsoft.Dynamics.AX.Metadata.Core.MetaModel.NoYes;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// AxClass domain mapper, bridge-side. The class semantics that matter
    /// for authoring are the opaque X++ <c>Declaration</c> string and the
    /// <c>Methods</c> collection (each an <see cref="AxMethod"/> with an
    /// opaque <c>Source</c>). MS-shipped classes seldom set the XML-level
    /// scalar flags (IsAbstract/Extends/RunOn/...) — those live in the
    /// Declaration X++ — but we map them when present for completeness.
    ///
    /// JSON mirrors CreateClassRequest: name, sourceCode { declaration,
    /// methods[] { name, source } }, isObsolete, tags, advanced { ... }.
    /// </summary>
    internal sealed class AxClassDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxClass";
        protected override string AccessorProperty => "Classes";

        protected override object BuildFromJson(JObject json) => BuildAxClass(json);

        protected override object ApplyPatch(object current, JObject patch)
        {
            ApplyPatchToClass((AxClass)current, patch);
            return current;
        }

        protected override JObject ReadToJson(object meta) => ToDomainJson((AxClass)meta);

        // -------------------------------------------------------------------
        private static AxClass BuildAxClass(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxClass name is required.");
            var ax = new AxClass { Name = name };

            var sc = json["sourceCode"] as JObject;
            var decl = (string?)sc?["declaration"];
            ax.Declaration = string.IsNullOrEmpty(decl)
                ? $"\npublic class {name}\n{{\n}}\n"
                : decl;

            if (sc?["methods"] is JArray methods)
                foreach (var m in methods.OfType<JObject>())
                    ax.Methods.Add(BuildAxMethod(m));

            ApplyScalars(ax, json);
            return ax;
        }

        private static AxMethod BuildAxMethod(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxMethod name is required.");
            var m = new AxMethod { Name = name };
            if (json["source"] is JToken s && s.Type == JTokenType.String) m.Source = MethodSource.NormalizeIndent((string)s!);
            return m;
        }

        private static void ApplyScalars(AxClass ax, JObject json)
        {
            if (json["isObsolete"] is JToken io && io.Type == JTokenType.Boolean)
                ax.IsObsolete = (bool)io ? NoYes.Yes : NoYes.No;
            if (json["tags"] is JToken tg && tg.Type == JTokenType.String) ax.Tags = (string)tg!;

            if (json["advanced"] is JObject adv)
            {
                if (adv["isAbstract"] is JToken a && a.Type == JTokenType.Boolean) ax.IsAbstract = (bool)a;
                if (adv["isFinal"] is JToken f && f.Type == JTokenType.Boolean) ax.IsFinal = (bool)f;
                if (adv["isInterface"] is JToken it && it.Type == JTokenType.Boolean) ax.IsInterface = (bool)it;
                if (adv["isInternal"] is JToken inr && inr.Type == JTokenType.Boolean) ax.IsInternal = (bool)inr;
                if (adv["isPrivate"] is JToken p && p.Type == JTokenType.Boolean) ax.IsPrivate = (bool)p;
                if (adv["isPublic"] is JToken pu && pu.Type == JTokenType.Boolean) ax.IsPublic = (bool)pu;
                if (adv["isStatic"] is JToken st && st.Type == JTokenType.Boolean) ax.IsStatic = (bool)st;
                if (adv["extends"] is JToken ex && ex.Type == JTokenType.String) ax.Extends = (string)ex!;
                if (adv["runOn"] is JToken ro && ro.Type == JTokenType.String) AssignEnumByName(ax, "RunOn", (string)ro!);
            }
        }

        private static void ApplyPatchToClass(AxClass ax, JObject patch)
        {
            if (patch["sourceCode"] is JObject sc)
            {
                if (sc["declaration"] is JToken d && d.Type == JTokenType.String) ax.Declaration = (string)d!;
                if (sc["methods"] is JArray methods)
                {
                    ax.Methods.Clear();
                    foreach (var m in methods.OfType<JObject>())
                        ax.Methods.Add(BuildAxMethod(m));
                }
            }
            ApplyScalars(ax, patch);
        }

        // -------------------------------------------------------------------
        private static JObject ToDomainJson(AxClass ax)
        {
            var jo = new JObject { ["name"] = ax.Name };

            var sc = new JObject();
            if (!string.IsNullOrEmpty(ax.Declaration)) sc["declaration"] = ax.Declaration;
            var methods = new JArray();
            foreach (var m in ax.Methods)
            {
                var mj = new JObject { ["name"] = m.Name };
                if (!string.IsNullOrEmpty(m.Source)) mj["source"] = m.Source;
                methods.Add(mj);
            }
            if (methods.Count > 0) sc["methods"] = methods;
            if (sc.Count > 0) jo["sourceCode"] = sc;

            if (ax.IsObsolete == NoYes.Yes) jo["isObsolete"] = true;
            if (!string.IsNullOrEmpty(ax.Tags)) jo["tags"] = ax.Tags;

            var adv = new JObject();
            if (ax.IsAbstract) adv["isAbstract"] = true;
            if (ax.IsFinal) adv["isFinal"] = true;
            if (ax.IsInterface) adv["isInterface"] = true;
            if (ax.IsInternal) adv["isInternal"] = true;
            if (ax.IsPrivate) adv["isPrivate"] = true;
            if (ax.IsPublic) adv["isPublic"] = true;
            if (ax.IsStatic) adv["isStatic"] = true;
            if (!string.IsNullOrEmpty(ax.Extends)) adv["extends"] = ax.Extends;
            var runOn = typeof(AxClass).GetProperty("RunOn")?.GetValue(ax)?.ToString();
            // RunOn default is typically "Called" — emit only when meaningful.
            if (!string.IsNullOrEmpty(runOn) && runOn != "Called") adv["runOn"] = runOn;
            if (adv.Count > 0) jo["advanced"] = adv;

            return jo;
        }

        private static void AssignEnumByName(object target, string propName, string value)
        {
            var prop = target.GetType().GetProperty(propName);
            if (prop == null || !prop.PropertyType.IsEnum) return;
            try { prop.SetValue(target, Enum.Parse(prop.PropertyType, value, ignoreCase: true)); }
            catch { /* unknown enum name — ignore */ }
        }

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
