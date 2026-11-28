using System.Diagnostics;

namespace RevitMcpBridge;

/// <summary>Hard clashes between this model's elements and the elements of a loaded Revit link (an IFC link too, which Revit
/// keeps as a Revit link): which pairs intersect, by how much and where.</summary>
internal static class ClashRead
{
    private static readonly BuiltInCategory[] NotPhysical =
    {
        BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces, BuiltInCategory.OST_HVAC_Zones,
        BuiltInCategory.OST_Mass, BuiltInCategory.OST_Lines, BuiltInCategory.OST_RvtLinks, BuiltInCategory.OST_Cameras,
    };

    private sealed class Clash
    {
        public Element Host, Linked;
        public double Volume;           // cubic feet
        public XYZ Where;               // host coordinates
    }

    private static Outline Box(BoundingBoxXYZ box, Transform tf)
    {
        var corners = new List<XYZ>();
        foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
                foreach (double z in new[] { box.Min.Z, box.Max.Z })
                    corners.Add(tf.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z))));
        return new Outline(new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)),
                           new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z)));
    }

    public static object LinkClashes(Rc rc, Obj p)
    {
        var host = rc.Doc;
        if (!Has(p, "link")) throw new ArgumentException("Give link: the Revit (or IFC) link to check against, from list_links.");
        var link = LinkScope.Of(host, p);
        var linkDoc = link.Doc;
        var toLink = link.Total.Inverse;

        var hostCats = Find.Cats(host, p, "category", Overlaps.DefaultCategories);
        List<BuiltInCategory> linkCats = Has(p, "link_category") ? Find.Cats(linkDoc, p, "link_category") : null;
        double minVolume = Dbl(p, "min_volume") is double mv ? mv / U.M3PerFt3 : 1e-6;
        double seconds = Dbl(p, "max_seconds") ?? 40;
        if (seconds <= 0) throw new ArgumentException("max_seconds must be positive.");
        int limit = Int(p, "limit", 200, 1, 5000);
        long after = Has(p, "resume_after") ? long.Parse(NeedRef(p, "resume_after"), CultureInfo.InvariantCulture) : long.MinValue;

        var hostElems = Find.Scope(host, p, hostCats).Where(e => e.Id.Value > after).OrderBy(e => e.Id.Value).ToList();
        ElementFilter linkFilter = linkCats != null
            ? new ElementMulticategoryFilter(linkCats)
            : (ElementFilter)new ElementMulticategoryFilter(NotPhysical.ToList(), true);

        var clashes = new List<Clash>();
        var sw = Stopwatch.StartNew();
        int scanned = 0, skippedGeometry = 0, unmeasured = 0;
        string resume = null;
        var linkSolids = new Dictionary<long, List<Solid>>();

        foreach (var h in hostElems)
        {
            if (sw.Elapsed.TotalSeconds > seconds) { resume = Text(h.Id.Value - 1); break; }
            scanned++;
            var box = h.get_BoundingBox(null);
            var solids = Geo.Solids(h);
            if (box == null || solids.Count == 0) { skippedGeometry++; continue; }
            var outline = Box(box, toLink);
            var inLink = solids.Select(s => SolidUtils.CreateTransformed(s, toLink)).ToList();

            var candidates = new FilteredElementCollector(linkDoc).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline)).WherePasses(linkFilter).ToElements();
            foreach (var l in candidates)
            {
                if (l.Category == null || l.Category.CategoryType != CategoryType.Model) continue;
                if (!linkSolids.TryGetValue(l.Id.Value, out var ls)) linkSolids[l.Id.Value] = ls = Geo.Solids(l);
                if (ls.Count == 0) continue;
                double volume = 0; XYZ where = null; bool failed = false;
                foreach (var a in inLink)
                    foreach (var b in ls)
                    {
                        try
                        {
                            var r = BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Intersect);
                            if (r == null || r.Volume <= 1e-9) continue;
                            if (where == null) where = r.ComputeCentroid();
                            volume += r.Volume;
                        }
                        catch { failed = true; }
                    }
                if (volume >= minVolume) clashes.Add(new Clash { Host = h, Linked = l, Volume = volume, Where = link.Total.OfPoint(where) });
                else if (failed) unmeasured++;
            }
        }

        string Cat(Element e) => e.Category?.Name ?? "(none)";
        var groups = clashes.GroupBy(c => Cat(c.Host) + " / " + Cat(c.Linked)).OrderByDescending(g => g.Count()).Select(g => new
        {
            categories = g.Key, clashes = g.Count(), volume_m3 = U.Volume(g.Sum(c => c.Volume)),
        }).ToList();
        var list = clashes.OrderByDescending(c => c.Volume).Take(limit).Select(c => new
        {
            hostId = c.Host.Id.Value, host = Describe.Label(host, c.Host), hostLevel = Describe.LevelName(host, c.Host),
            linkedId = c.Linked.Id.Value, linked = Describe.Label(linkDoc, c.Linked),
            volume_m3 = U.Volume(c.Volume), at = U.Xyz(c.Where),
        }).ToList();

        return new
        {
            units = U.Name, link = link.Info(), hostElementsInScope = hostElems.Count, scanned, clashCount = clashes.Count,
            byCategories = groups, clashes = list, truncated = clashes.Count > limit,
            hostElementsWithoutSolids = skippedGeometry, pairsNotMeasured = unmeasured,
            complete = resume == null, resumeAfter = resume,
            note = (resume != null ? $"Time budget used: pass resume_after='{resume}' with the same parameters to continue. " : "")
                   + "Hard clashes: solids that intersect (volume above min_volume). at = a point inside the clash, in host coordinates. "
                   + "Clearance (soft) clashes are not checked. "
                   + "Linked ids belong to the linked model (read them with get_element and link=).",
        };
    }
}
