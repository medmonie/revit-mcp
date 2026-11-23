namespace RevitMcpBridge;

/// <summary>
/// Documentation: view filters and graphic overrides, colour by parameter, view templates, dimensions to element faces,
/// spot elevations, revisions and revision clouds, several views and schedules arranged on a sheet.
/// </summary>
internal static class DocOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["filter_create"] = FilterCreate;
        ops["filter_apply"] = FilterApply;
        ops["color_by_parameter"] = ColorByParameter;
        ops["graphics_override"] = GraphicsOverride;
        ops["view_template_create"] = ViewTemplateCreate;
        ops["view_template_apply"] = ViewTemplateApply;
        ops["dimension_elements"] = DimensionElements;
        ops["spot_elevation"] = SpotElevation;
        ops["revision_create"] = RevisionCreate;
        ops["revision_cloud_create"] = RevisionCloudCreate;
        ops["sheet_layout"] = SheetLayout;
    }

    // ---------------------------------------------------------------- colours and overrides

    private static readonly Dictionary<string, (byte, byte, byte)> Named = new Dictionary<string, (byte, byte, byte)>(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = (220, 50, 47), ["green"] = (60, 160, 70), ["blue"] = (38, 110, 210), ["yellow"] = (240, 200, 30),
        ["orange"] = (240, 130, 30), ["purple"] = (140, 80, 180), ["cyan"] = (40, 180, 200), ["magenta"] = (210, 60, 160),
        ["grey"] = (128, 128, 128), ["gray"] = (128, 128, 128), ["black"] = (0, 0, 0), ["white"] = (255, 255, 255),
        ["brown"] = (140, 90, 50), ["pink"] = (240, 150, 180),
    };

    /// <summary>"#RRGGBB", [r, g, b] or a colour name.</summary>
    public static Color ColorOf(object v, string what)
    {
        if (v is string s)
        {
            s = s.Trim();
            if (Named.TryGetValue(s, out var n)) return new Color(n.Item1, n.Item2, n.Item3);
            if (s.StartsWith("#") && s.Length == 7)
                return new Color(Convert.ToByte(s.Substring(1, 2), 16), Convert.ToByte(s.Substring(3, 2), 16), Convert.ToByte(s.Substring(5, 2), 16));
        }
        if (v is List<object> l && l.Count == 3)
        {
            byte B(object x) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Number(x, what))));
            return new Color(B(l[0]), B(l[1]), B(l[2]));
        }
        throw new ArgumentException($"{what}: give a colour as \"#RRGGBB\", [r, g, b] or a name ({string.Join(", ", Named.Keys.Take(12))}).");
    }

    private static readonly (byte, byte, byte)[] Palette =
    {
        (230, 25, 75), (60, 180, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180), (70, 240, 240), (240, 50, 230),
        (210, 245, 60), (250, 190, 190), (0, 128, 128), (230, 190, 255), (170, 110, 40), (255, 225, 25), (128, 0, 0),
        (170, 255, 195), (128, 128, 0), (255, 215, 180), (0, 0, 128), (128, 128, 128),
    };

    private static ElementId SolidFill(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting)?.Id
        ?? throw new InvalidOperationException("The model has no solid fill pattern.");

    /// <summary>Override settings from: color, line_weight (1-16), halftone, transparency (0-100), fill (solid surface in the colour).</summary>
    private static OverrideGraphicSettings Overrides(Document doc, Obj p)
    {
        var o = new OverrideGraphicSettings();
        if (Has(p, "color"))
        {
            var col = ColorOf(p["color"], "color");
            o.SetProjectionLineColor(col);
            o.SetCutLineColor(col);
            if (Flag(p, "fill", false))
            {
                var solid = SolidFill(doc);
                o.SetSurfaceForegroundPatternId(solid);
                o.SetSurfaceForegroundPatternColor(col);
                o.SetCutForegroundPatternId(solid);
                o.SetCutForegroundPatternColor(col);
            }
        }
        if (Has(p, "line_weight"))
        {
            int w = Int(p, "line_weight", 1, 1, 16);
            o.SetProjectionLineWeight(w);
            o.SetCutLineWeight(w);
        }
        if (Bool(p, "halftone") is bool h) o.SetHalftone(h);
        if (Has(p, "transparency")) o.SetSurfaceTransparency(Int(p, "transparency", 0, 0, 100));
        return o;
    }

    private static List<View> Views(Document doc, Obj p)
    {
        if (Has(p, "views")) return StrList(p, "views", 500).Select(v => Find.ViewOf(doc, v)).ToList();
        return new List<View> { Find.ViewOrActive(doc, p) };
    }

    private static void NeedEditableGraphics(View v)
    {
        if (v.ViewTemplateId != ElementId.InvalidElementId && !v.IsTemplate)
        {
            var t = v.Document.GetElement(v.ViewTemplateId) as View;
            throw new ArgumentException($"View '{v.Name}' uses view template '{t?.Name}', which controls its graphics. " +
                                        "Apply the change to the template (give the template's name as the view) or remove the template first.");
        }
    }

    /// <summary>A view by name or id, view templates included (filters and overrides can go on a template).</summary>
    private static View ViewOrTemplate(Document doc, string name)
    {
        var t = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return t ?? Find.ViewOf(doc, name);
    }

    // ---------------------------------------------------------------- filters

    /// <summary>The parameter a rule tests, found on an element (or its type) of the filter's categories.</summary>
    private static Parameter RuleParameter(Document doc, List<BuiltInCategory> cats, string name)
    {
        foreach (var cat in cats)
        {
            var e = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().FirstElement();
            if (e == null) continue;
            var prm = Find.ParamOrNull(e, name) ?? (doc.GetElement(e.GetTypeId()) is Element t ? Find.ParamOrNull(t, name) : null);
            if (prm != null) return prm;
        }
        throw new ArgumentException($"No element of {string.Join(", ", cats)} has a parameter '{name}' (needed to build the rule).");
    }

    private static FilterRule Rule(Parameter prm, string op, object value)
    {
        var id = prm.Id;
        string what = $"rule on '{prm.Definition.Name}'";
        switch (prm.StorageType)
        {
            case StorageType.String:
                string text = Text(value);
                switch (op)
                {
                    case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(id, text);
                    case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, text);
                    case "contains": return ParameterFilterRuleFactory.CreateContainsRule(id, text);
                    case "not_contains": return ParameterFilterRuleFactory.CreateNotContainsRule(id, text);
                    case "begins_with": return ParameterFilterRuleFactory.CreateBeginsWithRule(id, text);
                    case "ends_with": return ParameterFilterRuleFactory.CreateEndsWithRule(id, text);
                    case "has_value": return ParameterFilterRuleFactory.CreateHasValueParameterRule(id);
                    case "has_no_value": return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(id);
                }
                break;
            case StorageType.Integer:
                int n = value is bool b ? (b ? 1 : 0) : (int)Math.Round(Number(value, what));
                switch (op)
                {
                    case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(id, n);
                    case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, n);
                    case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(id, n);
                    case "less": return ParameterFilterRuleFactory.CreateLessRule(id, n);
                }
                break;
            case StorageType.Double:
                if (op == "has_value") return ParameterFilterRuleFactory.CreateHasValueParameterRule(id);
                double d = Describe.ToInternal(prm, Number(value, what));
                const double eps = 1e-6;
                switch (op)
                {
                    case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(id, d, eps);
                    case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, d, eps);
                    case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(id, d, eps);
                    case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(id, d, eps);
                    case "less": return ParameterFilterRuleFactory.CreateLessRule(id, d, eps);
                    case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(id, d, eps);
                }
                break;
            case StorageType.ElementId:
                var doc = prm.Element.Document;
                ElementId target = value is string s && !long.TryParse(s.Trim(), out _)
                    ? new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElements())
                        .FirstOrDefault(e => e.Name.Equals(s.Trim(), StringComparison.OrdinalIgnoreCase) && (e is Level || e is Material || e is ElementType || e is Phase))?.Id
                      ?? throw new ArgumentException($"{what}: nothing named '{s}'.")
                    : ToId(value, what);
                switch (op)
                {
                    case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(id, target);
                    case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(id, target);
                }
                break;
        }
        throw new ArgumentException($"{what}: operator '{op}' does not apply to a {prm.StorageType} parameter.");
    }

    private static readonly string[] Ops =
        { "equals", "not_equals", "contains", "not_contains", "begins_with", "ends_with", "greater", "greater_or_equal", "less", "less_or_equal", "has_value", "has_no_value" };

    /// <summary>Create (or update) a rule-based filter; returns it.</summary>
    private static ParameterFilterElement MakeFilter(Document doc, string name, List<BuiltInCategory> cats, List<FilterRule> rules, bool any, string ifExists)
    {
        var catIds = cats.Select(x => new ElementId(x)).ToList();
        ElementFilter filter = null;
        if (rules.Count == 1 || !any) filter = rules.Count == 0 ? null : new ElementParameterFilter(rules);
        else filter = new LogicalOrFilter(rules.Select(r => (ElementFilter)new ElementParameterFilter(r)).ToList());
        var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (ifExists == "skip") return existing;
            if (ifExists != "update") throw new ArgumentException($"A filter named '{name}' exists. Pass if_exists: skip or update.");
            existing.SetCategories(catIds);
            if (filter != null) existing.SetElementFilter(filter);
            return existing;
        }
        if (filter != null && !ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(doc, new HashSet<ElementId>(catIds), filter))
            throw new ArgumentException($"Filter '{name}': a rule parameter is not available for all of {string.Join(", ", cats)}.");
        return filter == null ? ParameterFilterElement.Create(doc, name, catIds) : ParameterFilterElement.Create(doc, name, catIds, filter);
    }

    private static object FilterCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var cats = Find.Cats(doc, p, "categories");
        if (cats.Count == 0) throw new ArgumentException("Give 'categories'.");
        var rules = new List<FilterRule>();
        foreach (var r in ObjList(p, "rules", 20, required: false) ?? new List<Obj>())
        {
            string op = Choice(r, "op", "equals", Ops);
            rules.Add(Rule(RuleParameter(doc, cats, NeedStr(r, "parameter")), op, Has(r, "value") ? r["value"] : ""));
        }
        bool any = Choice(p, "match", "all", "all", "any") == "any";
        var f = MakeFilter(doc, NeedStr(p, "name"), cats, rules, any, Choice(p, "if_exists", "fail", "fail", "skip", "update"));
        return new { id = f.Id.Value, name = f.Name, categories = cats.Select(x => x.ToString().Substring(4)).ToList(), rules = rules.Count, match = any ? "any" : "all" };
    }

    private static object FilterApply(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string name = NeedStr(p, "filter");
        var filter = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No filter named '{name}'.");
        var views = Has(p, "views") ? StrList(p, "views", 500).Select(v => ViewOrTemplate(doc, v)).ToList()
                                     : new List<View> { Find.ViewOrActive(doc, p) };
        var done = new List<string>();
        foreach (var v in views)
        {
            NeedEditableGraphics(v);
            if (Flag(p, "remove", false)) { if (v.GetFilters().Contains(filter.Id)) v.RemoveFilter(filter.Id); done.Add(v.Name); continue; }
            if (!v.GetFilters().Contains(filter.Id)) v.AddFilter(filter.Id);
            v.SetFilterOverrides(filter.Id, Overrides(doc, p));
            v.SetFilterVisibility(filter.Id, Flag(p, "visible", true));
            done.Add(v.Name);
        }
        return new { filter = filter.Name, views = done };
    }

    private static object ColorByParameter(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        NeedEditableGraphics(view);
        var cats = Find.Cats(doc, p, "categories");
        if (cats.Count == 0) throw new ArgumentException("Give 'categories'.");
        string pname = NeedStr(p, "parameter");
        string prefix = Str(p, "prefix") ?? "Claude - ";
        var colors = Has(p, "colors") ? Sub(p, "colors") : new Obj();

        // distinct values among the elements of those categories shown in the view
        var values = new Dictionary<string, (Parameter prm, object raw)>();
        foreach (var e in new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(cats.Select(x => new ElementId(x)).ToList()))
                     .WhereElementIsNotElementType())
        {
            var prm = Find.ParamOrNull(e, pname) ?? (doc.GetElement(e.GetTypeId()) is Element t ? Find.ParamOrNull(t, pname) : null);
            if (prm == null || !prm.HasValue) continue;
            string label; object raw;
            switch (prm.StorageType)
            {
                case StorageType.String: label = prm.AsString(); raw = label; break;
                case StorageType.Integer: label = prm.AsValueString() ?? prm.AsInteger().ToString(); raw = (double)prm.AsInteger(); break;
                case StorageType.ElementId: label = doc.GetElement(prm.AsElementId())?.Name ?? prm.AsElementId().Value.ToString(); raw = prm.AsElementId().Value.ToString(); break;
                default: label = prm.AsValueString(); raw = null; break;   // doubles: equals on the internal value below
            }
            if (string.IsNullOrEmpty(label) || values.ContainsKey(label)) continue;
            values[label] = (prm, raw);
            if (values.Count > 60) throw new ArgumentException($"'{pname}' has more than 60 different values in this view; colour by a coarser parameter.");
        }
        if (values.Count == 0) throw new ArgumentException($"No element of those categories in '{view.Name}' has a value for '{pname}'.");

        var made = new List<object>();
        int i = 0;
        foreach (var kv in values.OrderBy(k => k.Key))
        {
            var prm = kv.Value.prm;
            FilterRule rule = prm.StorageType == StorageType.Double
                ? ParameterFilterRuleFactory.CreateEqualsRule(prm.Id, prm.AsDouble(), 1e-6)
                : Rule(prm, "equals", kv.Value.raw);
            var f = MakeFilter(doc, $"{prefix}{pname} = {kv.Key}", cats, new List<FilterRule> { rule }, false, "update");
            var colorValue = colors.TryGetValue(kv.Key, out var given) ? given : null;
            var (r, g, b) = Palette[i++ % Palette.Length];
            var col = colorValue != null ? ColorOf(colorValue, $"colour for '{kv.Key}'") : new Color(r, g, b);
            var o = new OverrideGraphicSettings();
            var solid = SolidFill(doc);
            o.SetSurfaceForegroundPatternId(solid); o.SetSurfaceForegroundPatternColor(col);
            o.SetCutForegroundPatternId(solid); o.SetCutForegroundPatternColor(col);
            if (!view.GetFilters().Contains(f.Id)) view.AddFilter(f.Id);
            view.SetFilterOverrides(f.Id, o);
            made.Add(new { value = kv.Key, filter = f.Name, color = $"#{col.Red:X2}{col.Green:X2}{col.Blue:X2}" });
        }
        return new { view = view.Name, parameter = pname, values = made.Count, legend = made };
    }

    private static object GraphicsOverride(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        NeedEditableGraphics(view);
        var ids = Ids(p, "ids", 20000, required: false);
        var cats = Has(p, "categories") ? Find.Cats(doc, p, "categories") : new List<BuiltInCategory>();
        if ((ids == null || ids.Count == 0) && cats.Count == 0) throw new ArgumentException("Give 'ids' or 'categories'.");
        bool reset = Flag(p, "reset", false);
        var o = reset ? new OverrideGraphicSettings() : Overrides(doc, p);
        int elements = 0, categories = 0;
        if (ids != null && ids.Count > 0)
        {
            if (Bool(p, "hide") is bool hide)
            {
                if (hide) view.HideElements(ids); else view.UnhideElements(ids);
            }
            else foreach (var id in ids) view.SetElementOverrides(id, o);
            elements = ids.Count;
        }
        foreach (var cat in cats)
        {
            var catId = new ElementId(cat);
            if (Bool(p, "hide") is bool hide) view.SetCategoryHidden(catId, hide);
            else view.SetCategoryOverrides(catId, o);
            categories++;
        }
        return new { view = view.Name, elements, categories, reset };
    }

    // ---------------------------------------------------------------- view templates

    private static object ViewTemplateCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOf(doc, NeedRef(p, "view"));
        View t;
        try { t = view.CreateViewTemplate(); }
        catch (Exception ex) { throw new ArgumentException($"'{view.Name}' cannot make a view template: {ex.Message}"); }
        t.Name = NeedStr(p, "name");
        return new { id = t.Id.Value, name = t.Name, from = view.Name };
    }

    private static object ViewTemplateApply(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var views = StrList(p, "views", 2000).Select(v => Find.ViewOf(doc, v)).ToList();
        string tname = Str(p, "template");
        View template = null;
        if (tname != null && !tname.Equals("none", StringComparison.OrdinalIgnoreCase))
            template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(tname, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No view template named '{tname}'. list_documentation kind view_templates lists them.");
        var done = new List<string>(); var refused = new List<object>();
        foreach (var v in views)
        {
            if (template != null && !v.IsValidViewTemplate(template.Id)) { refused.Add(new { view = v.Name, reason = "the template does not fit this view type" }); continue; }
            v.ViewTemplateId = template?.Id ?? ElementId.InvalidElementId;
            done.Add(v.Name);
        }
        return new { template = template?.Name ?? "none", applied = done.Count, views = done, refused };
    }

    // ---------------------------------------------------------------- dimensions and spot elevations

    /// <summary>Planar faces of an element with references usable in a dimension, in model coordinates.</summary>
    private static IEnumerable<(PlanarFace face, Reference reference, XYZ normal, XYZ origin)> Faces(Element e, View view)
    {
        var opts = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false };
        if (view != null) opts.View = view;
        var geo = e.get_Geometry(opts);
        if (geo == null) yield break;
        foreach (var obj in geo)
        {
            if (obj is Solid s)
            {
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf && pf.Reference != null) yield return (pf, pf.Reference, pf.FaceNormal, pf.Origin);
            }
            else if (obj is GeometryInstance gi)
            {
                // symbol geometry keeps references to the instance; positions are moved by the instance transform
                var tf = gi.Transform;
                foreach (var so in gi.GetSymbolGeometry())
                    if (so is Solid ss)
                        foreach (Face f in ss.Faces)
                            if (f is PlanarFace pf && pf.Reference != null)
                                yield return (pf, pf.Reference, tf.OfVector(pf.FaceNormal), tf.OfPoint(pf.Origin));
            }
        }
    }

    private static object DimensionElements(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        if (!(view is ViewPlan)) throw new ArgumentException("dimension_elements works in plan views ('view').");
        double z = view.GenLevel?.ProjectElevation ?? 0;
        var elements = Has(p, "ids")
            ? Ids(p, "ids", 2000).Select(id => doc.GetElement(id) ?? throw new ArgumentException($"No element {id.Value}.")).ToList()
            : new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(Find.Cats(doc, p, "categories").Select(x => new ElementId(x)).ToList()))
                .WhereElementIsNotElementType().ToElements().ToList();
        if (elements.Count == 0) throw new ArgumentException("Nothing to dimension: give 'ids' or 'categories' with elements in this view.");
        string dirs = Choice(p, "direction", "both", "x", "y", "both");
        double offset = U.LenMm(p, "offset", 1500);
        bool grids = Flag(p, "include_grids", true);
        var made = new List<object>();
        foreach (var axis in dirs == "both" ? new[] { "x", "y" } : new[] { dirs })
        {
            var d = axis == "x" ? XYZ.BasisX : XYZ.BasisY;      // measured along d
            var across = axis == "x" ? XYZ.BasisY : XYZ.BasisX;
            var stations = new List<(double at, Reference r)>();
            double maxAcross = double.MinValue;
            foreach (var e in elements)
            {
                var faces = Faces(e, view).Where(f => Math.Abs(f.normal.DotProduct(d)) > 0.999).ToList();
                if (faces.Count == 0) continue;
                // the two outer faces of the element along d
                var lo = faces.OrderBy(f => f.origin.DotProduct(d)).First();
                var hi = faces.OrderBy(f => f.origin.DotProduct(d)).Last();
                stations.Add((lo.origin.DotProduct(d), lo.reference));
                if (hi.origin.DotProduct(d) - lo.origin.DotProduct(d) > 0.003) stations.Add((hi.origin.DotProduct(d), hi.reference));
                var bb = e.get_BoundingBox(view);
                if (bb != null) maxAcross = Math.Max(maxAcross, Math.Max(bb.Min.DotProduct(across), bb.Max.DotProduct(across)));
            }
            if (grids)
                foreach (var g in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
                    if (g.Curve is Line gl && Math.Abs(gl.Direction.DotProduct(d)) < 0.001)
                        stations.Add((gl.Origin.DotProduct(d), new Reference(g)));
            // one reference per position (1 mm)
            var refs = new ReferenceArray();
            double first = double.MaxValue, last = double.MinValue, prev = double.MinValue;
            foreach (var st in stations.OrderBy(s => s.at))
            {
                if (st.at - prev < 0.003) continue;
                refs.Append(st.r); prev = st.at;
                first = Math.Min(first, st.at); last = Math.Max(last, st.at);
            }
            if (refs.Size < 2) continue;
            double line = maxAcross + offset;
            XYZ a = d * first + across * line + XYZ.BasisZ * z, b = d * last + across * line + XYZ.BasisZ * z;
            var dim = doc.Create.NewDimension(view, Line.CreateBound(a, b), refs);
            made.Add(new { id = dim.Id.Value, direction = axis, references = refs.Size });
        }
        if (made.Count == 0) throw new ArgumentException("Found no faces square to x or y to dimension (rotated elements need a rotated dimension; not supported yet).");
        return new { view = view.Name, dimensions = made.Count, created = made };
    }

    private static object SpotElevation(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        bool leader = Flag(p, "leader", true);
        var made = new List<object>(); var skipped = new List<object>();
        foreach (var id in Ids(p, "ids", 2000))
        {
            var e = doc.GetElement(id) ?? throw new ArgumentException($"No element {id.Value}.");
            var top = Faces(e, view).Where(f => f.normal.Z > 0.999).OrderByDescending(f => f.origin.Z).FirstOrDefault();
            if (top.face == null) top = Faces(e, null).Where(f => f.normal.Z > 0.999).OrderByDescending(f => f.origin.Z).FirstOrDefault();
            if (top.face == null) { skipped.Add(new { id = id.Value, reason = "no exposed top face (covered by a joined slab or not horizontal); tag the slab above instead" }); continue; }
            var bb = top.face.GetBoundingBox();
            var uv = (bb.Min + bb.Max) / 2;
            XYZ at = Has(p, "point") ? new XYZ(U.NeedPt(p, "point").X, U.NeedPt(p, "point").Y, top.origin.Z) : null;
            // the face point in model coordinates: evaluate on the face, then move it like the face origin was moved
            var local = top.face.Evaluate(uv);
            var shift = top.origin - top.face.Origin;
            XYZ origin = at ?? local + shift;
            XYZ bend = origin + new XYZ(U.Mm(600), U.Mm(600), 0), end = origin + new XYZ(U.Mm(1200), U.Mm(600), 0);
            var spot = doc.Create.NewSpotElevation(view, top.reference, origin, bend, end, origin, leader);
            made.Add(new { id = spot.Id.Value, element = id.Value, elevation = U.Out(top.origin.Z) });
        }
        return new { view = view.Name, created = made.Count, spots = made, skipped };
    }

    // ---------------------------------------------------------------- revisions

    /// <summary>The project-wide revision number, or null when the model numbers revisions per sheet (Revit then refuses it).</summary>
    public static string NumberOf(Revision r)
    {
        try { return r.RevisionNumber; }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
    }

    private static Revision RevisionOf(Document doc, string spec)
    {
        var all = Revision.GetAllRevisionIds(doc).Select(id => (Revision)doc.GetElement(id)).ToList();
        return all.FirstOrDefault(r => r.Description.Equals(spec, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(r => NumberOf(r) == spec || r.SequenceNumber.ToString() == spec || r.Id.Value.ToString() == spec)
               ?? throw new ArgumentException($"No revision '{spec}'. Revisions: {string.Join("; ", all.Select(r => $"{r.SequenceNumber} {r.Description}"))}.");
    }

    private static object RevisionCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var r = Revision.Create(doc);
        r.Description = NeedStr(p, "description");
        if (Str(p, "date") is string date) r.RevisionDate = date;
        if (Str(p, "issued_by") is string by) r.IssuedBy = by;
        if (Str(p, "issued_to") is string to) r.IssuedTo = to;
        if (Bool(p, "issued") is bool issued) r.Issued = issued;
        var sheets = new List<string>();
        foreach (var s in StrList(p, "sheets", 2000, required: false) ?? new List<string>())
        {
            var sheet = Find.ViewOf(doc, s) as ViewSheet ?? throw new ArgumentException($"'{s}' is not a sheet.");
            var ids = sheet.GetAdditionalRevisionIds();
            if (!ids.Contains(r.Id)) { ids.Add(r.Id); sheet.SetAdditionalRevisionIds(ids); }
            sheets.Add(sheet.SheetNumber);
        }
        return new { id = r.Id.Value, sequence = r.SequenceNumber, number = NumberOf(r), description = r.Description, sheets };
    }

    private static object RevisionCloudCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        var rev = RevisionOf(doc, NeedRef(p, "revision"));
        var loop = EditContext.Loop(p["boundary"], "boundary", view.GenLevel?.ProjectElevation ?? 0);
        var cloud = RevisionCloud.Create(doc, view, rev.Id, loop.ToList());
        return new { id = cloud.Id.Value, view = view.Name, revision = rev.Description };
    }

    // ---------------------------------------------------------------- several views on a sheet

    private static object SheetLayout(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var sheet = Find.ViewOf(doc, NeedRef(p, "sheet")) as ViewSheet ?? throw new ArgumentException("'sheet' is not a sheet.");
        var views = StrList(p, "views", 60).Select(v => Find.ViewOf(doc, v)).ToList();
        // usable area on the paper (mm on the sheet): given, or the sheet outline less a margin
        var o = sheet.Outline;
        double margin = U.Mm(Dbl(p, "margin") ?? 20), gap = U.Mm(Dbl(p, "gap") ?? 15);
        double x0 = o.Min.U + margin, y0 = o.Min.V + margin, x1 = o.Max.U - margin, y1 = o.Max.V - margin;
        if (Has(p, "area"))
        {
            // paper millimetres from the sheet's lower-left corner, whatever the request units
            var a = Sub(p, "area");
            double[] Corner(string key) => NeedArr(a, key, 2).Select(v => U.Mm(Number(v, $"area {key}"))).ToArray();
            var lo = Corner("min"); var hi = Corner("max");
            x0 = o.Min.U + lo[0]; y0 = o.Min.V + lo[1]; x1 = o.Min.U + hi[0]; y1 = o.Min.V + hi[1];
        }
        int columns = Int(p, "columns", Math.Max(1, (int)Math.Ceiling(Math.Sqrt(views.Count))), 1, 20);

        // place everything at the centre first to learn each one's size on the paper
        var placed = new List<(Element el, string name, double w, double h, bool schedule)>();
        var centre = new XYZ((x0 + x1) / 2, (y0 + y1) / 2, 0);
        foreach (var v in views)
        {
            if (v is ViewSchedule vs)
            {
                var si = ScheduleSheetInstance.Create(doc, sheet.Id, vs.Id, centre);
                doc.Regenerate();
                var bb = si.get_BoundingBox(sheet);
                placed.Add((si, v.Name, bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y, true));
            }
            else
            {
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id))
                    throw new InvalidOperationException($"View '{v.Name}' cannot be placed on {sheet.SheetNumber} (already on a sheet?).");
                var vp = Viewport.Create(doc, sheet.Id, v.Id, centre);
                doc.Regenerate();
                var box = vp.GetBoxOutline();
                placed.Add((vp, v.Name, box.MaximumPoint.X - box.MinimumPoint.X, box.MaximumPoint.Y - box.MinimumPoint.Y, false));
            }
        }
        // rows top to bottom, left to right; each row as tall as its tallest item
        var result = new List<object>();
        double y = y1; bool overflow = false;
        for (int row = 0; row * columns < placed.Count; row++)
        {
            var items = placed.Skip(row * columns).Take(columns).ToList();
            double rowH = items.Max(i => i.h), x = x0;
            foreach (var it in items)
            {
                var at = new XYZ(x + it.w / 2, y - it.h / 2, 0);
                if (it.schedule)
                {
                    var si = (ScheduleSheetInstance)it.el;
                    var bb = si.get_BoundingBox(sheet);
                    // move so the schedule's box centre lands on 'at'
                    ElementTransformUtils.MoveElement(doc, si.Id, at - (bb.Min + bb.Max) / 2);
                }
                else ((Viewport)it.el).SetBoxCenter(at);
                if (x + it.w > x1 + 1e-6 || y - it.h < y0 - 1e-6) overflow = true;
                result.Add(new { view = it.name, id = it.el.Id.Value, schedule = it.schedule, sizeMm = new[] { Math.Round(it.w * U.MmPerFoot), Math.Round(it.h * U.MmPerFoot) } });
                x += it.w + gap;
            }
            y -= rowH + gap;
        }
        if (overflow) c.Warn("the views do not all fit in the sheet area; change their scale or use more sheets.");
        return new { sheet = sheet.SheetNumber, placed = result.Count, columns, items = result };
    }
}
