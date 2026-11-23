namespace RevitMcpBridge;

/// <summary>Plan polygons (lists of points at z = 0, feet): area, containment, clean-up, and Revit curve loops.</summary>
internal static class Poly
{
    public static double Area(List<XYZ> l)
    {
        double a = 0;
        for (int i = 0; i < l.Count; i++) { var p = l[i]; var q = l[(i + 1) % l.Count]; a += p.X * q.Y - q.X * p.Y; }
        return Math.Abs(a) / 2;
    }

    public static bool Inside(List<XYZ> poly, XYZ pt)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > pt.Y) != (b.Y > pt.Y) && pt.X < (b.X - a.X) * (pt.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The point at z = 0.</summary>
    public static XYZ Flat(XYZ q) => new XYZ(q.X, q.Y, 0);

    public static XYZ Centroid(List<XYZ> l) => new XYZ(l.Average(q => q.X), l.Average(q => q.Y), 0);

    /// <summary>A closed Revit curve loop through the points at elevation z; 'what' names the outline in errors.</summary>
    public static CurveLoop Loop(List<XYZ> pts, double z, string what = "outline")
    {
        if (pts.Count < 3) throw new ArgumentException($"{what} needs at least 3 different points.");
        var loop = new CurveLoop();
        for (int i = 0; i < pts.Count; i++)
        {
            XYZ a = pts[i], b = pts[(i + 1) % pts.Count];
            loop.Append(U.Segment(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z), $"{what} edge {i + 1}"));
        }
        return loop;
    }

    /// <summary>Drop repeated points, spikes (the outline doubling back) and straight-through points, until none is left.</summary>
    public static List<XYZ> Tidy(List<XYZ> pts)
    {
        var o = pts.ToList();
        bool changed = true;
        while (changed && o.Count > 3)
        {
            changed = false;
            for (int i = 0; i < o.Count && o.Count > 3; i++)
            {
                var a = o[(i + o.Count - 1) % o.Count]; var b = o[i]; var c = o[(i + 1) % o.Count];
                var u = b - a; var v = c - b;
                double lu = u.GetLength(), lv = v.GetLength();
                bool drop = lu < 0.01 || lv < 0.01;                                       // repeated point
                if (!drop)
                {
                    double cross = Math.Abs(u.X * v.Y - u.Y * v.X) / (lu * lv);
                    drop = cross < 1e-3;                                                  // straight through or doubling back
                }
                if (drop) { o.RemoveAt(i); changed = true; i--; }
            }
        }
        return o;
    }

    /// <summary>No two non-adjacent edges of the closed loop touch or cross.</summary>
    public static bool IsSimple(List<XYZ> p)
    {
        int n = p.Count;
        double Orient(XYZ a, XYZ b, XYZ c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        bool Cross(XYZ a, XYZ b, XYZ c, XYZ d)
        {
            if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) ||
                Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y)) return false;
            double o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
            return o1 * o2 <= 0 && o3 * o4 <= 0;
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;   // adjacent through the closing edge
                if (Cross(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n])) return false;
            }
        return true;
    }
}
