namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 15 (P1): what is above, below or beside a point or an element, and clear distances, by casting rays
/// through a 3D view (ReferenceIntersector), into loaded links too. Reads only: nothing is placed or changed.
/// </summary>
internal static class QueryRead
{
    private static readonly BuiltInCategory[] NotPhysical =
    {
        BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces, BuiltInCategory.OST_HVAC_Zones,
        BuiltInCategory.OST_Mass, BuiltInCategory.OST_Lines, BuiltInCategory.OST_Cameras, BuiltInCategory.OST_Levels,
        BuiltInCategory.OST_Grids, BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_CLines,
    };

    /// <summary>A 3D view to cast in: the one named, else the default {3D}, else the first 3D view (not a template).</summary>
    private static View3D View(Document doc, Obj p)
    {
        if (Has(p, "view3d"))
            return Find.ViewOf(doc, NeedRef(p, "view3d")) as View3D ?? throw new ArgumentException("'view3d' must be a 3D view.");
        var all = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate).ToList();
        return all.FirstOrDefault(v => v.Name == "{3D}") ?? all.FirstOrDefault(v => !v.IsPerspective)
               ?? throw new InvalidOperationException("Ray casting needs a 3D view in this model; create one with view_3d_create or open the default 3D view once.");
    }

    public static XYZ Direction(object v, string what)
    {
        if (v is string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "up": return XYZ.BasisZ;
                case "down": return -XYZ.BasisZ;
                case "east": case "+x": return XYZ.BasisX;
                case "west": case "-x": return -XYZ.BasisX;
                case "north": case "+y": return XYZ.BasisY;
                case "south": case "-y": return -XYZ.BasisY;
            }
            throw new ArgumentException($"{what}: up, down, east, west, north, south or [x, y, z].");
        }
        if (!(v is List<object> c) || c.Count != 3) throw new ArgumentException($"{what}: up, down, east, west, north, south or [x, y, z].");
        var d = new XYZ(Number(c[0], what), Number(c[1], what), Number(c[2], what));
        if (d.GetLength() < 1e-9) throw new ArgumentException($"{what} has no length.");
        return d.Normalize();
    }

    private sealed class Caster
    {
        public ReferenceIntersector Ri;
        public Document Host;
        public View3D View;
        public bool Links;

        public static Caster Make(Document doc, Obj p, string catParam)
        {
            var view = View(doc, p);
            bool links = Flag(p, "links", true);
            ElementFilter filter;
            if (Has(p, catParam))
            {
                var cats = Find.Cats(doc, p, catParam);
                if (links && !cats.Contains(BuiltInCategory.OST_RvtLinks)) cats.Add(BuiltInCategory.OST_RvtLinks);   // a link's elements are reached through the link instance
                filter = new ElementMulticategoryFilter(cats);
            }
            else filter = new ElementMulticategoryFilter(NotPhysical.ToList(), true);
            var ri = new ReferenceIntersector(filter, FindReferenceTarget.Element, view) { FindReferencesInRevitLinks = links };
            return new Caster { Ri = ri, Host = doc, View = view, Links = links };
        }

        /// <summary>Hits along a ray: the first per element, nearest first, skipping the given (host or link) element.</summary>
        public List<Obj> Cast(XYZ origin, XYZ dir, double maxFeet, int limit, (long host, long linked) skip)
        {
            var seen = new HashSet<(long, long)>();
            var hits = new List<Obj>();
            foreach (var rwc in Ri.Find(origin, dir).OrderBy(r => r.Proximity))
            {
                if (rwc.Proximity > maxFeet) break;
                var r = rwc.GetReference();
                long hid = r.ElementId.Value, lid = r.LinkedElementId != ElementId.InvalidElementId ? r.LinkedElementId.Value : -1;
                if ((hid, lid) == skip || (lid < 0 && skip.linked < 0 && hid == skip.host) || !seen.Add((hid, lid))) continue;
                var hostEl = Host.GetElement(r.ElementId);
                Element el = hostEl; string linkName = null; Document doc = Host;
                if (lid >= 0 && hostEl is RevitLinkInstance li)
                {
                    doc = li.GetLinkDocument();
                    el = doc?.GetElement(r.LinkedElementId);
                    linkName = doc?.Title;
                }
                else if (hostEl is RevitLinkInstance) continue;   // the link instance itself, not an element in it
                if (el == null) continue;
                var o = new Obj
                {
                    ["id"] = el.Id.Value, ["category"] = el.Category?.Name, ["type"] = Describe.TypeLabel(doc, el),
                    ["level"] = Describe.LevelName(doc, el), ["distance"] = U.Out(rwc.Proximity), ["at"] = U.Xyz(r.GlobalPoint),
                };
                if (linkName != null) { o["link"] = linkName; o["linkInstance"] = hid; }
                hits.Add(o);
                if (hits.Count >= limit) break;
            }
            return hits;
        }
    }

    /// <summary>The source element (in the host or a link) and its box in host coordinates.</summary>
    private static (Element e, Document doc, BoundingBoxXYZ box, long linkInstance) Source(Document host, Obj p, string idParam)
    {
        var doc = LinkScope.DocOf(host, p, out var link);
        var e = doc.GetElement(NeedId(p, idParam)) ?? throw new ArgumentException($"No element {Ref(p, idParam)} in {(link != null ? "link '" + doc.Title + "'" : "this model")}.");
        return (e, doc, BoxIn(e, link?.Total), link?.Instance.Id.Value ?? -1);
    }

    private static BoundingBoxXYZ BoxIn(Element e, Transform tf)
    {
        var bb = e.get_BoundingBox(null) ?? throw new ArgumentException($"Element {e.Id.Value} has no extents.");
        if (tf == null) return bb;
        var pts = new List<XYZ>();
        foreach (double x in new[] { bb.Min.X, bb.Max.X })
            foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                    pts.Add(tf.OfPoint(bb.Transform.OfPoint(new XYZ(x, y, z))));
        return new BoundingBoxXYZ { Min = new XYZ(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z)), Max = new XYZ(pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z)) };
    }

    private const double Gap = 0.001;   // start a ray this far outside the source's box (feet) so it does not hit the source

    private static XYZ Start(BoundingBoxXYZ b, XYZ dir)
    {
        var c = (b.Min + b.Max) / 2;
        if (dir.Z < -0.7) return new XYZ(c.X, c.Y, b.Min.Z - Gap);
        if (dir.Z > 0.7) return new XYZ(c.X, c.Y, b.Max.Z + Gap);
        return c;
    }

    public static object RayCast(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        double max = U.Len(p, "max_distance", U.Mm(50000));
        int limit = Int(p, "limit", 5, 1, 100);
        var caster = Caster.Make(doc, p, "category");
        var dirs = Has(p, "direction") && p["direction"] is List<object> l && l.Count > 0 && l[0] is string
            ? l.Select((v, i) => Direction(v, $"direction {i + 1}")).ToList()
            : new List<XYZ> { Direction(Has(p, "direction") ? p["direction"] : "down", "'direction'") };

        XYZ origin = null; (long, long) skip = (-1, -1); Obj from = null; BoundingBoxXYZ box = null;
        if (Has(p, "id"))
        {
            var (e, sdoc, b, linkInst) = Source(doc, p, "id");
            box = b;
            skip = linkInst >= 0 ? (linkInst, e.Id.Value) : (e.Id.Value, -1L);
            from = new Obj { ["id"] = e.Id.Value, ["category"] = e.Category?.Name, ["type"] = Describe.TypeLabel(sdoc, e), ["box"] = new Obj { ["min"] = U.Xyz(b.Min), ["max"] = U.Xyz(b.Max) } };
            if (linkInst >= 0) from["link"] = sdoc.Title;
        }
        else origin = U.NeedPt(p, "origin");

        var rays = dirs.Select(d =>
        {
            var o = origin ?? Start(box, d);
            return (object)new { direction = new[] { Math.Round(d.X, 4), Math.Round(d.Y, 4), Math.Round(d.Z, 4) }, origin = U.Xyz(o), hits = caster.Cast(o, d, max, limit, skip) };
        }).ToList();
        return new
        {
            units = U.Name, view = caster.View.Name, links = caster.Links, from, rays,
            note = "Hits are the first face of each element along the ray, nearest first (distance from the ray origin). "
                 + "Only elements visible in the 3D view are found; links are searched when links is true (default).",
        };
    }

    /// <summary>
    /// Clear distance from each element of a category (bottom, or top for 'up') to the first element beyond it, sampled at
    /// the centre and, for linear elements, near both ends. The smallest is reported.
    /// </summary>
    public static object Clearances(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var sdoc = LinkScope.DocOf(doc, p, out var link);
        var cats = Find.Cats(sdoc, p, "category");
        var dir = Direction(Has(p, "direction") ? p["direction"] : "down", "'direction'");
        if (Math.Abs(dir.Z) < 0.7) throw new ArgumentException("clearances measures up or down; use ray_cast for other directions.");
        double max = U.Len(p, "max_distance", U.Mm(50000));
        double? below = Dbl(p, "less_than") is double lt ? U.In(lt) : (double?)null;
        int limit = Int(p, "limit", 200, 1, 5000);
        double seconds = Dbl(p, "max_seconds") ?? 40;
        long after = Has(p, "resume_after") ? long.Parse(NeedRef(p, "resume_after"), CultureInfo.InvariantCulture) : long.MinValue;
        var caster = Caster.Make(doc, p, "target_category");
        var tf = link?.Total;

        var elems = Find.Scope(sdoc, p, cats).Where(e => e.Id.Value > after).OrderBy(e => e.Id.Value).ToList();
        var rows = new List<Obj>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string resume = null; int scanned = 0, noHit = 0;
        foreach (var e in elems)
        {
            if (sw.Elapsed.TotalSeconds > seconds) { resume = rows.Count > 0 || scanned > 0 ? (e.Id.Value - 1).ToString(CultureInfo.InvariantCulture) : null; break; }
            scanned++;
            var bb = e.get_BoundingBox(null);
            if (bb == null) continue;
            var box = BoxIn(e, tf);
            double z = dir.Z < 0 ? box.Min.Z - Gap : box.Max.Z + Gap;
            var samples = new List<XYZ> { new XYZ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, z) };
            if (e.Location is LocationCurve lc)
            {
                var c = lc.Curve;
                foreach (double t in new[] { 0.1, 0.9 })
                {
                    var q = c.Evaluate(t, true);
                    if (tf != null) q = tf.OfPoint(q);
                    samples.Add(new XYZ(q.X, q.Y, z));
                }
            }
            var skip = link != null ? (link.Instance.Id.Value, e.Id.Value) : (e.Id.Value, -1L);
            Obj best = null;
            foreach (var s in samples)
            {
                var hit = caster.Cast(s, dir, max, 1, skip).FirstOrDefault();
                if (hit != null && (best == null || (double)hit["distance"] < (double)best["distance"])) best = hit;
            }
            if (best == null) { noHit++; continue; }
            if (below is double b && U.In((double)best["distance"]) >= b) continue;
            var row = new Obj
            {
                ["id"] = e.Id.Value, ["category"] = e.Category?.Name, ["type"] = Describe.TypeLabel(sdoc, e), ["level"] = Describe.LevelName(sdoc, e),
                ["clearance"] = best["distance"], ["to"] = best,
            };
            rows.Add(row);
        }
        return new
        {
            units = U.Name, direction = dir.Z < 0 ? "down" : "up", view = caster.View.Name, scanned, inScope = elems.Count, noHit,
            complete = resume == null, resumeAfter = resume, count = rows.Count, truncated = rows.Count > limit,
            items = rows.OrderBy(r => (double)r["clearance"]).Take(limit).ToList(),
            note = "clearance: from the element's " + (dir.Z < 0 ? "bottom" : "top") + " to the first element " + (dir.Z < 0 ? "below" : "above")
                 + " (smallest of the centre and, for linear elements, points near both ends). Elements hidden in the 3D view are not seen.",
        };
    }
}
