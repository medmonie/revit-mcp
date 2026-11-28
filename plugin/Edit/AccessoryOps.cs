using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 18, fourth part: duct and pipe accessories (dampers, fire dampers, valves) inserted in existing runs at
/// given points or where a run crosses a given wall / slab (host or link), and duct / pipe insulation and duct lining of a
/// given type and thickness. The engineer chooses the accessory type, where it goes, and the insulation type and
/// thickness; nothing is selected, sized or checked against a code here.
/// </summary>
internal static class AccessoryOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["mep_accessory_place"] = AccessoryPlace;
        ops["mep_insulation_add"] = InsulationAdd;
        ops["mep_insulation_remove"] = InsulationRemove;
        ops["sleeve_place"] = SleevePlace;
    }

    private static IEnumerable<Connector> Conns(Element e) =>
        (e is MEPCurve c ? c.ConnectorManager : (e as FamilyInstance)?.MEPModel?.ConnectorManager)?.Connectors.Cast<Connector>() ?? Enumerable.Empty<Connector>();

    private static Line RunLine(MEPCurve m) =>
        m.Location is LocationCurve lc && lc.Curve is Line l ? l : throw new ArgumentException($"Run {m.Id.Value} is not straight.");

    // ---------------------------------------------------------------- accessories

    /// <summary>Where on a run the accessory goes, and how that point was found.</summary>
    private sealed class Spot
    {
        public XYZ At;
        public string How;
        public Obj Crossing;
        public double Inside;   // length of the run inside the crossed element (feet), 0 for points
    }

    /// <summary>
    /// Insert a duct accessory (damper, fire damper) or pipe accessory (valve) of the type given into existing straight
    /// ducts / pipes, at a point on the run or where the run crosses a given wall or slab. Revit splits the run, gives the
    /// accessory the run's size and connects both sides. Several items on one run follow the pieces it is split into.
    /// </summary>
    private static object AccessoryPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var items = ObjList(p, "items", 500);
        var first = c.Editable(Find.Elem(doc, NeedId(items[0], "run")));
        bool pipe = first is Pipe;
        if (!pipe && !(first is Duct)) throw new ArgumentException($"Item 1: 'run' {first.Id.Value} must be a straight duct or pipe ({first.Category?.Name}).");
        string kind = pipe ? "pipe" : "duct";
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), pipe ? BuiltInCategory.OST_PipeAccessory : BuiltInCategory.OST_DuctAccessory);

        // crossing hosts may sit in a link (fire walls usually live in the architectural model)
        Document hdoc = doc; Transform htf = Transform.Identity; string linkName = null;
        if (Has(p, "crossing_link"))
        {
            var inst = LinkScope.Resolve(doc, NeedRef(p, "crossing_link"));
            hdoc = inst.GetLinkDocument() ?? throw new ArgumentException($"Link '{inst.Name}' is not loaded.");
            htf = inst.GetTotalTransform(); linkName = inst.Name;
        }

        var pieces = new Dictionary<long, List<ElementId>>();
        var rows = new List<Obj>();
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            string what = $"Item {i + 1}";
            var runId = NeedId(it, "run");
            var orig = c.Editable(Find.Elem(doc, runId));
            if (!pieces.TryGetValue(runId.Value, out var list))
            {
                if (pipe ? !(orig is Pipe) : !(orig is Duct))
                    throw new ArgumentException($"{what}: run {runId.Value} is a {orig.Category?.Name}; one operation places {kind} accessories on {kind}s only.");
                pieces[runId.Value] = list = Chain(doc, (MEPCurve)orig);
            }
            list.RemoveAll(id => !(doc.GetElement(id) is MEPCurve));
            bool hasPoint = Has(it, "point"), hasCrossing = Has(it, "crossing");
            if (hasPoint == hasCrossing) throw new ArgumentException($"{what}: give either 'point' or 'crossing'.");

            var runs = list.Select(id => (MEPCurve)doc.GetElement(id)).ToList();
            var (run, spot) = hasPoint ? AtPoint(it, runs, what) : AtCrossing(hdoc, htf, NeedId(it, "crossing"), runs, what);
            if (spot.Crossing != null && linkName != null) spot.Crossing["link"] = linkName;

            var line = RunLine(run);
            double fromEnd = Math.Min(spot.At.DistanceTo(line.GetEndPoint(0)), spot.At.DistanceTo(line.GetEndPoint(1)));
            var (acc, how, err) = Insert(c, run, symbol, spot.At);
            if (acc == null)
                throw new InvalidOperationException($"{what}: Revit could not insert '{Find.TypeLabel(symbol)}' into {kind} {run.Id.Value} at "
                    + $"({string.Join(", ", U.Xyz(spot.At))}), {U.Out(fromEnd):0} {U.Name} from the nearer end of that piece: {err}. "
                    + "Check that the type is an in-line accessory with two connectors for this shape, and that the point leaves room for it on the run.");
            doc.Regenerate();

            var ends = Conns(acc).Where(x => x.ConnectorType == ConnectorType.End && x.Domain == (pipe ? Domain.DomainPiping : Domain.DomainHvac)).ToList();
            var joined = new List<long>();
            foreach (var con in ends)
            {
                if (!con.IsConnected) continue;
                foreach (Connector r in con.AllRefs)
                    if (r.Owner is MEPCurve mc && mc.Id != acc.Id)
                    {
                        joined.Add(mc.Id.Value);
                        if (!list.Contains(mc.Id)) list.Add(mc.Id);
                    }
            }
            int open = ends.Count(x => !x.IsConnected);
            if (open > 0) c.Warn($"{what}: accessory {acc.Id.Value} has {open} connector(s) not connected to the run.");
            var sizes = ends.Select(x => SizeText(x)).Distinct().ToList();
            if (sizes.Any(z => z != RunSize(run))) c.Warn($"{what}: accessory {acc.Id.Value} is {string.Join(" / ", sizes)}, the run is {RunSize(run)} ({U.Name}).");
            var row = new Obj
            {
                ["id"] = acc.Id.Value, ["run"] = runId.Value, ["piece"] = run.Id.Value, ["at"] = U.Xyz(spot.At), ["position"] = spot.How,
                ["method"] = how, ["connectedTo"] = joined.Distinct().ToList(), ["openConnectors"] = open,
                ["accessorySize"] = string.Join(" / ", sizes), ["runSize"] = RunSize(run),
                ["system"] = ends.Select(x => { try { return x.MEPSystem?.Name; } catch { return null; } }).FirstOrDefault(s => s != null),
            };
            if (spot.Crossing != null) row["crossing"] = spot.Crossing;
            rows.Add(row);
        }
        return new
        {
            kind, type = Find.TypeLabel(symbol), placed = rows.Count, items = rows,
            note = "Accessories of the type given, where given; Revit splits the run and gives the accessory the run's size. "
                 + "Nothing is selected or sized here (fire ratings, damper types and positions are the engineer's).",
        };
    }

    /// <summary>The run's size in request units, written like SizeText ("600x300", "400 dia").</summary>
    private static string RunSize(MEPCurve m)
    {
        try { if (m.Diameter > 0) return $"{U.Out(m.Diameter):0.#} dia"; } catch { /* not round */ }
        try { return $"{U.Out(m.Width):0.#}x{U.Out(m.Height):0.#}"; } catch { return MepRead.Size(m).text; }
    }

    /// <summary>
    /// A run and the straight pieces in line with it that earlier in-line accessories split it into (run, accessory, run ...),
    /// so a later item on the same run id finds the piece its point is on.
    /// </summary>
    private static List<ElementId> Chain(Document doc, MEPCurve run)
    {
        var l0 = RunLine(run);
        XYZ a0 = l0.GetEndPoint(0), dir = l0.Direction.Normalize();
        bool InLine(MEPCurve m) => m.Location is LocationCurve lc && lc.Curve is Line l && l.Direction.CrossProduct(dir).GetLength() < 1e-6
                                   && (l.GetEndPoint(0) - a0).CrossProduct(dir).GetLength() < 0.01;
        bool Inline(FamilyInstance fi)
        {
            var cat = fi.Category?.BuiltInCategory;
            return (cat == BuiltInCategory.OST_DuctAccessory || cat == BuiltInCategory.OST_PipeAccessory)
                   && Conns(fi).Count(x => x.ConnectorType == ConnectorType.End) == 2;
        }
        var seen = new HashSet<long> { run.Id.Value };
        var list = new List<ElementId> { run.Id };
        var queue = new Queue<Element>();
        queue.Enqueue(run);
        while (queue.Count > 0 && seen.Count < 2000)
        {
            var e = queue.Dequeue();
            foreach (var con in Conns(e).Where(x => x.ConnectorType == ConnectorType.End && x.IsConnected))
                foreach (Connector r in con.AllRefs)
                {
                    var o = r.Owner;
                    if (o == null || seen.Contains(o.Id.Value)) continue;
                    if (e is FamilyInstance && o is MEPCurve m && InLine(m)) { seen.Add(o.Id.Value); list.Add(o.Id); queue.Enqueue(o); }
                    else if (e is MEPCurve && o is FamilyInstance fi && Inline(fi)) { seen.Add(o.Id.Value); queue.Enqueue(o); }
                }
        }
        return list;
    }

    /// <summary>
    /// Give the accessory the run's size through the family parameters its connectors' sizes are tied to (instance
    /// parameters only: a type parameter would change every accessory of the type). Null when it took, else why not.
    /// </summary>
    private static string SizeToRun(FamilyInstance fi, MEPCurve run, Domain domain)
    {
        var doc = fi.Document;
        bool round = false; double d = 0, w = 0, h = 0;
        try { d = run.Diameter; round = d > 0; } catch { /* not round */ }
        if (!round) { try { w = run.Width; h = run.Height; } catch { return "the run has no width and height"; } }
        var notes = new List<string>();
        var done = new HashSet<long>();
        int set = 0;
        foreach (var con in Conns(fi).Where(x => x.ConnectorType == ConnectorType.End && x.Domain == domain))
        {
            if ((con.Shape == ConnectorProfileType.Round) != round) { notes.Add($"the accessory's connector is {con.Shape}, the run is {(round ? "round" : "not round")}"); continue; }
            if (!(con.GetMEPConnectorInfo() is MEPFamilyConnectorInfo info)) { notes.Add("no connector information"); continue; }
            var wanted = round
                ? new[] { (BuiltInParameter.CONNECTOR_DIAMETER, d), (BuiltInParameter.CONNECTOR_RADIUS, d / 2) }
                : new[] { (BuiltInParameter.CONNECTOR_WIDTH, w), (BuiltInParameter.CONNECTOR_HEIGHT, h) };
            foreach (var (bip, value) in wanted)
            {
                ElementId pid;
                try { pid = info.GetAssociateFamilyParameterId(new ElementId(bip)); } catch { continue; }
                if (pid == null || pid == ElementId.InvalidElementId || !done.Add(pid.Value)) continue;
                var prm = fi.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == pid);
                if (prm == null)
                {
                    var tp = fi.Symbol?.Parameters.Cast<Parameter>().FirstOrDefault(x => x.Id == pid);
                    notes.Add(tp != null ? $"its size '{tp.Definition.Name}' is a type parameter (duplicate the type for this size)" : "the connector's size parameter was not found");
                    continue;
                }
                if (prm.IsReadOnly) { notes.Add($"'{prm.Definition.Name}' is read-only (a formula drives it)"); continue; }
                if (prm.Set(value)) set++;
            }
        }
        doc.Regenerate();
        if (set == 0 && notes.Count == 0) notes.Add("its connector sizes are not tied to family parameters");
        return notes.Count == 0 ? null : string.Join("; ", notes.Distinct());
    }

    private static string SizeText(Connector x)
    {
        try
        {
            if (x.Shape == ConnectorProfileType.Round) return $"{U.Out(2 * x.Radius):0.#} dia";
            return $"{U.Out(x.Width):0.#}x{U.Out(x.Height):0.#}";
        }
        catch { return "?"; }
    }

    /// <summary>A point on the run nearest the one given ([x, y] is matched in plan, [x, y, z] in 3D).</summary>
    private static (MEPCurve, Spot) AtPoint(Obj it, List<MEPCurve> runs, string what)
    {
        bool plan = it["point"] is List<object> raw && raw.Count == 2;
        var given = U.Pt(it["point"], $"{what} 'point'");
        MEPCurve best = null; XYZ bestAt = null; double bestOff = double.MaxValue;
        foreach (var r in runs)
        {
            var l = RunLine(r);
            XYZ a = l.GetEndPoint(0), b = l.GetEndPoint(1), d = b - a;
            double t;
            if (plan)
            {
                var d2 = new XYZ(d.X, d.Y, 0);
                if (d2.GetLength() < 1e-6) throw new ArgumentException($"{what}: run {r.Id.Value} is vertical: give the point as [x, y, z].");
                t = (new XYZ(given.X - a.X, given.Y - a.Y, 0)).DotProduct(d2) / d2.DotProduct(d2);
            }
            else t = (given - a).DotProduct(d) / d.DotProduct(d);
            if (t <= 0 || t >= 1) continue;
            var on = a + d * t;
            double off = plan ? new XYZ(on.X - given.X, on.Y - given.Y, 0).GetLength() : on.DistanceTo(given);
            if (off < bestOff) { bestOff = off; best = r; bestAt = on; }
        }
        if (best == null) throw new ArgumentException($"{what}: the point is beyond the ends of run {runs[0].Id.Value}" + (runs.Count > 1 ? $" and of the {runs.Count - 1} piece(s) in line with it through accessories" : "") + ".");
        double tol = Math.Max(MepRead.Size(best).feet / 2, U.Mm(10));
        if (bestOff > tol)
            throw new ArgumentException($"{what}: the point is {U.Out(bestOff):0} {U.Name} off the centreline of run {best.Id.Value} (allowed: half its size). Give a point on the run.");
        return (best, new Spot { At = bestAt, How = plan ? "point (in plan)" : "point" });
    }

    /// <summary>
    /// Where a run crosses a wall (its centre plane, so an opening already cut there does not matter), a slab / roof /
    /// ceiling (half its depth), or any other element (the middle of the run's centreline inside its solids, else its box).
    /// </summary>
    private static (MEPCurve, Spot) AtCrossing(Document hdoc, Transform htf, ElementId hostId, List<MEPCurve> runs, string what)
    {
        var host = hdoc.GetElement(hostId) ?? throw new ArgumentException($"{what}: no element {hostId.Value} in {hdoc.Title}.");
        var box = host.get_BoundingBox(null) ?? throw new ArgumentException($"{what}: element {hostId.Value} has no geometry.");
        var inv = htf.Inverse;
        foreach (var r in runs)
        {
            var l = RunLine(r);
            XYZ a = inv.OfPoint(l.GetEndPoint(0)), b = inv.OfPoint(l.GetEndPoint(1)), d = b - a;
            XYZ hit = null; string how = null; double inside = 0;
            if (host is Wall w && w.Location is LocationCurve wl && wl.Curve is Line wline)
            {
                var n = XYZ.BasisZ.CrossProduct(wline.Direction).Normalize();
                double den = n.DotProduct(d);
                if (Math.Abs(den) < 1e-9) continue;
                double t = n.DotProduct(wline.GetEndPoint(0) - a) / den;
                if (t <= 0 || t >= 1) continue;
                var q = a + d * t;
                double s = (q - wline.GetEndPoint(0)).DotProduct(wline.Direction);
                if (s < 0 || s > wline.Length || q.Z < box.Min.Z - 1e-6 || q.Z > box.Max.Z + 1e-6) continue;
                hit = q; how = "wall centre plane"; inside = w.Width / Math.Abs(n.DotProduct(d.Normalize()));
            }
            else if (host is Floor || host is RoofBase || host is Ceiling)
            {
                if (Math.Abs(d.Z) < 1e-9) continue;
                double z = (box.Min.Z + box.Max.Z) / 2, t = (z - a.Z) / d.Z;
                if (t <= 0 || t >= 1) continue;
                var q = a + d * t;
                if (q.X < box.Min.X || q.X > box.Max.X || q.Y < box.Min.Y || q.Y > box.Max.Y) continue;
                hit = q; how = "mid-depth of the slab"; inside = (box.Max.Z - box.Min.Z) / Math.Abs(d.Normalize().Z);
            }
            else
            {
                var opt = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
                foreach (var solid in Geo.Solids(host))
                {
                    SolidCurveIntersection x;
                    try { x = solid.IntersectWithCurve(Line.CreateBound(a, b), opt); } catch { continue; }
                    for (int k = 0; k < x.SegmentCount && hit == null; k++)
                    {
                        var seg = x.GetCurveSegment(k);
                        if (seg.Length > 0.003) { hit = (seg.GetEndPoint(0) + seg.GetEndPoint(1)) / 2; how = "middle of the run inside the element"; inside = seg.Length; }
                    }
                    if (hit != null) break;
                }
            }
            if (hit == null) continue;
            var at = htf.OfPoint(hit);
            return (r, new Spot
            {
                At = at, How = how, Inside = inside,
                Crossing = new Obj { ["element"] = hostId.Value, ["category"] = host.Category?.Name, ["type"] = Describe.TypeLabel(hdoc, host), ["lengthInside"] = U.Out(inside) },
            });
        }
        throw new ArgumentException($"{what}: run {runs[0].Id.Value} does not cross element {hostId.Value} ({host.Category?.Name})"
            + (runs.Count > 1 ? " on any of the pieces it was split into" : "") + ". mep_crossings lists where runs cross structure.");
    }

    /// <summary>
    /// The accessory on the run: first as Revit's own in-line insert (the run as host: split, sized, connected); if Revit
    /// refuses that, split the run, place the accessory free, line it up with the run, trim both pieces to its connectors
    /// and connect them. The second way keeps the family's own size: a size different from the run's is reported.
    /// </summary>
    private static (FamilyInstance acc, string how, string err) Insert(EditContext c, MEPCurve run, FamilySymbol symbol, XYZ at)
    {
        var doc = c.Doc;
        string firstErr;
        using (var st = new SubTransaction(doc))
        {
            st.Start();
            try
            {
                var fi = doc.Create.NewFamilyInstance(at, symbol, run, RStruct.StructuralType.NonStructural);
                doc.Regenerate();
                var ends = Conns(fi).Where(x => x.ConnectorType == ConnectorType.End).ToList();
                if (ends.Count < 2) throw new InvalidOperationException($"the family has {ends.Count} end connector(s), an in-line accessory needs two");
                if (ends.Count(x => x.IsConnected) < 2) throw new InvalidOperationException("Revit placed it but did not connect it into the run");
                st.Commit();
                return (fi, "inserted in the run (Revit)", null);
            }
            catch (Exception ex) { st.RollBack(); firstErr = ex.Message; }
        }

        using (var st = new SubTransaction(doc))
        {
            st.Start();
            try
            {
                bool pipe = run is Pipe;
                var line = RunLine(run);
                var dir = line.Direction.Normalize();
                var otherId = pipe ? PlumbingUtils.BreakCurve(doc, run.Id, at) : MechanicalUtils.BreakCurve(doc, run.Id, at);
                doc.Regenerate();
                var level = doc.GetElement(run.ReferenceLevel?.Id ?? run.LevelId) as Level
                            ?? throw new InvalidOperationException("the run has no reference level");
                var fi = doc.Create.NewFamilyInstance(at, symbol, level, RStruct.StructuralType.NonStructural);
                doc.Regenerate();
                var domain = pipe ? Domain.DomainPiping : Domain.DomainHvac;
                string sizeNote = SizeToRun(fi, run, domain);
                var ends = Conns(fi).Where(x => x.ConnectorType == ConnectorType.End && x.Domain == domain).ToList();
                if (ends.Count != 2) throw new InvalidOperationException($"the family has {ends.Count} {(pipe ? "pipe" : "duct")} end connector(s), an in-line accessory needs two");
                // line the accessory's connector axis up with the run, then centre it on the point
                var axis = (ends[1].Origin - ends[0].Origin);
                if (axis.GetLength() < 1e-6) axis = ends[0].CoordinateSystem.BasisZ;
                axis = axis.Normalize();
                var mid = (ends[0].Origin + ends[1].Origin) / 2;
                double cos = Math.Max(-1, Math.Min(1, axis.DotProduct(dir)));
                if (Math.Abs(cos) < 1 - 1e-9)
                {
                    var rot = axis.CrossProduct(dir).Normalize();
                    ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateUnbound(mid, rot), Math.Acos(cos));
                    doc.Regenerate();
                }
                ends = Conns(fi).Where(x => x.ConnectorType == ConnectorType.End && x.Domain == domain).ToList();
                mid = (ends[0].Origin + ends[1].Origin) / 2;
                // rectangular: turn it about the run's axis so its width lies along the run's width
                var rc = Conns(run).Where(x => x.ConnectorType == ConnectorType.End).OrderBy(x => x.Origin.DistanceTo(at)).FirstOrDefault();
                if (rc != null && rc.Shape != ConnectorProfileType.Round && ends[0].Shape != ConnectorProfileType.Round)
                {
                    XYZ Flat(XYZ v) => (v - dir * v.DotProduct(dir)).Normalize();
                    XYZ ax = Flat(ends[0].CoordinateSystem.BasisX), rx = Flat(rc.CoordinateSystem.BasisX);
                    double ang = Math.Atan2(dir.DotProduct(ax.CrossProduct(rx)), ax.DotProduct(rx));
                    if (ang > Math.PI / 2) ang -= Math.PI; else if (ang < -Math.PI / 2) ang += Math.PI;   // width either way round is the same
                    if (Math.Abs(ang) > 1e-6)
                    {
                        ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateUnbound(mid, dir), ang);
                        doc.Regenerate();
                        ends = Conns(fi).Where(x => x.ConnectorType == ConnectorType.End && x.Domain == domain).ToList();
                        mid = (ends[0].Origin + ends[1].Origin) / 2;
                    }
                }
                if (!mid.IsAlmostEqualTo(at)) { ElementTransformUtils.MoveElement(doc, fi.Id, at - mid); doc.Regenerate(); }
                ends = Conns(fi).Where(x => x.ConnectorType == ConnectorType.End && x.Domain == domain).ToList();
                // trim each piece back to the accessory connector on its side and connect it
                foreach (var pieceId in new[] { run.Id, otherId })
                {
                    var piece = (MEPCurve)doc.GetElement(pieceId);
                    var pl = RunLine(piece);
                    bool startAtBreak = pl.GetEndPoint(0).DistanceTo(at) < pl.GetEndPoint(1).DistanceTo(at);
                    var far = startAtBreak ? pl.GetEndPoint(1) : pl.GetEndPoint(0);
                    var con = ends.OrderBy(x => x.Origin.DistanceTo(far)).First();
                    if (con.Origin.DistanceTo(far) < 0.01) throw new InvalidOperationException("the accessory is longer than the run piece beside it");
                    ((LocationCurve)piece.Location).Curve = startAtBreak ? Line.CreateBound(con.Origin, far) : Line.CreateBound(far, con.Origin);
                    doc.Regenerate();
                    var pc = Conns(piece).Where(x => x.ConnectorType == ConnectorType.End && !x.IsConnected).OrderBy(x => x.Origin.DistanceTo(con.Origin)).First();
                    pc.ConnectTo(con);
                }
                doc.Regenerate();
                st.Commit();
                if (sizeNote != null) c.Warn($"Accessory {fi.Id.Value} keeps its own size: {sizeNote}.");
                return (fi, $"split, placed and connected ({(sizeNote == null ? "sized to the run" : "own size")}; Revit's in-line insert: {firstErr})", null);
            }
            catch (Exception ex) { st.RollBack(); return (null, null, $"in-line insert: {firstErr}; split and connect: {ex.Message}"); }
        }
    }

    // ---------------------------------------------------------------- sleeves

    private static readonly BuiltInCategory[] SleeveCats =
    {
        BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting,
    };

    /// <summary>
    /// A value for a sleeve parameter: a number or text as given, or a length measured at this crossing: crossing_length
    /// (the run inside the element), run_diameter, run_outside_diameter, run_width, run_height, each optionally "+ n" or
    /// "- n" in request units (the engineer's clearance). Measured values are returned in request units.
    /// </summary>
    private static object SleeveValue(object v, MEPCurve run, Spot spot, string what)
    {
        if (!(v is string text)) return v;
        string t = text.Replace(" ", "");
        string[] tokens = { "crossing_length", "run_outside_diameter", "run_diameter", "run_width", "run_height" };
        string tok = tokens.FirstOrDefault(k => t.StartsWith(k, StringComparison.OrdinalIgnoreCase));
        if (tok == null) return v;
        double feet;
        switch (tok)
        {
            case "crossing_length": feet = spot.Inside; if (feet <= 0) throw new ArgumentException($"{what}: no crossing length (the item has no 'crossing')."); break;
            case "run_outside_diameter":
                feet = Find.Builtin(run, "RBS_PIPE_OUTER_DIAMETER")?.AsDouble() ?? 0;
                if (feet <= 0) try { feet = run.Diameter; } catch { feet = 0; }
                if (feet <= 0) throw new ArgumentException($"{what}: run {run.Id.Value} is not round."); break;
            case "run_diameter": try { feet = run.Diameter; } catch { feet = 0; } if (feet <= 0) throw new ArgumentException($"{what}: run {run.Id.Value} is not round."); break;
            case "run_width": try { feet = run.Width; } catch { feet = 0; } if (feet <= 0) throw new ArgumentException($"{what}: run {run.Id.Value} has no width."); break;
            default: try { feet = run.Height; } catch { feet = 0; } if (feet <= 0) throw new ArgumentException($"{what}: run {run.Id.Value} has no height."); break;
        }
        double value = U.Out(feet);
        string rest = t.Substring(tok.Length);
        if (rest.Length > 0)
        {
            if ((rest[0] != '+' && rest[0] != '-') || !double.TryParse(rest.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out double add))
                throw new ArgumentException($"{what}: '{text}' must be {tok}, {tok} + n or {tok} - n (n in {U.Name}).");
            value += rest[0] == '+' ? add : -add;
        }
        if (value <= 0) throw new ArgumentException($"{what}: '{text}' gives {value:0.#} {U.Name}.");
        return value;
    }

    /// <summary>
    /// Sleeves of the family type the engineer gives where runs cross walls / slabs / beams (host or link): centred on the
    /// crossing (wall centre plane, slab mid-depth), turned so the family's own axis (x, y or z) lies along the run, with the
    /// parameter values given; a value can be a length measured here (crossing_length, run_outside_diameter + clearance ...).
    /// The run is not cut or connected; crossing_opening cuts the opening.
    /// </summary>
    private static object SleevePlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var symbol = (FamilySymbol)Find.TypeOf(doc, NeedStr(p, "type"), null, typeof(FamilySymbol));
        var cat = symbol.Category?.BuiltInCategory;
        if (cat == null || !SleeveCats.Contains(cat.Value))
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is a {symbol.Category?.Name}; sleeve_place takes generic models, pipe / duct accessories or fittings, specialty or mechanical equipment.");
        var placement = symbol.Family.FamilyPlacementType;
        if (placement != FamilyPlacementType.OneLevelBased && placement != FamilyPlacementType.WorkPlaneBased)
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is a {placement} family; sleeve_place places level-based or work-plane-based families (not hosted on a face).");
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        string axisName = Choice(p, "axis", "z", "x", "y", "z");
        var values = Has(p, "set") ? (p["set"] as Obj ?? throw new ArgumentException("'set' must be an object of parameter name: value.")) : new Obj();
        Document hdoc = doc; Transform htf = Transform.Identity; string linkName = null;
        if (Has(p, "crossing_link"))
        {
            var inst = LinkScope.Resolve(doc, NeedRef(p, "crossing_link"));
            hdoc = inst.GetLinkDocument() ?? throw new ArgumentException($"Link '{inst.Name}' is not loaded.");
            htf = inst.GetTotalTransform(); linkName = inst.Name;
        }
        var rows = new List<Obj>();
        var items = ObjList(p, "items", 500);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            string what = $"Item {i + 1}";
            var run = Find.Elem(doc, NeedId(it, "run")) as MEPCurve
                      ?? throw new ArgumentException($"{what}: 'run' must be a duct, pipe, cable tray or conduit.");
            var (piece, spot) = AtCrossing(hdoc, htf, NeedId(it, "crossing"), Chain(doc, run).Select(id => (MEPCurve)doc.GetElement(id)).ToList(), what);
            if (linkName != null) spot.Crossing["link"] = linkName;
            var dir = RunLine(piece).Direction.Normalize();
            var level = doc.GetElement(piece.ReferenceLevel?.Id ?? piece.LevelId) as Level
                        ?? throw new ArgumentException($"{what}: run {piece.Id.Value} has no reference level.");
            var fi = doc.Create.NewFamilyInstance(spot.At, symbol, level, RStruct.StructuralType.NonStructural);
            doc.Regenerate();
            // sizes first (they change the box the sleeve is centred by)
            var set = new Obj();
            foreach (var kv in values)
            {
                var v = SleeveValue(kv.Value, piece, spot, $"{what} '{kv.Key}'");
                Describe.SetValue(Find.Param(fi, kv.Key), v, $"{what} '{kv.Key}'");
                set[kv.Key] = v;
            }
            doc.Regenerate();
            // turn the family's own axis onto the run
            var tf = fi.GetTransform();
            var axis = (axisName == "x" ? tf.BasisX : axisName == "y" ? tf.BasisY : tf.BasisZ).Normalize();
            if (axis.DotProduct(dir) < 0) dir = -dir;
            double ang = Math.Acos(Math.Max(-1, Math.Min(1, axis.DotProduct(dir))));
            if (ang > 1e-6)
            {
                var rot = axis.CrossProduct(dir);
                if (Math.Abs(rot.Normalize().Z) < 1 - 1e-6 && placement == FamilyPlacementType.OneLevelBased)
                    throw new ArgumentException($"{what}: the family's {axisName} axis would have to tilt to follow run {piece.Id.Value}; a level-based family turns only about the vertical. "
                        + (Math.Abs(dir.Z) > 0.99 ? "For a vertical run use axis z (a family drawn standing up)." : "For a horizontal run use a family drawn lying along x or y and give that axis."));
                ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateUnbound(spot.At, rot.Normalize()), ang);
                doc.Regenerate();
            }
            // centre it on the crossing
            var bb = fi.get_BoundingBox(null);
            if (bb != null)
            {
                var mid = (bb.Min + bb.Max) / 2;
                if (!mid.IsAlmostEqualTo(spot.At)) { ElementTransformUtils.MoveElement(doc, fi.Id, spot.At - mid); doc.Regenerate(); }
            }
            rows.Add(new Obj
            {
                ["id"] = fi.Id.Value, ["run"] = run.Id.Value, ["piece"] = piece.Id.Value, ["at"] = U.Xyz(spot.At), ["position"] = spot.How,
                ["crossing"] = spot.Crossing, ["runSize"] = RunSize(piece), ["set"] = set, ["turnedDeg"] = Math.Round(ang * 180 / Math.PI, 2),
            });
        }
        return new
        {
            type = Find.TypeLabel(symbol), axis = axisName, placed = rows.Count, items = rows,
            note = "Sleeves of the type given, centred where each run crosses the element, with the values given (measured lengths in request units). "
                 + "Sleeve sizes and clearances are the engineer's; the run is not cut (crossing_opening cuts openings).",
        };
    }

    // ---------------------------------------------------------------- insulation and lining

    private enum Net { Duct, Pipe, None }

    private static Net NetOf(Element e)
    {
        if (e is Duct || e is FlexDuct) return Net.Duct;
        if (e is Pipe || e is FlexPipe) return Net.Pipe;
        var cat = e.Category?.BuiltInCategory;
        if (cat == BuiltInCategory.OST_DuctFitting || cat == BuiltInCategory.OST_DuctAccessory) return Net.Duct;
        if (cat == BuiltInCategory.OST_PipeFitting || cat == BuiltInCategory.OST_PipeAccessory) return Net.Pipe;
        return Net.None;
    }

    /// <summary>The elements given, or the ducts / pipes / fittings / accessories of a system (its network).</summary>
    private static List<Element> Targets(EditContext c, Obj p)
    {
        var doc = c.Doc;
        if (Has(p, "elements") == Has(p, "system")) throw new ArgumentException("Give either 'elements' or 'system'.");
        if (Has(p, "elements")) return c.Elements(p, "elements", 5000);
        var sys = c.Editable(Find.Elem(doc, NeedId(p, "system")));
        ElementSet net = sys is MechanicalSystem ms ? ms.DuctNetwork : sys is PipingSystem ps ? ps.PipingNetwork
            : throw new ArgumentException("'system' must be the id of a duct or piping system (mep_systems lists them).");
        var list = net.Cast<Element>().Where(e => NetOf(e) != Net.None).Select(c.Editable).ToList();
        if (list.Count == 0) throw new ArgumentException($"System {sys.Id.Value} '{sys.Name}' has no ducts, pipes, fittings or accessories in its network yet.");
        return list;
    }

    private static T TypeNamed<T>(Document doc, string spec, string what) where T : ElementType
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
        if (all.Count == 0) throw new ArgumentException($"This model has no {what} types. Transfer them from a template first (standards_copy or Revit's Transfer Project Standards).");
        return all.FirstOrDefault(x => x.Id.Value.ToString(CultureInfo.InvariantCulture) == spec || string.Equals(x.Name, spec, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No {what} type '{spec}'. Available: {string.Join(", ", all.Select(x => x.Name).OrderBy(x => x).Take(40))}.");
    }

    private static ICollection<ElementId> Existing(Document doc, ElementId host, bool lining)
    {
        // Revit throws for lining on the pipe side: none there
        try { return lining ? InsulationLiningBase.GetLiningIds(doc, host) : InsulationLiningBase.GetInsulationIds(doc, host); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { return new List<ElementId>(); }
    }

    /// <summary>
    /// Duct / pipe insulation or duct lining of the type and thickness the engineer gives, on ducts, pipes, their fittings
    /// and accessories (or a whole system's network). Elements that already have it are listed, or changed with replace.
    /// </summary>
    private static object InsulationAdd(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", null, "insulation", "lining");
        bool lining = kind == "lining";
        double thickness = U.In(NeedDbl(p, "thickness"));
        if (thickness <= 0) throw new ArgumentException("'thickness' must be positive.");
        string spec = NeedStr(p, "type");
        bool replace = Flag(p, "replace", false);
        var targets = Targets(c, p);

        ElementId ductType = null, pipeType = null;
        ElementId TypeFor(Net n)
        {
            if (n == Net.Duct) return ductType ??= lining ? TypeNamed<DuctLiningType>(doc, spec, "duct lining").Id : TypeNamed<DuctInsulationType>(doc, spec, "duct insulation").Id;
            return pipeType ??= TypeNamed<PipeInsulationType>(doc, spec, "pipe insulation").Id;
        }

        var made = new List<Obj>(); var changed = new List<Obj>(); var skipped = new List<string>();
        foreach (var e in targets)
        {
            var net = NetOf(e);
            if (net == Net.None) { skipped.Add($"{e.Id.Value}: {e.Category?.Name} (not a duct, pipe, fitting or accessory)"); continue; }
            if (lining && net == Net.Pipe) { skipped.Add($"{e.Id.Value}: lining is for ducts only"); continue; }
            if (lining && (e is FlexDuct)) { skipped.Add($"{e.Id.Value}: flex ducts take no lining"); continue; }
            var typeId = TypeFor(net);
            var have = Existing(doc, e.Id, lining);
            if (have.Count > 0)
            {
                if (!replace) { skipped.Add($"{e.Id.Value}: already has {kind} {string.Join(", ", have.Select(x => x.Value))} (replace: true changes it)"); continue; }
                foreach (var hid in have)
                {
                    var ins = (InsulationLiningBase)c.Editable(doc.GetElement(hid));
                    double was = ins.Thickness; string wasType = doc.GetElement(ins.GetTypeId())?.Name;
                    if (ins.GetTypeId() != typeId) ins.ChangeTypeId(typeId);
                    ins.Thickness = thickness;
                    changed.Add(new Obj { ["host"] = e.Id.Value, ["id"] = hid.Value, ["was"] = $"{wasType} {U.Out(was):0.#}" });
                }
                continue;
            }
            InsulationLiningBase created;
            try
            {
                created = lining ? (InsulationLiningBase)DuctLining.Create(doc, e.Id, typeId, thickness)
                        : net == Net.Duct ? DuctInsulation.Create(doc, e.Id, typeId, thickness)
                        : PipeInsulation.Create(doc, e.Id, typeId, thickness);
            }
            catch (Exception ex) { skipped.Add($"{e.Id.Value}: Revit refused it ({ex.Message})"); continue; }
            made.Add(new Obj { ["host"] = e.Id.Value, ["id"] = created.Id.Value, ["hostCategory"] = e.Category?.Name });
        }
        if (made.Count == 0 && changed.Count == 0)
            throw new ArgumentException($"No {kind} was added: " + string.Join("; ", skipped.Take(30)) + (skipped.Count > 30 ? "; ..." : ""));
        if (skipped.Count > 0) c.Warn($"Left out {skipped.Count}: " + string.Join("; ", skipped.Take(30)) + (skipped.Count > 30 ? "; ..." : "") + ".");
        doc.Regenerate();
        return new
        {
            kind, type = spec, thickness = U.Out(thickness), created = made.Count, changedCount = changed.Count, skippedCount = skipped.Count,
            items = made.Take(500).ToList(), changed = changed.Take(500).ToList(), skipped = skipped.Take(200).ToList(),
            truncated = made.Count > 500 || changed.Count > 500 || skipped.Count > 200,
            note = "Type and thickness are the engineer's values; nothing is selected or sized here.",
        };
    }

    /// <summary>Remove the insulation or lining of the elements given (or of a system's network).</summary>
    private static object InsulationRemove(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", null, "insulation", "lining");
        bool lining = kind == "lining";
        var removed = new List<long>(); var none = new List<long>();
        foreach (var e in Targets(c, p))
        {
            var have = Existing(doc, e.Id, lining);
            if (have.Count == 0) { none.Add(e.Id.Value); continue; }
            foreach (var hid in have) { c.Editable(doc.GetElement(hid)); removed.Add(hid.Value); }
        }
        if (removed.Count == 0) throw new ArgumentException($"None of the {none.Count} element(s) has {kind}.");
        doc.Delete(removed.Select(v => new ElementId(v)).ToList());
        return new { kind, removed = removed.Count, ids = removed.Take(500).ToList(), withoutAny = none.Take(200).ToList(), truncated = removed.Count > 500 || none.Count > 200 };
    }
}
