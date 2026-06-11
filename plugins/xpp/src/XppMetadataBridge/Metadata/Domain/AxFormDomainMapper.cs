using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using XppMetadataBridge.Rpc;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// AxForm domain mapper, bridge-side. The deepest tree. Strategy:
    ///
    /// - Scalars + the few domain-typed fields map by name; everything else
    ///   on a metaclass object that isn't structural is dumped into the
    ///   domain "otherProperties" bag as PascalName -> ToString strings, and
    ///   Assign coerces them back on write. No per-control-subtype tables.
    /// - SOURCE/METADATA SPLIT: the domain FormSourceCode separates method
    ///   bodies (per-datasource, per-control) from the metadata tree, mirroring
    ///   the on-disk XML. The metaclass unifies them on each object's .Methods
    ///   collection. We SPLIT on read (emit metadata under dataSources/design,
    ///   emit methods under sourceCode.dataSources / sourceCode.dataControls)
    ///   and MERGE on write (match by name).
    /// - Members are derived from the Declaration source (no separate on-disk
    ///   serialization) — not round-tripped.
    /// </summary>
    internal sealed class AxFormDomainMapper : DomainBridgeMapperBase
    {
        public override string AxType => "AxForm";
        protected override string AccessorProperty => "Forms";
        private const string MetaNs = "Microsoft.Dynamics.AX.Metadata.MetaModel.";

        // Run MS's pattern engine over the built form: auto-stamp pattern-
        // prescribed property values and report the residual the stamp can't
        // fix (missing controls, negative constraints, author overrides).
        // Best-effort — never blocks the write.
        protected override JObject? Conform(object meta, JObject requestJson, bool isPatch)
            => FormPatternConformance.Analyze(meta, requestJson);

        // ---- control kind <-> metaclass type ------------------------------
        private static readonly Dictionary<string, string> KindToType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Group"] = "AxFormGroupControl", ["Tab"] = "AxFormTabControl", ["TabPage"] = "AxFormTabPageControl",
            ["Grid"] = "AxFormGridControl", ["Container"] = "AxFormContainerControl",
            ["ActionPane"] = "AxFormActionPaneControl", ["ActionPaneTab"] = "AxFormActionPaneTabControl",
            ["ButtonGroup"] = "AxFormButtonGroupControl", ["String"] = "AxFormStringControl",
            ["Integer"] = "AxFormIntegerControl", ["Int64"] = "AxFormInt64Control", ["Real"] = "AxFormRealControl",
            ["Date"] = "AxFormDateControl", ["DateTime"] = "AxFormDateTimeControl", ["ComboBox"] = "AxFormComboBoxControl",
            ["CheckBox"] = "AxFormCheckBoxControl", ["ReferenceGroup"] = "AxFormReferenceGroupControl",
            ["Button"] = "AxFormButtonControl", ["MenuFunctionButton"] = "AxFormMenuFunctionButtonControl",
            ["CommandButton"] = "AxFormCommandButtonControl", ["StaticText"] = "AxFormStaticTextControl",
            ["SegmentedEntry"] = "AxFormSegmentedEntryControl",
            // Promoted from kind=Other after the ContosoRetail coverage analysis
            // showed these are common control kinds (forms reached for rawType).
            ["Image"] = "AxFormImageControl", ["MenuButton"] = "AxFormMenuButtonControl",
            ["DropDialogButton"] = "AxFormDropDialogButtonControl",
            ["ButtonSeparator"] = "AxFormButtonSeparatorControl",
            ["Time"] = "AxFormTimeControl", ["RadioButton"] = "AxFormRadioButtonControl",
            // Added after the Foundation comparison surfaced these in richer MS
            // forms (Tree 145, ListView 111, ListBox 13) that the custom models
            // never exercised.
            ["Tree"] = "AxFormTreeControl", ["ListView"] = "AxFormListViewControl",
            ["ListBox"] = "AxFormListBoxControl",
        };
        private static readonly Dictionary<string, string> TypeToKind =
            KindToType.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> DsKindToType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Root"] = "AxFormDataSourceRoot", ["Derived"] = "AxFormDataSourceDerived",
            ["Referenced"] = "AxFormDataSourceReferenced", ["Concrete"] = "AxFormDataSourceRoot",
        };

        // Domain-typed control fields → (metaclassProp, bool?). Everything
        // else non-structural is dumped to otherProperties.
        private static readonly (string Key, string Prop, bool Bool)[] ControlTyped =
        {
            ("visible","Visible",true), ("enabled","Enabled",true), ("allowEdit","AllowEdit",true),
            ("skip","Skip",true), ("autoDeclaration","AutoDeclaration",true), ("mandatory","Mandatory",true),
            ("pattern","Pattern",false), ("patternVersion","PatternVersion",false), ("helpText","HelpText",false),
            ("widthMode","WidthMode",false), ("heightMode","HeightMode",false),
            ("configurationKey","ConfigurationKey",false), ("tags","Tags",false),
            ("dataField","DataField",false), ("dataSource","DataSource",false), ("label","Label",false),
            ("caption","Caption",false), ("style","Style",false), ("viewEditMode","ViewEditMode",false),
            ("text","Text",false), ("command","Command",false), ("menuItemName","MenuItemName",false),
            ("menuItemType","MenuItemType",false),
        };

        private static readonly HashSet<string> ControlStructural = new(StringComparer.Ordinal)
        {
            "Name", "Type", "Controls", "FormControlExtension", "Methods", "DeltaMethods",
            "Attributes", "Conflicts", "CompilerMetadata", "TypeParameters", "UnparsableSource",
        };

        private static readonly (string Key, string Prop, bool Bool)[] DesignTyped =
        {
            ("caption","Caption",false), ("pattern","Pattern",false), ("patternVersion","PatternVersion",false),
            ("style","Style",false), ("viewEditMode","ViewEditMode",false), ("titleDataSource","TitleDataSource",false),
        };
        private static readonly HashSet<string> DesignStructural = new(StringComparer.Ordinal)
        {
            "Name", "Controls", "Attributes", "Conflicts", "CompilerMetadata",
        };

        private static readonly (string Key, string Prop, bool Bool)[] DsTyped =
        {
            ("table","Table",false), ("allowEdit","AllowEdit",true), ("allowCreate","AllowCreate",true),
            ("allowDelete","AllowDelete",true), ("onlyFetchActive","OnlyFetchActive",true),
            ("joinSource","JoinSource",false), ("startPosition","StartPosition",false),
            ("index","Index",false), ("insertIfEmpty","InsertIfEmpty",true), ("tags","Tags",false),
        };
        private static readonly HashSet<string> DsStructural = new(StringComparer.Ordinal)
        {
            "Name", "Fields", "ReferencedDataSources", "DataSourceLinks", "DerivedDataSources",
            "Methods", "DeltaMethods", "Attributes", "Conflicts", "CompilerMetadata",
            // LinkType is emitted first-class (camelCase enum) outside DsTyped, so
            // it must be skipped here or EmitOther would ALSO dump it into the
            // otherProperties bag (duplicate, PascalCase) for non-default values.
            "LinkType",
        };

        private static readonly (string Key, string Prop, bool Bool)[] FieldTyped =
        {
            ("allowEdit","AllowEdit",true), ("visible","Visible",true), ("skip","Skip",true),
            ("mandatory","Mandatory",true), ("tags","Tags",false),
        };
        private static readonly HashSet<string> FieldStructural = new(StringComparer.Ordinal)
        {
            "Name", "DataField", "Methods", "DeltaMethods", "Attributes", "Conflicts", "CompilerMetadata",
        };

        protected override object BuildFromJson(JObject json) => BuildForm(json);

        protected override object ApplyPatch(object current, JObject patch)
        {
            // Forms patch wholesale-replaces collections (matches legacy): take
            // the current form's domain view, overlay the patch keys, rebuild.
            var merged = (JObject)ToDomainJson((AxForm)current).DeepClone();
            foreach (var p in patch.Properties()) merged[p.Name] = p.Value;
            return BuildForm(merged);
        }

        protected override JObject ReadToJson(object meta) => ToDomainJson((AxForm)meta);

        // ===================================================================
        // BUILD
        // ===================================================================
        private static AxForm BuildForm(JObject json)
        {
            var name = (string?)json["name"]
                ?? throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "AxForm name is required.");
            var ax = new AxForm { Name = name };

            // Top scalars.
            foreach (var p in json.Properties())
            {
                if (p.Name is "name" or "sourceCode" or "dataSources" or "design" or "parts" or "advanced") continue;
                MetaclassJson.Assign(ax, Pascal(p.Name), p.Value);
            }
            if (json["advanced"] is JObject adv) MetaclassJson.Assign(ax, "Visibility", adv["visibility"]);

            // SourceCode: the form classDeclaration is an AxMethod named
            // "classDeclaration"; the rest are regular form methods. Member
            // variable declarations live inside that classDeclaration text.
            var sc = json["sourceCode"] as JObject;
            var declaration = (sc?["declaration"] as JToken)?.Type == JTokenType.String
                ? (string)sc!["declaration"]!
                : $"\n[Form]\npublic class {name} extends FormRun\n{{\n}}\n";
            MetaclassJson.AllowDuplicates(ax.Methods);
            var addM = ax.Methods.GetType().GetMethod("Add", new[] { typeof(AxMethod) });
            addM?.Invoke(ax.Methods, new object[] { new AxMethod { Name = "classDeclaration", Source = declaration } });
            // Other methods (skip any duplicate classDeclaration the caller sent).
            if (sc?["methods"] is JArray formMethodsArr)
                foreach (var m in formMethodsArr.OfType<JObject>())
                {
                    if (string.Equals((string?)m["name"], "classDeclaration", StringComparison.Ordinal)) continue;
                    var am = new AxMethod { Name = (string?)m["name"] ?? string.Empty };
                    if (m["source"] is JToken ms && ms.Type == JTokenType.String) am.Source = (string)ms!;
                    addM?.Invoke(ax.Methods, new object[] { am });
                }

            // DataSources (metadata) + merge per-DS / per-field method bodies.
            var dsSource = IndexByName(sc?["dataSources"] as JArray);
            if (json["dataSources"] is JArray dss)
            {
                MetaclassJson.AllowDuplicates(ax.DataSources);
                foreach (var ds in dss.OfType<JObject>())
                    ax.DataSources.Add(BuildDataSource(ds, dsSource));
            }

            // Design + merge per-control method bodies.
            var ctlSource = IndexByName(sc?["dataControls"] as JArray);
            if (json["design"] is JObject design)
                ApplyDesign(ax.Design, design, ctlSource);

            // Parts.
            if (json["parts"] is JArray parts)
            {
                MetaclassJson.AllowDuplicates(ax.Parts);
                foreach (var pt in parts.OfType<JObject>()) ax.Parts.Add(BuildPart(pt));
            }
            return ax;
        }

        private static void AddMethods(object methodColl, JArray? methods)
        {
            if (methods == null) return;
            MetaclassJson.AllowDuplicates(methodColl);
            var add = methodColl.GetType().GetMethod("Add", new[] { typeof(AxMethod) });
            foreach (var m in methods.OfType<JObject>())
            {
                var am = new AxMethod { Name = (string?)m["name"] ?? string.Empty };
                if (m["source"] is JToken s && s.Type == JTokenType.String) am.Source = (string)s!;
                add?.Invoke(methodColl, new object[] { am });
            }
        }

        internal static Dictionary<string, JObject> IndexByName(JArray? arr)
        {
            var d = new Dictionary<string, JObject>(StringComparer.Ordinal);
            if (arr == null) return d;
            foreach (var o in arr.OfType<JObject>())
                if ((string?)o["name"] is { Length: > 0 } n) d[n] = o;
            return d;
        }

        private static void ApplyTyped(object target, JObject json, (string Key, string Prop, bool Bool)[] typed)
        {
            foreach (var (key, prop, _) in typed)
                MetaclassJson.Assign(target, prop, json[key]);
            if (json["otherProperties"] is JObject op)
                foreach (var kv in op.Properties())
                    MetaclassJson.Assign(target, kv.Name, kv.Value);
        }

        internal static object BuildDataSource(JObject json, Dictionary<string, JObject> dsSource)
        {
            var kind = (string?)json["kind"] ?? "Root";
            var typeName = DsKindToType.TryGetValue(kind, out var tn) ? tn : "AxFormDataSourceRoot";
            var ds = Instantiate<object>(typeName, $"unknown DS kind '{kind}'");
            SetName(ds, (string?)json["name"] ?? string.Empty);
            ApplyTyped(ds, json, DsTyped);
            // LinkType (the join mode) is an enum on the metaclass; the domain
            // models it as a typed enum that serializes camelCase, so it's
            // emitted camelCase (below) rather than through the Raw DsTyped path.
            MetaclassJson.Assign(ds, "LinkType", json["linkType"]);

            // Fields (metadata) + merge per-field methods from source.
            var name = (string?)json["name"] ?? string.Empty;
            dsSource.TryGetValue(name, out var srcDs);
            var fieldSource = IndexByName(srcDs?["fields"] as JArray);
            var fieldsColl = ds.GetType().GetProperty("Fields")?.GetValue(ds);
            if (fieldsColl != null && json["fields"] is JArray fields)
            {
                MetaclassJson.AllowDuplicates(fieldsColl);
                var add = AddMethod(fieldsColl, "AxFormDataSourceField");
                foreach (var fj in fields.OfType<JObject>())
                    add?.Invoke(fieldsColl, new[] { BuildField(fj, fieldSource) });
            }

            // Per-DS methods.
            if (srcDs?["methods"] is JArray dsMethods)
            {
                var mc = ds.GetType().GetProperty("Methods")?.GetValue(ds);
                if (mc != null) AddMethods(mc, dsMethods);
            }

            // Nested.
            BuildNestedDs(ds, "DataSources", json["dataSources"], dsSource);
            BuildNestedDs(ds, "ReferencedDataSources", json["referencedDataSources"], dsSource);
            BuildNestedDs(ds, "DerivedDataSources", json["derivedDataSources"], dsSource);

            // DataSourceLinks (advanced). A form link is a relation NAME + a
            // behavior — there is no field/relatedField pair in F&O. Standard
            // master/detail joins carry no links at all (just JoinSource + the
            // table relation).
            var linksColl = ds.GetType().GetProperty("DataSourceLinks")?.GetValue(ds);
            if (linksColl != null && json["links"] is JArray links)
            {
                MetaclassJson.AllowDuplicates(linksColl);
                var add = AddMethod(linksColl, "AxFormDataSourceRootLink");
                foreach (var lj in links.OfType<JObject>())
                {
                    var lk = Instantiate<object>("AxFormDataSourceRootLink", "link type not found");
                    MetaclassJson.Assign(lk, "Name", lj["name"]);
                    MetaclassJson.Assign(lk, "LinkType", lj["behavior"]);
                    MetaclassJson.Assign(lk, "Tags", lj["tags"]);
                    add?.Invoke(linksColl, new[] { lk });
                }
            }
            return ds;
        }

        private static void BuildNestedDs(object parent, string prop, JToken? arr, Dictionary<string, JObject> dsSource)
        {
            if (arr is not JArray ja) return;
            var coll = parent.GetType().GetProperty(prop)?.GetValue(parent);
            if (coll == null) return;
            MetaclassJson.AllowDuplicates(coll);
            var add = coll.GetType().GetMethod("Add", new[] { typeof(AxFormDataSource) });
            foreach (var dj in ja.OfType<JObject>())
                add?.Invoke(coll, new[] { BuildDataSource(dj, dsSource) });
        }

        private static object BuildField(JObject json, Dictionary<string, JObject> fieldSource)
        {
            var f = Instantiate<object>("AxFormDataSourceField", "field type not found");
            MetaclassJson.Assign(f, "DataField", json["dataField"]);
            ApplyTyped(f, json, FieldTyped);
            var df = (string?)json["dataField"] ?? string.Empty;
            // Per-field method bodies are keyed by DataField in the source split.
            if (fieldSource.TryGetValue(df, out var sf) && sf["methods"] is JArray fm)
            {
                var mc = f.GetType().GetProperty("Methods")?.GetValue(f);
                if (mc != null) AddMethods(mc, fm);
            }
            return f;
        }

        private static void ApplyDesign(object design, JObject json, Dictionary<string, JObject> ctlSource)
        {
            ApplyTyped(design, json, DesignTyped);
            var controlsColl = design.GetType().GetProperty("Controls")?.GetValue(design);
            if (controlsColl != null && json["controls"] is JArray controls)
            {
                MetaclassJson.AllowDuplicates(controlsColl);
                var add = controlsColl.GetType().GetMethod("Add", new[] { typeof(AxFormControl) });
                foreach (var cj in controls.OfType<JObject>())
                    add?.Invoke(controlsColl, new[] { BuildControl(cj, ctlSource) });
            }
        }

        internal static AxFormControl BuildControl(JObject json, Dictionary<string, JObject> ctlSource)
        {
            var kind = (string?)json["kind"] ?? "Other";
            string typeName = kind.Equals("Other", StringComparison.OrdinalIgnoreCase)
                ? (string?)json["rawType"] ?? "AxFormControl"
                : (KindToType.TryGetValue(kind, out var tn) ? tn : "AxFormControl");
            var c = Instantiate<AxFormControl>(typeName, $"unknown control type '{typeName}'");
            c.Name = (string?)json["name"] ?? string.Empty;
            ApplyTyped(c, json, ControlTyped);

            // FormControlExtension.
            if (json["formControlExtension"] is JObject ext)
                SetProp(c, "FormControlExtension", BuildExtension(ext));

            // Child controls.
            var childColl = c.GetType().GetProperty("Controls")?.GetValue(c);
            if (childColl != null && json["controls"] is JArray children)
            {
                MetaclassJson.AllowDuplicates(childColl);
                var add = childColl.GetType().GetMethod("Add", new[] { typeof(AxFormControl) });
                foreach (var cj in children.OfType<JObject>())
                    add?.Invoke(childColl, new[] { BuildControl(cj, ctlSource) });
            }

            // Per-control method bodies (matched by control name).
            if (ctlSource.TryGetValue(c.Name, out var sc) && sc["methods"] is JArray cm)
            {
                var mc = c.GetType().GetProperty("Methods")?.GetValue(c);
                if (mc != null) AddMethods(mc, cm);
            }
            return c;
        }

        private static object BuildExtension(JObject json)
        {
            var ext = Instantiate<object>("AxFormControlExtension", "extension type not found");
            MetaclassJson.Assign(ext, "Name", json["name"]);
            MetaclassJson.Assign(ext, "Tags", json["tags"]);
            BuildExtensionPropsInto(ext, json["extensionProperties"] as JArray);
            BuildExtensionComponentsInto(ext, json["extensionComponents"] as JArray);
            return ext;
        }

        // Fill an owner's ExtensionProperties collection (the FormControlExtension
        // itself OR a leaf component — both expose the same collection shape).
        private static void BuildExtensionPropsInto(object owner, JArray? eps)
        {
            if (eps == null) return;
            var propsColl = owner.GetType().GetProperty("ExtensionProperties")?.GetValue(owner);
            if (propsColl == null) return;
            MetaclassJson.AllowDuplicates(propsColl);
            var add = AddMethod(propsColl, "AxFormControlExtensionProperty");
            foreach (var ep in eps.OfType<JObject>())
            {
                var p = Instantiate<object>("AxFormControlExtensionProperty", "ext prop not found");
                MetaclassJson.Assign(p, "Name", ep["name"]);
                MetaclassJson.Assign(p, "Type", ep["type"]);
                MetaclassJson.Assign(p, "Value", ep["value"]);
                if (ep["otherProperties"] is JObject op)
                    foreach (var kv in op.Properties()) MetaclassJson.Assign(p, kv.Name, kv.Value);
                add?.Invoke(propsColl, new[] { p });
            }
        }

        // Fill an owner's ExtensionComponents collection (the FormControlExtension
        // itself OR a composite component — recursive). AxFormControlExtension
        // Component is polymorphic: base {Name,Tags}, Composite {+nested
        // ExtensionComponents}, Leaf {+ComponentType,IsSystem,ExtensionProperties}.
        private static void BuildExtensionComponentsInto(object owner, JArray? ecs)
        {
            if (ecs == null) return;
            var compColl = owner.GetType().GetProperty("ExtensionComponents")?.GetValue(owner);
            if (compColl == null) return;
            MetaclassJson.AllowDuplicates(compColl);
            var add = AddMethod(compColl, "AxFormControlExtensionComponent");
            foreach (var ec in ecs.OfType<JObject>())
                add?.Invoke(compColl, new[] { BuildExtensionComponent(ec) });
        }

        private static object BuildExtensionComponent(JObject ec)
        {
            var kind = (string?)ec["kind"];
            var typeName = kind switch
            {
                "Composite" => "AxFormControlExtensionComponentComposite",
                "Leaf" => "AxFormControlExtensionComponentLeaf",
                _ => "AxFormControlExtensionComponent",
            };
            var comp = Instantiate<object>(typeName, $"ext comp type '{typeName}' not found");
            MetaclassJson.Assign(comp, "Name", ec["name"]);
            MetaclassJson.Assign(comp, "Tags", ec["tags"]);
            // Leaf fields — Assign no-ops the ones the subtype lacks.
            MetaclassJson.Assign(comp, "ComponentType", ec["componentType"]);
            MetaclassJson.Assign(comp, "IsSystem", ec["isSystem"]);
            BuildExtensionPropsInto(comp, ec["extensionProperties"] as JArray);
            // Composite recursion.
            BuildExtensionComponentsInto(comp, ec["components"] as JArray);
            return comp;
        }

        internal static object BuildPart(JObject json)
        {
            var p = Instantiate<object>("AxFormPartReference", "part type not found");
            MetaclassJson.Assign(p, "Name", json["name"]);
            MetaclassJson.Assign(p, "MenuItemName", json["partName"]);
            MetaclassJson.Assign(p, "DataSource", json["dataSource"]);
            MetaclassJson.Assign(p, "DataSourceRelation", json["dataSourceRelation"]);
            MetaclassJson.Assign(p, "PartLocation", json["partLocation"]);
            MetaclassJson.Assign(p, "Visible", json["visible"]);
            MetaclassJson.Assign(p, "Tags", json["tags"]);
            if (json["otherProperties"] is JObject op)
                foreach (var kv in op.Properties()) MetaclassJson.Assign(p, kv.Name, kv.Value);
            return p;
        }

        // ===================================================================
        // READ
        // ===================================================================
        private static JObject ToDomainJson(AxForm ax)
        {
            var jo = new JObject { ["name"] = ax.Name };
            var formRef = Reference(typeof(AxForm));
            MetaclassJson.EmitDefaulted(jo, ax, formRef, "FormTemplate", "formTemplate", MetaclassJson.EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, formRef, "IsObsolete", "isObsolete", MetaclassJson.EmitAs.Bool);
            MetaclassJson.EmitDefaulted(jo, ax, formRef, "Tags", "tags", MetaclassJson.EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, formRef, "DataSourceQuery", "dataSourceQuery", MetaclassJson.EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, formRef, "DataSourceChangeGroupMode", "dataSourceChangeGroupMode", MetaclassJson.EmitAs.Raw);
            MetaclassJson.EmitDefaulted(jo, ax, formRef, "AllowPreLoading", "allowPreLoading", MetaclassJson.EmitAs.Bool);
            MetaclassJson.EmitDefaulted(jo, ax, formRef, "AutoCacheUpdate", "autoCacheUpdate", MetaclassJson.EmitAs.Bool);
            MetaclassJson.EmitDefaulted(jo, ax, formRef, "InteractionClass", "interactionClass", MetaclassJson.EmitAs.Raw);
            var vis = MetaclassJson.ReadEnumCamel(ax, "Visibility");
            if (vis != null && vis != "public") jo["advanced"] = new JObject { ["visibility"] = vis };

            // SourceCode: split the classDeclaration method out as declaration;
            // the rest are form methods.
            var sc = new JObject();
            string? declaration = null;
            var formMethods = new JArray();
            foreach (var m in (IEnumerable)ax.Methods)
            {
                var am = (AxMethod)m;
                if (string.Equals(am.Name, "classDeclaration", StringComparison.Ordinal))
                {
                    declaration = am.Source;
                    continue;
                }
                var mo = new JObject { ["name"] = am.Name };
                if (!string.IsNullOrEmpty(am.Source)) mo["source"] = am.Source;
                formMethods.Add(mo);
            }
            if (!string.IsNullOrEmpty(declaration)) sc["declaration"] = declaration;
            if (formMethods.Count > 0) sc["methods"] = formMethods;
            var dsSrc = new JArray();
            var ctlSrc = new JArray();

            // DataSources metadata + collect their source.
            var dataSources = new JArray();
            foreach (var ds in ax.DataSources)
            {
                dataSources.Add(EmitDataSource(ds, dsSrc));
            }
            if (dataSources.Count > 0) jo["dataSources"] = dataSources;
            if (dsSrc.Count > 0) sc["dataSources"] = dsSrc;

            // Design metadata + collect control source.
            jo["design"] = EmitDesign(ax.Design, ctlSrc);
            if (ctlSrc.Count > 0) sc["dataControls"] = ctlSrc;

            if (sc.Count > 0) jo["sourceCode"] = sc;

            // Parts.
            var parts = new JArray();
            foreach (var p in ax.Parts) parts.Add(EmitPart(p));
            if (parts.Count > 0) jo["parts"] = parts;

            return jo;
        }

        private static JArray EmitMethods(object methodColl)
        {
            var arr = new JArray();
            if (methodColl is IEnumerable en)
                foreach (var m in en)
                {
                    var am = (AxMethod)m;
                    var o = new JObject { ["name"] = am.Name };
                    if (!string.IsNullOrEmpty(am.Source)) o["source"] = am.Source;
                    arr.Add(o);
                }
            return arr;
        }

        internal static JObject EmitDataSource(object ds, JArray dsSrc)
        {
            var t = ds.GetType();
            var kind = TypeToDsKind(t.Name);
            var name = (string)t.GetProperty("Name")!.GetValue(ds)!;
            var o = new JObject { ["name"] = name, ["kind"] = kind };
            EmitTyped(o, ds, DsTyped);
            // Join mode (enum) emitted camelCase to match the typed domain enum.
            // EmitDefaulted suppresses the default (Delayed) on a normal read but
            // emits it under the drift round-trip (IncludeDefaults), so a caller
            // who set it explicitly to the default doesn't see false drift.
            MetaclassJson.EmitDefaulted(o, ds, Reference(t), "LinkType", "linkType", MetaclassJson.EmitAs.EnumCamel);
            EmitOther(o, ds, DsTyped, DsStructural);

            // Fields metadata + per-field source.
            var fieldSrc = new JArray();
            var fields = new JArray();
            if (t.GetProperty("Fields")?.GetValue(ds) is IEnumerable fe)
                foreach (var f in fe) fields.Add(EmitField(f, fieldSrc));
            if (fields.Count > 0) o["fields"] = fields;

            // Per-DS methods.
            var dsMethods = EmitMethods(t.GetProperty("Methods")?.GetValue(ds)!);
            if (dsMethods.Count > 0 || fieldSrc.Count > 0)
            {
                var src = new JObject { ["name"] = name };
                if (dsMethods.Count > 0) src["methods"] = dsMethods;
                if (fieldSrc.Count > 0) src["fields"] = fieldSrc;
                dsSrc.Add(src);
            }

            EmitNestedDs(o, ds, "DataSources", "dataSources", dsSrc);
            EmitNestedDs(o, ds, "ReferencedDataSources", "referencedDataSources", dsSrc);
            EmitNestedDs(o, ds, "DerivedDataSources", "derivedDataSources", dsSrc);

            // Links.
            if (t.GetProperty("DataSourceLinks")?.GetValue(ds) is IEnumerable le)
            {
                var links = new JArray();
                foreach (var lk in le)
                {
                    var lo = new JObject { ["name"] = (string)lk.GetType().GetProperty("Name")!.GetValue(lk)! };
                    // The link's metaclass LinkType (DataSourceLinkBehavior) is
                    // surfaced as the domain "behavior"; suppress its None default.
                    var beh = MetaclassJson.ReadEnumCamel(lk, "LinkType");
                    if (beh != null && beh != "none") lo["behavior"] = beh;
                    var tags = lk.GetType().GetProperty("Tags")?.GetValue(lk) as string;
                    if (!string.IsNullOrEmpty(tags)) lo["tags"] = tags;
                    links.Add(lo);
                }
                if (links.Count > 0) o["links"] = links;
            }
            return o;
        }

        private static void EmitNestedDs(JObject o, object ds, string prop, string key, JArray dsSrc)
        {
            if (ds.GetType().GetProperty(prop)?.GetValue(ds) is not IEnumerable en) return;
            var arr = new JArray();
            foreach (var child in en) arr.Add(EmitDataSource(child, dsSrc));
            if (arr.Count > 0) o[key] = arr;
        }

        private static JObject EmitField(object f, JArray fieldSrc)
        {
            var df = (string)f.GetType().GetProperty("DataField")!.GetValue(f)!;
            var o = new JObject { ["dataField"] = df };
            EmitTyped(o, f, FieldTyped);
            EmitOther(o, f, FieldTyped, FieldStructural);
            var methods = EmitMethods(f.GetType().GetProperty("Methods")?.GetValue(f)!);
            if (methods.Count > 0) fieldSrc.Add(new JObject { ["name"] = df, ["methods"] = methods });
            return o;
        }

        private static JObject EmitDesign(object design, JArray ctlSrc)
        {
            var o = new JObject();
            EmitTyped(o, design, DesignTyped);
            EmitOther(o, design, DesignTyped, DesignStructural);
            var controls = new JArray();
            if (design.GetType().GetProperty("Controls")?.GetValue(design) is IEnumerable ce)
                foreach (var c in ce) controls.Add(EmitControl((AxFormControl)c, ctlSrc));
            if (controls.Count > 0) o["controls"] = controls;
            return o;
        }

        internal static JObject EmitControl(AxFormControl c, JArray ctlSrc)
        {
            var t = c.GetType();
            var o = new JObject { ["name"] = c.Name };
            if (TypeToKind.TryGetValue(t.Name, out var kind)) o["kind"] = kind;
            else { o["kind"] = "Other"; o["rawType"] = t.Name; }
            EmitTyped(o, c, ControlTyped);
            EmitOther(o, c, ControlTyped, ControlStructural);

            // FormControlExtension.
            var ext = t.GetProperty("FormControlExtension")?.GetValue(c);
            if (ext != null) o["formControlExtension"] = EmitExtension(ext);

            // Children.
            if (t.GetProperty("Controls")?.GetValue(c) is IEnumerable ce)
            {
                var children = new JArray();
                foreach (var child in ce) children.Add(EmitControl((AxFormControl)child, ctlSrc));
                if (children.Count > 0) o["controls"] = children;
            }

            // Per-control source methods.
            var methods = EmitMethods(t.GetProperty("Methods")?.GetValue(c)!);
            if (methods.Count > 0)
            {
                var src = new JObject { ["name"] = c.Name };
                var typeVal = t.GetProperty("Type")?.GetValue(c)?.ToString();
                if (!string.IsNullOrEmpty(typeVal)) src["type"] = typeVal;
                src["methods"] = methods;
                ctlSrc.Add(src);
            }
            return o;
        }

        private static JObject EmitExtension(object ext)
        {
            var o = new JObject { ["name"] = (string)ext.GetType().GetProperty("Name")!.GetValue(ext)! };
            var tags = ext.GetType().GetProperty("Tags")?.GetValue(ext) as string;
            if (!string.IsNullOrEmpty(tags)) o["tags"] = tags;
            var props = EmitExtensionPropsFrom(ext);
            if (props.Count > 0) o["extensionProperties"] = props;
            var comps = EmitExtensionComponentsFrom(ext);
            if (comps.Count > 0) o["extensionComponents"] = comps;
            return o;
        }

        private static JArray EmitExtensionPropsFrom(object owner)
        {
            var props = new JArray();
            if (owner.GetType().GetProperty("ExtensionProperties")?.GetValue(owner) is not IEnumerable pe) return props;
            foreach (var p in pe)
            {
                var pt = p.GetType();
                var po = new JObject { ["name"] = (string)pt.GetProperty("Name")!.GetValue(p)! };
                var typ = pt.GetProperty("Type")?.GetValue(p)?.ToString();
                if (!string.IsNullOrEmpty(typ) && typ != "None") po["type"] = typ;
                var val = pt.GetProperty("Value")?.GetValue(p) as string;
                if (!string.IsNullOrEmpty(val)) po["value"] = val;
                var pref = Reference(pt);
                var op = new JObject();
                MetaclassJson.EmitDefaulted(op, p, pref, "TypeName", "TypeName", MetaclassJson.EmitAs.Raw);
                if (op.Count > 0) po["otherProperties"] = op;
                props.Add(po);
            }
            return props;
        }

        private static JArray EmitExtensionComponentsFrom(object owner)
        {
            var comps = new JArray();
            if (owner.GetType().GetProperty("ExtensionComponents")?.GetValue(owner) is not IEnumerable ke) return comps;
            foreach (var k in ke) comps.Add(EmitExtensionComponent(k));
            return comps;
        }

        private static JObject EmitExtensionComponent(object comp)
        {
            var t = comp.GetType();
            var o = new JObject { ["name"] = (string)t.GetProperty("Name")!.GetValue(comp)! };
            var kind = t.Name switch
            {
                "AxFormControlExtensionComponentComposite" => "Composite",
                "AxFormControlExtensionComponentLeaf" => "Leaf",
                _ => null,
            };
            if (kind != null) o["kind"] = kind;
            var tags = t.GetProperty("Tags")?.GetValue(comp) as string;
            if (!string.IsNullOrEmpty(tags)) o["tags"] = tags;
            // Leaf fields.
            var ct = t.GetProperty("ComponentType")?.GetValue(comp) as string;
            if (!string.IsNullOrEmpty(ct)) o["componentType"] = ct;
            if (t.GetProperty("IsSystem")?.GetValue(comp) is bool isSys && isSys) o["isSystem"] = true;
            var props = EmitExtensionPropsFrom(comp);
            if (props.Count > 0) o["extensionProperties"] = props;
            // Composite recursion.
            var nested = EmitExtensionComponentsFrom(comp);
            if (nested.Count > 0) o["components"] = nested;
            return o;
        }

        internal static JObject EmitPart(object p)
        {
            var t = p.GetType();
            var o = new JObject { ["name"] = (string)t.GetProperty("Name")!.GetValue(p)!, ["kind"] = "Reference" };
            EmitStr(o, p, "MenuItemName", "partName");
            EmitStr(o, p, "DataSource", "dataSource");
            EmitStr(o, p, "DataSourceRelation", "dataSourceRelation");
            var pref = Reference(t);
            MetaclassJson.EmitDefaulted(o, p, pref, "PartLocation", "partLocation", MetaclassJson.EmitAs.EnumCamel);
            MetaclassJson.EmitDefaulted(o, p, pref, "Visible", "visible", MetaclassJson.EmitAs.Bool);
            EmitStr(o, p, "Tags", "tags");
            var op = new JObject();
            foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanWrite || pi.PropertyType == typeof(string) && pi.Name is "Name" or "MenuItemName" or "DataSource" or "DataSourceRelation" or "Tags") continue;
                if (pi.Name is "PartLocation" or "Visible" or "Conflicts" or "Attributes" or "CompilerMetadata") continue;
                MetaclassJson.EmitDefaulted(op, p, pref, pi.Name, pi.Name, MetaclassJson.EmitAs.Raw);
            }
            if (op.Count > 0) o["otherProperties"] = op;
            return o;
        }

        // ---- shared emit helpers ------------------------------------------
        private static void EmitTyped(JObject o, object source, (string Key, string Prop, bool Bool)[] typed)
        {
            var reference = Reference(source.GetType());
            foreach (var (key, prop, isBool) in typed)
                MetaclassJson.EmitDefaulted(o, source, reference, prop, key,
                    isBool ? MetaclassJson.EmitAs.Bool : MetaclassJson.EmitAs.Raw);
        }

        private static void EmitOther(JObject o, object source, (string Key, string Prop, bool Bool)[] typed, HashSet<string> structural)
        {
            var reference = Reference(source.GetType());
            var typedProps = new HashSet<string>(typed.Select(t => t.Prop), StringComparer.Ordinal);
            var op = new JObject();
            foreach (var pi in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanWrite) continue;
                if (typedProps.Contains(pi.Name) || structural.Contains(pi.Name)) continue;
                if (!IsScalar(pi.PropertyType)) continue;
                MetaclassJson.EmitDefaulted(op, source, reference, pi.Name, pi.Name, MetaclassJson.EmitAs.Raw);
            }
            if (op.Count > 0) o["otherProperties"] = op;
        }

        private static bool IsScalar(Type t)
            => t == typeof(string) || t == typeof(int) || t == typeof(bool) || t.IsEnum;

        // ---- reflection plumbing ------------------------------------------
        private static MethodInfo? AddMethod(object coll, string elementSimpleName)
        {
            var et = typeof(AxForm).Assembly.GetType(MetaNs + elementSimpleName);
            return et == null ? null : coll.GetType().GetMethod("Add", new[] { et });
        }

        private static void SetName(object o, string name) => o.GetType().GetProperty("Name")?.SetValue(o, name);
        private static void SetProp(object o, string prop, object val) => o.GetType().GetProperty(prop)?.SetValue(o, val);

        private static void EmitStr(JObject o, object source, string prop, string key)
        {
            var v = source.GetType().GetProperty(prop)?.GetValue(source) as string;
            if (!string.IsNullOrEmpty(v)) o[key] = v;
        }

        private static string TypeToDsKind(string typeName) => typeName switch
        {
            "AxFormDataSourceDerived" => "Derived",
            "AxFormDataSourceReferenced" => "Referenced",
            _ => "Root",
        };

        private static readonly Dictionary<Type, object> _refCache = new();
        private static object Reference(Type t)
        {
            if (!_refCache.TryGetValue(t, out var inst)) { inst = Activator.CreateInstance(t)!; _refCache[t] = inst; }
            return inst;
        }

        private static T Instantiate<T>(string simpleName, string errMsg) where T : class
        {
            var type = typeof(AxForm).Assembly.GetType(MetaNs + simpleName);
            if (type == null) throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, errMsg);
            return (T)Activator.CreateInstance(type)!;
        }

        private static string Pascal(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
