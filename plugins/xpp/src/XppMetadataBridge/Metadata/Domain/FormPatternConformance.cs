using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json.Linq;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Patterns;

namespace XppMetadataBridge.Metadata.Domain
{
    /// <summary>
    /// Form-pattern conformance, driven by MS's own headless pattern engine
    /// (the same one behind the VS designer's "Pattern" tab). For a built
    /// AxForm metaclass object we:
    ///
    ///   1. select the form's DECLARED pattern (Design.Pattern + Version),
    ///   2. run <see cref="PatternAnalyzer.TestPattern"/> over the live
    ///      control tree (AxFormDesign / AxFormControl implement IPatternable),
    ///   3. auto-stamp every <c>Equals</c> property violation to the value the
    ///      pattern prescribes — replicating MS's private
    ///      <c>Extensions.FixPatternResultPropertyViolations</c> — so the form
    ///      is conformant-by-construction the way a VS-authored one is, and
    ///   4. return the residual the stamp can't fix: structural <c>missing</c>
    ///      slots (can't default a control into existence) and non-<c>Equals</c>
    ///      <c>mismatches</c> (negative constraints with no single value to set).
    ///
    /// Disposition of the auto-stamped violations follows their ORIGIN:
    ///   - default-origin (the author never set the property; it sat at the
    ///     metaclass default) -> stamped SILENTLY. Zero agent noise.
    ///   - author-intent (the property is present in the request JSON) ->
    ///     stamped AND surfaced in <c>overrides</c> so the agent learns their
    ///     explicit value lost to the pattern. This is the only set that could
    ///     otherwise look like a silent loss; the drift detector never flags it
    ///     because drift is presence-only and the property is still present
    ///     (just revalued).
    ///
    /// Mutation note: stamping mutates the live metaclass control tree IN
    /// PLACE. Callers run this BEFORE the provider write so the conformant
    /// values persist to disk. Best-effort: any failure returns null and the
    /// write proceeds unstamped — a conformance bug must never block a write.
    /// </summary>
    internal static class FormPatternConformance
    {
        private static readonly object Gate = new object();
        private static PatternFactory? _factory;

        // One factory per bridge process; ctor(true) self-loads the embedded
        // pattern + sub-pattern catalog (no external files).
        private static PatternFactory Factory
        {
            get { lock (Gate) { return _factory ??= new PatternFactory(true); } }
        }

        /// <summary>Analyze + auto-stamp. Returns the conformance JObject, or
        /// null when there's nothing to report (no declared pattern) or on any
        /// internal failure (write proceeds regardless).</summary>
        public static JObject? Analyze(object axForm, JObject requestJson)
        {
            try { return AnalyzeCore(axForm, requestJson); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bridge] pattern conformance failed (write proceeds): {ex.Message}");
                return null;
            }
        }

        private static JObject? AnalyzeCore(object axForm, JObject requestJson)
        {
            if (axForm.GetType().GetProperty("Design")?.GetValue(axForm) is not IPatternable design)
                return null;

            var patternName = design.Pattern;
            var patternVersion = design.PatternVersion;
            if (string.IsNullOrWhiteSpace(patternName))
                return null; // no pattern declared — nothing to conform to

            var pattern = Factory.GetPatternByName(patternName, patternVersion, false);
            if (pattern == null)
            {
                // Declared a pattern we have no definition for. Report honestly
                // rather than pretend it's clean or fail the write.
                return new JObject
                {
                    ["pattern"] = patternName,
                    ["version"] = patternVersion,
                    ["ok"] = true,
                    ["note"] = "pattern not in catalog; conformance not analyzed",
                };
            }

            var result = new PatternAnalyzer().TestPattern(design, pattern, null);
            if (result == null) return null;

            // What did the author actively set THIS request? (control name ->
            // camelCase property keys; plus design-level keys.) Used to split
            // stamped violations into silent (default) vs surfaced (override).
            var authorKeys = BuildAuthorKeyMap(requestJson, out var designKeys);

            var missing = new JArray();
            var overrides = new JArray();
            var mismatches = new JArray();
            var toStamp = new List<(PropertyViolation v, object control)>();

            void Visit(PatternResult r, string path)
            {
                object? control = r.Control;
                bool isRoot = ReferenceEquals(control, design);
                string? controlName = ControlName(control);

                if (r.Violations != null)
                {
                    foreach (var v in r.Violations)
                    {
                        if (v.Op == PropertyViolation.Operator.Equals)
                        {
                            // Auto-fixable. Stamp it; surface only if the author
                            // had explicitly set this property.
                            if (control != null) toStamp.Add((v, control));
                            if (IsAuthorSet(isRoot, controlName, v.PropertyName, authorKeys, designKeys))
                            {
                                overrides.Add(new JObject
                                {
                                    ["path"] = path,
                                    ["control"] = controlName,
                                    ["property"] = MetaclassJson.ToCamel(v.PropertyName),
                                    ["requested"] = v.ActualValue,
                                    ["patternValue"] = v.PatternValue,
                                });
                            }
                            // else: default-origin -> stamped silently.
                        }
                        else
                        {
                            // Negative / non-Equals constraint: no single value
                            // to stamp. Report as residual.
                            mismatches.Add(new JObject
                            {
                                ["path"] = path,
                                ["control"] = controlName,
                                ["property"] = MetaclassJson.ToCamel(v.PropertyName),
                                ["actual"] = v.ActualValue,
                                ["patternValue"] = v.PatternValue,
                                ["op"] = v.Op.ToString(),
                            });
                        }
                    }
                }

                var node = r.Node;
                if (node?.SubNodes == null) return;
                foreach (var sn in node.SubNodes)
                {
                    List<PatternResult>? list = null;
                    r.ChildResults?.TryGetValue(sn, out list);
                    bool has = list != null && list.Count > 0;
                    string friendly = sn.FriendlyName ?? "";
                    if (!has)
                    {
                        if (sn.RequireOne)
                        {
                            string type = sn.Type ?? "";
                            missing.Add(new JObject
                            {
                                ["path"] = path,
                                ["expected"] = string.IsNullOrEmpty(type) ? friendly : $"{friendly} ({type})",
                            });
                        }
                    }
                    else
                    {
                        string childPath = string.IsNullOrEmpty(path) ? friendly : $"{path} > {friendly}";
                        foreach (var child in list!) Visit(child, childPath);
                    }
                }
            }

            Visit(result, result.Node?.FriendlyName ?? "");

            // Validate every control's DECLARED sub-pattern name/version against
            // the catalog — a RAW control-tree sweep, independent of TestPattern
            // matching. A bogus sub-pattern (e.g. 'ToolbarAndList 1.0',
            // 'NavigationListSimpleListAndDetails 1.1', a wrong UX7 version) makes
            // the control fail to match its slot, so the structural walk above
            // only reports it as a confusing "missing" — not the real cause. The
            // compiler's pattern validator rejects unknown names outright; the
            // patch-time engine must too, or patternConformance.ok is a false
            // promise. The mismatch enumerates the sub-patterns legal for a
            // control of this KIND in this form pattern (scoped from the node
            // tree), so the caller gets "valid here: [...]" inline, not just a
            // pointer to go read the skill.
            SweepDeclaredPatterns(design, pattern, mismatches);

            // STAMP last — replicate FixPatternResultPropertyViolations. The
            // violation objects already carry the pre-stamp ActualValue we
            // reported above, so order doesn't matter for the report.
            foreach (var (v, control) in toStamp) StampOne(v, control);

            bool ok = missing.Count == 0 && mismatches.Count == 0;
            var jo = new JObject
            {
                ["pattern"] = patternName,
                ["version"] = patternVersion,
                ["ok"] = ok,
            };
            if (missing.Count > 0) jo["missing"] = missing;
            if (overrides.Count > 0) jo["overrides"] = overrides;
            if (mismatches.Count > 0) jo["mismatches"] = mismatches;
            return jo;
        }

        // ---- stamping (MS Extensions.FixPatternResultPropertyViolations) ----
        private static void StampOne(PropertyViolation v, object control)
        {
            try
            {
                var prop = control.GetType().GetProperty(v.PropertyName);
                if (prop == null || !prop.CanWrite) return;
                var conv = TypeDescriptor.GetConverter(prop.PropertyType);
                prop.SetValue(control, conv.ConvertFromInvariantString(v.PatternValue));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[bridge] pattern stamp skipped {v.PropertyName}='{v.PatternValue}': {ex.Message}");
            }
        }

        // ---- author-intent discrimination ----------------------------------
        private static Dictionary<string, HashSet<string>> BuildAuthorKeyMap(
            JObject requestJson, out HashSet<string> designKeys)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            designKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (requestJson["design"] is not JObject design) return map;
            foreach (var p in design.Properties())
                if (!string.Equals(p.Name, "controls", StringComparison.OrdinalIgnoreCase))
                    designKeys.Add(p.Name);

            void Walk(JArray? controls)
            {
                if (controls == null) return;
                foreach (var c in controls.OfType<JObject>())
                {
                    var name = (string?)c["name"];
                    if (!string.IsNullOrEmpty(name))
                    {
                        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in c.Properties()) keys.Add(p.Name);
                        map[name!] = keys;
                    }
                    Walk(c["controls"] as JArray);
                }
            }
            Walk(design["controls"] as JArray);
            return map;
        }

        private static bool IsAuthorSet(
            bool isRoot, string? controlName, string propertyName,
            Dictionary<string, HashSet<string>> map, HashSet<string> designKeys)
        {
            var camel = MetaclassJson.ToCamel(propertyName);
            if (isRoot) return designKeys.Contains(camel) || designKeys.Contains(propertyName);
            if (controlName == null) return false;
            return map.TryGetValue(controlName, out var keys)
                   && (keys.Contains(camel) || keys.Contains(propertyName));
        }

        private static string? ControlName(object? control)
            => control?.GetType().GetProperty("Name")?.GetValue(control) as string;

        // ---- declared sub-pattern validation (raw control-tree sweep) ----
        /// <summary>Walk the form's control tree and flag every control whose
        /// declared Pattern/PatternVersion doesn't resolve in the catalog. Runs
        /// independent of TestPattern matching (a bogus pattern un-matches the
        /// control, so it never reaches the structural walk as anything but a
        /// confusing "missing"). The design's own Pattern is the form pattern,
        /// validated separately by selection, so we skip the root and sweep its
        /// children.</summary>
        private static void SweepDeclaredPatterns(object design, Pattern formPattern, JArray mismatches)
        {
            void Walk(object ctrl, string path)
            {
                if (ctrl is IPatternable ip && !string.IsNullOrWhiteSpace(ip.Pattern)
                    && Factory.GetPatternByName(ip.Pattern, ip.PatternVersion, false) == null)
                {
                    var opts = LegalSubPatternsForKind(formPattern, ctrl);
                    mismatches.Add(new JObject
                    {
                        ["path"] = path,
                        ["control"] = ControlName(ctrl),
                        ["property"] = "pattern",
                        ["actual"] = $"{ip.Pattern} {ip.PatternVersion}".Trim(),
                        ["patternValue"] = opts.Count > 0
                            ? "valid here: " + string.Join(" | ", opts)
                            : "(not a known sub-pattern — load dynamics-xpp:xpp-form-subpatterns for the legal names + versions)",
                        ["op"] = "MustBeOneOf",
                    });
                }
                if (ctrl.GetType().GetProperty("Controls")?.GetValue(ctrl) is System.Collections.IEnumerable kids)
                    foreach (var k in kids)
                    {
                        var nm = ControlName(k) ?? "";
                        Walk(k, string.IsNullOrEmpty(path) ? nm : $"{path} > {nm}");
                    }
            }
            if (design.GetType().GetProperty("Controls")?.GetValue(design) is System.Collections.IEnumerable top)
                foreach (var c in top)
                {
                    var nm = ControlName(c) ?? "";
                    Walk(c, nm);
                }
        }

        /// <summary>The sub-patterns legal for a control of this KIND in the given
        /// form pattern, as "Name Version(s)" strings. Walks the pattern's node
        /// tree and unions the SubPatterns of every node whose Type matches the
        /// control's kind (e.g. a TabPage control -> the "TabPage" node's allowed
        /// set). Kind-scoped (not exact-slot) so it works even though a bogus
        /// pattern un-matches the control — no re-match, no mutation.</summary>
        private static IReadOnlyList<string> LegalSubPatternsForKind(Pattern formPattern, object control)
        {
            // AxFormTabPageControl -> "TabPage"; AxFormGroupControl -> "Group"; etc.
            var kind = control.GetType().Name;
            if (kind.StartsWith("AxForm", StringComparison.Ordinal)) kind = kind.Substring("AxForm".Length);
            if (kind.EndsWith("Control", StringComparison.Ordinal)) kind = kind.Substring(0, kind.Length - "Control".Length);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSubPatternNames(formPattern.Root, kind, names);
            var result = new List<string>();
            foreach (var n in names.OrderBy(x => x, StringComparer.Ordinal))
            {
                var vers = ActiveVersions(n);
                result.Add(vers.Count > 0 ? $"{n} {string.Join("/", vers)}" : n);
            }
            return result;
        }

        private static void CollectSubPatternNames(object? node, string kind, HashSet<string> acc)
        {
            if (node == null) return;
            if (string.Equals(node.GetType().GetProperty("Type")?.GetValue(node) as string, kind, StringComparison.OrdinalIgnoreCase)
                && node.GetType().GetProperty("SubPatterns")?.GetValue(node) is System.Collections.IEnumerable subs)
            {
                // node.SubPatterns is IEnumerable<string> — each item IS the
                // sub-pattern name.
                foreach (var sp in subs)
                    if (sp is string nm && nm.Length > 0) acc.Add(nm);
            }
            if (node.GetType().GetProperty("SubNodes")?.GetValue(node) is System.Collections.IEnumerable kids)
                foreach (var k in kids) CollectSubPatternNames(k, kind, acc);
        }

        private static readonly object VerGate = new object();
        private static Dictionary<string, List<string>>? _activeVersions;
        /// <summary>Active catalog versions for a pattern name (memoized once).</summary>
        private static List<string> ActiveVersions(string name)
        {
            lock (VerGate)
            {
                if (_activeVersions == null)
                {
                    _activeVersions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in Factory.AllPatterns)
                    {
                        if (!p.Active) continue;
                        if (!_activeVersions.TryGetValue(p.Name, out var l)) { l = new List<string>(); _activeVersions[p.Name] = l; }
                        if (!l.Contains(p.Version)) l.Add(p.Version);
                    }
                }
                return _activeVersions.TryGetValue(name, out var vs) ? vs : new List<string>();
            }
        }
    }
}
