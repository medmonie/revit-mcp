namespace RevitMcpBridge;

/// <summary>
/// Members drawn by their two edges (beams, walls): parallel lines a member width apart, or concentric arcs, give the
/// member's centre line and width. Closed outlines (slabs): polylines, circles and chains of lines / arcs.
/// All lengths in feet, plan coordinates (z = 0).
/// </summary>
internal static class CadPairs
{
    public sealed class Member
    {
        public Curve Axis;        // centre line (Line or Arc) at z = 0
        public double Width;
        public bool Curved => Axis is Arc;
    }

    private sealed class Seg
    {
        public XYZ A, B, Dir, Normal;
        public double Len;
        public readonly List<(double t0, double t1)> Used = new List<(double, double)>();
    }

    private static bool Free(Seg s, double t0, double t1)
    {
        double len = t1 - t0;
        foreach (var (u0, u1) in s.Used)
            if (Math.Min(t1, u1) - Math.Max(t0, u0) > 0.1 * len) return false;
        return true;
    }

    /// <summary>Straight and curved members from edge pairs whose gap is between minW and maxW.</summary>
    public static List<Member> Members(List<Cad.Item> items, double minW, double maxW, double minLen)
    {
        var result = new List<Member>();

        // ---- straight: every line and every straight polyline segment
        var segs = new List<Seg>();
        foreach (var it in items.Where(i => !i.IsCircle && i.Arc == null))
            for (int k = 0; k + 1 < it.Points.Count; k++)
            {
                var a = it.Points[k]; var b = it.Points[k + 1];
                double len = a.DistanceTo(b);
                if (len < minLen) continue;
                var d = (b - a) / len;
                segs.Add(new Seg { A = a, B = b, Dir = d, Normal = new XYZ(-d.Y, d.X, 0), Len = len });
            }
        var cands = new List<(Seg s, Seg o, double w, double t0, double t1)>();
        var order = Enumerable.Range(0, segs.Count).OrderBy(i => Math.Min(segs[i].A.X, segs[i].B.X)).ToList();
        for (int x = 0; x < order.Count; x++)
        {
            var s = segs[order[x]];
            double sMaxX = Math.Max(s.A.X, s.B.X);
            for (int y = x + 1; y < order.Count; y++)
            {
                var o = segs[order[y]];
                if (Math.Min(o.A.X, o.B.X) > sMaxX + maxW) break;
                if (Math.Abs(s.Dir.DotProduct(o.Dir)) < Math.Cos(Math.PI / 180)) continue;   // parallel within 1 deg
                double w = Math.Abs((o.A - s.A).DotProduct(s.Normal));
                if (w < minW || w > maxW) continue;
                double p0 = (o.A - s.A).DotProduct(s.Dir), p1 = (o.B - s.A).DotProduct(s.Dir);
                double t0 = Math.Max(0, Math.Min(p0, p1)), t1 = Math.Min(s.Len, Math.Max(p0, p1));
                double ov = t1 - t0;
                if (ov < minLen || ov < 0.5 * Math.Min(s.Len, o.Len)) continue;
                cands.Add((s, o, w, t0, t1));
            }
        }
        // Narrowest gap first: the two faces of one member are closer than the faces of neighbouring members.
        foreach (var c in cands.OrderBy(c => c.w).ThenByDescending(c => c.t1 - c.t0))
        {
            var s = c.s; var o = c.o;
            double q0 = (s.A + s.Dir * c.t0 - o.A).DotProduct(o.Dir), q1 = (s.A + s.Dir * c.t1 - o.A).DotProduct(o.Dir);
            double u0 = Math.Min(q0, q1), u1 = Math.Max(q0, q1);
            if (!Free(s, c.t0, c.t1) || !Free(o, u0, u1)) continue;
            s.Used.Add((c.t0, c.t1)); o.Used.Add((u0, u1));
            double side = (o.A - s.A).DotProduct(s.Normal) > 0 ? 1 : -1;
            var shift = s.Normal * (side * c.w / 2);
            var a = s.A + s.Dir * c.t0 + shift; var b = s.A + s.Dir * c.t1 + shift;
            result.Add(new Member { Axis = Line.CreateBound(a, b), Width = c.w });
        }

        // ---- curved: concentric arcs
        var arcs = items.Where(i => i.Arc != null).Select(i => i.Arc).OrderBy(a => a.Center.X).ToList();
        var usedArc = new HashSet<int>();
        var arcCands = new List<(int i, int j, double w)>();
        for (int i = 0; i < arcs.Count; i++)
            for (int j = i + 1; j < arcs.Count; j++)
            {
                if (arcs[j].Center.X - arcs[i].Center.X > 0.02) break;
                if (arcs[i].Center.DistanceTo(arcs[j].Center) > 0.02) continue;
                double w = Math.Abs(arcs[i].Radius - arcs[j].Radius);
                if (w < minW || w > maxW) continue;
                arcCands.Add((i, j, w));
            }
        foreach (var (i, j, w) in arcCands.OrderBy(c => c.w))
        {
            if (usedArc.Contains(i) || usedArc.Contains(j)) continue;
            var c0 = arcs[i].Center; double rm = (arcs[i].Radius + arcs[j].Radius) / 2;
            // angular overlap of the two arcs, measured from the first arc's start, counter-clockwise
            double Ang(XYZ q) { var v = q - c0; return Math.Atan2(v.Y, v.X); }
            (double s0, double s1) Span(Arc a)
            {
                double x0 = Ang(a.GetEndPoint(0)), x1 = Ang(a.GetEndPoint(1)), xm = Ang(a.Evaluate(0.5, true));
                // order so that going counter-clockwise from x0 passes xm before x1
                double Norm(double v, double from) { while (v < from) v += 2 * Math.PI; while (v >= from + 2 * Math.PI) v -= 2 * Math.PI; return v; }
                double e = Norm(x1, x0), m = Norm(xm, x0);
                return m <= e ? (x0, e) : (x1, Norm(x0, x1));
            }
            var (a0, a1) = Span(arcs[i]); var (b0, b1) = Span(arcs[j]);
            while (b0 < a0 - Math.PI) { b0 += 2 * Math.PI; b1 += 2 * Math.PI; }
            while (b0 > a0 + Math.PI) { b0 -= 2 * Math.PI; b1 -= 2 * Math.PI; }
            double o0 = Math.Max(a0, b0), o1 = Math.Min(a1, b1);
            if ((o1 - o0) * rm < minLen) continue;
            usedArc.Add(i); usedArc.Add(j);
            result.Add(new Member { Axis = Arc.Create(new XYZ(c0.X, c0.Y, 0), rm, o0, o1, XYZ.BasisX, XYZ.BasisY), Width = w });
        }
        return result;
    }

    // ---------------------------------------------------------------- closed outlines

    /// <summary>Closed loops: closed polylines, circles, and chains of lines / arcs / open polylines that meet end to end.</summary>
    private const int MaxPairs = 2_000_000;

    public static List<List<XYZ>> Loops(List<Cad.Item> items, double tol)
    {
        var loops = new List<List<XYZ>>();
        var open = new List<List<XYZ>>();   // open pieces: lines, arcs (as points), open polylines
        foreach (var it in items)
        {
            if (it.IsCircle)
            {
                loops.Add(Enumerable.Range(0, 24).Select(k => it.Center + new XYZ(Math.Cos(k * Math.PI / 12), Math.Sin(k * Math.PI / 12), 0) * it.Radius).ToList());
                continue;
            }
            var pts = it.Points.ToList();
            if (it.Closed || pts.Count >= 4 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.003) { pts.RemoveAt(pts.Count - 1); loops.Add(pts); }
            else if (pts.Count >= 2) open.Add(pts);
        }
        // Bridge open pieces end to end: every piece end is matched to its nearest free end within 'tol' (shortest gaps
        // first, each end used once), so dashed edges and edges broken at columns still close. Closed cycles are loops.
        int n = open.Count;
        XYZ End(int k) => (k & 1) == 0 ? open[k >> 1][0] : open[k >> 1][open[k >> 1].Count - 1];
        var pairs = new List<(int a, int b, double d)>();
        var byX = Enumerable.Range(0, 2 * n).OrderBy(k => End(k).X).ToList();
        for (int x = 0; x < byX.Count; x++)
            for (int y = x + 1; y < byX.Count; y++)
            {
                XYZ pa = End(byX[x]), pb = End(byX[y]);
                if (pb.X - pa.X > tol) break;
                double d = pa.DistanceTo(pb);
                if (d <= tol && (byX[x] >> 1 != byX[y] >> 1 || open[byX[x] >> 1].Count > 2))
                {
                    pairs.Add((byX[x], byX[y], d));
                    if (pairs.Count > MaxPairs)
                        throw new ArgumentException($"More than {MaxPairs:N0} possible end-to-end joins within the gap; reduce 'gap' or narrow the layer.");
                }
            }
        var link = Enumerable.Repeat(-1, 2 * n).ToArray();
        foreach (var (a, b, d) in pairs.OrderBy(q => q.d))
            if (link[a] < 0 && link[b] < 0) { link[a] = b; link[b] = a; }
        var seen = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (seen[i]) continue;
            var chain = new List<XYZ>(); var visited = new List<int>();
            int piece = i, enter = 2 * i;   // enter at the start of piece i
            bool closed = false;
            while (true)
            {
                if (seen[piece]) { closed = piece == i; break; }
                seen[piece] = true; visited.Add(piece);
                var pts = open[piece];
                chain.AddRange((enter & 1) == 0 ? pts : Enumerable.Reverse(pts));
                int exit = enter ^ 1, next = link[exit];
                if (next < 0) break;
                piece = next >> 1; enter = next;
            }
            if (closed && chain.Count >= 3) loops.Add(chain);
            else foreach (var v in visited.Skip(1)) seen[v] = false;   // let the other pieces start their own chain
        }
        // drop repeated points, spikes and straight-through vertices
        return loops.Select(Poly.Tidy).Where(l => l.Count >= 3).ToList();
    }
}
