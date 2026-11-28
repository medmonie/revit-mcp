namespace RevitMcpBridge;

/// <summary>
/// Outer outline of a drawing whose edge is spread over several layers, broken at columns or drawn dashed: the curves
/// are drawn onto a grid, thickened to close gaps up to 'gap', everything reachable from outside is flood-filled, and
/// what remains is the slab. Its outline is traced, pulled back by the thickening, simplified and snapped to the
/// nearest CAD vertex, so straight edges land exactly on the drawing. Feet, plan coordinates.
/// </summary>
internal static class CadRaster
{
    public sealed class Result
    {
        public List<List<XYZ>> Outlines = new List<List<XYZ>>();
        public List<bool> Snapped = new List<bool>();   // per outline: corners snapped to CAD points (false: grid outline kept, snapping made it cross itself)
        public double Cell;
        public int Width, Height;
    }

    private const double MaxCells = 24e6;          // about 24 million grid cells (several arrays of this size are held)
    private const double MaxRadiusCells = 2000;    // the gap may span at most this many cells

    public static Result Outlines(List<Cad.Item> items, double gap, double cell, double minArea)
    {
        if (!(cell > 0) || double.IsInfinity(cell)) throw new ArgumentException("'cell' must be a positive length.");
        if (!(gap >= 0) || double.IsInfinity(gap)) throw new ArgumentException("'gap' must be zero or a positive length.");
        // every curve as straight pieces
        var segs = new List<(XYZ a, XYZ b)>();
        foreach (var it in items)
        {
            if (it.IsCircle)
            {
                for (int k = 0; k < 32; k++)
                {
                    double a0 = k * Math.PI / 16, a1 = (k + 1) * Math.PI / 16;
                    segs.Add((it.Center + new XYZ(Math.Cos(a0), Math.Sin(a0), 0) * it.Radius, it.Center + new XYZ(Math.Cos(a1), Math.Sin(a1), 0) * it.Radius));
                }
                continue;
            }
            for (int k = 0; k + 1 < it.Points.Count; k++) segs.Add((it.Points[k], it.Points[k + 1]));
        }
        var res = new Result();
        if (segs.Count == 0) return res;

        double minX = segs.Min(s => Math.Min(s.a.X, s.b.X)), maxX = segs.Max(s => Math.Max(s.a.X, s.b.X));
        double minY = segs.Min(s => Math.Min(s.a.Y, s.b.Y)), maxY = segs.Max(s => Math.Max(s.a.Y, s.b.Y));
        // grid size: keep it under ~16 million cells by enlarging the cell if needed
        double span = Math.Max(maxX - minX, maxY - minY);
        while ((maxX - minX) / cell * (maxY - minY) / cell > 16e6) cell *= 1.5;
        double radiusCells = Math.Ceiling(gap / 2 / cell);
        if (radiusCells > MaxRadiusCells)
            throw new ArgumentException("'gap' is too large for this 'cell' size; reduce 'gap' or increase 'cell'.");
        int r = Math.Max(1, (int)radiusCells);                              // thickening radius in cells
        int pad = r + 3;
        double width = Math.Ceiling((maxX - minX) / cell) + 2.0 * pad + 1, height = Math.Ceiling((maxY - minY) / cell) + 2.0 * pad + 1;
        if (width * height > MaxCells)    // a long, thin drawing is not caught by the area loop above
            throw new ArgumentException($"The outline grid would need {width * height:N0} cells (limit {MaxCells:N0}); increase 'cell' or reduce 'gap'.");
        int W = (int)width, H = (int)height;
        res.Cell = cell; res.Width = W; res.Height = H;
        double ox = minX - pad * cell, oy = minY - pad * cell;
        int Ix(double x) => (int)Math.Floor((x - ox) / cell);
        int Iy(double y) => (int)Math.Floor((y - oy) / cell);

        // 1. thin lines
        var wall = new bool[W * H];
        foreach (var (a, b) in segs)
        {
            double len = a.DistanceTo(b);
            int steps = Math.Max(1, (int)Math.Ceiling(len / (cell * 0.5)));
            for (int k = 0; k <= steps; k++)
            {
                var q = a + (b - a) * ((double)k / steps);
                int ix = Ix(q.X), iy = Iy(q.Y);
                if (ix >= 0 && iy >= 0 && ix < W && iy < H) wall[iy * W + ix] = true;
            }
        }
        // 2. thicken: chessboard distance to the nearest line cell <= r
        var dist = Distance(wall, W, H);
        var thick = new bool[W * H];
        for (int i = 0; i < W * H; i++) thick[i] = dist[i] <= r;

        // 3. flood the outside from the border through cells that are not thickened lines
        var outside = new bool[W * H];
        var queue = new Queue<int>();
        void Seed(int i) { if (!thick[i] && !outside[i]) { outside[i] = true; queue.Enqueue(i); } }
        for (int x = 0; x < W; x++) { Seed(x); Seed((H - 1) * W + x); }
        for (int y = 0; y < H; y++) { Seed(y * W); Seed(y * W + W - 1); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % W, y = i / W;
            if (x > 0) Seed(i - 1);
            if (x < W - 1) Seed(i + 1);
            if (y > 0) Seed(i - W);
            if (y < H - 1) Seed(i + W);
        }
        // 4. pull the outline back by the thickening: inside = not within r of the outside
        var outDist = Distance(outside, W, H);
        var inside = new bool[W * H];
        for (int i = 0; i < W * H; i++) inside[i] = !outside[i] && outDist[i] > r;

        // 5. trace, simplify, snap
        var vertices = segs.SelectMany(s => new[] { s.a, s.b }).ToList();
        var vIndex = new Dictionary<(int, int), List<XYZ>>();
        foreach (var v in vertices)
        {
            var key = (Ix(v.X) / 4, Iy(v.Y) / 4);
            if (!vIndex.TryGetValue(key, out var l)) vIndex[key] = l = new List<XYZ>();
            l.Add(v);
        }
        XYZ Snap(XYZ q, double tol)
        {
            int kx = Ix(q.X) / 4, ky = Iy(q.Y) / 4; XYZ best = null; double bd = tol;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (vIndex.TryGetValue((kx + dx, ky + dy), out var l))
                        foreach (var v in l) { double d = v.DistanceTo(q); if (d < bd) { bd = d; best = v; } }
            return best ?? q;
        }
        foreach (var loop in Trace(inside, W, H))
        {
            var pts = loop.Select(p => new XYZ(ox + p.x * cell, oy + p.y * cell, 0)).ToList();
            if (Poly.Area(pts) < minArea) continue;
            var simple = Poly.Tidy(Simplify(pts, cell * 0.75));
            var snapped = Poly.Tidy(simple.Select(q => Snap(q, cell * 3)).ToList());
            bool useSnapped = snapped.Count >= 3 && Poly.IsSimple(snapped);
            var chosen = useSnapped ? snapped : simple;
            if (chosen.Count >= 3 && Poly.Area(chosen) >= minArea) { res.Outlines.Add(chosen); res.Snapped.Add(useSnapped); }
        }
        return res;
    }

    /// <summary>Chessboard distance (in cells) from every cell to the nearest 'true' cell (two-pass chamfer).</summary>
    private static int[] Distance(bool[] src, int W, int H)
    {
        const int Inf = int.MaxValue / 4;
        var d = new int[W * H];
        for (int i = 0; i < W * H; i++) d[i] = src[i] ? 0 : Inf;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x, v = d[i];
                if (x > 0) v = Math.Min(v, d[i - 1] + 1);
                if (y > 0)
                {
                    v = Math.Min(v, d[i - W] + 1);
                    if (x > 0) v = Math.Min(v, d[i - W - 1] + 1);
                    if (x < W - 1) v = Math.Min(v, d[i - W + 1] + 1);
                }
                d[i] = v;
            }
        for (int y = H - 1; y >= 0; y--)
            for (int x = W - 1; x >= 0; x--)
            {
                int i = y * W + x, v = d[i];
                if (x < W - 1) v = Math.Min(v, d[i + 1] + 1);
                if (y < H - 1)
                {
                    v = Math.Min(v, d[i + W] + 1);
                    if (x < W - 1) v = Math.Min(v, d[i + W + 1] + 1);
                    if (x > 0) v = Math.Min(v, d[i + W - 1] + 1);
                }
                d[i] = v;
            }
        return d;
    }

    /// <summary>
    /// Outer boundaries of the 'inside' regions as loops of grid corners, counter-clockwise. Boundary edges keep the
    /// region on their left; at a corner touched diagonally the walk turns left, which keeps diagonal regions apart.
    /// </summary>
    private static List<List<(int x, int y)>> Trace(bool[] inside, int W, int H)
    {
        bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && inside[y * W + x];
        // directed edges: from corner -> list of next corners
        var next = new Dictionary<(int, int), List<(int, int)>>();
        void Add((int, int) a, (int, int) b) { if (!next.TryGetValue(a, out var l)) next[a] = l = new List<(int, int)>(2); l.Add(b); }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (!inside[y * W + x]) continue;
                if (!In(x, y - 1)) Add((x, y), (x + 1, y));          // bottom edge, going +x
                if (!In(x + 1, y)) Add((x + 1, y), (x + 1, y + 1));  // right edge, going +y
                if (!In(x, y + 1)) Add((x + 1, y + 1), (x, y + 1));  // top edge, going -x
                if (!In(x - 1, y)) Add((x, y + 1), (x, y));          // left edge, going -y
            }
        var loops = new List<List<(int x, int y)>>();
        while (next.Count > 0)
        {
            var start = next.Keys.First();
            var loop = new List<(int x, int y)>();
            var cur = start; (int, int) prevDir = (0, 0);
            while (true)
            {
                loop.Add(cur);
                if (!next.TryGetValue(cur, out var outs) || outs.Count == 0) break;
                int pick = 0;
                if (outs.Count > 1)
                {
                    // turn left first: the candidate whose direction is a left turn from the previous one
                    for (int k = 0; k < outs.Count; k++)
                    {
                        var d = (outs[k].Item1 - cur.Item1, outs[k].Item2 - cur.Item2);
                        if (d.Item1 == -prevDir.Item2 && d.Item2 == prevDir.Item1) { pick = k; break; }
                    }
                }
                var nxt = outs[pick];
                outs.RemoveAt(pick);
                if (outs.Count == 0) next.Remove(cur);
                prevDir = (nxt.Item1 - cur.Item1, nxt.Item2 - cur.Item2);
                cur = nxt;
                if (cur.Equals(start)) break;
            }
            // keep counter-clockwise loops (outer boundaries); clockwise ones are holes
            double area = 0;
            for (int i = 0; i < loop.Count; i++) { var a = loop[i]; var b = loop[(i + 1) % loop.Count]; area += (double)a.Item1 * b.Item2 - (double)b.Item1 * a.Item2; }
            if (area > 0 && loop.Count >= 4) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>Douglas-Peucker on a closed loop.</summary>
    private static List<XYZ> Simplify(List<XYZ> pts, double tol)
    {
        if (pts.Count < 4) return pts;
        // split at the two points farthest apart so both halves are open polylines
        int i0 = 0, i1 = 0; double best = -1;
        for (int k = 0; k < pts.Count; k++) { double d = pts[k].DistanceTo(pts[0]); if (d > best) { best = d; i1 = k; } }
        var a = pts.Skip(i0).Take(i1 - i0 + 1).ToList();
        var b = pts.Skip(i1).Concat(pts.Take(1)).ToList();
        var ra = Dp(a, tol); var rb = Dp(b, tol);
        ra.RemoveAt(ra.Count - 1); rb.RemoveAt(rb.Count - 1);
        return ra.Concat(rb).ToList();
    }

    private static List<XYZ> Dp(List<XYZ> p, double tol)
    {
        if (p.Count < 3) return p.ToList();
        var keep = new bool[p.Count]; keep[0] = keep[p.Count - 1] = true;
        var stack = new Stack<(int, int)>(); stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            var a = p[s]; var d = p[e] - a; double len = d.GetLength();
            int idx = -1; double max = tol;
            for (int k = s + 1; k < e; k++)
            {
                var v = p[k] - a;
                double dd = len < 1e-9 ? v.GetLength() : Math.Abs(v.X * d.Y - v.Y * d.X) / len;
                if (dd > max) { max = dd; idx = k; }
            }
            if (idx > 0) { keep[idx] = true; stack.Push((s, idx)); stack.Push((idx, e)); }
        }
        return p.Where((q, k) => keep[k]).ToList();
    }
}
