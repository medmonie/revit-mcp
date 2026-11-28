using Autodesk.Revit.DB.Architecture;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 17 P2 / P3 (architecture): area plans, boundaries and areas; ceilings; footprint roofs; curtain grids,
/// mullions and panels; stairs (after the commit, in Revit's stairs edit scope) and railings; slab edges; wall sweeps and
/// reveals; toposolids. Every size, slope, height and position is the architect's.
/// </summary>
internal static class ArchBuildOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["area_plan_create"] = AreaPlanCreate;
        ops["area_boundary"] = AreaBoundary;
        ops["area_create"] = AreaCreate;
        ops["ceiling_create"] = CeilingCreate;
        ops["roof_create"] = RoofCreate;
        ops["curtain_grid_set"] = CurtainGridSet;
        ops["stairs_create"] = StairsCreate;
        ops["railing_create"] = RailingCreate;
        ops["slab_edge_create"] = SlabEdgeCreate;
        ops["wall_sweep_create"] = WallSweepCreate;
        ops["toposolid_create"] = ToposolidCreate;
    }

    private static T TypeOrFirst<T>(Document doc, Obj p, string name, string what, Func<T, bool> ok = null) where T : ElementType
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().Where(t => ok == null || ok(t)).ToList();
        if (all.Count == 0) throw new ArgumentException($"This model has no {what} types. Load or transfer one first.");
        if (!Has(p, name)) return all[0];
        string spec = NeedStr(p, name);
        return all.FirstOrDefault(t => t.Id.Value.ToString(CultureInfo.InvariantCulture) == spec || string.Equals(t.Name, spec, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(Find.TypeLabel(t), spec, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No {what} type '{spec}'. Available: {string.Join(", ", all.Select(Find.TypeLabel).OrderBy(x => x).Take(40))}.");
    }

    // ---------------------------------------------------------------- areas

    private static AreaScheme Scheme(Document doc, Obj p)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
        if (all.Count == 0) throw new ArgumentException("This model has no area schemes (Revit's API cannot create one; add it in Area and Volume Computations).");
        if (!Has(p, "scheme")) return all.FirstOrDefault(s => s.IsGrossBuildingArea) ?? all[0];
        string n = NeedStr(p, "scheme");
        return all.FirstOrDefault(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No area scheme '{n}'. Schemes: {string.Join(", ", all.Select(s => s.Name))} (the API cannot create schemes).");
    }

    private static ViewPlan AreaPlan(Document doc, Obj p)
    {
        var v = Find.ViewOf(doc, NeedRef(p, "view")) as ViewPlan;
        if (v == null || v.ViewType != ViewType.AreaPlan) throw new ArgumentException("'view' must be an area plan (area_plan_create makes one).");
        return v;
    }

    private static object AreaPlanCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var scheme = Scheme(doc, p);
        var level = Find.NeedLevel(doc, p);
        var plan = ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);
        if (Has(p, "name")) plan.Name = NeedStr(p, "name");
        return new { id = plan.Id.Value, name = plan.Name, scheme = scheme.Name, level = level.Name, grossBuildingArea = scheme.IsGrossBuildingArea };
    }

    /// <summary>
    /// Area boundary lines in an area plan: the segments given, or one along each straight wall of the plan's level at the
    /// face the architect names (exterior, interior or the wall's centre), extended past its ends so corners close.
    /// </summary>
    private static object AreaBoundary(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = AreaPlan(doc, p);
        double z = view.GenLevel.Elevation;
        var sketch = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
        var curves = new List<Curve>();
        var skipped = new List<object>();
        if (Has(p, "lines") == Has(p, "from_walls")) throw new ArgumentException("Give 'lines' or 'from_walls', not both.");
        if (Has(p, "lines"))
        {
            int n = 0;
            foreach (var item in ObjList(p, "lines", 1000)) curves.Add(U.Curve(item, $"line {++n}", z, flatten: true));
        }
        else
        {
            string face = Choice(p, "from_walls", null, "outside", "inside", "exterior", "interior", "centre", "center");
            var walls = Has(p, "walls") ? Ids(p, "walls", 2000).Select(id => Find.Elem(doc, id) as Wall ?? throw new ArgumentException($"{id.Value} is not a wall.")).ToList()
                : new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.LevelId == view.GenLevel.Id).ToList();
            if (walls.Count == 0) throw new ArgumentException($"No walls on level '{view.GenLevel.Name}'.");
            double ext = walls.Max(w => w.Width) * 1.5;
            // the plan's walls as segments, to tell the building's outside from its inside (ray crossing count)
            var segs = new List<(XYZ a, XYZ b)>();
            foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.LevelId == view.GenLevel.Id))
                if (w.Location is LocationCurve wl && wl.Curve != null)
                {
                    var pts = wl.Curve.Tessellate();
                    for (int i = 1; i < pts.Count; i++) segs.Add((pts[i - 1], pts[i]));
                }
            bool InsideBuilding(XYZ q)
            {
                int hits = 0;
                foreach (var (a, b) in segs)
                    if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < a.X + (q.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) hits++;
                return hits % 2 == 1;
            }
            foreach (var w in walls)
            {
                if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line line)) { skipped.Add(new { wall = w.Id.Value, why = "not a straight wall" }); continue; }
                var a = new XYZ(line.GetEndPoint(0).X, line.GetEndPoint(0).Y, z);
                var b = new XYZ(line.GetEndPoint(1).X, line.GetEndPoint(1).Y, z);
                var d = (b - a).Normalize();
                XYZ shift = XYZ.Zero;
                if (face != "centre" && face != "center")
                {
                    PlanarFace Side(ShellLayerType t)
                    {
                        try { return HostObjectUtils.GetSideFaces(w, t).Select(r => w.GetGeometryObjectFromReference(r) as PlanarFace).FirstOrDefault(f => f != null); }
                        catch { return null; }
                    }
                    PlanarFace pf;
                    if (face == "exterior" || face == "interior") pf = Side(face == "exterior" ? ShellLayerType.Exterior : ShellLayerType.Interior);
                    else
                    {
                        // outside / inside of the building: the side face whose outward point lies outside (inside) the walls' outline
                        var mid = (a + b) / 2;
                        pf = new[] { Side(ShellLayerType.Exterior), Side(ShellLayerType.Interior) }.Where(f => f != null).FirstOrDefault(f =>
                        {
                            var n0 = new XYZ(f.FaceNormal.X, f.FaceNormal.Y, 0).Normalize();
                            var onFace = mid + n0 * n0.DotProduct(new XYZ(f.Origin.X, f.Origin.Y, z) - mid);
                            return InsideBuilding(onFace + n0 * U.Mm(50)) == (face == "inside");
                        });
                    }
                    if (pf == null) { skipped.Add(new { wall = w.Id.Value, why = $"no flat {face} face" }); continue; }
                    var n = new XYZ(pf.FaceNormal.X, pf.FaceNormal.Y, 0);
                    shift = n * n.DotProduct(new XYZ(pf.Origin.X, pf.Origin.Y, z) - a);
                }
                else
                {
                    // the wall's centre, whatever its location line is
                    var bb = w.get_BoundingBox(null);
                    var o = w.Orientation; var n = new XYZ(o.X, o.Y, 0).Normalize();
                    var mid = (bb.Min + bb.Max) / 2;
                    shift = n * n.DotProduct(new XYZ(mid.X, mid.Y, z) - a);
                }
                curves.Add(Line.CreateBound(a + shift - d * ext, b + shift + d * ext));
            }
        }
        var ids = curves.Select(cv => doc.Create.NewAreaBoundaryLine(sketch, cv, view).Id.Value).ToList();
        return new { view = view.Name, created = ids.Count, ids, skipped };
    }

    private static object AreaCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = AreaPlan(doc, p);
        var pts = U.PtList(p, "points", 1, 500);
        var names = Has(p, "names") ? StrList(p, "names", 500) : null;
        var numbers = Has(p, "numbers") ? StrList(p, "numbers", 500) : null;
        if (names != null && names.Count != pts.Count) throw new ArgumentException("'names' needs one name per point.");
        if (numbers != null && numbers.Count != pts.Count) throw new ArgumentException("'numbers' needs one number per point.");
        var made = new List<Area>();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = doc.Create.NewArea(view, new UV(pts[i].X, pts[i].Y)) ?? throw new InvalidOperationException($"Revit did not create an area at point {i + 1}.");
            if (names != null) a.Name = names[i];
            if (numbers != null) a.Number = numbers[i];
            made.Add(a);
        }
        doc.Regenerate();
        var rows = made.Select(a => new { id = a.Id.Value, number = a.Number, name = Find.Builtin(a, "ROOM_NAME")?.AsString(), areaM2 = U.Area(a.Area), enclosed = a.Area > 1e-9 }).ToList();
        int open = rows.Count(r => !r.enclosed);
        if (open > 0) c.Warn($"{open} of the new areas are not enclosed by area boundary lines.");
        return new { view = view.Name, created = rows.Count, notEnclosed = open, items = rows };
    }

    // ---------------------------------------------------------------- ceilings and roofs

    private static object CeilingCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        var type = TypeOrFirst<CeilingType>(doc, p, "type", "ceiling");
        var loops = new List<CurveLoop> { EditContext.Loop(p["boundary"], "boundary", level.Elevation) };
        foreach (var (h, i) in Arr(p, "holes", 100).Select((h, i) => (h, i))) loops.Add(EditContext.Loop(h, $"hole {i + 1}", level.Elevation));
        Ceiling ceiling;
        if (Has(p, "slope_arrow"))
        {
            var a = Sub(p, "slope_arrow");
            var arrow = U.Segment(U.NeedPt(a, "start", level.Elevation), U.NeedPt(a, "end", level.Elevation), "slope_arrow");
            double slope = (Dbl(p, "slope") ?? throw new ArgumentException("slope_arrow needs 'slope' in percent.")) / 100.0;
            ceiling = Ceiling.Create(doc, loops, type.Id, level.Id, arrow, slope);
        }
        else ceiling = Ceiling.Create(doc, loops, type.Id, level.Id);
        var hp = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
        hp?.Set(U.Len(p, "height", 0));
        doc.Regenerate();
        return new
        {
            id = ceiling.Id.Value, type = type.Name, level = level.Name, height = hp == null ? (double?)null : U.Out(hp.AsDouble()),
            areaM2 = U.Area(ceiling.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0),
        };
    }

    /// <summary>
    /// A footprint roof from the outline given, on a level: each edge flat or sloped at the angle given (degrees), with the
    /// overhang and base offset given.
    /// </summary>
    private static object RoofCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        var type = TypeOrFirst<RoofType>(doc, p, "type", "roof");
        var loop = EditContext.Loop(p["footprint"], "footprint", level.Elevation);
        var edges = loop.ToList();
        List<object> slopes = Has(p, "slopes") ? NeedArr(p, "slopes", 200) : null;
        if (slopes != null && slopes.Count != edges.Count) throw new ArgumentException($"'slopes' needs one value per footprint edge ({edges.Count}); null for a flat edge.");
        double? all = Dbl(p, "slope");
        if (slopes != null && all != null) throw new ArgumentException("Give 'slope' (every edge) or 'slopes' (per edge), not both.");
        var footprint = new CurveArray();
        foreach (var e in edges) footprint.Append(e);
        var mapping = new ModelCurveArray();
        var roof = doc.Create.NewFootPrintRoof(footprint, level, type, out mapping);
        double overhang = U.Len(p, "overhang", 0);
        var rows = new List<object>();
        int k = 0;
        foreach (ModelCurve mc in mapping)
        {
            double? deg = slopes != null ? (slopes[k] == null ? (double?)null : Number(slopes[k], $"slopes[{k + 1}]")) : all;
            if (deg != null && (deg <= 0 || deg >= 89)) throw new ArgumentException($"Edge {k + 1}: slope {deg} degrees is outside 0..89.");
            roof.set_DefinesSlope(mc, deg != null);
            if (deg != null) roof.set_SlopeAngle(mc, Math.Tan(U.Rad(deg.Value)));   // Revit takes the slope as rise / run
            if (overhang != 0) roof.set_Overhang(mc, overhang);
            rows.Add(new { edge = k + 1, slopeDeg = deg, overhang = U.Out(overhang) });
            k++;
        }
        if (Has(p, "base_offset")) roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(U.Len(p, "base_offset", 0));
        doc.Regenerate();
        var bb = roof.get_BoundingBox(null);
        return new
        {
            id = roof.Id.Value, type = type.Name, level = level.Name, edges = rows,
            top = bb == null ? (double?)null : U.Out(bb.Max.Z - level.Elevation), bottom = bb == null ? (double?)null : U.Out(bb.Min.Z - level.Elevation),
            note = "top / bottom: highest and lowest point of the roof above the level.",
        };
    }

    // ---------------------------------------------------------------- curtain walls

    /// <summary>
    /// On a curtain wall: vertical grid lines at the distances given along the wall, horizontal ones at the heights given above
    /// its base, mullions of the type given on the new lines, and panel types at the points given (distance along, height).
    /// </summary>
    private static object CurtainGridSet(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var wall = c.Editable(Find.Elem(doc, NeedId(p, "wall"))) as Wall ?? throw new ArgumentException("'wall' must be a curtain wall.");
        var grid = wall.CurtainGrid ?? throw new ArgumentException($"Wall {wall.Id.Value} is not a curtain wall.");
        if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line line)) throw new ArgumentException("curtain_grid_set takes straight curtain walls.");
        double len = line.Length;
        var level = doc.GetElement(wall.LevelId) as Level;
        double baseZ = (level?.Elevation ?? 0) + (wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
        double height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
        XYZ At(double along, double up) => line.Evaluate(along / len, true) + new XYZ(0, 0, baseZ + up - line.GetEndPoint(0).Z);
        MullionType mullion = Has(p, "mullion_type") ? TypeOrFirst<MullionType>(doc, p, "mullion_type", "mullion") : null;
        var made = new List<object>();

        CurtainGridLine Add(bool vertical, XYZ at, string what)
        {
            // which of U / V runs vertically depends on the host: try one, check, and swap if needed
            foreach (bool isU in new[] { !vertical, vertical })
            {
                CurtainGridLine gl;
                try { gl = grid.AddGridLine(isU, at, false); } catch { continue; }
                var dir = (gl.FullCurve.GetEndPoint(1) - gl.FullCurve.GetEndPoint(0)).Normalize();
                if ((Math.Abs(dir.Z) > 0.9) == vertical) return gl;
                doc.Delete(gl.Id);
            }
            throw new ArgumentException($"{what}: Revit refused a grid line there (outside the wall, or on an existing line).");
        }

        foreach (var (v, i) in Arr(p, "u", 200).Select((v, i) => (v, i)))
        {
            double d = U.In(Number(v, $"u[{i + 1}]"));
            if (d <= 0 || d >= len) throw new ArgumentException($"u[{i + 1}]: {U.Out(d)} is outside the wall (length {U.Out(len)}).");
            var gl = Add(true, At(d, height / 2), $"u[{i + 1}]");
            if (mullion != null) foreach (Curve seg in gl.AllSegmentCurves) { gl.AddMullions(seg, mullion, false); break; }
            made.Add(new { kind = "vertical", along = U.Out(d), id = gl.Id.Value });
        }
        foreach (var (v, i) in Arr(p, "v", 200).Select((v, i) => (v, i)))
        {
            double h = U.In(Number(v, $"v[{i + 1}]"));
            if (h <= 0 || (height > 0 && h >= height)) throw new ArgumentException($"v[{i + 1}]: {U.Out(h)} is outside the wall height ({U.Out(height)}).");
            var gl = Add(false, At(len / 2, h), $"v[{i + 1}]");
            if (mullion != null) foreach (Curve seg in gl.AllSegmentCurves) { gl.AddMullions(seg, mullion, false); break; }
            made.Add(new { kind = "horizontal", height = U.Out(h), id = gl.Id.Value });
        }
        doc.Regenerate();
        var panels = new List<object>();
        foreach (var (it, i) in ObjList(p, "panels", 500, required: false).Select((it, i) => (it, i)))
        {
            string what = $"panels[{i + 1}]";
            var pt = At(U.Len(it, "along", 0), U.Len(it, "height", 0));
            var spec = NeedStr(it, "type");
            var ptype = (ElementType)new FilteredElementCollector(doc).WhereElementIsElementType().OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                            .Cast<ElementType>().FirstOrDefault(t => string.Equals(t.Name, spec, StringComparison.OrdinalIgnoreCase) || string.Equals(Find.TypeLabel(t), spec, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"{what}: no curtain panel type '{spec}' (list_types category curtain_panels).");
            Element hit = null;
            foreach (var pid in grid.GetPanelIds())
            {
                var pe = doc.GetElement(pid); var bb = pe?.get_BoundingBox(null);
                if (bb == null) continue;
                double tol = U.Mm(5);
                var flat = new XYZ(pt.X, pt.Y, pt.Z);
                bool inXY = flat.X >= bb.Min.X - wall.Width - tol && flat.X <= bb.Max.X + wall.Width + tol && flat.Y >= bb.Min.Y - wall.Width - tol && flat.Y <= bb.Max.Y + wall.Width + tol;
                double along0 = line.Project(bb.Min)?.Parameter ?? 0, along1 = line.Project(bb.Max)?.Parameter ?? 0, alongP = line.Project(flat)?.Parameter ?? -1;
                if (inXY && flat.Z >= bb.Min.Z - tol && flat.Z <= bb.Max.Z + tol && alongP >= Math.Min(along0, along1) - tol && alongP <= Math.Max(along0, along1) + tol) { hit = pe; break; }
            }
            if (hit == null) throw new ArgumentException($"{what}: no panel of wall {wall.Id.Value} at {U.Out(U.Len(it, "along", 0))} along, {U.Out(U.Len(it, "height", 0))} up.");
            if (hit.Pinned) throw new ArgumentException($"{what}: panel {hit.Id.Value} is pinned (unpin it in Revit first).");
            var changed = grid.ChangePanelType(hit, ptype);
            panels.Add(new { was = hit.Id.Value, id = changed?.Id.Value, type = Find.TypeLabel(ptype) });
        }
        int Lines(bool vertical) => grid.GetUGridLineIds().Concat(grid.GetVGridLineIds()).Select(id => doc.GetElement(id) as CurtainGridLine)
            .Count(gl => gl != null && (Math.Abs((gl.FullCurve.GetEndPoint(1) - gl.FullCurve.GetEndPoint(0)).Normalize().Z) > 0.9) == vertical);
        return new
        {
            wall = wall.Id.Value, gridLines = made, mullionType = mullion?.Name, panelsChanged = panels,
            panelCount = grid.NumPanels, verticalLines = Lines(true), horizontalLines = Lines(false),
        };
    }

    // ---------------------------------------------------------------- stairs and railings

    /// <summary>
    /// Stairs between two levels from the architect's numbers: total risers, tread depth, run width and the runs (a start
    /// point and a direction point each, with its number of risers). Each run's path is laid out from those numbers (risers
    /// - 1 treads long, rising from the risers before it); landings join consecutive runs. Built after the commit in Revit's
    /// stairs edit scope (stairs cannot be made inside the batch transaction).
    /// </summary>
    private static object StairsCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var baseLevel = Find.NeedLevel(doc, p, "base_level");
        var topLevel = Find.NeedLevel(doc, p, "top_level");
        double rise = topLevel.Elevation - baseLevel.Elevation;
        if (rise <= 0) throw new ArgumentException("'top_level' must be above 'base_level'.");
        int risers = IntegerValue(NeedDbl(p, "risers"), "risers");
        if (risers < 2 || risers > 200) throw new ArgumentException("'risers' must be 2..200.");
        double tread = U.Len(p, "tread_depth", 0);
        if (tread <= 0) throw new ArgumentException("Give 'tread_depth' above zero.");
        double width = U.Len(p, "width", 0);
        if (width <= 0) throw new ArgumentException("Give 'width' (run width) above zero.");
        double riserH = rise / risers;
        var runs = ObjList(p, "runs", 20);
        var plan = new List<(Line path, int n, double baseZ)>();
        int used = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            int n = IntegerValue(NeedDbl(r, "risers"), $"runs[{i + 1}].risers");
            if (n < 1) throw new ArgumentException($"runs[{i + 1}]: risers must be at least 1.");
            var a = U.NeedPt(r, "start"); var b = U.NeedPt(r, "toward");
            var d = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (d.GetLength() < 1e-6) throw new ArgumentException($"runs[{i + 1}]: 'toward' must differ from 'start' in plan.");
            d = d.Normalize();
            double baseZ = used * riserH;
            var start = new XYZ(a.X, a.Y, baseZ);
            double runLen = Math.Max(n - 1, 1) * tread;
            plan.Add((Line.CreateBound(start, start + d * runLen), n, baseZ));
            used += n;
        }
        if (used != risers) throw new ArgumentException($"The runs have {used} risers in all; 'risers' is {risers}.");
        string just = Choice(p, "justification", "center", "center", "left", "right");
        var justification = just == "left" ? StairsRunJustification.Left : just == "right" ? StairsRunJustification.Right : StairsRunJustification.Center;
        var stairsType = Has(p, "type") ? TypeOrFirst<StairsType>(doc, p, "type", "stairs") : null;
        RailingType railing = Has(p, "railing_type") ? TypeOrFirst<RailingType>(doc, p, "railing_type", "railing") : null;
        string railingAt = Choice(p, "railing_position", "treads", "treads", "stringer");

        var result = new Obj
        {
            ["baseLevel"] = baseLevel.Name, ["topLevel"] = topLevel.Name, ["risers"] = risers, ["riserHeight"] = U.Out(riserH),
            ["treadDepth"] = U.Out(tread), ["width"] = U.Out(width),
            ["runs"] = plan.Select((x, i) => (object)new { run = i + 1, risers = x.n, start = U.Xy(x.path.GetEndPoint(0)), end = U.Xy(x.path.GetEndPoint(1)), length = U.Out(x.path.Length), baseHeight = U.Out(x.baseZ) }).ToList(),
            ["status"] = c.DryRun ? "dry run: the stairs would be built after the commit (Revit's stairs edit scope)" : "queued: built after the commit",
        };
        if (c.DryRun) return result;
        c.Defer(() =>
        {
            var fc = new FailureCollector();
            ElementId stairsId;
            using (var scope = new StairsEditScope(doc, "Claude: stairs_create"))
            {
                stairsId = scope.Start(baseLevel.Id, topLevel.Id);
                using (var t = new Transaction(doc, "Claude: stairs runs"))
                {
                    t.Start();
                    var stairs = (Stairs)doc.GetElement(stairsId);
                    if (stairsType != null) stairs.ChangeTypeId(stairsType.Id);
                    stairs.DesiredRisersNumber = risers;
                    stairs.ActualTreadDepth = tread;
                    var runIds = new List<ElementId>();
                    foreach (var (path, _, _) in plan)
                    {
                        var run = StairsRun.CreateStraightRun(doc, stairsId, path, justification);
                        run.ActualRunWidth = width;
                        runIds.Add(run.Id);
                    }
                    var landings = new List<long>();
                    for (int i = 1; i < runIds.Count; i++)
                        if (StairsLanding.CanCreateAutomaticLanding(doc, runIds[i - 1], runIds[i]))
                            landings.AddRange(StairsLanding.CreateAutomaticLanding(doc, runIds[i - 1], runIds[i]).Select(x => x.Value));
                    t.Commit();
                    result["landings"] = landings;
                }
                scope.Commit(fc);
            }
            var made = doc.GetElement(stairsId) as Stairs ?? throw new InvalidOperationException("Revit did not keep the stairs: " + string.Join("; ", fc.Errors));
            result["id"] = stairsId.Value;
            result["actualRisers"] = made.ActualRisersNumber;
            result["actualRiserHeight"] = U.Out(made.ActualRiserHeight);
            result["actualTreads"] = made.ActualTreadsNumber;
            result["revitWarnings"] = fc.Warnings;
            // Revit may add its default railings with the stairs: those take the type given; otherwise railings are added
            if (railing != null)
                using (var t = new Transaction(doc, "Claude: stairs railings"))
                {
                    t.Start();
                    var have = made.GetAssociatedRailings();
                    if (have.Count > 0) foreach (var rid in have) doc.GetElement(rid)?.ChangeTypeId(railing.Id);
                    else Railing.Create(doc, stairsId, railing.Id, railingAt == "stringer" ? RailingPlacementPosition.Stringer : RailingPlacementPosition.Treads);
                    t.Commit();
                }
            result["railings"] = made.GetAssociatedRailings().Select(x => new { id = x.Value, type = doc.GetElement(doc.GetElement(x)?.GetTypeId() ?? ElementId.InvalidElementId)?.Name }).ToList();
            result["status"] = "built";
        });
        return result;
    }

    /// <summary>A railing along the path given (open or closed polyline) on a level, or on the stairs given.</summary>
    private static object RailingCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var type = TypeOrFirst<RailingType>(doc, p, "type", "railing");
        if (Has(p, "stairs"))
        {
            var host = Find.Elem(doc, NeedId(p, "stairs"));
            if (!(host is Stairs) && host.Category?.BuiltInCategory != BuiltInCategory.OST_Ramps) throw new ArgumentException("'stairs' must be stairs or a ramp.");
            string at = Choice(p, "position", "treads", "treads", "stringer");
            var ids = Railing.Create(doc, host.Id, type.Id, at == "stringer" ? RailingPlacementPosition.Stringer : RailingPlacementPosition.Treads);
            return new { type = type.Name, host = host.Id.Value, created = ids.Count, ids = ids.Select(x => x.Value).ToList() };
        }
        var level = Find.NeedLevel(doc, p);
        var pts = U.PtList(p, "path", 2, 500, level.Elevation).Select(q => new XYZ(q.X, q.Y, level.Elevation)).ToList();
        var loop = new CurveLoop();
        for (int i = 1; i < pts.Count; i++) loop.Append(U.Segment(pts[i - 1], pts[i], $"path segment {i}"));
        if (Flag(p, "closed", false)) loop.Append(U.Segment(pts[pts.Count - 1], pts[0], "closing segment"));
        var rail = Railing.Create(doc, loop, type.Id, level.Id);
        if (Has(p, "base_offset")) rail.get_Parameter(BuiltInParameter.STAIRS_RAILING_HEIGHT_OFFSET)?.Set(U.Len(p, "base_offset", 0));
        return new { type = type.Name, level = level.Name, id = rail.Id.Value, length = U.Out(loop.GetExactLength()) };
    }

    // ---------------------------------------------------------------- slab edges, sweeps and reveals, site

    /// <summary>Slab edges of the type given along a floor's top outer edges (all of them, or those nearest the points given).</summary>
    private static object SlabEdgeCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var floor = Find.Elem(doc, NeedId(p, "floor")) as Floor ?? throw new ArgumentException("'floor' must be a floor id.");
        var type = TypeOrFirst<SlabEdgeType>(doc, p, "type", "slab edge");
        var solids = floor.get_Geometry(new Options { ComputeReferences = true }).OfType<Solid>().Where(s => s.Volume > 1e-9).ToList();
        var top = solids.SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>()
            .Where(f => f.FaceNormal.Z > 0.9).OrderByDescending(f => f.Area).FirstOrDefault()
            ?? throw new ArgumentException($"Floor {floor.Id.Value} has no flat top face.");
        var edges = top.EdgeLoops.Cast<EdgeArray>().OrderByDescending(ea => ea.Cast<Edge>().Sum(e => e.ApproximateLength)).First().Cast<Edge>().ToList();
        var chosen = new List<Edge>();
        if (Has(p, "near"))
            foreach (var pt in U.PtList(p, "near", 1, 200))
                chosen.Add(edges.OrderBy(e => e.AsCurve().Project(new XYZ(pt.X, pt.Y, e.AsCurve().GetEndPoint(0).Z))?.Distance ?? double.MaxValue).First());
        else chosen.AddRange(edges);
        var refs = new ReferenceArray();
        foreach (var e in chosen.Distinct()) refs.Append(e.Reference ?? throw new InvalidOperationException("an edge has no reference"));
        var edge = doc.Create.NewSlabEdge(type, refs);
        return new { id = edge.Id.Value, type = type.Name, floor = floor.Id.Value, edges = chosen.Distinct().Count(), length = U.Out(chosen.Distinct().Sum(e => e.ApproximateLength)) };
    }

    /// <summary>Wall sweeps or reveals of the type given on walls: horizontal at a height above the wall base, or vertical at a distance from the wall start.</summary>
    private static object WallSweepCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string spec = NeedStr(p, "type");
        var type = new FilteredElementCollector(doc).WhereElementIsElementType()
                       .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Cornices, BuiltInCategory.OST_Reveals })).Cast<ElementType>()
                       .FirstOrDefault(t => string.Equals(t.Name, spec, StringComparison.OrdinalIgnoreCase) || string.Equals(Find.TypeLabel(t), spec, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"No wall sweep or reveal type '{spec}' (list_types category wall_sweeps / reveals).");
        bool reveal = type.Category?.BuiltInCategory == BuiltInCategory.OST_Reveals;
        bool vertical = Flag(p, "vertical", false);
        string side = Choice(p, "side", "exterior", "exterior", "interior");
        double dist = U.Len(p, vertical ? "along" : "height", 0);
        var made = new List<object>();
        foreach (var id in Ids(p, "walls", 500))
        {
            var wall = c.Editable(Find.Elem(doc, id)) as Wall ?? throw new ArgumentException($"{id.Value} is not a wall.");
            var info = new WallSweepInfo(reveal ? WallSweepType.Reveal : WallSweepType.Sweep, vertical)
            {
                Distance = dist, WallSide = side == "exterior" ? WallSide.Exterior : WallSide.Interior,
            };
            if (!vertical) info.DistanceMeasuredFrom = DistanceMeasuredFrom.Base;
            var sw = WallSweep.Create(wall, type.Id, info);
            made.Add(new { id = sw.Id.Value, wall = wall.Id.Value });
        }
        return new { kind = reveal ? "reveal" : "sweep", type = Find.TypeLabel(type), side, vertical, distance = U.Out(dist), created = made.Count, items = made };
    }

    /// <summary>A toposolid from the survey points given (x, y, z), optionally bounded by an outline, on a level.</summary>
    private static object ToposolidCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        var type = TypeOrFirst<ToposolidType>(doc, p, "type", "toposolid");
        var pts = new List<XYZ>();
        foreach (var (v, i) in NeedArr(p, "points", 20000).Select((v, i) => (v, i)))
        {
            var q = v as List<object>;
            if (q == null || q.Count != 3) throw new ArgumentException($"points[{i + 1}] must be [x, y, z].");
            pts.Add(new XYZ(U.In(Number(q[0], "x")), U.In(Number(q[1], "y")), U.In(Number(q[2], "z"))));
        }
        if (pts.Count < 3) throw new ArgumentException("Give at least 3 points.");
        Toposolid topo = Has(p, "boundary")
            ? Toposolid.Create(doc, new List<CurveLoop> { EditContext.Loop(p["boundary"], "boundary", level.Elevation) }, pts, type.Id, level.Id)
            : Toposolid.Create(doc, pts, type.Id, level.Id);
        doc.Regenerate();
        var bb = topo.get_BoundingBox(null);
        return new { id = topo.Id.Value, type = type.Name, level = level.Name, points = pts.Count, lowest = bb == null ? (double?)null : U.Out(bb.Min.Z), highest = bb == null ? (double?)null : U.Out(bb.Max.Z) };
    }
}
