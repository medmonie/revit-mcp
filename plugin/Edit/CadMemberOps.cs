namespace RevitMcpBridge;

/// <summary>Beams, walls and slabs from a linked / imported DWG (columns and grids are in CadOps).</summary>
internal static class CadMemberOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["beams_from_cad"] = BeamsFromCad;
        ops["walls_from_cad"] = WallsFromCad;
        ops["slabs_from_cad"] = SlabsFromCad;
    }

    private static string Mm(double feet) => U.MmText(feet);   // width key, whole mm

    private static List<Cad.Item> Items(Document doc, Obj p) => Cad.Curves(doc, Cad.NeedImport(doc, p), NeedStr(p, "layer"));

    // ---------------------------------------------------------------- beams

    private static object BeamsFromCad(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        double z = level.ProjectElevation + U.Len(p, "z_offset", 0);
        double tol = U.LenMm(p, "tolerance", 10);
        var members = CadPairs.Members(Items(doc, p), U.LenMm(p, "min_width", 150), U.LenMm(p, "max_width", 1500),
                                       U.LenMm(p, "min_length", 500));
        bool steel = Choice(p, "include", "concrete", "concrete", "steel") == "steel";
        double? depth = Dbl(p, "depth") is double dd ? U.In(dd) : (double?)null;
        var map = Cad.TypeMap(p);
        var allTypes = Sections.Of(doc, BuiltInCategory.OST_StructuralFraming);
        var types = allTypes.Where(t => t.Steel == steel).ToList();
        var baseNew = Has(p, "create_types") ? Sections.NeedResizable(allTypes, NeedStr(p, "create_types"), "structural framing") : null;
        string template = Str(p, "type_name");

        bool skipExisting = Flag(p, "skip_existing", true);
        var existing = skipExisting
            ? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().ToList()
            : new List<Element>();

        // Same justification as beam_create: top of beam at the level (+ z_offset), centred on its line.
        var made = new List<long>(); var created = new List<object>();
        var rows = new List<(string width, string type, bool curved, string status)>();
        foreach (var m in members.OrderBy(m => m.Axis.Evaluate(0.5, true).Y).ThenBy(m => m.Axis.Evaluate(0.5, true).X))
        {
            string w = Mm(m.Width);
            if (skipExisting && Cad.ExistingAlong(existing, m.Axis, z, Math.Max(m.Width / 2, U.Mm(50))) != null) { rows.Add((w, null, m.Curved, "existing")); continue; }
            FamilySymbol sym = null;
            if (map != null && map.TryGetValue(w, out var tn)) sym = Find.Symbol(doc, tn, BuiltInCategory.OST_StructuralFraming);
            if (sym == null)
            {
                var hits = types.Where(t => Math.Abs(t.B - m.Width) <= tol && (depth == null || Math.Abs(t.H - depth.Value) <= tol)).ToList();
                sym = hits.OrderByDescending(t => t.Instances).ThenBy(t => t.Symbol.Name).FirstOrDefault()?.Symbol;
            }
            if (sym == null && baseNew != null)
            {
                var made1 = Sections.Sized(doc, baseNew, m.Width, null, template);   // the base type's depth is kept
                sym = made1.symbol;
                if (made1.created) created.Add(Find.TypeLabel(sym));
            }
            if (sym == null) { rows.Add((w, null, m.Curved, "no type")); continue; }
            var axis = m.Axis.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, z)));
            made.Add(StructureOps.PlaceBeam(c, axis, sym, level).Id.Value);   // top of beam at the level, centred
            rows.Add((w, Find.TypeLabel(sym), m.Curved, "placed"));
        }
        int noType = rows.Count(r => r.status == "no type");
        if (noType > 0) c.Warn($"{noType} beam(s) have no type of their width and were not placed (see byWidth; map them in 'types' or use create_types).");
        if (created.Count > 0) c.Warn($"{created.Count} beam type(s) were created: check their names and depths.");
        return new
        {
            membersFound = members.Count, created = made.Count, ids = made, typesCreated = created,
            alreadyModelled = rows.Count(r => r.status == "existing"), unmatched = noType,
            byWidth = rows.GroupBy(r => (r.width, r.type, r.status)).OrderByDescending(g => g.Count())
                .Select(g => new { width = g.Key.width, type = g.Key.type, status = g.Key.status, count = g.Count(), curved = g.Count(r => r.curved) }).ToList(),
        };
    }

    // ---------------------------------------------------------------- walls

    private static object WallsFromCad(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        var top = Has(p, "top_level") ? Find.LevelOf(doc, Ref(p, "top_level")) : null;
        double baseOff = U.Len(p, "base_offset", 0), topOff = U.Len(p, "top_offset", 0);
        double height = top != null ? top.ProjectElevation + topOff - (level.ProjectElevation + baseOff)
                                    : U.In(Dbl(p, "height") ?? throw new ArgumentException("walls_from_cad needs 'top_level' or 'height'."));
        if (height <= 0) throw new ArgumentException("The wall top is not above its base.");
        double tol = U.LenMm(p, "tolerance", 10);
        var members = CadPairs.Members(Items(doc, p), U.LenMm(p, "min_width", 100), U.LenMm(p, "max_width", 1500),
                                       U.LenMm(p, "min_length", 500));
        var map = Cad.TypeMap(p);
        var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic).ToList();
        var counts = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().GroupBy(w => w.WallType.Id.Value).ToDictionary(g => g.Key, g => g.Count());
        bool skipExisting = Flag(p, "skip_existing", true), structural = Flag(p, "structural", true);
        var existing = skipExisting ? new FilteredElementCollector(doc).OfClass(typeof(Wall)).ToList() : new List<Element>();

        var made = new List<long>();
        var rows = new List<(string width, string type, string status)>();
        double baseZ = level.ProjectElevation + baseOff;
        foreach (var m in members)
        {
            string w = Mm(m.Width);
            if (skipExisting && Cad.ExistingAlong(existing, m.Axis, level.ProjectElevation, Math.Max(m.Width / 2, U.Mm(50))) != null) { rows.Add((w, null, "existing")); continue; }
            WallType wt = null;
            if (map != null && map.TryGetValue(w, out var tn)) wt = (WallType)Find.TypeOf(doc, tn, BuiltInCategory.OST_Walls, typeof(WallType));
            wt = wt ?? wallTypes.Where(t => Math.Abs(t.Width - m.Width) <= tol)
                                .OrderByDescending(t => counts.TryGetValue(t.Id.Value, out int k) ? k : 0).ThenBy(t => t.Name).FirstOrDefault();
            if (wt == null) { rows.Add((w, null, "no type")); continue; }
            var axis = m.Axis.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, level.ProjectElevation)));
            made.Add(StructureOps.PlaceWall(c, axis, wt, level, top, height, baseOff, topOff, structural).Id.Value);
            rows.Add((w, wt.Name, "placed"));
        }
        int noType = rows.Count(r => r.status == "no type");
        if (noType > 0) c.Warn($"{noType} wall(s) have no wall type of their thickness and were not placed (see byWidth).");
        return new
        {
            membersFound = members.Count, created = made.Count, ids = made,
            alreadyModelled = rows.Count(r => r.status == "existing"), unmatched = noType,
            byWidth = rows.GroupBy(r => (r.width, r.type, r.status)).OrderByDescending(g => g.Count())
                .Select(g => new { width = g.Key.width, type = g.Key.type, status = g.Key.status, count = g.Count() }).ToList(),
        };
    }

    // ---------------------------------------------------------------- slabs

    /// <summary>Floors already on the level, for the skip_existing check (none when skip_existing is false).</summary>
    private static List<Floor> FloorsToSkip(Document doc, Obj p, Level level) =>
        Flag(p, "skip_existing", true)
            ? new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.LevelId == level.Id).ToList()
            : new List<Floor>();

    private static object SlabsFromCad(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        var type = (FloorType)Find.TypeOf(doc, NeedStr(p, "type"), null, typeof(FloorType));
        double minArea = U.FromM2(Dbl(p, "min_area") ?? 1.0);
        string mode = Choice(p, "mode", "closed", "closed", "outer");
        if (mode == "outer") return OuterSlabs(c, p, level, type, minArea);
        var loops = CadPairs.Loops(Items(doc, p), U.LenMm(p, "gap", 5)).Where(l => Poly.Area(l) >= 0.01).ToList();

        // nesting depth: how many other loops contain a loop; even depth = slab outline, odd = opening in its parent
        var info = loops.Select(l => (loop: l, area: Poly.Area(l), pt: l[0])).OrderByDescending(x => x.area).ToList();
        var parent = new int[info.Count]; var depth = new int[info.Count];
        for (int i = 0; i < info.Count; i++)
        {
            parent[i] = -1;
            for (int j = i - 1; j >= 0; j--)   // smallest larger loop that contains it
                if (info[j].area > info[i].area && Poly.Inside(info[j].loop, Poly.Centroid(info[i].loop)) && Poly.Inside(info[j].loop, info[i].pt))
                { parent[i] = j; depth[i] = depth[j] + 1; break; }
        }
        var floors = FloorsToSkip(doc, p, level);
        double z = level.ProjectElevation;
        var made = new List<object>(); var failed = new List<object>(); int existing = 0, small = 0;
        for (int i = 0; i < info.Count; i++)
        {
            if (depth[i] % 2 == 1) continue;
            if (info[i].area < minArea) { small++; continue; }
            var cen = Poly.Centroid(info[i].loop);
            if (Cad.ExistingFloor(floors, cen) != null)
            { existing++; continue; }
            var holes = Enumerable.Range(0, info.Count).Where(k => parent[k] == i && depth[k] == depth[i] + 1).ToList();
            try
            {
                var curveLoops = new List<CurveLoop> { Poly.Loop(info[i].loop, z) };
                curveLoops.AddRange(holes.Select(k => Poly.Loop(info[k].loop, z)));
                var floor = Floor.Create(doc, curveLoops, type.Id, level.Id, Flag(p, "structural", true), null, 0.0);
                if (Dbl(p, "offset") is double off) c.SetBuiltin(floor, "FLOOR_HEIGHTABOVELEVEL_PARAM", U.In(off));
                made.Add(new { id = floor.Id.Value, area_m2 = Math.Round(info[i].area * U.M2PerFt2, 2), openings = holes.Count });
            }
            catch (Exception ex)
            {
                failed.Add(new { at = U.Xy(cen), area_m2 = Math.Round(info[i].area * U.M2PerFt2, 2), reason = ex.Message });
            }
        }
        if (failed.Count > 0) c.Warn($"{failed.Count} outline(s) could not become slabs (see failed; often a self-crossing outline).");
        return new { loopsFound = loops.Count, created = made.Count, slabs = made, alreadyModelled = existing, tooSmall = small, failed };
    }

    /// <summary>
    /// mode 'outer': the slab is the outer outline of everything on 'layer' (slab edge, beam and column layers together),
    /// gaps up to 'gap' closed; openings are the closed outlines on 'opening_layer' inside it.
    /// </summary>
    private static object OuterSlabs(EditContext c, Obj p, Level level, FloorType type, double minArea)
    {
        var doc = c.Doc;
        var imp = Cad.NeedImport(doc, p);
        double gap = U.LenMm(p, "gap", 500), cell = U.LenMm(p, "cell", 25);
        var raster = CadRaster.Outlines(Cad.Curves(doc, imp, NeedStr(p, "layer")), gap, cell, minArea);
        var openings = Has(p, "opening_layer")
            ? CadPairs.Loops(Cad.Curves(doc, imp, NeedStr(p, "opening_layer")), U.LenMm(p, "opening_gap", 5)).Where(l => Poly.Area(l) >= 0.1).ToList()
            : new List<List<XYZ>>();
        var floors = FloorsToSkip(doc, p, level);
        double z = level.ProjectElevation;
        var made = new List<object>(); var failed = new List<object>(); int existing = 0, usedOpenings = 0;
        foreach (var (outline, snapped) in raster.Outlines.Zip(raster.Snapped, (o, sn) => (o, sn)).OrderByDescending(x => Poly.Area(x.o)))
        {
            var cen = Poly.Centroid(outline);
            if (Cad.ExistingFloor(floors, cen) != null)
            { existing++; continue; }
            var holes = openings.Where(h => h.All(q => Poly.Inside(outline, q))).ToList();
            if (Flag(p, "preview", false))
            {
                // draw the outline (and openings) as model lines on the level instead of making a slab
                var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
                var ids = new List<long>();
                foreach (var loop in new[] { outline }.Concat(holes))
                    for (int i = 0; i < loop.Count; i++)
                    {
                        var a = loop[i]; var b = loop[(i + 1) % loop.Count];
                        if (a.DistanceTo(b) < 0.01) continue;
                        ids.Add(doc.Create.NewModelCurve(Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z)), sp).Id.Value);
                    }
                made.Add(new { preview = true, area_m2 = Math.Round(Poly.Area(outline) * U.M2PerFt2, 2), vertices = outline.Count, openings = holes.Count,
                               snappedToCad = snapped, modelLines = ids.Count, firstLineId = ids.FirstOrDefault() });
                continue;
            }
            try
            {
                var loops = new List<CurveLoop> { Poly.Loop(outline, z) };
                loops.AddRange(holes.Select(h => Poly.Loop(h, z)));
                var floor = Floor.Create(doc, loops, type.Id, level.Id, Flag(p, "structural", true), null, 0.0);
                if (Dbl(p, "offset") is double off) c.SetBuiltin(floor, "FLOOR_HEIGHTABOVELEVEL_PARAM", U.In(off));
                usedOpenings += holes.Count;
                var bb = (minX: outline.Min(q => q.X), minY: outline.Min(q => q.Y), maxX: outline.Max(q => q.X), maxY: outline.Max(q => q.Y));
                made.Add(new { id = floor.Id.Value, area_m2 = Math.Round(Poly.Area(outline) * U.M2PerFt2, 2), vertices = outline.Count, openings = holes.Count,
                               snappedToCad = snapped, box = new { min = U.Xy(new XYZ(bb.minX, bb.minY, 0)), max = U.Xy(new XYZ(bb.maxX, bb.maxY, 0)) } });
            }
            catch (Exception ex)
            {
                failed.Add(new { at = U.Xy(cen), area_m2 = Math.Round(Poly.Area(outline) * U.M2PerFt2, 2), vertices = outline.Count, snappedToCad = snapped, reason = ex.Message });
            }
        }
        if (failed.Count > 0) c.Warn($"{failed.Count} outline(s) could not become slabs (see failed).");
        return new
        {
            mode = "outer", outlinesFound = raster.Outlines.Count, created = made.Count, slabs = made, alreadyModelled = existing,
            openingsFound = openings.Count, openingsUsed = usedOpenings, failed,
            grid = new { cell_mm = Math.Round(raster.Cell * U.MmPerFoot, 1), raster.Width, raster.Height },
            note = "Outer outline of all curves on 'layer' (gaps up to 'gap' closed), corners snapped to the nearest CAD point. Inner courtyards that are not on 'opening_layer' are filled: draw them as openings.",
        };
    }

}
