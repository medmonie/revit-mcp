using Autodesk.Revit.DB.Structure;

namespace RevitMcpBridge;

/// <summary>
/// Reinforcement: bars and ties in columns, bars and stirrups in straight beams, bottom / top meshes in isolated
/// footings, and area reinforcement in slabs. Sizes come from the element's own geometry in its local axes; cover
/// is to the outer face of the outermost bar (ties / stirrups included).
/// </summary>
internal static class RebarOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["rebar_column"] = RebarColumn;
        ops["rebar_beam"] = RebarBeam;
        ops["rebar_footing"] = RebarFooting;
        ops["rebar_area"] = RebarArea;
    }

    // ---------------------------------------------------------------- shared

    /// <summary>A bar type by name, or by diameter in mm ("16", 16, "T16"); the error lists the model's bar types.</summary>
    public static RebarBarType BarType(Document doc, object spec, string what)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>().ToList();
        if (all.Count == 0) throw new InvalidOperationException("The model has no rebar bar types. Load or create them first (Structure > Rebar settings or a template).");
        string text = Text(spec)?.Trim() ?? "";
        var byName = all.FirstOrDefault(b => b.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        var digits = new string(text.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mm))
        {
            var hit = all.Where(b => Math.Abs(b.BarNominalDiameter * U.MmPerFoot - mm) < 0.6).OrderBy(b => b.Name.Length).FirstOrDefault();
            if (hit != null) return hit;
        }
        throw new ArgumentException($"{what}: no bar type '{text}'. Available: " +
            string.Join(", ", all.OrderBy(b => b.BarNominalDiameter).Select(b => $"{b.Name} ({Math.Round(b.BarNominalDiameter * U.MmPerFoot)} mm)")) + ".");
    }

    private static double Dia(RebarBarType t) => t.BarNominalDiameter;

    /// <summary>Create a bar set from curves (version-neutral), then lay it out.</summary>
    private static Rebar Bar(Document doc, RebarStyle style, RebarBarType type, Element host, XYZ normal, IList<Curve> curves)
    {
#if REVIT2024 || REVIT2025
        var bar = Rebar.CreateFromCurves(doc, style, type, null, null, host, normal, curves,
                                         RebarHookOrientation.Left, RebarHookOrientation.Left, true, true);
#else
        var bar = Rebar.CreateFromCurves(doc, style, type, host, normal, curves, new BarTerminationsData(doc), true, true);
#endif
        return bar ?? throw new InvalidOperationException("Revit could not create the bar (no matching shape).");
    }

    private static void FixedNumber(Rebar bar, int n, double length) =>
        bar.GetShapeDrivenAccessor().SetLayoutAsFixedNumber(n, length, true, true, true);

    private static void MaxSpacing(Rebar bar, double spacing, double length) =>
        bar.GetShapeDrivenAccessor().SetLayoutAsMaximumSpacing(spacing, length, true, true, true);

    /// <summary>
    /// The element's concrete extents in a local frame (origin + three axes). The section (and a column's height) comes from
    /// the member's own shape before joins, because a slab joined over a beam or column cuts its top off and the bars must
    /// still use the full section. With <paramref name="lengthFromJoined"/> the extent along local x is the joined concrete:
    /// a beam's original family geometry is at the family's default length, and its real ends are the support faces.
    /// </summary>
    private static (XYZ min, XYZ max) LocalBox(Element e, Transform frame, bool lengthFromJoined = false)
    {
        var inv = frame.Inverse;
        var opts = new Options { DetailLevel = ViewDetailLevel.Fine };
        (double[] lo, double[] hi)? Box(GeometryElement g, Transform tf)
        {
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            void Add(XYZ q)
            {
                var l = inv.OfPoint(tf.OfPoint(q));
                lo[0] = Math.Min(lo[0], l.X); lo[1] = Math.Min(lo[1], l.Y); lo[2] = Math.Min(lo[2], l.Z);
                hi[0] = Math.Max(hi[0], l.X); hi[1] = Math.Max(hi[1], l.Y); hi[2] = Math.Max(hi[2], l.Z);
            }
            void Walk(GeometryElement ge)
            {
                foreach (var o in ge)
                {
                    if (o is Solid s && s.Volume > 1e-9) { foreach (Edge edge in s.Edges) foreach (var q in edge.Tessellate()) Add(q); }
                    else if (o is GeometryInstance gi) Walk(gi.GetInstanceGeometry());
                }
            }
            if (g != null) Walk(g);
            return lo[0] == double.MaxValue ? ((double[], double[])?)null : (lo, hi);
        }
        var joined = Box(e.get_Geometry(opts), Transform.Identity)
                     ?? throw new InvalidOperationException($"Element {e.Id.Value} has no solid geometry.");
        var (jl, jh) = joined;
        if (e is FamilyInstance fi && Box(fi.GetOriginalGeometry(opts), fi.GetTransform()) is var orig && orig != null)
        {
            var (ol, oh) = orig.Value;
            // use the original shape only where it overlaps the placed member (guards against odd family coordinates)
            bool overlaps = true;
            for (int i = lengthFromJoined ? 1 : 0; i < 3; i++)
                if (ol[i] > jh[i] + 1e-3 || oh[i] < jl[i] - 1e-3) overlaps = false;
            if (overlaps)
                for (int i = lengthFromJoined ? 1 : 0; i < 3; i++) { jl[i] = Math.Min(jl[i], ol[i]); jh[i] = Math.Max(jh[i], oh[i]); }
        }
        return (new XYZ(jl[0], jl[1], jl[2]), new XYZ(jh[0], jh[1], jh[2]));
    }

    private static Transform Frame(XYZ origin, XYZ x, XYZ y)
    {
        var t = Transform.Identity;
        t.Origin = origin; t.BasisX = x.Normalize(); t.BasisY = y.Normalize(); t.BasisZ = t.BasisX.CrossProduct(t.BasisY);
        return t;
    }

    private static List<Element> Hosts(EditContext c, Obj p, BuiltInCategory cat, string what)
    {
        var doc = c.Doc;
        var hosts = c.Elements(p, max: 2000);
        foreach (var h in hosts)
        {
            if (h.Category?.Id.Value != (long)cat) throw new ArgumentException($"Element {h.Id.Value} is not a {what}.");
            if (!RebarHostData.IsValidHost(h)) throw new ArgumentException($"Element {h.Id.Value} cannot host rebar (check its material is concrete and 'Can Host Rebar').");
        }
        return hosts;
    }

    // ---------------------------------------------------------------- detailing

    /// <summary>A detailing length given as a multiple of the bar diameter ("anchorage_d": 40 means 40 x the diameter).</summary>
    private static double Times(Obj p, string name, double dflt, RebarBarType t) => (Dbl(p, name) ?? dflt) * Dia(t);

    /// <summary>The leg of a 90 degree bend: 12 x the diameter, at least 150 mm.</summary>
    private static double Leg(RebarBarType t) => Math.Max(12 * Dia(t), U.Mm(150));

    private static int Mm(double feet) => (int)Math.Round(feet * U.MmPerFoot);

    /// <summary>Elements of these categories whose bounding box contains the point (the host itself excluded).</summary>
    private static List<Element> At(Document doc, XYZ pt, ElementId skip, params BuiltInCategory[] cats) =>
        new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(cats))
            .WherePasses(new BoundingBoxContainsPointFilter(pt)).Where(e => e.Id != skip).ToList();

    /// <summary>Lines through the points, dropping zero-length pieces (a bend of length 0 is simply left out).</summary>
    private static List<Curve> Poly(IEnumerable<XYZ> pts)
    {
        var list = pts.ToList();
        var curves = new List<Curve>();
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i].DistanceTo(list[i + 1]) > U.Mm(1)) curves.Add(Line.CreateBound(list[i], list[i + 1]));
        return curves;
    }

    /// <summary>
    /// Ties or stirrups along a member from a to b (local positions along its axis): closer spacing over end zones,
    /// the normal spacing between. ring(x) builds one closed ring at position x; normal is the member's axis.
    /// </summary>
    private static (List<long> ids, string text) Zoned(Document doc, RebarBarType t, Element host, XYZ normal, Func<double, IList<Curve>> ring,
                                                      double a, double b, double spacing, double zone, double zoneSpacing)
    {
        var ids = new List<long>();
        void Set(double x0, double len, double s)
        {
            var r = Bar(doc, RebarStyle.StirrupTie, t, host, normal, ring(x0));
            if (len > U.Mm(1)) MaxSpacing(r, s, len);
            ids.Add(r.Id.Value);
        }
        double l = b - a;
        if (zone <= U.Mm(1) || zoneSpacing >= spacing - U.Mm(1))
        {
            Set(a, l, spacing);
            return (ids, $"{t.Name} @ {Mm(spacing)}");
        }
        if (l <= 2 * zone + 2 * spacing + U.Mm(10))
        {
            Set(a, l, zoneSpacing);
            return (ids, $"{t.Name} @ {Mm(zoneSpacing)} (member too short for end zones of {Mm(zone)})");
        }
        Set(a, zone, zoneSpacing);
        Set(a + zone + spacing, l - 2 * zone - 2 * spacing, spacing);
        Set(b - zone, zone, zoneSpacing);
        return (ids, $"{t.Name} @ {Mm(zoneSpacing)} over {Mm(zone)} at each end, @ {Mm(spacing)} between");
    }

    private static CurveLoop Rect(Transform f, double x0, double y0, double x1, double y1, double z)
    {
        XYZ P(double x, double y) => f.OfPoint(new XYZ(x, y, z));
        var loop = new List<Curve>
        {
            Line.CreateBound(P(x0, y0), P(x1, y0)), Line.CreateBound(P(x1, y0), P(x1, y1)),
            Line.CreateBound(P(x1, y1), P(x0, y1)), Line.CreateBound(P(x0, y1), P(x0, y0)),
        };
        return CurveLoop.Create(loop);
    }

    // ---------------------------------------------------------------- columns

    private static object RebarColumn(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var main = BarType(doc, NeedRef(p, "bar"), "bar");
        var tie = BarType(doc, NeedRef(p, "tie"), "tie");
        int nx = Int(p, "bars_x", 3, 2, 30), ny = Int(p, "bars_y", 3, 2, 30);
        double cover = U.LenMm(p, "cover", 40), spacing = U.LenMm(p, "tie_spacing", 200);
        double lap = Times(p, "lap_d", 50, main), leg = Leg(main);
        double footCover = U.LenMm(p, "footing_cover", 50);
        string topWant = Choice(p, "top", "auto", "auto", "lap", "bend", "none");
        string botWant = Choice(p, "bottom", "auto", "auto", "footing", "none");
        var made = new List<object>();
        foreach (FamilyInstance col in Hosts(c, p, BuiltInCategory.OST_StructuralColumns, "structural column"))
        {
            var tf = col.GetTotalTransform();
            var f = Frame(tf.Origin, tf.BasisX, tf.BasisY);
            var (lo, hi) = LocalBox(col, f);
            double dm = Dia(main);
            double off = cover + Dia(tie) + dm / 2;                              // bar centre from the face
            double x0 = lo.X + off, x1 = hi.X - off, y0 = lo.Y + off, y1 = hi.Y - off;
            if (x1 - x0 < dm || y1 - y0 < dm) throw new ArgumentException($"Column {col.Id.Value} is too small for this cover and these bars.");
            double cx = (lo.X + hi.X) / 2, cy = (lo.Y + hi.Y) / 2;

            // what is above and below decides how the bars end
            var above = At(doc, f.OfPoint(new XYZ(cx, cy, hi.Z + U.Mm(100))), col.Id, BuiltInCategory.OST_StructuralColumns).FirstOrDefault();
            var under = At(doc, f.OfPoint(new XYZ(cx, cy, lo.Z - U.Mm(100))), col.Id,
                           BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFoundation).FirstOrDefault();
            string top = topWant != "auto" ? topWant : above != null ? "lap" : "bend";
            string bottom = botWant != "auto" ? botWant
                          : under != null && under.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation ? "footing" : "none";
            double zb = under != null ? lo.Z : lo.Z + cover;                     // a member below: bars start at its top
            (XYZ min, XYZ max)? fb = null;
            Element footingHost = null;
            if (bottom == "footing")
            {
                var footing = under != null && under.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFoundation ? under
                    : throw new ArgumentException($"Column {col.Id.Value}: 'bottom: footing' but there is no structural foundation under it.");
                fb = LocalBox(footing, f);
                footingHost = footing;
                zb = fb.Value.min.Z + footCover + 2 * dm + dm / 2;              // standing on the footing's bottom mesh
            }
            double zt = top == "lap" ? hi.Z + lap : top == "bend" ? hi.Z - cover - dm / 2 : hi.Z - cover;

            XYZ L(double x, double y, double z) => f.OfPoint(new XYZ(x, y, z));
            double Foot(double room) { double v = Math.Min(Math.Max(leg, U.Mm(200)), room); return v < 4 * dm ? 0 : v; }
            // one bar at (x, y); (ox, oy) points out of the column through the face the bar is on
            IList<Curve> ColumnBar(double x, double y, double ox, double oy, double inward)
            {
                var pts = new List<XYZ>();
                if (fb is (XYZ fmin, XYZ fmax))
                {
                    double room = ox < 0 ? x - fmin.X - footCover : ox > 0 ? fmax.X - footCover - x
                                : oy < 0 ? y - fmin.Y - footCover : fmax.Y - footCover - y;
                    double foot = Foot(room);
                    pts.Add(L(x + ox * foot, y + oy * foot, zb));
                }
                pts.Add(L(x, y, zb));
                pts.Add(L(x, y, zt));
                if (top == "bend") { double t = Math.Min(leg, inward); pts.Add(L(x - ox * t, y - oy * t, zt)); }
                return Poly(pts);
            }

            var ids = new List<long>();
            // two faces along local x carry nx bars each (corners included); the other two faces carry the ny-2 middle bars
            foreach (var (y, oy) in new[] { (y0, -1.0), (y1, 1.0) })
            {
                var bar = Bar(doc, RebarStyle.Standard, main, col, f.BasisX, ColumnBar(x0, y, 0, oy, y1 - y0));
                FixedNumber(bar, nx, x1 - x0);
                ids.Add(bar.Id.Value);
            }
            if (ny > 2)
                foreach (var (x, ox) in new[] { (x0, -1.0), (x1, 1.0) })
                {
                    double step = (y1 - y0) / (ny - 1);
                    var bar = Bar(doc, RebarStyle.Standard, main, col, f.BasisY, ColumnBar(x, y0 + step, ox, 0, x1 - x0));
                    FixedNumber(bar, ny - 2, (y1 - y0) - 2 * step);
                    ids.Add(bar.Id.Value);
                }

            // closed ties, closer at both ends of the column (default end zone: the larger of the long side, height / 6, 450 mm)
            double toff = cover + Dia(tie) / 2;
            double ta = lo.Z + Math.Max(cover, U.Mm(50)), tb = hi.Z - Math.Max(cover, U.Mm(50));
            double zone = Has(p, "end_zone") ? U.LenMm(p, "end_zone", 0)
                        : Math.Max(Math.Max(hi.X - lo.X, hi.Y - lo.Y), Math.Max((hi.Z - lo.Z) / 6, U.Mm(450)));
            double zoneSpacing = U.LenMm(p, "end_spacing", Math.Min(spacing * U.MmPerFoot, 100));
            var ties = Zoned(doc, tie, col, f.BasisZ, z => Rect(f, lo.X + toff, lo.Y + toff, hi.X - toff, hi.Y - toff, z).ToList(),
                             ta, tb, spacing, zone, zoneSpacing);
            ids.AddRange(ties.ids);
            // locating ties for the starter bars inside the footing: from above its bottom mesh to under its top cover
            string starterTies = null;
            if (fb is (XYZ smin, XYZ smax))
            {
                double sa0 = zb + U.Mm(100), sb0 = smax.Z - footCover;
                if (sb0 - sa0 > U.Mm(1))
                {
                    var st = Bar(doc, RebarStyle.StirrupTie, tie, footingHost, f.BasisZ, Rect(f, lo.X + toff, lo.Y + toff, hi.X - toff, hi.Y - toff, sa0).ToList());
                    MaxSpacing(st, Math.Min(spacing, U.Mm(150)), sb0 - sa0);
                    ids.Add(st.Id.Value);
                    starterTies = $"{tie.Name} in the footing from {Mm(sa0 - smin.Z)} to {Mm(sb0 - smin.Z)} above its bottom";
                }
            }
            made.Add(new
            {
                column = col.Id.Value, starterTies, bars = 2 * nx + 2 * Math.Max(0, ny - 2), barType = main.Name,
                sizeMm = new[] { Mm(hi.X - lo.X), Mm(hi.Y - lo.Y) },
                top = top == "lap" ? $"bars continue {Mm(lap)} above the column for the lap with the column above"
                    : top == "bend" ? $"bars bent inward {Mm(Math.Min(leg, Math.Min(x1 - x0, y1 - y0)))} at the top" : "straight, stop at cover",
                bottom = bottom == "footing" ? $"bars stand on the footing mesh with feet outward (footing {under!.Id.Value})"
                       : under != null ? $"bars start at the top of {under.Category?.Name} {under.Id.Value} (its bars lap into this column)" : "straight, stop at cover",
                ties = ties.text, rebarIds = ids,
            });
        }
        return new { columns = made.Count, items = made };
    }

    // ---------------------------------------------------------------- beams

    private static object RebarBeam(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var bottom = BarType(doc, NeedRef(p, "bottom_bar"), "bottom_bar");
        var top = BarType(doc, Has(p, "top_bar") ? p["top_bar"] : p["bottom_bar"], "top_bar");
        var stirrup = BarType(doc, NeedRef(p, "stirrup"), "stirrup");
        int nb = Int(p, "bottom_count", 3, 2, 30), nt = Int(p, "top_count", 2, 2, 30);
        double cover = U.LenMm(p, "cover", 40), spacing = U.LenMm(p, "stirrup_spacing", 150);
        double stock = U.LenMm(p, "stock_length", 12000);
        string ends = Choice(p, "ends", "anchor", "anchor", "none");
        var made = new List<object>();
        foreach (FamilyInstance beam in Hosts(c, p, BuiltInCategory.OST_StructuralFraming, "structural framing member"))
        {
            if (!(beam.Location is LocationCurve lc) || !(lc.Curve is Line axis))
                throw new ArgumentException($"Beam {beam.Id.Value} is not straight; curved beams are not reinforced by this operation.");
            var d = axis.Direction;
            if (Math.Abs(d.Z) > 0.01) throw new ArgumentException($"Beam {beam.Id.Value} is sloped; only level beams are supported.");
            d = new XYZ(d.X, d.Y, 0).Normalize();
            var f = Frame(axis.GetEndPoint(0), d, XYZ.BasisZ.CrossProduct(d));   // local x along the beam, y across
            var (lo, hi) = LocalBox(beam, f, lengthFromJoined: true);
            double sOff = cover + Dia(stirrup);
            double ob = sOff + Dia(bottom) / 2, ot = sOff + Dia(top) / 2;
            double midY = (lo.Y + hi.Y) / 2, midZ = (lo.Z + hi.Z) / 2;

            // the supports at each end: what the beam runs into just past its end faces
            (Element e, string kind, double room) Support(bool atStart)
            {
                double x = atStart ? lo.X - U.Mm(50) : hi.X + U.Mm(50);
                var s = At(doc, f.OfPoint(new XYZ(x, midY, midZ)), beam.Id, BuiltInCategory.OST_StructuralColumns,
                           BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFoundation).FirstOrDefault();
                if (s == null)
                {
                    // a member meeting this beam at a corner overlaps its end instead of lying past it
                    double xin = atStart ? lo.X + U.Mm(50) : hi.X - U.Mm(50);
                    var corner = At(doc, f.OfPoint(new XYZ(xin, midY, midZ)), beam.Id, BuiltInCategory.OST_StructuralColumns,
                                    BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Walls).FirstOrDefault();
                    return corner == null ? (null, "free end", 0) : (corner, "corner", 0);
                }
                if (s is FamilyInstance sf && sf.Location is LocationCurve sl && sl.Curve is Line sline
                    && Math.Abs(sline.Direction.Normalize().DotProduct(d)) > 0.98)
                    return (s, "continuous", 0);                                // the next span in line: bars lap into it
                var (sl0, sh0) = LocalBox(s, f);
                return (s, s.Category?.Name ?? "support", atStart ? lo.X - sl0.X - cover : sh0.X - cover - hi.X);
            }
            var startSup = Support(true); var endSup = Support(false);

            var ids = new List<long>();
            var endText = new List<string>();
            // one layer of bars: z its level, up = +1 for bars that bend up at the ends (bottom bars), -1 bend down (top bars)
            void Layer(RebarBarType t, int n, double z, int up, string what)
            {
                double need = Times(p, "anchorage_d", 40, t), lap = Times(p, "lap_d", 50, t);
                double legRoom = (hi.Z - lo.Z) - ob - ot;
                (double x, double leg, string text) End((Element e, string kind, double room) s, bool atStart)
                {
                    double face = atStart ? lo.X : hi.X, sign = atStart ? -1 : 1;
                    if (ends == "none" || s.e == null) return (face - sign * cover, 0, s.e == null ? "free end: stops at cover" : "stops at cover");
                    if (s.kind == "continuous") return (face + sign * lap, 0, $"continues {Mm(lap)} into the next span (lap)");
                    if (s.kind == "corner")
                    {
                        double cleg = Math.Min(Math.Max(Leg(t), need / 2), legRoom);
                        return (face - sign * cover, cleg, $"corner with {s.e.Category?.Name} {s.e.Id.Value}: to cover + 90 degree bend {Mm(cleg)}");
                    }
                    if (s.room >= need) return (face + sign * need, 0, $"{Mm(need)} straight into {s.kind} {s.e.Id.Value}");
                    double into = Math.Max(0, s.room);
                    double leg = Math.Min(Math.Max(Leg(t), need - into), legRoom);
                    return (face + sign * into, leg, $"{Mm(into)} into {s.kind} {s.e.Id.Value} + 90 degree bend {Mm(leg)}");
                }
                var (xa, la, ta) = End(startSup, true);
                var (xb, lb, tb) = End(endSup, false);
                endText.Add($"{what}: start {ta}; end {tb}");
                var pieces = BarStock.Pieces(xa, xb, stock, lap, la, lb);
                double sideOffset = sOff + Dia(t) / 2;
                double firstY = lo.Y + sideOffset;
                double width = hi.Y - lo.Y - 2 * sideOffset;
                if (width <= 0) throw new ArgumentException($"Beam {beam.Id.Value} is too narrow for the {what} layer.");
                for (int i = 0; i < pieces.Count; i++)
                {
                    var (pa, pb) = pieces[i];
                    var pts = new List<XYZ>();
                    if (i == 0 && la > 0) pts.Add(f.OfPoint(new XYZ(pa, firstY, z + up * la)));
                    pts.Add(f.OfPoint(new XYZ(pa, firstY, z)));
                    pts.Add(f.OfPoint(new XYZ(pb, firstY, z)));
                    if (i == pieces.Count - 1 && lb > 0) pts.Add(f.OfPoint(new XYZ(pb, firstY, z + up * lb)));
                    var bar = Bar(doc, RebarStyle.Standard, t, beam, f.BasisY, Poly(pts));
                    FixedNumber(bar, n, width);
                    ids.Add(bar.Id.Value);
                }
                if (pieces.Count > 1) endText.Add($"{what}: {pieces.Count} bars per line, lapped {Mm(lap)} (stock {Mm(stock)})");
            }
            Layer(bottom, nb, lo.Z + ob, +1, "bottom");
            Layer(top, nt, hi.Z - ot, -1, "top");

            // closed stirrups in the cross-section plane (local y-z), closer near the supports (default zone 2 x depth)
            double so = cover + Dia(stirrup) / 2;
            IList<Curve> Ring(double x)
            {
                XYZ Q(double y, double z) => f.OfPoint(new XYZ(x, y, z));
                return new List<Curve>
                {
                    Line.CreateBound(Q(lo.Y + so, lo.Z + so), Q(hi.Y - so, lo.Z + so)), Line.CreateBound(Q(hi.Y - so, lo.Z + so), Q(hi.Y - so, hi.Z - so)),
                    Line.CreateBound(Q(hi.Y - so, hi.Z - so), Q(lo.Y + so, hi.Z - so)), Line.CreateBound(Q(lo.Y + so, hi.Z - so), Q(lo.Y + so, lo.Z + so)),
                };
            }
            double sa = lo.X + Math.Max(cover, U.Mm(50)), sb = hi.X - Math.Max(cover, U.Mm(50));
            double zone = U.LenMm(p, "end_zone", 2 * (hi.Z - lo.Z) * U.MmPerFoot);
            double zoneSpacing = U.LenMm(p, "end_spacing", Math.Min(spacing * U.MmPerFoot, 100));
            var st = Zoned(doc, stirrup, beam, f.BasisX, Ring, sa, sb, spacing, zone, zoneSpacing);
            ids.AddRange(st.ids);
            bool underSlab = JoinGeometryUtils.GetJoinedElements(doc, beam).Any(j => doc.GetElement(j) is Floor);
            made.Add(new
            {
                beam = beam.Id.Value, bottom = $"{nb} x {bottom.Name}", top = $"{nt} x {top.Name}", stirrups = st.text,
                clearSpanMm = Mm(hi.X - lo.X), ends = endText, rebarIds = ids,
                note = underSlab ? "A slab is joined to this beam. If the slab cuts the beam, the top bars sit in the slab zone and Revit may warn 'Rebar is placed completely outside of its host'; the bars are in the right place." : null,
            });
        }
        return new { beams = made.Count, items = made };
    }

    // ---------------------------------------------------------------- footings

    private static object RebarFooting(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var bar = BarType(doc, NeedRef(p, "bar"), "bar");
        double spacing = U.LenMm(p, "spacing", 200), cover = U.LenMm(p, "cover", 50);
        bool topMesh = Flag(p, "top", false), bends = Flag(p, "end_bends", false);
        var topBar = topMesh ? BarType(doc, Has(p, "top_bar") ? p["top_bar"] : p["bar"], "top_bar") : null;
        double topSpacing = U.LenMm(p, "top_spacing", spacing * U.MmPerFoot);
        double stock = U.LenMm(p, "stock_length", 12000);
        var made = new List<object>();
        foreach (var e in Hosts(c, p, BuiltInCategory.OST_StructuralFoundation, "structural foundation"))
        {
            Transform f = e is FamilyInstance fi ? Frame(fi.GetTotalTransform().Origin, fi.GetTotalTransform().BasisX, fi.GetTotalTransform().BasisY)
                                                 : Transform.Identity;
            var (lo, hi) = LocalBox(e, f);
            var ids = new List<long>();
            string bendText = null;
            // one layer: bars along local x (alongX) or y at height z, spread across the other direction
            void Layer(RebarBarType t, double s, double z, bool alongX, double legUp)
            {
                double d = Dia(t), lap = Times(p, "lap_d", 50, t);
                double a = (alongX ? lo.X : lo.Y) + cover + (legUp != 0 ? d / 2 : 0), b = (alongX ? hi.X : hi.Y) - cover - (legUp != 0 ? d / 2 : 0);
                double across0 = (alongX ? lo.Y : lo.X) + cover + d / 2, across1 = (alongX ? hi.Y : hi.X) - cover - d / 2;
                XYZ P(double along, double h) => f.OfPoint(alongX ? new XYZ(along, across0, h) : new XYZ(across0, along, h));
                var pieces = BarStock.Pieces(a, b, stock, lap, Math.Abs(legUp), Math.Abs(legUp));
                for (int i = 0; i < pieces.Count; i++)
                {
                    var (pa, pb) = pieces[i];
                    var pts = new List<XYZ>();
                    if (i == 0 && legUp != 0) pts.Add(P(pa, z + legUp));
                    pts.Add(P(pa, z)); pts.Add(P(pb, z));
                    if (i == pieces.Count - 1 && legUp != 0) pts.Add(P(pb, z + legUp));
                    var r = Bar(doc, RebarStyle.Standard, t, e, alongX ? f.BasisY : f.BasisX, Poly(pts));
                    MaxSpacing(r, s, across1 - across0);
                    ids.Add(r.Id.Value);
                }
            }
            void Mesh(RebarBarType t, double s, bool atTop)
            {
                double d = Dia(t);
                // first layer along local x, second along local y on top of (or under, for the top mesh) the first
                double z1 = atTop ? hi.Z - cover - d / 2 : lo.Z + cover + d / 2;
                double z2 = atTop ? z1 - d : z1 + d;
                double leg = 0;
                if (bends && !atTop)
                {
                    // bottom bars turned up at the edges, up to under the top mesh (or to the top cover)
                    double ceiling = hi.Z - cover - (topMesh ? 2 * Dia(topBar) : 0) - d / 2;
                    leg = Math.Min(U.LenMm(p, "bend_leg", 1e6), ceiling - z2);
                    if (leg < 4 * d) leg = 0;
                    else bendText = $"bottom bars turned up {Mm(leg)} at the edges";
                }
                Layer(t, s, z1, true, leg);
                Layer(t, s, z2, false, leg > 0 ? leg - d : 0);
            }
            Mesh(bar, spacing, false);
            if (topMesh) Mesh(topBar, topSpacing, true);
            made.Add(new
            {
                footing = e.Id.Value, bottom = $"{bar.Name} @ {Mm(spacing)} both ways",
                top = topMesh ? $"{topBar.Name} @ {Mm(topSpacing)} both ways" : null, ends = bendText ?? "straight", rebarIds = ids,
            });
        }
        return new { footings = made.Count, items = made };
    }

    // ---------------------------------------------------------------- slabs: area reinforcement

    private static object RebarArea(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var bar = BarType(doc, NeedRef(p, "bar"), "bar");
        double spacing = U.LenMm(p, "spacing", 200);
        string layers = Choice(p, "layers", "both", "bottom", "top", "both");
        var arType = new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcementType)).FirstElementId();
        if (arType == ElementId.InvalidElementId) arType = AreaReinforcementType.CreateDefaultAreaReinforcementType(doc);
        var hook = ElementId.InvalidElementId;
        var made = new List<object>();
        foreach (var host in c.Elements(p, max: 500))
        {
            if (!(host is Floor floor)) throw new ArgumentException($"Element {host.Id.Value} is not a floor / slab (area reinforcement here works on slabs).");
            if (!RebarHostData.IsValidHost(floor)) throw new ArgumentException($"Slab {floor.Id.Value} cannot host rebar.");
            List<Curve> boundary;
            if (Has(p, "boundary")) boundary = EditContext.Loop(p["boundary"], "boundary", 0).ToList();
            else
            {
                var sketch = doc.GetElement(floor.SketchId) as Sketch ?? throw new InvalidOperationException($"Slab {floor.Id.Value} has no sketch; give 'boundary'.");
                boundary = sketch.Profile.get_Item(0).Cast<Curve>().ToList();
            }
            var major = Has(p, "direction") ? U.NeedPt(p, "direction").Normalize() : XYZ.BasisX;
            var ar = AreaReinforcement.Create(doc, floor, boundary, major, arType, bar.Id, hook);
            // layers: Revit's parameters for the four directions (top / bottom x major / minor)
            var set = new List<string>();
            foreach (var (name, on) in new[]
                     {
                         ("TOP_DIR_1", layers != "bottom"), ("TOP_DIR_2", layers != "bottom"),
                         ("BOTTOM_DIR_1", layers != "top"), ("BOTTOM_DIR_2", layers != "top"),
                     })
            {
                Find.Builtin(ar, "REBAR_SYSTEM_ACTIVE_" + name)?.Set(on ? 1 : 0);
                if (!on) continue;
                Find.Builtin(ar, "REBAR_SYSTEM_BAR_TYPE_" + name)?.Set(bar.Id);
                if (Find.Builtin(ar, "REBAR_SYSTEM_SPACING_" + name) is Parameter sp && !sp.IsReadOnly) sp.Set(spacing);
                set.Add(name.ToLowerInvariant());
            }
            made.Add(new { slab = floor.Id.Value, areaReinforcement = ar.Id.Value, bar = bar.Name, spacingMm = Math.Round(spacing * U.MmPerFoot), layers = set });
        }
        return new { slabs = made.Count, items = made };
    }
}
