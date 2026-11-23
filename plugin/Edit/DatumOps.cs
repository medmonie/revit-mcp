namespace RevitMcpBridge;

/// <summary>Levels and grids.</summary>
internal static class DatumOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["level_create"] = LevelCreate;
        ops["grid_create"] = GridCreate;
        ops["grid_layout"] = GridLayout;
        ops["grid_extents"] = GridExtents;
    }

    private static object LevelCreate(EditContext c, Obj p)
    {
        string name = Str(p, "name");
        if (name != null && Choice(p, "if_exists", "fail", "fail", "skip") == "skip")
        {
            var existing = Find.Levels(c.Doc).FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return new { id = existing.Id.Value, name = existing.Name,
                elevation = U.Out(existing.ProjectElevation), displayElevation = U.Out(existing.Elevation), existed = true };
        }
        var level = Level.Create(c.Doc, U.In(NeedDbl(p, "elevation")));
        if (name != null) level.Name = name;

        string plan = Choice(p, "plan", "none", "none", "structural", "floor", "ceiling");
        long? planId = plan == "none" ? (long?)null : ViewOps.CreatePlan(c.Doc, level, plan, null).Id.Value;
        return new { id = level.Id.Value, name = level.Name, elevation = U.Out(level.ProjectElevation),
            displayElevation = U.Out(level.Elevation), planViewId = planId };
    }

    internal static Grid MakeGrid(Document doc, XYZ a, XYZ b, string name, string what) =>
        Named(Grid.Create(doc, U.Segment(new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0), what)), name, what);

    private static Grid Named(Grid grid, string name, string what)
    {
        if (!string.IsNullOrEmpty(name))
        {
            try { grid.Name = name; }
            catch (Exception ex) { throw new ArgumentException($"{what}: cannot name the grid '{name}' ({ex.Message}). Is the name already used?"); }
        }
        SpanAllLevels(grid);   // API grids get a default height that can stop below the upper levels, so they would not show in those plans
        return grid;
    }

    /// <summary>Vertical range covering every level (1 m below the lowest, 1 m above the highest), in feet.</summary>
    private static (double lo, double hi) LevelRange(Document doc)
    {
        var els = Find.Levels(doc).Select(l => l.ProjectElevation).ToList();
        double m = U.Mm(1000);
        return els.Count == 0 ? (-m, m) : (els.Min() - m, els.Max() + m);
    }

    private static void SpanAllLevels(Grid grid, double? bottom = null, double? top = null)
    {
        var (lo, hi) = LevelRange(grid.Document);
        var cur = grid.GetExtents();
        double b = bottom ?? Math.Min(lo, cur.MinimumPoint.Z), t = top ?? Math.Max(hi, cur.MaximumPoint.Z);
        if (t - b < 0.01) throw new ArgumentException("grid extents: 'top' must be above 'bottom'.");
        grid.SetVerticalExtents(b, t);
    }

    /// <summary>Stretch grids vertically so they show in the plans of every level (or between 'bottom' and 'top' elevations).</summary>
    private static object GridExtents(EditContext c, Obj p)
    {
        var names = Has(p, "grids") ? new HashSet<string>(StrList(p, "grids", 2000), StringComparer.OrdinalIgnoreCase) : null;
        var grids = new FilteredElementCollector(c.Doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(g => names == null || names.Contains(g.Name)).ToList();
        if (names != null)
        {
            var missing = names.Except(grids.Select(g => g.Name), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0) throw new ArgumentException("grid_extents: no grid named " + string.Join(", ", missing.Select(n => $"'{n}'")) + ".");
        }
        double? bottom = Dbl(p, "bottom") is double bb ? U.In(bb) : (double?)null;
        double? top = Dbl(p, "top") is double tt ? U.In(tt) : (double?)null;
        foreach (var g in grids) SpanAllLevels(g, bottom, top);
        var (lo, hi) = LevelRange(c.Doc);
        return new { grids = grids.Count, bottom = U.Out(bottom ?? lo), top = U.Out(top ?? hi) };
    }

    private static object GridCreate(EditContext c, Obj p)
    {
        // Straight, or an arc through start, mid and end.
        var curve = U.Curve(p, "grid_create", 0, flatten: true);
        var grid = Named(curve is Arc arc ? Grid.Create(c.Doc, arc) : Grid.Create(c.Doc, (Line)curve), Ref(p, "name"), "grid_create");
        return new { id = grid.Id.Value, name = grid.Name, curved = curve is Arc };
    }

    internal static string Letter(int index)
    {
        // A, B, ... Z, AA, AB, ...
        string s = "";
        for (index++; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s;
        return s;
    }

    /// <summary>An orthogonal grid: one grid line at every x position and one at every y position.</summary>
    private static object GridLayout(EditContext c, Obj p)
    {
        var xs = NeedArr(p, "x", 500).Select((v, i) => U.In(Number(v, $"'x' item {i + 1}"))).ToList();
        var ys = NeedArr(p, "y", 500).Select((v, i) => U.In(Number(v, $"'y' item {i + 1}"))).ToList();
        var xNames = StrList(p, "x_names", 500, required: false);
        var yNames = StrList(p, "y_names", 500, required: false);
        if (xNames.Count > 0 && xNames.Count != xs.Count) throw new ArgumentException("'x_names' must have one name per 'x' position.");
        if (yNames.Count > 0 && yNames.Count != ys.Count) throw new ArgumentException("'y_names' must have one name per 'y' position.");
        double over = U.LenMm(p, "overhang", 2000);

        var made = new List<object>();
        for (int i = 0; i < xs.Count; i++)
        {
            string name = xNames.Count > 0 ? xNames[i] : (i + 1).ToString(CultureInfo.InvariantCulture);
            var g = MakeGrid(c.Doc, new XYZ(xs[i], ys.Min() - over, 0), new XYZ(xs[i], ys.Max() + over, 0), name, $"grid at x item {i + 1}");
            made.Add(new { id = g.Id.Value, name = g.Name, x = U.Out(xs[i]) });
        }
        for (int i = 0; i < ys.Count; i++)
        {
            string name = yNames.Count > 0 ? yNames[i] : Letter(i);
            var g = MakeGrid(c.Doc, new XYZ(xs.Min() - over, ys[i], 0), new XYZ(xs.Max() + over, ys[i], 0), name, $"grid at y item {i + 1}");
            made.Add(new { id = g.Id.Value, name = g.Name, y = U.Out(ys[i]) });
        }
        return new { created = made.Count, grids = made };
    }
}
