namespace RevitMcpBridge;

/// <summary>
/// Shapes read from a linked / imported DWG for modelling: raw curves of one layer, column footprints (clusters of touching
/// curves fitted with an oriented rectangle) and grid lines. All lengths in feet, model coordinates.
/// </summary>
internal static class Cad
{
    /// <summary>A curve of the CAD file: a polyline (2 points = a line) or a circle.</summary>
    public sealed class Item
    {
        public List<XYZ> Points = new List<XYZ>();
        public XYZ Center;           // circles only
        public double Radius;        // circles only
        public bool IsCircle => Center != null;
        public bool Closed;
        public Arc Arc;              // bound arcs: the arc itself (Points holds its tessellation)
        public string Layer;
        public double MinX, MinY, MaxX, MaxY;

        public void Box()
        {
            var pts = IsCircle
                ? new List<XYZ> { Center - new XYZ(Radius, Radius, 0), Center + new XYZ(Radius, Radius, 0) }
                : Points;
            MinX = pts.Min(q => q.X); MinY = pts.Min(q => q.Y); MaxX = pts.Max(q => q.X); MaxY = pts.Max(q => q.Y);
        }
    }

    public static ImportInstance NeedImport(Document doc, Obj p) =>
        Find.Elem(doc, NeedId(p, "id")) as ImportInstance
        ?? throw new ArgumentException("'id' is not a linked or imported CAD file. Use list_links.");

    /// <summary>The curves of one or more layers ('S-BEAM' or 'S-BEAM, S-BEAM-HDLN'; case-insensitive), flattened to z = 0.</summary>
    public static List<Item> Curves(Document doc, ImportInstance imp, string layer)
    {
        var wanted = new HashSet<string>(layer.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()),
                                         StringComparer.OrdinalIgnoreCase);
        var items = new List<Item>();
        var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(GeometryElement ge, Transform tf)
        {
            if (ge == null) return;
            foreach (GeometryObject g in ge)
            {
                if (g is GeometryInstance gi) { Walk(gi.GetSymbolGeometry(), tf.Multiply(gi.Transform)); continue; }
                var style = g.GraphicsStyleId != null && g.GraphicsStyleId != ElementId.InvalidElementId ? doc.GetElement(g.GraphicsStyleId) as GraphicsStyle : null;
                string name = style?.GraphicsStyleCategory?.Name ?? "";
                layers.Add(name);
                if (!wanted.Contains(name)) continue;
                XYZ Flat(XYZ q) { var t = tf.OfPoint(q); return new XYZ(t.X, t.Y, 0); }
                Item it = null;
                if (g is Line line && line.IsBound)
                    it = new Item { Points = { Flat(line.GetEndPoint(0)), Flat(line.GetEndPoint(1)) } };
                else if (g is Arc arc && !arc.IsBound)
                    it = new Item { Center = Flat(arc.Center), Radius = arc.Radius * tf.Scale, Closed = true };
                else if (g is Arc barc)
                {
                    var pts = barc.Tessellate().Select(Flat).ToList();
                    Arc flatArc = null;
                    try { if (pts.Count >= 3) flatArc = Arc.Create(pts[0], pts[pts.Count - 1], pts[pts.Count / 2]); }
                    catch { /* almost straight: kept as its points */ }
                    it = new Item { Points = pts, Arc = flatArc };
                }
                else if (g is PolyLine pl)
                {
                    var pts = pl.GetCoordinates().Select(Flat).ToList();
                    it = new Item { Points = pts, Closed = pts.Count > 3 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.003 };
                }
                if (it == null) continue;
                it.Layer = name;
                it.Box();
                items.Add(it);
            }
        }
        Walk(imp.get_Geometry(new Options()), Transform.Identity);
        var missing = wanted.Where(w => !layers.Contains(w)).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"The CAD file has no layer '{string.Join("', '", missing)}'. Layers: " + string.Join(", ", layers.OrderBy(x => x).Take(80)) + ".");
        return items;
    }

    // ---------------------------------------------------------------- columns

    /// <summary>A column outline found in the drawing.</summary>
    public sealed class Footprint
    {
        public XYZ Center;
        public double Width, Depth;     // along the local x (angle) and y axes, feet
        public double Angle;            // radians, [0, pi/2)
        public string Kind;             // rectangle | circle | section (steel and other outlines)
        public int Curves;
    }

    /// <summary>Group touching curves (bounding boxes within 'gap') and fit each group with an oriented rectangle.</summary>
    public static List<Footprint> Footprints(List<Item> items, double gap)
    {
        int n = items.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Root(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var order = Enumerable.Range(0, n).OrderBy(i => items[i].MinX).ToList();
        for (int a = 0; a < n; a++)
        {
            var A = items[order[a]];
            for (int b = a + 1; b < n; b++)
            {
                var B = items[order[b]];
                if (B.MinX > A.MaxX + gap) break;                       // sweep along x
                if (B.MinY > A.MaxY + gap || B.MaxY < A.MinY - gap) continue;
                parent[Root(order[a])] = Root(order[b]);
            }
        }

        var result = new List<Footprint>();
        foreach (var grp in Enumerable.Range(0, n).GroupBy(Root))
        {
            var members = grp.Select(i => items[i]).ToList();
            if (members.Count == 1 && members[0].IsCircle)
            {
                var c = members[0];
                result.Add(new Footprint { Center = c.Center, Width = 2 * c.Radius, Depth = 2 * c.Radius, Angle = 0, Kind = "circle", Curves = 1 });
                continue;
            }
            var pts = members.SelectMany(m => m.IsCircle ? Circle(m) : m.Points).ToList();
            if (pts.Count < 2) continue;

            // Orientation: direction of the longest straight segment, folded into [0, 90 deg).
            double best = 0, angle = 0;
            foreach (var m in members.Where(m => !m.IsCircle))
                for (int i = 0; i + 1 < m.Points.Count; i++)
                {
                    var d = m.Points[i + 1] - m.Points[i];
                    double len = d.GetLength();
                    if (len > best + 1e-9) { best = len; angle = Math.Atan2(d.Y, d.X); }
                }
            angle %= Math.PI / 2; if (angle < 0) angle += Math.PI / 2;
            if (Math.PI / 2 - angle < 1e-4) angle = 0;
            double cs = Math.Cos(angle), sn = Math.Sin(angle);
            var local = pts.Select(q => (x: q.X * cs + q.Y * sn, y: -q.X * sn + q.Y * cs)).ToList();
            double x0 = local.Min(q => q.x), x1 = local.Max(q => q.x), y0 = local.Min(q => q.y), y1 = local.Max(q => q.y);
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            var center = new XYZ(cx * cs - cy * sn, cx * sn + cy * cs, 0);

            string kind = "section";
            if (IsRectangle(members)) kind = "rectangle";
            result.Add(new Footprint { Center = center, Width = x1 - x0, Depth = y1 - y0, Angle = angle, Kind = kind, Curves = members.Count });
        }
        return result;
    }

    private static IEnumerable<XYZ> Circle(Item c)
    {
        for (int i = 0; i < 8; i++) yield return c.Center + new XYZ(Math.Cos(i * Math.PI / 4), Math.Sin(i * Math.PI / 4), 0) * c.Radius;
    }

    /// <summary>One closed polyline with 4 right-angled corners, or 4 lines forming one.</summary>
    private static bool IsRectangle(List<Item> members)
    {
        List<XYZ> corners;
        if (members.Count == 1 && !members[0].IsCircle && (members[0].Closed || members[0].Points.Count == 4))
        {
            corners = members[0].Points.ToList();
            if (corners.Count > 1 && corners[0].DistanceTo(corners[corners.Count - 1]) < 0.003) corners.RemoveAt(corners.Count - 1);
        }
        else if (members.Count == 4 && members.All(m => !m.IsCircle && m.Points.Count == 2))
        {
            corners = new List<XYZ>();
            foreach (var q in members.SelectMany(m => m.Points))
                if (!corners.Any(k => k.DistanceTo(q) < 0.003)) corners.Add(q);
            if (corners.Count != 4) return false;
            var c0 = new XYZ(corners.Average(q => q.X), corners.Average(q => q.Y), 0);
            corners = corners.OrderBy(q => Math.Atan2(q.Y - c0.Y, q.X - c0.X)).ToList();
        }
        else return false;
        if (corners.Count != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            var a = corners[(i + 3) % 4] - corners[i]; var b = corners[(i + 1) % 4] - corners[i];
            if (a.GetLength() < 0.003 || b.GetLength() < 0.003) return false;
            if (Math.Abs(a.Normalize().DotProduct(b.Normalize())) > 0.01) return false;
        }
        return true;
    }

    /// <summary>The type for a footprint and the rotation to give it, or null. 'map': size key -> type name, checked first.</summary>
    public static (SectionSize type, double rotation)? Match(Footprint f, List<SectionSize> types, double tol, Dictionary<string, string> map)
    {
        if (map != null)
        {
            string direct = Sections.Key(f.Width, f.Depth), swapped = Sections.Key(f.Depth, f.Width);
            string name = map.TryGetValue(direct, out var n1) ? n1 : map.TryGetValue(swapped, out var n2) ? n2 : null;
            if (name != null)
            {
                var t = types.FirstOrDefault(x => Find.TypeLabel(x.Symbol).Equals(name, StringComparison.OrdinalIgnoreCase)
                                                || x.Symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (t == null) throw new ArgumentException($"'types': no structural column type named '{name}'.");
                bool sw = Math.Abs(f.Width - t.H) + Math.Abs(f.Depth - t.B) < Math.Abs(f.Width - t.B) + Math.Abs(f.Depth - t.H);
                return (t, f.Angle + (sw ? Math.PI / 2 : 0));
            }
        }
        var hits = new List<(SectionSize t, double rot)>();
        foreach (var t in types)
        {
            if (t.Round != (f.Kind == "circle")) continue;
            if (t.Steel != (f.Kind == "section")) continue;   // steel outlines to steel types, concrete to concrete
            if (Math.Abs(f.Width - t.B) <= tol && Math.Abs(f.Depth - t.H) <= tol) hits.Add((t, f.Angle));
            else if (Math.Abs(f.Width - t.H) <= tol && Math.Abs(f.Depth - t.B) <= tol) hits.Add((t, f.Angle + Math.PI / 2));
        }
        if (hits.Count == 0) return null;
        // Several types of the same size: the one used most in the model (the project's standard), then by name.
        var best = hits.OrderByDescending(h => h.t.Instances).ThenBy(h => h.t.Symbol.Name).First();
        return (best.t, best.rot);
    }

    /// <summary>
    /// The 'types' parameter: size key -> type name ({"650x1300": "C4 650x1300"} for columns, {"300": "B 300x600"} for
    /// beams and walls). Keys are compared without spaces and case.
    /// </summary>
    public static Dictionary<string, string> TypeMap(Obj p) =>
        Sub(p, "types") is Obj tm
            ? tm.ToDictionary(kv => kv.Key.Replace(" ", "").ToLowerInvariant(), kv => Text(kv.Value), StringComparer.OrdinalIgnoreCase)
            : null;

    // ---------------------------------------------------------------- already modelled?

    /// <summary>A floor of the list whose bounding box contains the plan point.</summary>
    public static Floor ExistingFloor(IEnumerable<Floor> floors, XYZ pt) =>
        floors.FirstOrDefault(f =>
        {
            var bb = f.get_BoundingBox(null);
            return bb != null && pt.X >= bb.Min.X && pt.X <= bb.Max.X && pt.Y >= bb.Min.Y && pt.Y <= bb.Max.Y;
        });

    /// <summary>An existing element of the list whose location curve runs along 'axis' (within 'near') at about elevation z.</summary>
    public static Element ExistingAlong(IEnumerable<Element> els, Curve axis, double z, double near)
    {
        var mid = axis.Evaluate(0.5, true);
        var dir = axis is Line l ? l.Direction : null;
        foreach (var e in els)
        {
            if (!(e.Location is LocationCurve lc)) continue;
            var c = lc.Curve;
            double cz = c.Evaluate(0.5, true).Z;
            if (Math.Abs(cz - z) > 3.3) continue;                       // more than ~1 m apart in height
            var probe = new XYZ(mid.X, mid.Y, cz);
            var r = c.Project(probe);
            if (r == null || r.Distance > near) continue;
            if (dir != null && c is Line cl && Math.Abs(cl.Direction.DotProduct(dir)) < 0.995) continue;
            return e;
        }
        return null;
    }

    /// <summary>An existing structural column within 'near' of the point whose height covers elevation z, or null.</summary>
    public static FamilyInstance ExistingColumn(List<FamilyInstance> cols, XYZ pt, double z, double near)
    {
        foreach (var c in cols)
        {
            if (!(c.Location is LocationPoint lp)) continue;
            if (Math.Abs(lp.Point.X - pt.X) > near || Math.Abs(lp.Point.Y - pt.Y) > near) continue;
            var bb = c.get_BoundingBox(null);
            if (bb == null || (z >= bb.Min.Z - 0.01 && z <= bb.Max.Z + 0.01)) return c;
        }
        return null;
    }

    // ---------------------------------------------------------------- grids

    /// <summary>Straight lines of the layer at least minLen long; collinear overlapping copies are merged.</summary>
    public static List<(XYZ a, XYZ b)> GridLines(List<Item> items, double minLen)
    {
        var lines = items.Where(i => !i.IsCircle && i.Points.Count == 2 && i.Points[0].DistanceTo(i.Points[1]) >= minLen)
                         .Select(i => (a: i.Points[0], b: i.Points[1])).ToList();
        var merged = new List<(XYZ a, XYZ b)>();
        foreach (var l in lines)
        {
            int k = merged.FindIndex(m => SameLine(m, l, 0.02));
            if (k < 0) { merged.Add(l); continue; }
            // union of the two extents along the line
            var m0 = merged[k]; var dir = (m0.b - m0.a).Normalize();
            var all = new[] { m0.a, m0.b, l.a, l.b }.OrderBy(q => (q - m0.a).DotProduct(dir)).ToList();
            merged[k] = (all[0], all[3]);
        }
        return merged;
    }

    /// <summary>Parallel (within 0.5 deg) and on the same infinite line (within 'dist' feet).</summary>
    public static bool SameLine((XYZ a, XYZ b) m, (XYZ a, XYZ b) l, double dist)
    {
        var d1 = (m.b - m.a).Normalize(); var d2 = (l.b - l.a).Normalize();
        if (Math.Abs(d1.DotProduct(d2)) < Math.Cos(0.5 * Math.PI / 180)) return false;
        var v = l.a - m.a;
        return Math.Abs(v.X * d1.Y - v.Y * d1.X) < dist;
    }
}
