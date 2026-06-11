using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Dynamics.AX.Metadata.Providers;

namespace XppMetadataBridge.Metadata
{
    /// <summary>
    /// Result of <see cref="ObjectProjection.ReadObjectWithSource"/>: the
    /// loaded AOT object plus the provider tag identifying which storage
    /// surface produced it. Source matters for callers that need to know
    /// whether X++ source bodies will be empty (Runtime) or present
    /// (Custom / Standard).
    /// </summary>
    internal readonly struct ObjectReadResult
    {
        public object Value { get; }
        public ProviderSource Source { get; }
        public ObjectReadResult(object value, ProviderSource source)
        {
            Value = value;
            Source = source;
        }
    }

    /// <summary>
    /// Shared object-load + projection logic used by all "read an object"
    /// handlers. The expensive part - provider.Read() to materialize an
    /// AxFoo instance - happens here once; callers project methods,
    /// references, or both off the same instance.
    ///
    /// Keeping these as static helpers (rather than instance methods on a
    /// service) avoids any per-call allocation overhead and makes them
    /// trivially callable from the bridge's plain handler classes.
    /// </summary>
    internal static class ObjectProjection
    {
        /// <summary>
        /// Load an object from the metadata providers in read-priority
        /// order (Custom -> Standard -> Runtime). Returns the result with
        /// a source tag, or null when no provider resolves it. Runtime is
        /// the fallback for binary-only modules whose XML never lands on
        /// disk; reads succeed but X++ source bodies come back empty.
        /// </summary>
        public static ObjectReadResult? ReadObjectWithSource(MetadataProviderHost providers, string axType, string name)
        {
            foreach (var (provider, source) in providers.ReadOrder())
            {
                var found = ReadFrom(provider, axType, name);
                if (found != null) return new ObjectReadResult(found, source);
            }
            return null;
        }

        /// <summary>
        /// Convenience wrapper for callers that don't need the source tag.
        /// </summary>
        public static object? ReadObject(MetadataProviderHost providers, string axType, string name)
            => ReadObjectWithSource(providers, axType, name)?.Value;

        private static object? ReadFrom(IMetadataProvider provider, string axType, string name)
        {
            var prop = TypeMap.ResolveProperty(provider, axType);
            if (prop == null) return null;
            var reader = prop.GetValue(provider);
            if (reader == null) return null;
            var readMethod = reader.GetType().GetMethod(
                "Read",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            if (readMethod == null) return null;
            try { return readMethod.Invoke(reader, new object[] { name }); }
            catch { return null; }
        }

        // -------------------------------------------------------------------
        // Method projection — walks obj.Methods (when present) and returns
        // one anonymous record per method with the shape consumers expect.
        // -------------------------------------------------------------------

        public static List<object> ProjectMethods(object obj)
        {
            var methods = new List<object>();
            var methodsProp = obj.GetType().GetProperty("Methods");
            if (methodsProp?.GetValue(obj) is IEnumerable col)
            {
                foreach (var m in col)
                {
                    if (m != null) methods.Add(ProjectMethod(m));
                }
            }
            return methods;
        }

        private static object ProjectMethod(object method)
        {
            var t = method.GetType();
            var nameStr = (string?)t.GetProperty("Name")?.GetValue(method) ?? string.Empty;
            var source = (string?)t.GetProperty("Source")?.GetValue(method) ?? string.Empty;

            var (signature, accessLevel, returnType, isStatic) = ParseSignature(source);
            return new
            {
                name = nameStr,
                source,
                signature,
                isStatic,
                accessLevel,
                returnType
            };
        }

        /// <summary>
        /// Light-touch parse of the first declaration-looking line in the
        /// X++ source. Good enough to populate schema columns (signature,
        /// isStatic, accessLevel, returnType). A real X++ parser is the
        /// service's job if higher fidelity ever matters.
        /// </summary>
        private static (string signature, string? accessLevel, string? returnType, bool isStatic) ParseSignature(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return (string.Empty, null, null, false);

            string? sigLine = null;
            foreach (var raw in source.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("//")) continue;
                if (line.StartsWith("/*")) continue;
                if (line.StartsWith("[")) continue;
                if (line.Contains("(")) { sigLine = line; break; }
                sigLine = line;
            }
            if (sigLine == null) return (string.Empty, null, null, false);

            string? accessLevel = null;
            if (sigLine.Contains("public")) accessLevel = "public";
            else if (sigLine.Contains("protected")) accessLevel = "protected";
            else if (sigLine.Contains("private")) accessLevel = "private";

            var isStatic = sigLine.Contains("static");

            string? returnType = null;
            var parenIdx = sigLine.IndexOf('(');
            if (parenIdx > 0)
            {
                var head = sigLine.Substring(0, parenIdx).TrimEnd();
                var lastSpace = head.LastIndexOf(' ');
                if (lastSpace >= 0)
                {
                    var beforeName = head.Substring(0, lastSpace).TrimEnd();
                    var prevSpace = beforeName.LastIndexOf(' ');
                    returnType = (prevSpace >= 0 ? beforeName.Substring(prevSpace + 1) : beforeName).Trim();
                    if (returnType is "static" or "public" or "private" or "protected")
                        returnType = null;
                }
            }

            return (sigLine, accessLevel, returnType, isStatic);
        }

        // -------------------------------------------------------------------
        // Structural reference projection — per-type extraction of declared
        // graph edges (extends, datasource, relation, etc.). Anything not
        // covered is silently skipped rather than emitted as a malformed
        // edge.
        // -------------------------------------------------------------------

        public static List<object> ProjectReferences(object obj, string axType)
        {
            var edges = new List<object>();

            switch (axType)
            {
                case "AxClass":
                    AddSingle(edges, obj, "Extends", "extends", "AxClass");
                    AddCollectionOfNames(edges, obj, "Implements", "AxInterface", "implements");
                    AddClassExtensionOfTargets(edges, obj);
                    break;

                case "AxInterface":
                    AddCollectionOfNames(edges, obj, "Extends", "AxInterface", "interfaceExtends");
                    break;

                case "AxTable":
                    AddSingle(edges, obj, "Extends", "tableExtends", "AxTable");
                    AddTableRelations(edges, obj);
                    break;

                case "AxForm":
                    AddFormDataSources(edges, obj);
                    AddFormParts(edges, obj);
                    break;

                case "AxEdt":
                case "AxEdtString":
                case "AxEdtInt":
                case "AxEdtInt64":
                case "AxEdtReal":
                case "AxEdtDate":
                case "AxEdtEnum":
                case "AxEdtGuid":
                case "AxEdtUtcDateTime":
                case "AxEdtContainer":
                    AddSingle(edges, obj, "Extends", "edtExtends", "AxEdt");
                    AddSingle(edges, obj, "ReferenceTable", "edtReferenceTable", "AxTable");
                    AddSingle(edges, obj, "EnumType", "edtEnumType", "AxEnum");
                    break;

                case "AxView":
                    AddSingle(edges, obj, "Query", "viewQuery", "AxQuery");
                    break;

                case "AxMenuItemDisplay":
                case "AxMenuItemAction":
                case "AxMenuItemOutput":
                    AddMenuItemTarget(edges, obj, axType);
                    AddSingle(edges, obj, "Query", "menuItemQuery", "AxQuery");
                    AddSingle(edges, obj, "EnumTypeParameter", "menuItemEnumTypeParameter", "AxEnum");
                    AddSingle(edges, obj, "ReportDesign", "menuItemReportDesign", "AxReport");
                    break;

                case "AxMenu":
                    AddMenuElements(edges, obj);
                    break;

                case "AxTile":
                    AddTileTarget(edges, obj);
                    AddSingle(edges, obj, "Query", "tileQuery", "AxQuery");
                    break;

                case "AxQuery":
                case "AxQuerySimple":
                    AddQueryDataSources(edges, obj, "queryDataSource");
                    break;

                case "AxDataEntityView":
                    AddSingle(edges, obj, "Query", "entityQuery", "AxQuery");
                    AddEntityViewDataSources(edges, obj);
                    break;

                case "AxService":
                    AddSingle(edges, obj, "Class", "serviceClass", "AxClass");
                    break;

                case "AxServiceGroup":
                    AddCollectionViaProperty(edges, obj, "Services", "Service",
                        "serviceGroupMember", "AxService", contextProperty: "Name");
                    break;

                case "AxSecurityPrivilege":
                    AddPrivilegeEdges(edges, obj);
                    break;

                case "AxSecurityDuty":
                    AddCollectionViaProperty(edges, obj, "Privileges", "Name",
                        "dutyPrivilege", "AxSecurityPrivilege");
                    break;

                case "AxSecurityRole":
                    AddCollectionViaProperty(edges, obj, "Duties", "Name",
                        "roleDuty", "AxSecurityDuty");
                    AddCollectionViaProperty(edges, obj, "Privileges", "Name",
                        "rolePrivilege", "AxSecurityPrivilege");
                    AddCollectionViaProperty(edges, obj, "SubRoles", "Name",
                        "roleSubRole", "AxSecurityRole");
                    break;

                case "AxSecurityPolicy":
                    AddSingle(edges, obj, "PrimaryTable", "policyPrimaryTable", "AxTable");
                    AddSingle(edges, obj, "Query", "policyQuery", "AxQuery");
                    AddPolicyConstrainedTables(edges, obj);
                    break;

                case "AxTableExtension":
                    AddExtensionTarget(edges, obj, "AxTable");
                    AddTableRelations(edges, obj);
                    break;

                case "AxFormExtension":
                    AddExtensionTarget(edges, obj, "AxForm");
                    AddFormDataSources(edges, obj);
                    AddFormParts(edges, obj);
                    break;

                case "AxEdtExtension":
                    AddExtensionTarget(edges, obj, "AxEdt");
                    break;

                case "AxEnumExtension":
                    AddExtensionTarget(edges, obj, "AxEnum");
                    break;

                case "AxViewExtension":
                    AddExtensionTarget(edges, obj, "AxView");
                    break;

                case "AxDataEntityViewExtension":
                    AddExtensionTarget(edges, obj, "AxDataEntityView");
                    break;

                case "AxMenuExtension":
                    AddExtensionTarget(edges, obj, "AxMenu");
                    AddMenuElements(edges, obj);
                    break;

                case "AxResource":
                    // No outgoing structural edges — the manifest references a
                    // file path, not another AOT object.
                    break;
            }

            return edges;
        }

        private static void AddSingle(List<object> edges, object obj, string propertyName, string kind, string? targetType)
        {
            var prop = obj.GetType().GetProperty(propertyName);
            if (prop == null) return;
            var name = prop.GetValue(obj) as string;
            if (string.IsNullOrWhiteSpace(name)) return;
            edges.Add(new { targetName = name, targetType, kind });
        }

        private static void AddCollectionOfNames(List<object> edges, object obj, string propertyName, string? targetType, string kind)
        {
            var prop = obj.GetType().GetProperty(propertyName);
            if (prop?.GetValue(obj) is not IEnumerable col) return;
            foreach (var item in col)
            {
                if (item is string s && !string.IsNullOrWhiteSpace(s))
                {
                    edges.Add(new { targetName = s, targetType, kind });
                }
            }
        }

        private static void AddTableRelations(List<object> edges, object tableObj)
        {
            if (tableObj.GetType().GetProperty("Relations")?.GetValue(tableObj) is not IEnumerable rels) return;
            foreach (var rel in rels)
            {
                if (rel == null) continue;
                var relType = rel.GetType();
                var related = relType.GetProperty("RelatedTable")?.GetValue(rel) as string;
                if (string.IsNullOrWhiteSpace(related)) continue;
                var relName = relType.GetProperty("Name")?.GetValue(rel) as string;
                edges.Add(new
                {
                    targetName = related,
                    targetType = "AxTable",
                    kind = "tableRelation",
                    context = relName
                });
            }
        }

        // -------------------------------------------------------------------
        // Label projection — extracts (key, value, language, description)
        // tuples from an AxLabelFile object. The metadata API shape for
        // labels isn't documented externally, so we probe via reflection
        // and log the discovered structure once per process so an
        // unexpected shape on a new corpus surfaces in stderr instead of
        // silently returning empty.
        // -------------------------------------------------------------------

        private static int _labelShapeLogged = 0;

        public static List<object> ProjectLabels(MetadataProviderHost providers, object labelFileObj, string labelFileName, string modelName, IReadOnlyList<string> languages)
        {
            var entries = new List<object>();
            if (languages == null || languages.Count == 0)
            {
                languages = new[] { "en-US" };
            }

            LogShapeOnce("AxLabelFile", labelFileObj);

            // The AxLabelFile object already carries a Language property —
            // each row is per-language. Skip rows whose language we don't
            // want to index.
            var fileLang = labelFileObj.GetType().GetProperty("Language")?.GetValue(labelFileObj) as string
                ?? string.Empty;
            if (!LanguageMatches(fileLang, languages))
            {
                return entries;
            }

            // The label content lives in a separate file on disk; the
            // provider's LabelFiles property exposes GetContent(file, modelRef)
            // -> Stream. We need an IModelReference for the owning model.
            var modelRef = ResolveModelReference(providers, modelName);
            if (modelRef == null) return entries;

            // Try both providers (custom first matches the rest of the
            // codebase's preference order). The disk provider on Tier-1 VMs
            // is the same instance for both, so the second call is free.
            foreach (var provider in providers.CustomDistinctFromStandard
                ? new[] { providers.Custom, providers.Standard }
                : new[] { providers.Standard })
            {
                if (TryReadLabelStream(provider, labelFileObj, modelRef, fileLang, entries)) break;
            }
            return entries;
        }

        private static bool TryReadLabelStream(IMetadataProvider provider, object labelFileObj, object modelRef, string language, List<object> sink)
        {
            var labelFilesProp = provider.GetType().GetProperty("LabelFiles");
            if (labelFilesProp == null) return false;
            var labelFilesProvider = labelFilesProp.GetValue(provider);
            if (labelFilesProvider == null) return false;

            // GetContent(AxLabelFile, IModelReference) -> Stream
            // Match by parameter count + name rather than exact types, since
            // the formal type is an interface (IModelReference) but we have
            // a concrete ModelInfo.
            MethodInfo? getContent = null;
            foreach (var m in labelFilesProvider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "GetContent") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                if (!ps[0].ParameterType.IsAssignableFrom(labelFileObj.GetType())) continue;
                if (!ps[1].ParameterType.IsAssignableFrom(modelRef.GetType())) continue;
                getContent = m;
                break;
            }
            if (getContent == null) return false;

            Stream? stream = null;
            try { stream = getContent.Invoke(labelFilesProvider, new[] { labelFileObj, modelRef }) as Stream; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bridge] label GetContent threw: {ex.Message}");
                return false;
            }
            if (stream == null) return false;

            try
            {
                ParseLabelStream(stream, language, sink);
            }
            finally
            {
                stream.Dispose();
            }
            return true;
        }

        private static readonly Dictionary<string, object> _modelRefCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _modelRefGate = new object();

        private static object? ResolveModelReference(MetadataProviderHost providers, string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return null;
            lock (_modelRefGate)
            {
                if (_modelRefCache.TryGetValue(modelName, out var cached)) return cached;
            }

            // Look for IMetadataProvider.ModelManifest (or any property whose
            // name contains "ModelManifest"). It exposes Read(name) -> ModelInfo,
            // and ModelInfo implements IModelReference.
            object? FindManifest(IMetadataProvider provider)
            {
                foreach (var p in provider.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name.IndexOf("ModelManifest", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return p.GetValue(provider);
                    }
                }
                return null;
            }

            var manifest = FindManifest(providers.Standard);
            if (manifest == null && providers.CustomDistinctFromStandard)
            {
                manifest = FindManifest(providers.Custom);
            }
            if (manifest == null)
            {
                LogShapeOnce("provider (no ModelManifest)", providers.Standard);
                return null;
            }
            LogShapeOnce("provider.ModelManifest", manifest);

            // Prefer Read(string)
            var read = manifest.GetType().GetMethod("Read",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string) }, null);
            if (read == null) return null;

            object? modelInfo;
            try { modelInfo = read.Invoke(manifest, new object[] { modelName }); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bridge] ModelManifest.Read({modelName}) threw: {ex.Message}");
                return null;
            }
            if (modelInfo == null) return null;

            LogShapeOnce("ModelManifest.Read result", modelInfo);

            lock (_modelRefGate)
            {
                _modelRefCache[modelName] = modelInfo;
            }
            return modelInfo;
        }

        /// <summary>
        /// Parse a D365 label-file stream. The on-disk format is plain text
        /// keyed by '=': "LabelId=The label value ;optional description".
        /// Lines starting with ';' or '#' are comments. Empty lines are
        /// ignored. We don't try to be a full Java .properties parser —
        /// the D365 format is simple and stable.
        /// </summary>
        private static void ParseLabelStream(Stream stream, string language, List<object> sink)
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                if (key.Length == 0) continue;
                var rest = line.Substring(eq + 1);

                // Inline description marker after the value: ' ;Description'.
                // The leading space matters (semicolons inside the value
                // are allowed when not preceded by whitespace).
                string value;
                string? description = null;
                var descMark = rest.IndexOf(" ;", StringComparison.Ordinal);
                if (descMark >= 0)
                {
                    value = rest.Substring(0, descMark);
                    description = rest.Substring(descMark + 2).Trim();
                    if (description.Length == 0) description = null;
                }
                else
                {
                    value = rest;
                }
                sink.Add(new { key, value, language, description });
            }
        }

        private static bool LanguageMatches(string lang, IReadOnlyList<string> wanted)
        {
            foreach (var w in wanted)
            {
                if (string.Equals(lang, w, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void LogShapeOnce(string label, object obj)
        {
            // First-call introspection diagnostic. Logs at most a few times
            // total so we don't flood stderr on a 10k-label corpus.
            if (Interlocked.Increment(ref _labelShapeLogged) > 10) return;
            try
            {
                var t = obj.GetType();
                var sb = new System.Text.StringBuilder();
                sb.Append("[bridge] label-shape ").Append(label).Append(" type=").Append(t.FullName);
                if (obj is IEnumerable e && obj is not string)
                {
                    int n = 0; object? first = null;
                    foreach (var x in e) { if (n == 0) first = x; n++; if (n > 100) break; }
                    sb.Append(" count~=").Append(n);
                    if (first != null) sb.Append(" first=").Append(first.GetType().Name);
                }
                else
                {
                    sb.Append("\n  props=[");
                    var first = true;
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!first) sb.Append(',');
                        sb.Append(p.Name).Append(':').Append(p.PropertyType.Name);
                        first = false;
                    }
                    sb.Append("]\n  methods=[");
                    first = true;
                    // ALL methods, not just declared-only — we want inherited
                    // + interface impls (where the label-reading API actually
                    // lives on these provider types).
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                       .OrderBy(m => m.Name))
                    {
                        if (m.IsSpecialName) continue;
                        if (m.DeclaringType == typeof(object)) continue;
                        if (!first) sb.Append(',');
                        sb.Append(m.Name).Append('(');
                        var ps = m.GetParameters();
                        for (int i = 0; i < ps.Length; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append(ps[i].ParameterType.Name);
                        }
                        sb.Append(")->").Append(m.ReturnType.Name);
                        first = false;
                    }
                    sb.Append("]\n  interfaces=[");
                    first = true;
                    foreach (var i in t.GetInterfaces())
                    {
                        if (!first) sb.Append(',');
                        sb.Append(i.Name);
                        first = false;
                    }
                    sb.Append(']');
                }
                Console.Error.WriteLine(sb.ToString());
            }
            catch { /* diagnostic only */ }
        }

        private static void AddFormDataSources(List<object> edges, object formObj)
        {
            if (formObj.GetType().GetProperty("DataSources")?.GetValue(formObj) is not IEnumerable dsCollection) return;
            foreach (var ds in dsCollection)
            {
                if (ds == null) continue;
                var dsType = ds.GetType();
                var tableName = dsType.GetProperty("Table")?.GetValue(ds) as string;
                if (string.IsNullOrWhiteSpace(tableName)) continue;
                var dsName = dsType.GetProperty("Name")?.GetValue(ds) as string;
                edges.Add(new
                {
                    targetName = tableName,
                    targetType = "AxTable",
                    kind = "formDataSource",
                    context = dsName
                });
            }
        }

        // ===================================================================
        // Tier B: field-level edge extraction.
        //
        // Where ProjectReferences answers "object X references object Y",
        // ProjectFieldReferences answers "object X's member Y references
        // field F on table T". Stored in schema v2's `field_refs` table.
        //
        // Shape of each item:
        //   { sourceMember, targetTable, targetField, kind, context }
        //
        // Coverage in this pass:
        //   AxForm                 - DataSource field overrides + bound controls
        //   AxFormExtension        - new DataSources + ControlModifications + new bound controls
        //   AxQuery / AxQuerySimple - Ranges + OrderBy + GroupBy + Having
        //   AxDataEntityView       - Mapped fields (via the entity's own DataSources)
        //   AxTable / AxTableExtension - Relation constraints (Field + RelatedField)
        //
        // Deferred (need cross-object resolution): AxView Bound fields,
        // AxViewExtension fields, AxDataEntityViewExtension fields.
        // ===================================================================

        public static List<object> ProjectFieldReferences(object obj, string axType)
        {
            var edges = new List<object>();

            switch (axType)
            {
                case "AxForm":
                    AddFormFieldRefs(edges, obj);
                    break;

                case "AxFormExtension":
                    AddFormFieldRefs(edges, obj);
                    break;

                case "AxQuery":
                case "AxQuerySimple":
                    AddQueryFieldRefs(edges, obj);
                    break;

                case "AxDataEntityView":
                    AddEntityFieldRefs(edges, obj);
                    break;

                case "AxTable":
                case "AxTableExtension":
                    AddTableRelationFieldRefs(edges, obj);
                    break;
            }

            return edges;
        }

        // ---- AxForm field-level extraction ---------------------------------
        // Strategy: build a DataSource.Name -> Table map from the form's
        // own DataSources collection, then walk:
        //   (1) per-data-source Fields[] (per-field AllowEdit/Skip/etc. overrides)
        //   (2) the recursive Controls tree, emitting any bound control's
        //       (DataSource, DataField) pair.
        // For AxFormExtension the shape is the same — DataSources is the
        // collection of NEW data sources; ControlModifications carries
        // existing-control mods; Controls is new controls wrapped in
        // AxFormExtensionControl.
        private static void AddFormFieldRefs(List<object> edges, object formObj)
        {
            var formType = formObj.GetType();
            var dsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CollectFormDataSources(formObj, dsMap, edges, includeFieldOverrides: true);

            // For AxFormExtension, also consider DataSourceReferences (refs to
            // existing data sources on the base form). We can't resolve their
            // tables without reading the base — skip resolution but record the
            // ds name so the recursive Controls walk can fall through gracefully.

            // Walk new Controls tree (AxForm.Design.Controls / AxFormExtension's
            // wrapped Controls).
            var designProp = formType.GetProperty("Design");
            object? design = designProp?.GetValue(formObj);
            if (design != null)
            {
                if (design.GetType().GetProperty("Controls")?.GetValue(design) is IEnumerable rootCtrls)
                {
                    foreach (var c in rootCtrls)
                    {
                        if (c != null) WalkControlForFieldRefs(c, dsMap, edges, controlScope: "form");
                    }
                }
            }
            // AxFormExtension exposes Controls directly (each item is an
            // AxFormExtensionControl wrapper carrying a FormControl child).
            if (formType.GetProperty("Controls")?.GetValue(formObj) is IEnumerable extCtrls)
            {
                foreach (var c in extCtrls)
                {
                    if (c == null) continue;
                    // Drill through extension wrapper if present.
                    var actual = c.GetType().GetProperty("FormControl")?.GetValue(c) ?? c;
                    WalkControlForFieldRefs(actual, dsMap, edges, controlScope: "formExtension");
                }
            }
            // ControlModifications: per-control property changes. Each item
            // has a Name (existing control name); no DataField field-ref here
            // since modifications target properties not fields.
        }

        private static void CollectFormDataSources(object formObj, Dictionary<string, string> dsMap, List<object> edges, bool includeFieldOverrides)
        {
            var t = formObj.GetType();
            if (t.GetProperty("DataSources")?.GetValue(formObj) is not IEnumerable col) return;
            foreach (var ds in col)
            {
                if (ds == null) continue;
                var dst = ds.GetType();
                var dsName = dst.GetProperty("Name")?.GetValue(ds) as string;
                var table = dst.GetProperty("Table")?.GetValue(ds) as string;
                if (!string.IsNullOrWhiteSpace(dsName) && !string.IsNullOrWhiteSpace(table))
                {
                    dsMap[dsName] = table;
                }

                if (includeFieldOverrides && !string.IsNullOrWhiteSpace(table)
                    && dst.GetProperty("Fields")?.GetValue(ds) is IEnumerable fields)
                {
                    foreach (var f in fields)
                    {
                        if (f == null) continue;
                        var fname = f.GetType().GetProperty("DataField")?.GetValue(f) as string;
                        if (string.IsNullOrWhiteSpace(fname)) continue;
                        edges.Add(new
                        {
                            sourceMember = dsName,
                            targetTable = table,
                            targetField = fname,
                            kind = "formDataSourceField",
                            context = (string?)null,
                        });
                    }
                }
            }
        }

        private static void WalkControlForFieldRefs(object control, Dictionary<string, string> dsMap, List<object> edges, string controlScope)
        {
            if (control == null) return;
            var ct = control.GetType();
            var dsName = ct.GetProperty("DataSource")?.GetValue(control) as string;
            var dataField = ct.GetProperty("DataField")?.GetValue(control) as string;
            if (!string.IsNullOrWhiteSpace(dsName) && !string.IsNullOrWhiteSpace(dataField)
                && dsMap.TryGetValue(dsName, out var table))
            {
                var ctlName = ct.GetProperty("Name")?.GetValue(control) as string;
                edges.Add(new
                {
                    sourceMember = ctlName,
                    targetTable = table,
                    targetField = dataField,
                    kind = "formControlField",
                    context = controlScope,
                });
            }
            // Recurse into child controls (Group / Tab / TabPage / Grid / etc.).
            if (ct.GetProperty("Controls")?.GetValue(control) is IEnumerable children)
            {
                foreach (var child in children)
                {
                    if (child != null) WalkControlForFieldRefs(child, dsMap, edges, controlScope);
                }
            }
        }

        // ---- AxQuery field-level extraction --------------------------------
        // Build name -> table map across the recursive DataSources tree, then
        // emit field edges for Ranges / OrderBy / GroupBy / Having on each
        // data source.
        private static void AddQueryFieldRefs(List<object> edges, object queryObj)
        {
            var dsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CollectQueryDataSources(queryObj, dsMap);
            // Now walk again and emit field-level edges from each data source's
            // Ranges / OrderBy / etc. clauses.
            EmitQueryFieldClauses(queryObj, dsMap, edges);
        }

        private static void CollectQueryDataSources(object holder, Dictionary<string, string> dsMap)
        {
            var t = holder.GetType();
            if (t.GetProperty("DataSources")?.GetValue(holder) is not IEnumerable col) return;
            foreach (var ds in col)
            {
                if (ds == null) continue;
                var dst = ds.GetType();
                var name = dst.GetProperty("Name")?.GetValue(ds) as string;
                var table = dst.GetProperty("Table")?.GetValue(ds) as string;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(table))
                {
                    dsMap[name] = table;
                }
                CollectQueryDataSources(ds, dsMap);
                if (dst.GetProperty("DerivedDataSources")?.GetValue(ds) is IEnumerable dds)
                {
                    foreach (var d in dds)
                    {
                        if (d == null) continue;
                        var dN = d.GetType().GetProperty("Name")?.GetValue(d) as string;
                        var dT = d.GetType().GetProperty("Table")?.GetValue(d) as string;
                        if (!string.IsNullOrWhiteSpace(dN) && !string.IsNullOrWhiteSpace(dT))
                            dsMap[dN] = dT;
                        CollectQueryDataSources(d, dsMap);
                    }
                }
            }
        }

        private static void EmitQueryFieldClauses(object holder, Dictionary<string, string> dsMap, List<object> edges)
        {
            var t = holder.GetType();
            if (t.GetProperty("DataSources")?.GetValue(holder) is not IEnumerable col) return;
            foreach (var ds in col)
            {
                if (ds == null) continue;
                var dst = ds.GetType();
                var dsName = dst.GetProperty("Name")?.GetValue(ds) as string;
                var table = dst.GetProperty("Table")?.GetValue(ds) as string
                            ?? (dsName != null && dsMap.TryGetValue(dsName, out var t1) ? t1 : null);

                if (!string.IsNullOrWhiteSpace(table))
                {
                    EmitClauseFieldRefs(dst.GetProperty("Ranges")?.GetValue(ds) as IEnumerable, dsName!, table!, "queryRange", edges);
                    EmitClauseFieldRefs(dst.GetProperty("OrderBy")?.GetValue(ds) as IEnumerable, dsName!, table!, "queryOrderBy", edges);
                    EmitClauseFieldRefs(dst.GetProperty("GroupBy")?.GetValue(ds) as IEnumerable, dsName!, table!, "queryGroupBy", edges);
                    EmitClauseFieldRefs(dst.GetProperty("Having")?.GetValue(ds) as IEnumerable, dsName!, table!, "queryHaving", edges);
                    EmitClauseFieldRefs(dst.GetProperty("Fields")?.GetValue(ds) as IEnumerable, dsName!, table!, "queryField", edges);
                }
                // Recurse into nested data sources.
                EmitQueryFieldClauses(ds, dsMap, edges);
            }
        }

        private static void EmitClauseFieldRefs(IEnumerable? items, string dsName, string table, string kind, List<object> edges)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                if (item == null) continue;
                var it = item.GetType();
                // Each query clause carries a Field property (or Name when
                // Field is empty — but Field is canonical).
                var field = it.GetProperty("Field")?.GetValue(item) as string;
                if (string.IsNullOrWhiteSpace(field)) continue;
                var member = it.GetProperty("Name")?.GetValue(item) as string;
                edges.Add(new
                {
                    sourceMember = member,
                    targetTable = table,
                    targetField = field,
                    kind,
                    context = dsName,
                });
            }
        }

        // ---- AxDataEntityView field-level extraction -----------------------
        // The entity's typed Fields collection is polymorphic — Mapped fields
        // carry DataSource + DataField. Resolve via either the entity's own
        // DataSources (top-level on the entity) or ViewMetadata.DataSources.
        private static void AddEntityFieldRefs(List<object> edges, object entityObj)
        {
            var et = entityObj.GetType();
            var dsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Direct DataSources collection.
            CollectQueryDataSources(entityObj, dsMap);
            // ViewMetadata.DataSources fallback.
            var vm = et.GetProperty("ViewMetadata")?.GetValue(entityObj);
            if (vm != null) CollectQueryDataSources(vm, dsMap);

            if (et.GetProperty("Fields")?.GetValue(entityObj) is not IEnumerable fields) return;
            foreach (var f in fields)
            {
                if (f == null) continue;
                var ft = f.GetType();
                var dsName = ft.GetProperty("DataSource")?.GetValue(f) as string;
                var dataField = ft.GetProperty("DataField")?.GetValue(f) as string;
                if (string.IsNullOrWhiteSpace(dsName) || string.IsNullOrWhiteSpace(dataField)) continue;
                if (!dsMap.TryGetValue(dsName, out var table)) continue;
                var member = ft.GetProperty("Name")?.GetValue(f) as string;
                edges.Add(new
                {
                    sourceMember = member,
                    targetTable = table,
                    targetField = dataField,
                    kind = "entityMappedField",
                    context = dsName,
                });
            }
        }

        // ---- AxTable / AxTableExtension relation-constraint field refs -----
        // A relation's constraints carry (Field, RelatedField). Field belongs
        // to THIS table (the table or its base, for extensions); RelatedField
        // belongs to the relation's RelatedTable.
        private static void AddTableRelationFieldRefs(List<object> edges, object tableObj)
        {
            var tt = tableObj.GetType();
            // For AxTable, Name = the table itself. For AxTableExtension, Name
            // = "BaseName.Suffix" — split off the prefix.
            var thisTable = tt.GetProperty("Name")?.GetValue(tableObj) as string;
            if (!string.IsNullOrWhiteSpace(thisTable))
            {
                var dot = thisTable.IndexOf('.');
                if (dot > 0) thisTable = thisTable.Substring(0, dot);
            }

            if (tt.GetProperty("Relations")?.GetValue(tableObj) is not IEnumerable rels) return;
            foreach (var rel in rels)
            {
                if (rel == null) continue;
                var rt = rel.GetType();
                var relatedTable = rt.GetProperty("RelatedTable")?.GetValue(rel) as string;
                var relName = rt.GetProperty("Name")?.GetValue(rel) as string;
                if (rt.GetProperty("Constraints")?.GetValue(rel) is not IEnumerable cons) continue;
                foreach (var con in cons)
                {
                    if (con == null) continue;
                    var ct = con.GetType();
                    var field = ct.GetProperty("Field")?.GetValue(con) as string;
                    var relatedField = ct.GetProperty("RelatedField")?.GetValue(con) as string;
                    if (!string.IsNullOrWhiteSpace(field) && !string.IsNullOrWhiteSpace(thisTable))
                    {
                        edges.Add(new
                        {
                            sourceMember = relName,
                            targetTable = thisTable,
                            targetField = field,
                            kind = "tableRelationConstraint",
                            context = relatedTable,
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(relatedField) && !string.IsNullOrWhiteSpace(relatedTable))
                    {
                        edges.Add(new
                        {
                            sourceMember = relName,
                            targetTable = relatedTable,
                            targetField = relatedField,
                            kind = "tableRelationConstraintRelated",
                            context = thisTable,
                        });
                    }
                }
            }
        }

        // ===================================================================
        // Tier C: [ExtensionOf] structural edges + label reference edges.
        // ===================================================================

        // Regex for X++ `[ExtensionOf(classStr(SomeClass))]` (and formStr /
        // tableStr / etc.) attributes parsed off the class Declaration text.
        // The target kind tells us which AOT type the inner str() call
        // points at.
        private static readonly System.Text.RegularExpressions.Regex _extensionOfRegex
            = new(@"\[\s*ExtensionOf\s*\(\s*(class|form|table|enum|edt|view|query|map|menu|menuItem)Str\s*\(\s*(\w+)\s*\)\s*\)\s*\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        private static void AddClassExtensionOfTargets(List<object> edges, object classObj)
        {
            var declaration = ExtractClassDeclaration(classObj);
            if (string.IsNullOrEmpty(declaration)) return;
            foreach (System.Text.RegularExpressions.Match m in _extensionOfRegex.Matches(declaration))
            {
                var token = m.Groups[1].Value.ToLowerInvariant();
                var target = m.Groups[2].Value;
                if (string.IsNullOrEmpty(target)) continue;
                var (targetType, kind) = token switch
                {
                    "class"    => ("AxClass",          "classExtensionOf"),
                    "form"     => ("AxForm",           "formExtensionOf"),
                    "table"    => ("AxTable",          "tableExtensionOf"),
                    "enum"     => ("AxEnum",           "enumExtensionOf"),
                    "edt"      => ("AxEdt",            "edtExtensionOf"),
                    "view"     => ("AxView",           "viewExtensionOf"),
                    "query"    => ("AxQuery",          "queryExtensionOf"),
                    "map"      => ("AxMap",            "mapExtensionOf"),
                    "menu"     => ("AxMenu",           "menuExtensionOf"),
                    "menuItem" => ("AxMenuItemDisplay","menuItemExtensionOf"),
                    _          => (null,              "extensionOf"),
                };
                edges.Add(new
                {
                    targetName = target,
                    targetType,
                    kind,
                    context = (string?)null,
                });
            }
        }

        private static string? ExtractClassDeclaration(object classObj)
        {
            // The AxClass CLR shape carries the declaration either as a
            // direct Declaration string or nested under SourceCode.Declaration.
            // Probe both.
            var t = classObj.GetType();
            var direct = t.GetProperty("Declaration")?.GetValue(classObj) as string;
            if (!string.IsNullOrEmpty(direct)) return direct;
            var src = t.GetProperty("SourceCode")?.GetValue(classObj);
            if (src != null)
            {
                var nested = src.GetType().GetProperty("Declaration")?.GetValue(src) as string;
                if (!string.IsNullOrEmpty(nested)) return nested;
            }
            return null;
        }

        // ---- Label reference projection ------------------------------------
        // Walk well-known label-bearing properties on each object type and
        // emit a (file, key, source_member, kind) edge for each label-ref
        // string of the form @LabelFile:Key or @Key.

        public static List<object> ProjectLabelReferences(object obj, string axType)
        {
            var edges = new List<object>();

            switch (axType)
            {
                case "AxTable":
                case "AxView":
                case "AxDataEntityView":
                case "AxTableExtension":
                case "AxViewExtension":
                case "AxDataEntityViewExtension":
                    AddTableLikeLabelRefs(edges, obj);
                    break;

                case "AxEdt":
                case "AxEdtString":
                case "AxEdtInt":
                case "AxEdtInt64":
                case "AxEdtReal":
                case "AxEdtDate":
                case "AxEdtEnum":
                case "AxEdtGuid":
                case "AxEdtUtcDateTime":
                case "AxEdtContainer":
                case "AxEdtExtension":
                    EmitLabelRef(edges, obj, "Label", "label");
                    EmitLabelRef(edges, obj, "HelpText", "helpText");
                    break;

                case "AxEnum":
                case "AxEnumExtension":
                    EmitLabelRef(edges, obj, "Label", "label");
                    EmitLabelRef(edges, obj, "HelpText", "helpText");
                    AddCollectionLabelRefs(edges, obj, "EnumValues", "Name",
                        new[] { ("Label", "enumValueLabel") });
                    break;

                case "AxMenu":
                case "AxMenuExtension":
                    EmitLabelRef(edges, obj, "Label", "label");
                    EmitLabelRef(edges, obj, "HelpText", "helpText");
                    break;

                case "AxMenuItemDisplay":
                case "AxMenuItemAction":
                case "AxMenuItemOutput":
                    EmitLabelRef(edges, obj, "Label", "label");
                    EmitLabelRef(edges, obj, "HelpText", "helpText");
                    break;

                case "AxTile":
                    EmitLabelRef(edges, obj, "Label", "label");
                    EmitLabelRef(edges, obj, "HelpText", "helpText");
                    break;

                case "AxForm":
                case "AxFormExtension":
                    AddFormLabelRefs(edges, obj);
                    break;

                case "AxQuery":
                case "AxQuerySimple":
                    EmitLabelRef(edges, obj, "Label", "label");
                    EmitLabelRef(edges, obj, "Title", "title");
                    EmitLabelRef(edges, obj, "Description", "description");
                    break;

                case "AxSecurityPrivilege":
                case "AxSecurityDuty":
                case "AxSecurityRole":
                case "AxSecurityPolicy":
                    EmitLabelRef(edges, obj, "Label", "label");
                    EmitLabelRef(edges, obj, "Description", "description");
                    break;

                case "AxService":
                    EmitLabelRef(edges, obj, "Description", "description");
                    break;

                case "AxServiceGroup":
                    EmitLabelRef(edges, obj, "Description", "description");
                    break;
            }

            return edges;
        }

        // Tables / views / entities share roughly the same label-bearing
        // properties at the top level, plus per-field labels in their
        // Fields collection.
        private static void AddTableLikeLabelRefs(List<object> edges, object obj)
        {
            EmitLabelRef(edges, obj, "Label", "label");
            EmitLabelRef(edges, obj, "SingularLabel", "singularLabel");
            EmitLabelRef(edges, obj, "TitleField1", "titleField1");
            EmitLabelRef(edges, obj, "TitleField2", "titleField2");
            EmitLabelRef(edges, obj, "DeveloperDocumentation", "developerDocumentation");
            AddCollectionLabelRefs(edges, obj, "Fields", "Name",
                new[]
                {
                    ("Label", "fieldLabel"),
                    ("HelpText", "fieldHelpText"),
                    ("GroupPrompt", "fieldGroupPrompt"),
                });
            AddCollectionLabelRefs(edges, obj, "FieldGroups", "Name",
                new[] { ("Label", "fieldGroupLabel") });
        }

        // Form label-bearing properties live across the design root and
        // every control in the recursive Controls tree.
        private static void AddFormLabelRefs(List<object> edges, object formObj)
        {
            EmitLabelRef(edges, formObj, "Label", "label");
            // Design block.
            var design = formObj.GetType().GetProperty("Design")?.GetValue(formObj);
            if (design != null)
            {
                EmitLabelRef(edges, design, "Caption", "designCaption", memberOverride: "Design");
                EmitLabelRef(edges, design, "HelpText", "designHelpText", memberOverride: "Design");
                if (design.GetType().GetProperty("Controls")?.GetValue(design) is System.Collections.IEnumerable ctrls)
                {
                    foreach (var c in ctrls)
                    {
                        if (c != null) WalkControlForLabelRefs(c, edges);
                    }
                }
            }
            // AxFormExtension exposes Controls directly via wrappers.
            if (formObj.GetType().GetProperty("Controls")?.GetValue(formObj) is System.Collections.IEnumerable extCtrls)
            {
                foreach (var c in extCtrls)
                {
                    if (c == null) continue;
                    var actual = c.GetType().GetProperty("FormControl")?.GetValue(c) ?? c;
                    WalkControlForLabelRefs(actual, edges);
                }
            }
            // DataSources fields (per-field label overrides).
            if (formObj.GetType().GetProperty("DataSources")?.GetValue(formObj) is System.Collections.IEnumerable ds)
            {
                foreach (var d in ds)
                {
                    if (d == null) continue;
                    var dsName = d.GetType().GetProperty("Name")?.GetValue(d) as string;
                    if (d.GetType().GetProperty("Fields")?.GetValue(d) is System.Collections.IEnumerable fs)
                    {
                        foreach (var f in fs)
                        {
                            if (f == null) continue;
                            var fieldName = f.GetType().GetProperty("DataField")?.GetValue(f) as string;
                            EmitLabelRef(edges, f, "Label", "formDataSourceFieldLabel",
                                memberOverride: $"{dsName}.{fieldName}");
                        }
                    }
                }
            }
        }

        private static void WalkControlForLabelRefs(object control, List<object> edges)
        {
            var ct = control.GetType();
            var name = ct.GetProperty("Name")?.GetValue(control) as string;
            EmitLabelRef(edges, control, "Label", "controlLabel", memberOverride: name);
            EmitLabelRef(edges, control, "Caption", "controlCaption", memberOverride: name);
            EmitLabelRef(edges, control, "HelpText", "controlHelpText", memberOverride: name);
            EmitLabelRef(edges, control, "Text", "controlText", memberOverride: name);
            // Recurse into child controls.
            if (ct.GetProperty("Controls")?.GetValue(control) is System.Collections.IEnumerable children)
            {
                foreach (var child in children)
                {
                    if (child != null) WalkControlForLabelRefs(child, edges);
                }
            }
        }

        private static void AddCollectionLabelRefs(
            List<object> edges,
            object obj,
            string collectionProperty,
            string memberProperty,
            (string Property, string Kind)[] labelProps)
        {
            if (obj.GetType().GetProperty(collectionProperty)?.GetValue(obj) is not System.Collections.IEnumerable col) return;
            foreach (var item in col)
            {
                if (item == null) continue;
                var memberName = item.GetType().GetProperty(memberProperty)?.GetValue(item) as string;
                foreach (var (prop, kind) in labelProps)
                {
                    EmitLabelRef(edges, item, prop, kind, memberOverride: memberName);
                }
            }
        }

        // Emit a label-ref edge if the named property on `holder` is a
        // string starting with '@'. The label-ref format is one of:
        //   @LabelFile:LabelKey  -> labelFile="LabelFile", labelKey="LabelKey"
        //   @LabelKey            -> labelFile="",          labelKey="LabelKey"
        // Anything else (e.g. plain English captions) is skipped.
        private static void EmitLabelRef(
            List<object> edges,
            object holder,
            string propertyName,
            string kind,
            string? memberOverride = null)
        {
            var raw = holder.GetType().GetProperty(propertyName)?.GetValue(holder) as string;
            if (string.IsNullOrEmpty(raw)) return;
            if (raw[0] != '@') return;
            // Trim '@' prefix; split on first ':' if present.
            var body = raw.Substring(1);
            string file = string.Empty;
            string key = body;
            var colon = body.IndexOf(':');
            if (colon > 0)
            {
                file = body.Substring(0, colon);
                key = body.Substring(colon + 1);
            }
            if (string.IsNullOrEmpty(key)) return;
            edges.Add(new
            {
                sourceMember = memberOverride,
                labelFile = file,
                labelKey = key,
                kind,
                context = propertyName,
            });
        }

        // ---- Tier A: object-level edge expansion ----------------------------

        /// <summary>
        /// Iterate a collection property where each item exposes a Name (or
        /// other-named) string property carrying the reference. Optional
        /// contextProperty pulls a second string off the item to populate
        /// the edge's context column.
        /// </summary>
        private static void AddCollectionViaProperty(
            List<object> edges,
            object obj,
            string collectionProperty,
            string childProperty,
            string kind,
            string? targetType,
            string? contextProperty = null)
        {
            var prop = obj.GetType().GetProperty(collectionProperty);
            if (prop?.GetValue(obj) is not IEnumerable col) return;
            foreach (var item in col)
            {
                if (item == null) continue;
                var t = item.GetType();
                var name = t.GetProperty(childProperty)?.GetValue(item) as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                string? context = null;
                if (contextProperty != null)
                    context = t.GetProperty(contextProperty)?.GetValue(item) as string;
                edges.Add(new { targetName = name, targetType, kind, context });
            }
        }

        // ---- AxFormPart references (Parts collection on AxForm) ------------
        private static void AddFormParts(List<object> edges, object formObj)
        {
            if (formObj.GetType().GetProperty("Parts")?.GetValue(formObj) is not IEnumerable parts) return;
            foreach (var p in parts)
            {
                if (p == null) continue;
                var t = p.GetType();
                // PartName usually points to another AxFormPart / AxForm
                var partName = t.GetProperty("PartName")?.GetValue(p) as string;
                if (string.IsNullOrWhiteSpace(partName)) continue;
                var name = t.GetProperty("Name")?.GetValue(p) as string;
                edges.Add(new
                {
                    targetName = partName,
                    targetType = (string?)null,
                    kind = "formPart",
                    context = name
                });
            }
        }

        // ---- AxMenuItem*: Object property carries the AOT target -----------
        private static void AddMenuItemTarget(List<object> edges, object obj, string axType)
        {
            var target = obj.GetType().GetProperty("Object")?.GetValue(obj) as string;
            if (string.IsNullOrWhiteSpace(target)) return;
            // ObjectType property is an enum on the CLR side; ToString() gives us
            // "MenuItemDisplay" / "Class" / "Form" / "Output" / etc. We map a
            // few common ones to AOT type names; unknown values stay null.
            var rawType = obj.GetType().GetProperty("ObjectType")?.GetValue(obj)?.ToString();
            var targetType = MapMenuItemObjectType(rawType);
            edges.Add(new
            {
                targetName = target,
                targetType,
                kind = "menuItemTarget",
                context = rawType
            });
        }

        private static string? MapMenuItemObjectType(string? rawType) => rawType switch
        {
            "Class" => "AxClass",
            "Form" => "AxForm",
            "Report" => "AxReport",
            "Job" => "AxJob",
            "Query" => "AxQuery",
            "Output" => "AxReport",
            "SSRSReport" => "AxReport",
            _ => null,
        };

        // ---- AxTile: MenuItemName + MenuItemType discriminator -------------
        private static void AddTileTarget(List<object> edges, object obj)
        {
            var target = obj.GetType().GetProperty("MenuItemName")?.GetValue(obj) as string;
            if (string.IsNullOrWhiteSpace(target)) return;
            var rawType = obj.GetType().GetProperty("MenuItemType")?.GetValue(obj)?.ToString();
            var targetType = rawType switch
            {
                "Display" => "AxMenuItemDisplay",
                "Action" => "AxMenuItemAction",
                "Output" => "AxMenuItemOutput",
                _ => "AxMenuItemDisplay",  // default per MS convention
            };
            edges.Add(new
            {
                targetName = target,
                targetType,
                kind = "tileMenuItem",
                context = rawType
            });
        }

        // ---- AxMenu / AxMenuExtension: walk polymorphic Elements recursively
        private static void AddMenuElements(List<object> edges, object obj)
        {
            // For AxMenuExtension the collection is exposed as Elements, with
            // each item being an AxMenuExtensionElement that wraps a
            // MenuElement child. For AxMenu the collection is Elements with
            // direct AxMenuElement* polymorphic subtypes. Handle both shapes.
            if (obj.GetType().GetProperty("Elements")?.GetValue(obj) is not IEnumerable elems) return;
            foreach (var e in elems)
            {
                if (e == null) continue;
                // Drill through extension wrapper to the actual element.
                var et = e.GetType();
                var elementForType = e;
                var wrapped = et.GetProperty("MenuElement")?.GetValue(e);
                if (wrapped != null) elementForType = wrapped;
                EmitMenuElement(edges, elementForType);
            }
        }

        private static void EmitMenuElement(List<object> edges, object elem)
        {
            var et = elem.GetType();
            var typeName = et.Name;  // "AxMenuElementMenuItem" / "...MenuReference" / "...SubMenu" / "...Tile" / "...Separator"
            switch (typeName)
            {
                case "AxMenuElementMenuItem":
                {
                    var mname = et.GetProperty("MenuItemName")?.GetValue(elem) as string;
                    if (string.IsNullOrWhiteSpace(mname)) return;
                    var rawType = et.GetProperty("MenuItemType")?.GetValue(elem)?.ToString();
                    var targetType = rawType switch
                    {
                        "Display" => "AxMenuItemDisplay",
                        "Action" => "AxMenuItemAction",
                        "Output" => "AxMenuItemOutput",
                        _ => "AxMenuItemDisplay",
                    };
                    var ctx = et.GetProperty("Name")?.GetValue(elem) as string;
                    edges.Add(new { targetName = mname, targetType, kind = "menuMenuItem", context = ctx });
                    break;
                }
                case "AxMenuElementMenuReference":
                {
                    var menu = et.GetProperty("MenuName")?.GetValue(elem) as string;
                    if (string.IsNullOrWhiteSpace(menu)) return;
                    var ctx = et.GetProperty("Name")?.GetValue(elem) as string;
                    edges.Add(new { targetName = menu, targetType = "AxMenu", kind = "menuMenuReference", context = ctx });
                    break;
                }
                case "AxMenuElementTile":
                {
                    var tile = et.GetProperty("Tile")?.GetValue(elem) as string;
                    if (string.IsNullOrWhiteSpace(tile)) return;
                    var ctx = et.GetProperty("Name")?.GetValue(elem) as string;
                    edges.Add(new { targetName = tile, targetType = "AxTile", kind = "menuTile", context = ctx });
                    break;
                }
                case "AxMenuElementSubMenu":
                {
                    // SubMenu can itself host a MenuItem reference plus nested Elements.
                    var mname = et.GetProperty("MenuItemName")?.GetValue(elem) as string;
                    if (!string.IsNullOrWhiteSpace(mname))
                    {
                        var rawType = et.GetProperty("MenuItemType")?.GetValue(elem)?.ToString();
                        var targetType = rawType switch
                        {
                            "Action" => "AxMenuItemAction",
                            "Output" => "AxMenuItemOutput",
                            _ => "AxMenuItemDisplay",
                        };
                        var ctx = et.GetProperty("Name")?.GetValue(elem) as string;
                        edges.Add(new { targetName = mname, targetType, kind = "menuMenuItem", context = ctx });
                    }
                    if (et.GetProperty("Elements")?.GetValue(elem) is IEnumerable nested)
                    {
                        foreach (var n in nested)
                        {
                            if (n != null) EmitMenuElement(edges, n);
                        }
                    }
                    break;
                }
                // AxMenuElementSeparator: no edge.
            }
        }

        // ---- AxQuery: recursive DataSources tree ---------------------------
        private static void AddQueryDataSources(List<object> edges, object obj, string kind)
        {
            if (obj.GetType().GetProperty("DataSources")?.GetValue(obj) is not IEnumerable roots) return;
            foreach (var ds in roots)
            {
                if (ds != null) EmitQueryDataSource(edges, ds, kind);
            }
        }

        private static void EmitQueryDataSource(List<object> edges, object ds, string kind)
        {
            var dst = ds.GetType();
            var table = dst.GetProperty("Table")?.GetValue(ds) as string;
            if (!string.IsNullOrWhiteSpace(table))
            {
                var ctx = dst.GetProperty("Name")?.GetValue(ds) as string;
                edges.Add(new { targetName = table, targetType = "AxTable", kind, context = ctx });
            }
            // Recursive: each AxQuerySimple*DataSource carries a nested
            // DataSources collection of embedded joins.
            if (dst.GetProperty("DataSources")?.GetValue(ds) is IEnumerable children)
            {
                foreach (var c in children)
                {
                    if (c != null) EmitQueryDataSource(edges, c, kind);
                }
            }
            // DerivedDataSources (rare; modeled differently per provider version).
            if (dst.GetProperty("DerivedDataSources")?.GetValue(ds) is IEnumerable derived)
            {
                foreach (var c in derived)
                {
                    if (c != null) EmitQueryDataSource(edges, c, kind);
                }
            }
        }

        // ---- AxDataEntityView: walk ViewMetadata.DataSources tree ----------
        private static void AddEntityViewDataSources(List<object> edges, object obj)
        {
            var vm = obj.GetType().GetProperty("ViewMetadata")?.GetValue(obj);
            if (vm == null) return;
            if (vm.GetType().GetProperty("DataSources")?.GetValue(vm) is not IEnumerable roots) return;
            foreach (var ds in roots)
            {
                if (ds != null) EmitQueryDataSource(edges, ds, "entityDataSource");
            }
        }

        // ---- AxSecurityPrivilege edges -------------------------------------
        private static void AddPrivilegeEdges(List<object> edges, object obj)
        {
            // EntryPoints: each AxSecurityEntryPointReference carries
            // ObjectName + ObjectType (MenuItemDisplay/Action/Output/Form/Tile).
            if (obj.GetType().GetProperty("EntryPoints")?.GetValue(obj) is IEnumerable eps)
            {
                foreach (var ep in eps)
                {
                    if (ep == null) continue;
                    var et = ep.GetType();
                    var objName = et.GetProperty("ObjectName")?.GetValue(ep) as string;
                    if (string.IsNullOrWhiteSpace(objName)) continue;
                    var rawType = et.GetProperty("ObjectType")?.GetValue(ep)?.ToString();
                    var targetType = rawType switch
                    {
                        "MenuItemDisplay" => "AxMenuItemDisplay",
                        "MenuItemAction" => "AxMenuItemAction",
                        "MenuItemOutput" => "AxMenuItemOutput",
                        "Form" => "AxForm",
                        "Tile" => "AxTile",
                        _ => null,
                    };
                    var ctx = et.GetProperty("Name")?.GetValue(ep) as string;
                    edges.Add(new { targetName = objName, targetType, kind = "privilegeEntryPoint", context = ctx });
                }
            }
            // DataEntityPermissions: backing tables / data entities.
            if (obj.GetType().GetProperty("DataEntityPermissions")?.GetValue(obj) is IEnumerable deps)
            {
                foreach (var dep in deps)
                {
                    if (dep == null) continue;
                    var name = dep.GetType().GetProperty("Name")?.GetValue(dep) as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    edges.Add(new { targetName = name, targetType = (string?)null, kind = "privilegeDataEntity", context = (string?)null });
                }
            }
            // DirectAccessPermissions: bypass-entry-point grants.
            if (obj.GetType().GetProperty("DirectAccessPermissions")?.GetValue(obj) is IEnumerable daps)
            {
                foreach (var dap in daps)
                {
                    if (dap == null) continue;
                    var name = dap.GetType().GetProperty("Name")?.GetValue(dap) as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    edges.Add(new { targetName = name, targetType = (string?)null, kind = "privilegeDirectAccess", context = (string?)null });
                }
            }
        }

        // ---- AxSecurityPolicy: recursive ConstrainedTables -----------------
        private static void AddPolicyConstrainedTables(List<object> edges, object obj)
        {
            if (obj.GetType().GetProperty("ConstrainedTables")?.GetValue(obj) is not IEnumerable roots) return;
            foreach (var ct in roots)
            {
                if (ct != null) EmitPolicyConstrainedEntity(edges, ct);
            }
        }

        private static void EmitPolicyConstrainedEntity(List<object> edges, object ent)
        {
            var et = ent.GetType();
            var typeName = et.Name; // AxSecurityPolicyConstrainedTable / ...Expression
            var name = et.GetProperty("Name")?.GetValue(ent) as string;
            if (!string.IsNullOrWhiteSpace(name))
            {
                // Only Table entries name an actual AxTable; Expression entries
                // are grouping nodes and the Name there isn't always a table.
                var targetType = typeName == "AxSecurityPolicyConstrainedTable" ? "AxTable" : null;
                var ctx = et.GetProperty("TableRelation")?.GetValue(ent) as string;
                edges.Add(new { targetName = name, targetType, kind = "policyConstrainedTable", context = ctx });
            }
            if (et.GetProperty("ConstrainedTables")?.GetValue(ent) is IEnumerable children)
            {
                foreach (var c in children)
                {
                    if (c != null) EmitPolicyConstrainedEntity(edges, c);
                }
            }
        }

        // ---- Extensions: derive base-object name from Name prefix ----------
        // Extensions are named '<BaseName>.<Suffix>'. We emit an edge to the
        // base object so find-references on (e.g.) CustTable surfaces all of
        // its extensions.
        private static void AddExtensionTarget(List<object> edges, object obj, string baseAxType)
        {
            var name = obj.GetType().GetProperty("Name")?.GetValue(obj) as string;
            if (string.IsNullOrWhiteSpace(name)) return;
            var dot = name.IndexOf('.');
            if (dot <= 0) return;
            var baseName = name.Substring(0, dot);
            var suffix = name.Substring(dot + 1);
            edges.Add(new
            {
                targetName = baseName,
                targetType = baseAxType,
                kind = "extensionTarget",
                context = suffix
            });
        }
    }
}
