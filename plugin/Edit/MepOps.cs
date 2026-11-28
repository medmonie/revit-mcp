using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpBridge;

/// <summary>Roadmap phase 13 (edits, first part): straight ducts, pipes, cable trays and conduits between given points, with given types and sizes.</summary>
internal static class MepOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["mep_run_create"] = RunCreate;
        ops["mep_route"] = Route;
        ops["mep_connect"] = Connect;
        ops["mep_system_create"] = SystemCreate;
        ops["air_terminal_place"] = AirTerminalPlace;
        ops["mep_system_add"] = SystemAdd;
        ops["mep_system_remove"] = SystemRemove;
    }

    private static T Pick<T>(Document doc, string spec, string what) where T : Element
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
        if (all.Count == 0) throw new ArgumentException($"This model has no {what}. Load or transfer one first.");
        if (spec == null) return all.OrderBy(x => x.Name).First();
        return all.FirstOrDefault(x => x.Id.Value.ToString(CultureInfo.InvariantCulture) == spec || string.Equals(x.Name, spec, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No {what} '{spec}'. Available: {string.Join(", ", all.Select(x => x.Name).Take(30))}.");
    }

    private static void SetSize(EditContext c, Element e, string bip, double? value)
    {
        if (value is double v)
        {
            if (v <= 0) throw new ArgumentException("Sizes must be positive.");
            c.SetBuiltin(e, bip, U.In(v));
        }
    }

    private static object RunCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", null, "duct", "pipe", "cable_tray", "conduit");
        var level = Find.NeedLevel(doc, p);
        string typeSpec = Str(p, "type"), systemSpec = Str(p, "system");
        ElementId typeId, systemId = ElementId.InvalidElementId;
        string typeName, systemName = null;
        switch (kind)
        {
            case "duct": { var t = Pick<DuctType>(doc, typeSpec, "duct type"); typeId = t.Id; typeName = t.Name; var s = Pick<MechanicalSystemType>(doc, systemSpec, "duct system type"); systemId = s.Id; systemName = s.Name; break; }
            case "pipe": { var t = Pick<PipeType>(doc, typeSpec, "pipe type"); typeId = t.Id; typeName = t.Name; var s = Pick<PipingSystemType>(doc, systemSpec, "piping system type"); systemId = s.Id; systemName = s.Name; break; }
            case "cable_tray": { var t = Pick<CableTrayType>(doc, typeSpec, "cable tray type"); typeId = t.Id; typeName = t.Name; break; }
            default: { var t = Pick<ConduitType>(doc, typeSpec, "conduit type"); typeId = t.Id; typeName = t.Name; break; }
        }
        if (systemSpec != null && (kind == "cable_tray" || kind == "conduit")) throw new ArgumentException("'system' is for ducts and pipes.");
        double? dia = Dbl(p, "diameter"), w = Dbl(p, "width"), h = Dbl(p, "height");
        var items = new List<Obj>();
        int n = 0;
        foreach (var seg in ObjList(p, "runs", 500))
        {
            n++;
            XYZ a = U.NeedPt(seg, "start", level.Elevation), b = U.NeedPt(seg, "end", level.Elevation);
            U.Segment(a, b, $"run {n}");
            MEPCurve run;
            switch (kind)
            {
                case "duct": run = Duct.Create(doc, systemId, typeId, level.Id, a, b); break;
                case "pipe": run = Pipe.Create(doc, systemId, typeId, level.Id, a, b); break;
                case "cable_tray": run = CableTray.Create(doc, typeId, a, b, level.Id); break;
                default: run = Conduit.Create(doc, typeId, a, b, level.Id); break;
            }
            switch (kind)
            {
                case "duct":
                    bool round = false;
                    try { round = run.Diameter > 0; } catch { /* rectangular or oval */ }
                    if (round) { if (w != null || h != null) throw new ArgumentException($"Duct type '{typeName}' is round: give 'diameter'."); SetSize(c, run, "RBS_CURVE_DIAMETER_PARAM", dia); }
                    else { if (dia != null) throw new ArgumentException($"Duct type '{typeName}' is not round: give 'width' and 'height'."); SetSize(c, run, "RBS_CURVE_WIDTH_PARAM", w); SetSize(c, run, "RBS_CURVE_HEIGHT_PARAM", h); }
                    break;
                case "pipe": SetSize(c, run, "RBS_PIPE_DIAMETER_PARAM", dia); break;
                case "cable_tray": SetSize(c, run, "RBS_CABLETRAY_WIDTH_PARAM", w); SetSize(c, run, "RBS_CABLETRAY_HEIGHT_PARAM", h); break;
                default: SetSize(c, run, "RBS_CONDUIT_DIAMETER_PARAM", dia); break;
            }
            items.Add(new Obj { ["id"] = run.Id.Value, ["start"] = U.Xyz(a), ["end"] = U.Xyz(b) });
        }
        doc.Regenerate();
        foreach (var it in items) it["size"] = Find.Builtin(doc.GetElement(new ElementId((long)it["id"])), "RBS_CALCULATED_SIZE")?.AsString();
        return new { kind, type = typeName, system = systemName, level = level.Name, created = items.Count, items,
                     note = "Straight runs only; no fittings are added between runs. Sizes are the ones given (or the type's default)." };
    }

    // ---------------------------------------------------------------- phase 18: connected runs, connections, systems

    private static Obj ToObj(object o) => o as Obj ?? o.GetType().GetProperties().ToDictionary(pr => pr.Name, pr => pr.GetValue(o));

    internal static IEnumerable<Connector> Conns(Element e) =>
        (e is MEPCurve c ? c.ConnectorManager : (e as FamilyInstance)?.MEPModel?.ConnectorManager)?.Connectors.Cast<Connector>() ?? Enumerable.Empty<Connector>();

    internal static string ConnClass(Connector x) =>
        x.Domain == Domain.DomainHvac ? x.DuctSystemType.ToString() : x.Domain == Domain.DomainPiping ? x.PipeSystemType.ToString() : x.Domain.ToString();

    /// <summary>The open duct / pipe connector a route starts from: of the element given, of the classification given, else
    /// the one nearest the first path point.</summary>
    private static Connector StartConnector(EditContext c, Obj from, string kind, XYZ firstPoint)
    {
        var el = c.Editable(Find.Elem(c.Doc, NeedId(from, "element")));
        var domain = DomainOf(kind);
        var open = Conns(el).Where(x => x.ConnectorType == ConnectorType.End && !x.IsConnected && x.Domain == domain).ToList();
        if (open.Count == 0) throw new ArgumentException($"Element {el.Id.Value} has no open {kind} connector (mep_connectors lists its connectors).");
        if (Str(from, "classification") is string want)
        {
            var match = open.Where(x => string.Equals(ConnClass(x), want, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0)
                throw new ArgumentException($"Element {el.Id.Value} has no open {kind} connector of classification '{want}': its open ones are {string.Join(", ", open.Select(ConnClass).Distinct())}.");
            open = match;
        }
        return open.OrderBy(x => x.Origin.DistanceTo(firstPoint)).First();
    }

    private static Domain DomainOf(string kind) =>
        kind == "duct" ? Domain.DomainHvac : kind == "pipe" ? Domain.DomainPiping : Domain.DomainCableTrayConduit;

    private static string KindOf(MEPCurve m) => m is Duct ? "duct" : m is Pipe ? "pipe" : m is CableTray ? "cable_tray" : m is Conduit ? "conduit"
        : throw new ArgumentException($"Element {m.Id.Value} is not a straight duct, pipe, cable tray or conduit.");

    private static ElementId RunType(Document doc, string kind, string spec) =>
        kind == "duct" ? Pick<DuctType>(doc, spec, "duct type").Id : kind == "pipe" ? Pick<PipeType>(doc, spec, "pipe type").Id
        : kind == "cable_tray" ? Pick<CableTrayType>(doc, spec, "cable tray type").Id : Pick<ConduitType>(doc, spec, "conduit type").Id;

    /// <summary>
    /// A run that starts on a connector: ducts and pipes are drawn from the connector (they take its size and turn);
    /// cable trays and conduits are drawn from its origin, given its size and connected to it.
    /// </summary>
    private static MEPCurve FromConnector(Document doc, string kind, ElementId typeId, Level level, Connector con, XYZ end)
    {
        switch (kind)
        {
            case "duct": return Duct.Create(doc, typeId, level.Id, con, end);
            case "pipe": return Pipe.Create(doc, typeId, level.Id, con, end);
            case "cable_tray":
            {
                var t = CableTray.Create(doc, typeId, con.Origin, end, level.Id);
                try { t.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.Set(con.Width); t.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.Set(con.Height); } catch { /* the type's size stays */ }
                doc.Regenerate();
                Nearest(t, con.Origin)?.ConnectTo(con);
                return t;
            }
            default:
            {
                var t = Conduit.Create(doc, typeId, con.Origin, end, level.Id);
                try { t.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.Set(2 * con.Radius); } catch { /* the type's size stays */ }
                doc.Regenerate();
                Nearest(t, con.Origin)?.ConnectTo(con);
                return t;
            }
        }
    }

    /// <summary>
    /// Split a straight run at a point; the new piece (from the point to the old end) keeps what that end was connected to.
    /// Ducts and pipes through Revit's BreakCurve; cable trays and conduits (no BreakCurve) by a copy.
    /// </summary>
    private static ElementId SplitRun(Document doc, MEPCurve run, XYZ at)
    {
        if (run is Pipe) return PlumbingUtils.BreakCurve(doc, run.Id, at);
        if (run is Duct) return MechanicalUtils.BreakCurve(doc, run.Id, at);
        var line = (Line)((LocationCurve)run.Location).Curve;
        XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
        var endB = Conns(run).Where(x => x.ConnectorType == ConnectorType.End).OrderBy(x => x.Origin.DistanceTo(b)).First();
        var others = endB.IsConnected ? endB.AllRefs.Cast<Connector>().Where(r => r.Owner.Id != run.Id).ToList() : new List<Connector>();
        foreach (var r in others) endB.DisconnectFrom(r);
        var copyId = ElementTransformUtils.CopyElement(doc, run.Id, XYZ.Zero).First();
        ((LocationCurve)run.Location).Curve = Line.CreateBound(a, at);
        ((LocationCurve)doc.GetElement(copyId).Location).Curve = Line.CreateBound(at, b);
        doc.Regenerate();
        var copyB = Conns(doc.GetElement(copyId)).Where(x => x.ConnectorType == ConnectorType.End).OrderBy(x => x.Origin.DistanceTo(b)).First();
        foreach (var r in others) copyB.ConnectTo(r);
        return copyId;
    }

    private static Connector Nearest(Element e, XYZ at, bool openOnly = true) =>
        Conns(e).Where(c => c.ConnectorType == ConnectorType.End && (!openOnly || !c.IsConnected)).OrderBy(c => c.Origin.DistanceTo(at)).FirstOrDefault();

    private static void Size(EditContext c, MEPCurve run, string kind, double? dia, double? w, double? h, string typeName)
    {
        switch (kind)
        {
            case "duct":
                bool round = false;
                try { round = run.Diameter > 0; } catch { /* rectangular or oval */ }
                if (round) { if (w != null || h != null) throw new ArgumentException($"Duct type '{typeName}' is round: give 'diameter'."); SetSize(c, run, "RBS_CURVE_DIAMETER_PARAM", dia); }
                else { if (dia != null) throw new ArgumentException($"Duct type '{typeName}' is not round: give 'width' and 'height'."); SetSize(c, run, "RBS_CURVE_WIDTH_PARAM", w); SetSize(c, run, "RBS_CURVE_HEIGHT_PARAM", h); }
                break;
            case "pipe": SetSize(c, run, "RBS_PIPE_DIAMETER_PARAM", dia); break;
            case "cable_tray": SetSize(c, run, "RBS_CABLETRAY_WIDTH_PARAM", w); SetSize(c, run, "RBS_CABLETRAY_HEIGHT_PARAM", h); break;
            default: SetSize(c, run, "RBS_CONDUIT_DIAMETER_PARAM", dia); break;
        }
    }

    /// <summary>
    /// One connected run along a path of points: straight segments with the type, system and size given, joined by the
    /// elbows the type's routing preferences give (Revit picks the fitting; nothing is sized here).
    /// </summary>
    private static object Route(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", null, "duct", "pipe", "cable_tray", "conduit");
        var level = Find.NeedLevel(doc, p);
        // 2D points sit at the level plus z (the run's centreline height); 3D points are used as given
        double zf = level.Elevation + (Has(p, "z") ? U.In(NeedDbl(p, "z")) : 0);
        var from = Has(p, "from") ? p["from"] as Obj ?? throw new ArgumentException("'from' must be {element: id, classification?: name, stub?: length}.") : null;
        var pts = U.PtList(p, "path", from == null ? 2 : 1, 200, zf);
        if (Has(p, "slope"))
        {
            // the engineer's slope (percent, falling along the path; negative rises): each point drops by slope x the plan
            // length from the one before, starting at the first path point
            if (kind != "pipe" && kind != "duct") throw new ArgumentException("'slope' is for pipes and ducts.");
            if (Arr(p, "path", 200).Any(v => v is List<object> l && l.Count == 3))
                throw new ArgumentException("With 'slope' give the path as [x, y] points: the first sits at the level plus z and the rest fall by the slope.");
            double slope = NeedDbl(p, "slope") / 100;
            for (int i = 1; i < pts.Count; i++)
            {
                double plan = new XYZ(pts[i].X - pts[i - 1].X, pts[i].Y - pts[i - 1].Y, 0).GetLength();
                pts[i] = new XYZ(pts[i].X, pts[i].Y, pts[i - 1].Z - slope * plan);
            }
        }
        Connector src = null;
        var args0 = new Obj(p);
        if (from != null)
        {
            // start at an equipment / terminal / run connector: straight out of it first (a stub when the first path point is not ahead)
            src = StartConnector(c, from, kind, pts[0]);
            var o = src.Origin; var dir = src.CoordinateSystem.BasisZ.Normalize();
            var v = pts[0] - o;
            // within 2 degrees of the connector's axis counts as straight ahead: snap the point onto the axis (rounded
            // coordinates are never exactly on it); otherwise go straight out first (a stub), then to the point
            if (v.GetLength() > 1e-6 && v.Normalize().DotProduct(dir) > Math.Cos(2 * Math.PI / 180))
                pts[0] = o + dir * v.DotProduct(dir);
            else
                pts.Insert(0, o + dir * (Has(from, "stub") ? U.In(NeedDbl(from, "stub")) : U.Mm(300)));
            pts.Insert(0, o);
            if (Has(p, "diameter") || Has(p, "width") || Has(p, "height"))
                throw new ArgumentException("With 'from' the run takes the connector's size: leave diameter / width / height out (add a transition later if needed).");
        }
        for (int i = 1; i < pts.Count; i++) U.Segment(pts[i - 1], pts[i], $"path segment {i}");
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            var d1 = (pts[i] - pts[i - 1]).Normalize(); var d2 = (pts[i + 1] - pts[i]).Normalize();
            if (d1.IsAlmostEqualTo(d2)) throw new ArgumentException($"Path points {i}, {i + 1} and {i + 2} are in a straight line: leave out the middle one.");
            if (d1.IsAlmostEqualTo(-d2)) throw new ArgumentException($"The path turns back on itself at point {i + 1}.");
        }
        var args = new Obj(args0); args.Remove("path"); args.Remove("z"); args.Remove("from"); args.Remove("to_main"); args.Remove("slope");
        MEPCurve firstRun = null;
        int firstSeg = 1;
        if (src != null)
        {
            // the first segment is drawn from the connector itself: Revit gives it the connector's size and orientation and connects it
            ElementId typeId = RunType(doc, kind, Str(p, "type"));
            try { firstRun = FromConnector(doc, kind, typeId, level, src, pts[1]); }
            catch (Exception ex) { throw new InvalidOperationException($"Revit could not start a {kind} on the connector: {ex.Message}"); }
            doc.Regenerate();
            firstSeg = 2;
            // the rest of the run takes the first segment's size
            try { double d = firstRun.Diameter; if (d > 0) args["diameter"] = U.Out(d); } catch { /* not round */ }
            if (!args.ContainsKey("diameter")) { try { args["width"] = U.Out(firstRun.Width); args["height"] = U.Out(firstRun.Height); } catch { /* the type's size */ } }
        }
        var ids = new List<ElementId>();
        Obj created;
        if (firstRun != null) ids.Add(firstRun.Id);
        if (pts.Count > firstSeg)
        {
            created = ToObj(RunCreate(c, new Obj(args) { ["runs"] = Enumerable.Range(firstSeg, pts.Count - firstSeg).Select(i => (object)new Obj
            {
                ["start"] = new List<object> { U.Out(pts[i - 1].X), U.Out(pts[i - 1].Y), U.Out(pts[i - 1].Z) },
                ["end"] = new List<object> { U.Out(pts[i].X), U.Out(pts[i].Y), U.Out(pts[i].Z) },
            }).ToList() }));
            ids.AddRange(((List<Obj>)created["items"]).Select(o => new ElementId((long)o["id"])));
        }
        else created = new Obj { ["items"] = new List<Obj>() };
        if (firstRun != null)
            ((List<Obj>)created["items"]).Insert(0, new Obj { ["id"] = firstRun.Id.Value, ["start"] = U.Xyz(pts[0]), ["end"] = U.Xyz(pts[1]), ["size"] = MepRead.Size(firstRun).text });
        var fittings = new List<long>();
        for (int i = 0; i + 1 < ids.Count; i++)
        {
            Element a = doc.GetElement(ids[i]), b = doc.GetElement(ids[i + 1]);
            var ca = Nearest(a, pts[i + 1]); var cb = Nearest(b, pts[i + 1]);
            FamilyInstance f;
            try { f = doc.Create.NewElbowFitting(ca, cb); }
            catch (Exception first)
            {
                // a rectangular run that leaves a turned connector can have the next segment's width and height the other way
                // round: swap them on that segment and try once more
                f = null;
                if (b is MEPCurve mb && !(b is Pipe))
                {
                    var wp = mb.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM); var hp = mb.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                    if (wp != null && hp != null && !wp.IsReadOnly && !hp.IsReadOnly && Math.Abs(wp.AsDouble() - hp.AsDouble()) > 1e-6)
                    {
                        double w0 = wp.AsDouble(), h0 = hp.AsDouble();
                        using (var st = new SubTransaction(doc))
                        {
                            st.Start();
                            try
                            {
                                wp.Set(h0); hp.Set(w0); doc.Regenerate();
                                f = doc.Create.NewElbowFitting(Nearest(a, pts[i + 1]), Nearest(b, pts[i + 1]));
                                st.Commit();
                                c.Warn($"Path point {i + 2}: the segment after the elbow was turned (width and height swapped) to match the one before.");
                            }
                            catch { st.RollBack(); f = null; }
                        }
                    }
                }
                if (f == null) throw new InvalidOperationException($"No elbow at path point {i + 2}: {first.Message} Check the type's routing preferences (an elbow for this size and angle).");
            }
            fittings.Add(f.Id.Value);
        }
        doc.Regenerate();
        created["fittings"] = fittings;
        if (src != null)
        {
            if (!src.IsConnected)
            {
                var first = Nearest(doc.GetElement(ids[0]), src.Origin) ?? throw new InvalidOperationException("The first segment has no open end at the connector.");
                first.ConnectTo(src);
                doc.Regenerate();
            }
            created["from"] = new { element = src.Owner.Id.Value, classification = ConnClass(src), system = src.MEPSystem?.Name };
        }
        if (Has(p, "to_main"))
        {
            if (!(c.Editable(Find.Elem(doc, NeedId(p, "to_main"))) is MEPCurve tm)) throw new ArgumentException("'to_main' must be a straight duct, pipe, cable tray or conduit.");
            var sizeArgs = new Obj();
            foreach (var k in new[] { "diameter", "width", "height" }) if (args.TryGetValue(k, out var v)) sizeArgs[k] = v;
            var joined = ConnectCore(c, doc.GetElement(ids[ids.Count - 1]), tm, sizeArgs);
            joined.Remove("split"); joined.Remove("element");
            created["toMain"] = joined;
        }
        created["note"] = "One connected run: elbows from the type's routing preferences" + (src != null ? "; starts on the connector given" : "") +
                          (Has(p, "to_main") ? "; ends on the main with a tap or tee." : ".");
        return created;
    }

    /// <summary>
    /// Connect an element's open connector (air terminal, equipment, the open end of a run) to a main duct or pipe: a
    /// straight run along the connector's direction to the main, then the tap / tee the main's routing preferences give.
    /// The connector must point at the main (its line must pass through the main's section).
    /// </summary>
    private static object Connect(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var el = c.Editable(Find.Elem(doc, NeedId(p, "element")));
        if (!(c.Editable(Find.Elem(doc, NeedId(p, "main"))) is MEPCurve main)) throw new ArgumentException("'main' must be a straight duct, pipe, cable tray or conduit.");
        var r = ConnectCore(c, el, main, p);
        r.Remove("split");
        return r;
    }

    /// <summary>The branch, tap or tee of mep_connect; "split" is the new piece of the main when it was split for a tee.</summary>
    internal static Obj ConnectCore(EditContext c, Element el, MEPCurve main, Obj p)
    {
        var doc = c.Doc;
        if (!(main.Location is LocationCurve ml) || !(ml.Curve is Line mainLine))
            throw new ArgumentException("'main' must be a straight duct or pipe.");
        string mkind = KindOf(main);
        var open = Conns(el).Where(x => x.ConnectorType == ConnectorType.End && !x.IsConnected && x.Domain == DomainOf(mkind)).ToList();
        if (open.Count == 0) throw new ArgumentException($"Element {el.Id.Value} has no open {mkind.Replace('_', ' ')} connector.");
        if (Str(p, "classification") is string want)
        {
            var match = open.Where(x => string.Equals(ConnClass(x), want, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0)
                throw new ArgumentException($"Element {el.Id.Value} has no open connector of classification '{want}': its open ones are {string.Join(", ", open.Select(ConnClass).Distinct())}.");
            open = match;
        }
        var con = open.OrderBy(x => mainLine.Project(x.Origin).Distance).First();
        var o = con.Origin; var d = con.CoordinateSystem.BasisZ.Normalize();
        // closest approach of the connector's line to the main's centreline
        var m0 = mainLine.GetEndPoint(0); var md = mainLine.Direction;
        var w = o - m0; double a = d.DotProduct(d), b = d.DotProduct(md), cc = md.DotProduct(md), dd = d.DotProduct(w), e = md.DotProduct(w);
        double den = a * cc - b * b;
        if (Math.Abs(den) < 1e-9) throw new ArgumentException("The connector points along the main, not at it.");
        double t = (b * e - cc * dd) / den, s = (a * e - b * dd) / den;
        if (t <= 0) throw new ArgumentException("The connector points away from the main.");
        var hit = o + d * t; var onMain = m0 + md * s;
        double half = MepRead.Size(main).feet / 2;
        if (hit.DistanceTo(onMain) > Math.Max(half, 0.01) || s < 0 || s > mainLine.Length)
            throw new ArgumentException($"The connector's line misses the main by {U.Out(hit.DistanceTo(onMain)):0} {U.Name}: move the element in line with the main or draw the branch with mep_route.");
        bool pipe = main is Pipe;
        bool trayOrConduit = mkind == "cable_tray" || mkind == "conduit";
        var level = doc.GetElement(main.ReferenceLevel?.Id ?? el.LevelId) as Level ?? Find.NeedLevel(doc, p);
        ElementId typeId = Has(p, "type") ? RunType(doc, mkind, Str(p, "type")) : main.GetTypeId();
        // the branch runs to the main's centreline; Revit trims it back when it makes the tap or tee
        var end = onMain;
        MEPCurve branch = FromConnector(doc, mkind, typeId, level, con, end);
        double? dia = Dbl(p, "diameter"), wd = Dbl(p, "width"), ht = Dbl(p, "height");
        if (dia != null || wd != null || ht != null) Size(c, branch, mkind, dia, wd, ht, doc.GetElement(typeId).Name);
        doc.Regenerate();
        var free = Nearest(branch, end) ?? throw new InvalidOperationException("The new branch has no free end.");
        FamilyInstance tap;
        string how = "tap";
        ElementId split = null;
        try
        {
            using (var st = new SubTransaction(doc))
            {
                st.Start();
                if (trayOrConduit) throw new InvalidOperationException("cable trays and conduits take tees, not taps");
                try { tap = doc.Create.NewTakeoffFitting(free, main); st.Commit(); }
                catch { st.RollBack(); throw; }
            }
        }
        catch (Exception takeoffError)
        {
            // Types whose routing preferences use tees (not taps): split the main at the branch and insert a tee.
            how = "tee";
            try
            {
                var other = SplitRun(doc, main, onMain);
                split = other;
                doc.Regenerate();
                var c1 = Nearest(main, onMain); var c2 = Nearest(doc.GetElement(other), onMain);
                free = Nearest(branch, end);
                tap = doc.Create.NewTeeFitting(c1, c2, free);
            }
            catch (Exception teeError)
            {
                throw new InvalidOperationException($"Revit made neither a tap ({takeoffError.Message}) nor a tee ({teeError.Message}) on the main. Check the main type's routing preferences (junction: tap or tee, for these sizes).");
            }
        }
        doc.Regenerate();
        return new Obj
        {
            ["element"] = el.Id.Value, ["main"] = main.Id.Value, ["branch"] = branch.Id.Value, ["branchSize"] = MepRead.Size(branch).text,
            ["branchLength"] = U.Out(branch.Location is LocationCurve bl ? bl.Curve.Length : 0), ["fitting"] = tap.Id.Value,
            ["fittingType"] = Describe.Label(doc, tap), ["junction"] = how, ["split"] = split?.Value,
        };
    }

    /// <summary>A duct or piping system of a given system type from the open connectors of given terminals / equipment, with an optional base equipment.</summary>
    private static object SystemCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", null, "duct", "pipe");
        bool duct = kind == "duct";
        var domain = duct ? Domain.DomainHvac : Domain.DomainPiping;
        ElementId typeId = null;
        if (Has(p, "system_type"))
            typeId = duct ? Pick<MechanicalSystemType>(doc, NeedStr(p, "system_type"), "duct system type").Id : Pick<PipingSystemType>(doc, NeedStr(p, "system_type"), "piping system type").Id;
        var set = new ConnectorSet();
        var used = new List<long>();
        var notes = new List<string>();
        foreach (var id in Ids(p, "elements", 1000))
        {
            var e = c.Editable(Find.Elem(doc, id));
            var all = Conns(e).ToList();
            var free = all.Where(x => x.Domain == domain && x.MEPSystem == null && x.ConnectorType == ConnectorType.End).ToList();
            if (free.Count == 0)
            {
                string seen = all.Count == 0 ? "no connectors" : string.Join("; ", all.Select(x =>
                {
                    string sys; try { sys = x.MEPSystem?.Name ?? "none"; } catch { sys = "?"; }
                    return $"{x.Domain} {x.ConnectorType} system {sys}";
                }));
                c.Warn($"Element {id.Value} has no free {kind} connector ({seen}); left out.");
                notes.Add($"{id.Value}: {seen}");
                continue;
            }
            foreach (var x in free) set.Insert(x);
            used.Add(id.Value);
        }
        if (set.Size == 0) throw new ArgumentException("None of the elements has a free connector for this kind of system. Connectors: " + string.Join(" | ", notes));
        // One system holds one classification (Supply Air, Domestic Cold Water ...): keep the connectors of the system type's
        // classification (or, with no type given, of the first connector that has a system type), list the others.
        string Cls(Connector x) => duct ? x.DuctSystemType.ToString() : x.PipeSystemType.ToString();
        var types = new FilteredElementCollector(doc).OfClass(duct ? typeof(MechanicalSystemType) : typeof(PipingSystemType)).Cast<MEPSystemType>().ToList();
        var conns = set.Cast<Connector>().ToList();
        MEPSystemType chosen = typeId != null ? (MEPSystemType)doc.GetElement(typeId)
            : conns.Select(x => types.FirstOrDefault(t => t.SystemClassification.ToString() == Cls(x))).FirstOrDefault(t => t != null)
              ?? throw new ArgumentException($"No {kind} system type matches the connectors ({string.Join(", ", conns.Select(Cls).Distinct())}). Give 'system_type'. Types: {string.Join(", ", types.Select(t => $"{t.Name} ({t.SystemClassification})"))}.");
        typeId = chosen.Id;
        string want = chosen.SystemClassification.ToString();
        var keep = conns.Where(x => Cls(x) == want).ToList();
        var leftOut = conns.Where(x => Cls(x) != want).Select(x => $"{x.Owner.Id.Value}: {Cls(x)}").ToList();
        if (keep.Count == 0)
            throw new ArgumentException($"No connector of these elements is {want} (the classification of '{chosen.Name}'). Their connectors: {string.Join(", ", conns.Select(Cls).Distinct())}.");
        // The base equipment connector is the system's source, not one of its members: Revit refuses it as base
        // ("has been used") when it is also added to the system, so it is taken out of the member set.
        Connector bc = null;
        if (Has(p, "base_equipment"))
        {
            var eq = c.Editable(Find.Elem(doc, NeedId(p, "base_equipment")));
            bc = Conns(eq).Where(x => x.Domain == domain && x.ConnectorType == ConnectorType.End && x.MEPSystem == null)
                     .OrderBy(x => Cls(x) == want ? 0 : 1).FirstOrDefault()
                 ?? throw new ArgumentException($"Element {eq.Id.Value} has no free {kind} connector for a base equipment.");
            if (Cls(bc) != want) c.Warn($"The base equipment connector is {Cls(bc)}, not {want}.");
            keep = keep.Where(x => !(x.Owner.Id == bc.Owner.Id && x.Id == bc.Id)).ToList();
            if (keep.Count == 0)
                throw new ArgumentException($"A system needs at least one element besides its base equipment {eq.Id.Value}: give the terminals or other equipment it serves in 'elements'.");
        }
        set = new ConnectorSet();
        foreach (var x in keep) set.Insert(x);
        if (leftOut.Count > 0) c.Warn($"Connectors of another classification were left out: {string.Join("; ", leftOut)}.");
        string name = Str(p, "name");
        MEPSystem sys = duct ? (MEPSystem)(name == null ? MechanicalSystem.Create(doc, typeId) : MechanicalSystem.Create(doc, typeId, name))
                             : name == null ? PipingSystem.Create(doc, typeId) : PipingSystem.Create(doc, typeId, name);
        sys.Add(set);
        if (bc != null)
        {
            if (duct) ((MechanicalSystem)sys).BaseEquipmentConnector = bc; else ((PipingSystem)sys).BaseEquipmentConnector = bc;
        }
        doc.Regenerate();
        return new { id = sys.Id.Value, name = sys.Name, kind, systemType = doc.GetElement(typeId).Name, classification = want, connectors = keep.Count, elements = used, baseEquipment = sys.BaseEquipment?.Id.Value };
    }

    // ---------------------------------------------------------------- phase 18, second part: terminals, system membership

    /// <summary>One number for every point, or a list with one number per point (null when not given).</summary>
    internal static List<double?> PerPoint(Obj p, string name, int count)
    {
        if (!p.TryGetValue(name, out var raw) || raw == null) return Enumerable.Repeat((double?)null, count).ToList();
        if (raw is double one) return Enumerable.Repeat((double?)one, count).ToList();
        var list = Arr(p, name, 2000).Select((v, i) => (double?)Number(v, $"'{name}' item {i + 1}")).ToList();
        if (list.Count != count) throw new ArgumentException($"'{name}' needs one value, or one value per point ({count}).");
        return list;
    }

    /// <summary>A horizontal reference plane facing down at an elevation: the "ceiling" for face-based terminals (kept, reused).</summary>
    internal static ReferencePlane DownPlane(EditContext c, Level level, double z, string purpose = "terminals", bool up = false)
    {
        var doc = c.Doc;
        string name = $"MCP {purpose} {level.Name} +{U.Out(z - level.ProjectElevation):0}" + (up ? " up" : "");
        double sign = up ? 1 : -1;
        var existing = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
            .FirstOrDefault(r => r.Name == name && r.Normal.Z * sign > 0.999 && Math.Abs(r.GetPlane().Origin.Z - z) < 1e-4);
        if (existing != null) return existing;
        var view = (View)new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                       .FirstOrDefault(v => !v.IsTemplate && v.GenLevel?.Id == level.Id)
                   ?? new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate)
                   ?? throw new InvalidOperationException($"No plan of level '{level.Name}' and no 3D view to draw the terminals' reference plane in.");
        var b = new XYZ(0, 0, z); var f = new XYZ(10, 0, z);
        var rp = doc.Create.NewReferencePlane2(b, f, new XYZ(0, -10, z), view);
        if (rp.Normal.Z * sign < 0) { doc.Delete(rp.Id); rp = doc.Create.NewReferencePlane2(b, f, new XYZ(0, 10, z), view); }
        rp.Name = name;
        c.Warn($"Face-based {purpose} are hosted on a new reference plane '{name}' facing {(up ? "up" : "down")} (kept in the model).");
        return rp;
    }

    /// <summary>
    /// Air terminals of a given type at given points and height above a level, with the airflow the engineer gives (L/s),
    /// optionally each connected to a main duct above it (a branch up to the main and a tap or tee, as mep_connect).
    /// Face-based types are hosted on a downward-facing reference plane at that height.
    /// </summary>
    private static object AirTerminalPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), BuiltInCategory.OST_DuctTerminal);
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        var level = Find.NeedLevel(doc, p);
        double height = U.Len(p, "height", 0);
        var pts = U.PtList(p, "points", 1, 500);
        var flows = PerPoint(p, "flows", pts.Count);
        if (flows.Any(f => f != null && f < 0)) throw new ArgumentException("'flows' must not be negative.");
        var mains = new List<ElementId>();
        if (Has(p, "main"))
        {
            if (!(c.Editable(Find.Elem(doc, NeedId(p, "main"))) is Duct mainDuct)) throw new ArgumentException("'main' must be a duct.");
            mains.Add(mainDuct.Id);
        }
        // the branch's own type and size (the terminal's 'type' and 'height' mean the terminal and its mounting height)
        var branchArgs = new Obj();
        foreach (var (from, to) in new[] { ("branch_type", "type"), ("branch_diameter", "diameter"), ("branch_width", "width"), ("branch_height", "height") })
            if (p.TryGetValue(from, out var v)) branchArgs[to] = v;
        var placement = symbol.Family.FamilyPlacementType;
        ReferencePlane plane = null;
        if (placement == FamilyPlacementType.WorkPlaneBased) plane = DownPlane(c, level, level.ProjectElevation + height);
        else if (placement != FamilyPlacementType.OneLevelBased)
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is a {placement} family; air_terminal_place takes level-based or face-based terminals.");
        var rows = new List<Obj>();
        for (int i = 0; i < pts.Count; i++)
        {
            var at = new XYZ(pts[i].X, pts[i].Y, level.ProjectElevation + height);
            var inst = plane != null ? doc.Create.NewFamilyInstance(plane.GetReference(), at, XYZ.BasisX, symbol)
                                     : doc.Create.NewFamilyInstance(at, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            if (plane == null)
            {
                var ep = inst.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                if (ep != null && !ep.IsReadOnly) ep.Set(height);
            }
            doc.Regenerate();
            var row = new Obj { ["id"] = inst.Id.Value, ["point"] = U.Xyz(at) };
            var con = Conns(inst).FirstOrDefault(x => x.Domain == Domain.DomainHvac);
            if (flows[i] is double q)
            {
                string failed = SetTerminalFlow(inst, con, q);
                if (failed != null) c.Warn($"Terminal {inst.Id.Value}: the flow {q} L/s was not taken ({failed}).");
            }
            var fp = inst.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM);
            if (fp != null) row["flowLps"] = Math.Round(UnitUtils.ConvertFromInternalUnits(fp.AsDouble(), UnitTypeId.LitersPerSecond), 1);
            if (con == null) c.Warn($"Terminal {inst.Id.Value} has no duct connector.");
            else
            {
                row["classification"] = con.DuctSystemType.ToString();
                var dz = con.CoordinateSystem.BasisZ;
                row["connectorFacing"] = dz.Z > 0.99 ? "up" : dz.Z < -0.99 ? "down" : "side";
            }
            if (mains.Count > 0 && con != null)
            {
                // join the main's system first, so Revit does not put the terminal in a new default system when it connects
                var msys = (doc.GetElement(mains[0]) as MEPCurve)?.MEPSystem as MechanicalSystem;
                var mcls = (doc.GetElement(msys?.GetTypeId() ?? ElementId.InvalidElementId) as MEPSystemType)?.SystemClassification.ToString();
                if (msys != null && con.MEPSystem == null && mcls == con.DuctSystemType.ToString())
                {
                    var one = new ConnectorSet(); one.Insert(con);
                    msys.Add(one);
                    doc.Regenerate();
                }
                else if (msys != null && mcls != con.DuctSystemType.ToString())
                    c.Warn($"Terminal {inst.Id.Value} is {con.DuctSystemType}, the main's system '{msys.Name}' is {mcls}: it was not joined to that system.");
                Obj made = null; string why = null;
                foreach (var mid in mains.ToList())
                {
                    if (!(doc.GetElement(mid) is MEPCurve m)) continue;
                    try { made = ConnectCore(c, inst, m, branchArgs); break; }
                    catch (ArgumentException ex) when (ex.Message.Contains("misses the main") || ex.Message.Contains("points away") || ex.Message.Contains("along the main"))
                    { why = ex.Message; }
                }
                if (made == null) throw new ArgumentException($"Terminal at point {i + 1} cannot be connected to the main: {why}");
                if (made["split"] is long piece) mains.Add(new ElementId(piece));
                made.Remove("split"); made.Remove("element");
                row["connection"] = made;
                doc.Regenerate();
                var now = Conns(inst).FirstOrDefault(x => x.Domain == Domain.DomainHvac)?.MEPSystem;
                row["system"] = now?.Name;
                if (msys != null && now != null && now.Id != msys.Id) c.Warn($"Terminal {inst.Id.Value} ended in system '{now.Name}', not the main's '{msys.Name}'.");
            }
            rows.Add(row);
        }
        return new { type = Find.TypeLabel(symbol), placement = placement.ToString(), level = level.Name, created = rows.Count,
                     connected = rows.Count(r => r.ContainsKey("connection")), items = rows,
                     note = "Flows are the values given (L/s); Revit adds them up the connected system. Nothing is sized here." };
    }

    /// <summary>
    /// Set a terminal's airflow: the instance parameter that drives its connector (families name it "Flow"; the built-in
    /// flow parameter is often the calculated one). Checked by reading the value back; null when it took, else what was tried.
    /// </summary>
    private static string SetTerminalFlow(FamilyInstance inst, Connector con, double lps)
    {
        var doc = inst.Document;
        double want = UnitUtils.ConvertToInternalUnits(lps, UnitTypeId.LitersPerSecond);
        var cands = new List<Parameter> { inst.get_Parameter(BuiltInParameter.RBS_DUCT_FLOW_PARAM) };
        cands.AddRange(inst.GetParameters("Flow"));
        var tried = new List<string>();
        bool Took(Parameter prm)
        {
            double got = prm.AsDouble();
            try { if (con != null && con.Domain == Domain.DomainHvac) got = con.Flow; } catch { /* connector flow not readable: the parameter's own value */ }
            return Math.Abs(UnitUtils.ConvertFromInternalUnits(got, UnitTypeId.LitersPerSecond) - lps) < 0.5;
        }
        foreach (var prm in cands.Where(x => x != null && x.StorageType == StorageType.Double).GroupBy(x => x.Id.Value).Select(g => g.First()))
        {
            string label = prm.Definition.Name + (prm.IsShared ? " (shared)" : prm.Id.Value < 0 ? " (built-in)" : "");
            if (prm.IsReadOnly) { tried.Add(label + ": read-only"); continue; }
            bool ok = prm.Set(want);
            doc.Regenerate();
            if (ok && Took(prm)) return null;
            tried.Add(label + (ok ? ": set, but the connector flow did not follow" : ": refused"));
        }
        var typeFlow = inst.Symbol?.LookupParameter("Flow");
        if (typeFlow != null) tried.Add("'Flow' is a TYPE parameter of this family: duplicate the type per flow, or use a family with an instance flow");
        return tried.Count == 0 ? "no flow parameter on this terminal" : string.Join("; ", tried);
    }

    private static MEPSystem NeedSystem(EditContext c, Obj p) =>
        c.Editable(Find.Elem(c.Doc, NeedId(p, "system"))) as MEPSystem is MEPSystem s && (s is MechanicalSystem || s is PipingSystem) ? s
        : throw new ArgumentException("'system' must be the id of a duct or piping system (mep_systems lists them).");

    /// <summary>Add elements' free connectors of the system's classification to an existing duct or piping system.</summary>
    private static object SystemAdd(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var sys = NeedSystem(c, p);
        bool duct = sys is MechanicalSystem;
        var domain = duct ? Domain.DomainHvac : Domain.DomainPiping;
        var type = doc.GetElement(sys.GetTypeId()) as MEPSystemType;
        string want = type?.SystemClassification.ToString();
        string Cls(Connector x) => duct ? x.DuctSystemType.ToString() : x.PipeSystemType.ToString();
        var set = new ConnectorSet(); var added = new List<long>(); var skipped = new List<string>();
        foreach (var id in Ids(p, "elements", 1000))
        {
            var e = c.Editable(Find.Elem(doc, id));
            var free = Conns(e).Where(x => x.Domain == domain && x.ConnectorType == ConnectorType.End && x.MEPSystem == null).ToList();
            var match = free.Where(x => want == null || Cls(x) == want).ToList();
            if (match.Count == 0)
            {
                skipped.Add($"{id.Value}: " + (free.Count == 0 ? "no free connector of this kind" : $"free connectors are {string.Join(", ", free.Select(Cls).Distinct())}, not {want}"));
                continue;
            }
            foreach (var x in match) set.Insert(x);
            added.Add(id.Value);
        }
        if (set.Size == 0) throw new ArgumentException("No element has a free connector for this system: " + string.Join("; ", skipped));
        if (skipped.Count > 0) c.Warn("Left out: " + string.Join("; ", skipped) + ".");
        sys.Add(set);
        doc.Regenerate();
        return new { system = sys.Id.Value, name = sys.Name, classification = want, added, skipped, elementsNow = sys.Elements.Size };
    }

    /// <summary>Remove elements from a duct or piping system (the base equipment is changed with mep_system_create, not here).</summary>
    private static object SystemRemove(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var sys = NeedSystem(c, p);
        var members = new HashSet<long>(sys.Elements.Cast<Element>().Select(e => e.Id.Value));
        var ids = Ids(p, "elements", 1000);
        var notIn = ids.Where(i => !members.Contains(i.Value)).Select(i => i.Value).ToList();
        if (notIn.Count > 0) throw new ArgumentException($"Not in system {sys.Id.Value} '{sys.Name}': {string.Join(", ", notIn)}.");
        if (sys.BaseEquipment != null && ids.Any(i => i == sys.BaseEquipment.Id))
            throw new ArgumentException($"Element {sys.BaseEquipment.Id.Value} is the system's base equipment, not a member.");
        if (ids.Count >= members.Count)
            throw new ArgumentException($"Removing {ids.Count} of the {members.Count} element(s) of system {sys.Id.Value} '{sys.Name}' would leave it empty; Revit keeps at least one (delete the system instead).");
        foreach (var id in ids) c.Editable(doc.GetElement(id));
        sys.Remove(ids);
        doc.Regenerate();
        return new { system = sys.Id.Value, name = sys.Name, removed = ids.Select(i => i.Value).ToList(), elementsNow = sys.Elements.Size };
    }
}
