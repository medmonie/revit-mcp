namespace RevitMcpBridge;

/// <summary>Model from a linked / imported DWG: columns from the outlines on a column layer, grids from the lines on a grid layer.</summary>
internal static class CadOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["columns_from_cad"] = ColumnsFromCad;
        ops["grids_from_cad"] = GridsFromCad;
    }

    /// <summary>Everything columns_from_cad decides, without changing the model: shared by the read action cad_columns.</summary>
    internal sealed class ColumnPlan
    {
        public List<(Cad.Footprint f, SectionSize type, double rotation, long? existing)> Rows = new List<(Cad.Footprint, SectionSize, double, long?)>();
        public int Outlines;
        public string Include;
    }

    internal static ColumnPlan PlanColumns(Document doc, Obj p, double baseZ)
    {
        var imp = Cad.NeedImport(doc, p);
        string layer = NeedStr(p, "layer");
        double tol = U.LenMm(p, "tolerance", 10);
        double gap = U.LenMm(p, "gap", 25);
        double minSize = U.LenMm(p, "min_size", 150), maxSize = U.LenMm(p, "max_size", 4000);
        string include = Choice(p, "include", "concrete", "concrete", "steel", "all");
        bool skipExisting = Flag(p, "skip_existing", true);
        var map = Cad.TypeMap(p);

        var types = Sections.Of(doc, BuiltInCategory.OST_StructuralColumns);
        var cols = skipExisting
            ? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList()
            : new List<FamilyInstance>();
        var plan = new ColumnPlan { Include = include };
        var prints = Cad.Footprints(Cad.Curves(doc, imp, layer), gap);
        plan.Outlines = prints.Count;
        double probeZ = baseZ + 0.5;   // half a foot above the base: a column standing there is 'existing'
        foreach (var f in prints.OrderBy(f => Math.Round(f.Center.Y, 1)).ThenBy(f => f.Center.X))
        {
            if (Math.Max(f.Width, f.Depth) < minSize || Math.Max(f.Width, f.Depth) > maxSize) continue;
            bool steel = f.Kind == "section";
            if (include == "concrete" && steel || include == "steel" && !steel) continue;
            var m = Cad.Match(f, types, tol, map);
            var ex = skipExisting ? Cad.ExistingColumn(cols, f.Center, probeZ, Math.Max(f.Width, f.Depth) / 2) : null;
            plan.Rows.Add((f, m?.type, m?.rotation ?? f.Angle, ex?.Id.Value));
        }
        return plan;
    }

    internal static object Summary(ColumnPlan plan, int limit)
    {
        var rows = plan.Rows;
        return new
        {
            outlinesFound = plan.Outlines,
            included = plan.Include,
            columns = rows.Count,
            toPlace = rows.Count(r => r.type != null && r.existing == null),
            alreadyModelled = rows.Count(r => r.existing != null),
            unmatched = rows.Count(r => r.type == null && r.existing == null),
            bySize = BySize(plan),
            sample = rows.Take(limit).Select(r => new
            {
                center = U.Xy(r.f.Center), size = Sections.Key(r.f.Width, r.f.Depth), kind = r.f.Kind,
                rotation = Math.Round(r.rotation * 180 / Math.PI, 3),
                type = r.type == null ? null : Find.TypeLabel(r.type.Symbol), existing = r.existing,
            }).ToList(),
        };
    }

    internal static List<object> BySize(ColumnPlan plan) =>
        plan.Rows.GroupBy(r => (Sections.Key(r.f.Width, r.f.Depth), r.f.Kind, r.type == null ? null : Find.TypeLabel(r.type.Symbol)))
            .OrderByDescending(g => g.Count())
            .Select(g => (object)new { size = g.Key.Item1, kind = g.Key.Item2, type = g.Key.Item3 ?? "(no type of this size: add one, or map it in 'types')",
                                       count = g.Count(), alreadyModelled = g.Count(r => r.existing != null) }).ToList();

    private static object ColumnsFromCad(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        var top = Has(p, "top_level") ? Find.LevelOf(doc, Ref(p, "top_level")) : null;
        double? height = Dbl(p, "height");
        if (top == null && height == null) throw new ArgumentException("columns_from_cad needs 'top_level' or 'height'.");
        double baseOff = U.Len(p, "base_offset", 0), topOff = U.Len(p, "top_offset", 0);
        if (top != null && top.ProjectElevation + topOff <= level.ProjectElevation + baseOff)
            throw new ArgumentException("The column top is not above its base (check top_level and the offsets).");

        var plan = PlanColumns(doc, p, level.ProjectElevation + baseOff);
        var newTypes = Has(p, "create_types") ? CreateMissingTypes(c, p, plan) : new List<object>();
        var made = new List<long>();
        foreach (var r in plan.Rows.Where(r => r.type != null && r.existing == null))
            made.Add(StructureOps.PlaceColumn(c, r.type.Symbol, level, top, height == null ? (double?)null : U.In(height.Value),
                                              baseOff, topOff, r.f.Center, r.rotation).Id.Value);
        int unmatched = plan.Rows.Count(r => r.type == null && r.existing == null);
        if (unmatched > 0) c.Warn($"{unmatched} outline(s) have no column type of their size and were not placed (see bySize).");
        return new { created = made.Count, ids = made, typesCreated = newTypes, alreadyModelled = plan.Rows.Count(r => r.existing != null), unmatched, bySize = BySize(plan) };
    }

    /// <summary>
    /// For every unmatched size: duplicate 'create_types' (a type whose size is in parameters, e.g. b / h) and set the size.
    /// Name: 'type_name' with {b} and {h} (default: the base name with its size replaced). Fills the plan rows in place.
    /// </summary>
    private static List<object> CreateMissingTypes(EditContext c, Obj p, ColumnPlan plan)
    {
        var doc = c.Doc;
        var bt = Sections.NeedResizable(Sections.Of(doc, BuiltInCategory.OST_StructuralColumns), NeedStr(p, "create_types"), "structural column");
        string template = Str(p, "type_name");
        var made = new List<object>();
        for (int i = 0; i < plan.Rows.Count; i++)
        {
            var r = plan.Rows[i];
            if (r.type != null || r.existing != null || r.f.Kind == "circle" || (r.f.Kind == "section") != bt.Steel) continue;
            // the family's b runs along the outline's shorter side unless the base type is wider than deep
            double w = r.f.Width, d = r.f.Depth, rot = r.f.Angle;
            if ((w > d) != (bt.B > bt.H) && Math.Abs(w - d) > 1e-6) { (w, d) = (d, w); rot += Math.PI / 2; }
            var (sym, created) = Sections.Sized(doc, bt, w, d, template);
            if (created) made.Add(new { type = Find.TypeLabel(sym), from = Find.TypeLabel(bt.Symbol) });
            plan.Rows[i] = (r.f, new SectionSize { Symbol = sym, B = w, H = d, Steel = bt.Steel, Source = bt.Source }, rot, r.existing);
        }
        if (made.Count > 0) c.Warn($"{made.Count} column type(s) were created from '{Find.TypeLabel(bt.Symbol)}': check their names and marks.");
        return made;
    }

    // ---------------------------------------------------------------- grids

    private static object GridsFromCad(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var imp = Cad.NeedImport(doc, p);
        var lines = Cad.GridLines(Cad.Curves(doc, imp, NeedStr(p, "layer")), U.LenMm(p, "min_length", 3000));
        if (lines.Count == 0) throw new ArgumentException("No straight lines of at least 'min_length' on that layer.");
        bool skipExisting = Flag(p, "skip_existing", true);
        string prefix = Str(p, "prefix") ?? "";
        var xNames = StrList(p, "x_names", 500, required: false);
        var yNames = StrList(p, "y_names", 500, required: false);

        var existing = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
        var existingLines = existing.Where(g => g.Curve is Line)
            .Select(g => (g, line: (a: Poly.Flat(g.Curve.GetEndPoint(0)), b: Poly.Flat(g.Curve.GetEndPoint(1))))).ToList();
        var used = new HashSet<string>(existing.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);

        // 'x' grids run north-south (numbered by x), 'y' grids east-west (lettered by y); others numbered after them.
        bool IsX((XYZ a, XYZ b) l) { var d = l.b - l.a; return Math.Abs(d.Y) >= Math.Abs(d.X); }
        var xs = lines.Where(IsX).OrderBy(l => (l.a.X + l.b.X) / 2).ToList();
        var ys = lines.Where(l => !IsX(l)).OrderBy(l => (l.a.Y + l.b.Y) / 2).ToList();
        if (xNames.Count > 0 && xNames.Count != xs.Count) throw new ArgumentException($"'x_names' must have {xs.Count} names (one per north-south line, west to east).");
        if (yNames.Count > 0 && yNames.Count != ys.Count) throw new ArgumentException($"'y_names' must have {ys.Count} names (one per east-west line, south to north).");

        var made = new List<object>(); var skipped = new List<object>();
        void Make(List<(XYZ a, XYZ b)> set, List<string> names, Func<int, string> auto, string axis)
        {
            for (int i = 0; i < set.Count; i++)
            {
                var l = set[i];
                var hit = skipExisting ? existingLines.FirstOrDefault(e => Cad.SameLine(e.line, l, U.Mm(10))).g : null;
                if (hit != null) { skipped.Add(new { axis, at = U.Xy((l.a + l.b) / 2), existingGrid = hit.Name }); continue; }
                string name = names.Count > 0 ? names[i] : prefix + auto(i);
                if (used.Contains(name))
                    throw new ArgumentException($"Grid name '{name}' is already used in the model. Give 'prefix' (e.g. 'N-') or 'x_names' / 'y_names'.");
                used.Add(name);
                var g = DatumOps.MakeGrid(doc, l.a, l.b, name, "grids_from_cad");
                made.Add(new { id = g.Id.Value, name = g.Name, axis });
            }
        }
        Make(xs, xNames, i => (i + 1).ToString(CultureInfo.InvariantCulture), "x");
        Make(ys, yNames, DatumOps.Letter, "y");
        return new { linesFound = lines.Count, created = made.Count, grids = made, skippedExisting = skipped.Count, skipped = skipped.Take(50).ToList() };
    }

}
