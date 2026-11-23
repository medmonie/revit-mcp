using System.Diagnostics;

namespace RevitMcpBridge;

/// <summary>Solid geometry of elements: solids, overlaps between elements, face areas by orientation.</summary>
internal static class Geo
{
    private static readonly Options Fine = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };

    public static List<Solid> Solids(Element e)
    {
        var list = new List<Solid>();
        Collect(e.get_Geometry(Fine), list, 0);
        return list;
    }

    private static void Collect(GeometryElement ge, List<Solid> list, int depth)
    {
        if (ge == null || depth > 6) return;
        foreach (GeometryObject g in ge)
        {
            if (g is Solid s && s.Volume > 1e-9) list.Add(s);
            else if (g is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), list, depth + 1);
        }
    }

    /// <summary>Volume shared by two elements in cubic feet, or null when Revit cannot intersect their solids.</summary>
    public static double? OverlapVolume(Element a, Element b)
    {
        try
        {
            double v = 0;
            var sb = Solids(b);
            foreach (var x in Solids(a))
                foreach (var y in sb)
                {
                    var r = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                    if (r != null) v += r.Volume;
                }
            return v;
        }
        catch { return null; }
    }

    public sealed class Areas { public double Side, Bottom, Top; }

    /// <summary>Face areas of an element's solids (square feet), split by face direction: top, bottom (soffit) and sides.</summary>
    public static Areas FaceAreas(Element e)
    {
        var a = new Areas();
        foreach (var s in Solids(e))
            foreach (Face f in s.Faces)
            {
                double area = f.Area;
                if (area <= 0) continue;
                XYZ n;
                try
                {
                    var bb = f.GetBoundingBox();
                    n = f.ComputeNormal((bb.Min + bb.Max) / 2);
                }
                catch { continue; }
                if (n.Z > 0.7) a.Top += area;
                else if (n.Z < -0.7) a.Bottom += area;
                else a.Side += area;
            }
        return a;
    }
}

/// <summary>
/// Pairs of elements whose geometry overlaps but that are not joined: the overlap is counted twice in take-offs.
/// Shared by the read action find_overlaps and the edit operation join_overlaps.
/// </summary>
internal static class Overlaps
{
    public sealed class Pair
    {
        public Element A, B;
        public double? Volume;   // cubic feet; null when not computed or not computable
        public string Kind = "overlap";
        public bool NeedsReview => Kind != "overlap";
    }

    public static double ElementVolume(Element e)
    {
        var p = Find.Builtin(e, "HOST_VOLUME_COMPUTED");
        return p != null && p.HasValue ? p.AsDouble() : 0;
    }

    public sealed class Result
    {
        public readonly List<Pair> Pairs = new List<Pair>();
        public int Scanned, Candidates;
        public bool TimedOut;
        public string ResumeAfter;
        public int SkippedSteel, SkippedGeometry;
    }

    public static readonly BuiltInCategory[] DefaultCategories =
    {
        BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Floors,
    };

    public static Result Search(Document doc, Obj p, bool computeVolume, int maxPairs)
    {
        var cats = Find.Cats(doc, p, "category", DefaultCategories);
        double seconds = Dbl(p, "max_seconds") ?? 40;
        if (seconds <= 0) throw new ArgumentException("max_seconds must be positive.");
        bool includeSteel = Flag(p, "include_steel", false);
        string familyContains = Str(p, "family_contains");
        long after = long.MinValue, partnerAfter = long.MaxValue;
        if (Has(p, "resume_after"))
        {
            var parts = NeedRef(p, "resume_after").Split(':');
            if (parts.Length > 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out after)
                || (parts.Length == 2 && !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out partnerAfter)))
                throw new ArgumentException("resume_after must be the cursor returned by the previous call.");
        }

        var all = Find.Scope(doc, p, cats).OrderBy(e => e.Id.Value).ToList();
        var inScope = new HashSet<long>(all.Select(e => e.Id.Value));
        var categories = new ElementMulticategoryFilter(cats);
        var r = new Result { Candidates = all.Count };
        var sw = Stopwatch.StartNew();
        var steel = new Dictionary<long, bool>();
        bool IsSteel(Element e)
        {
            if (!steel.TryGetValue(e.Id.Value, out bool value)) steel[e.Id.Value] = value = Materials.IsSteel(doc, e, familyContains);
            return value;
        }

        bool Stop() => sw.Elapsed.TotalSeconds > seconds || r.Pairs.Count >= maxPairs;
        foreach (var a in all)
        {
            long aId = a.Id.Value;
            if (aId < after || (aId == after && partnerAfter == long.MaxValue)) continue;
            long lastPartner = aId == after ? partnerAfter : long.MinValue;
            r.ResumeAfter = $"{Text(aId)}:{Text(lastPartner)}";
            if (Stop()) { r.TimedOut = true; return r; }
            r.Scanned++;
            var box = a.get_BoundingBox(null);
            if (box == null) { r.SkippedGeometry++; continue; }
            ElementIntersectsElementFilter intersects;
            try
            {
                intersects = new ElementIntersectsElementFilter(a);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { r.SkippedGeometry++; continue; }
            using (intersects)
            using (var bounds = new Outline(box.Min, box.Max))
            using (var coarse = new BoundingBoxIntersectsFilter(bounds))
            using (var candidates = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(categories).WherePasses(coarse))
            {
                foreach (var bid in candidates.ToElementIds().OrderBy(id => id.Value))
                {
                    long bId = bid.Value;
                    if (bId <= lastPartner || bId <= aId || !inScope.Contains(bId)) continue;
                    if (Stop()) { r.TimedOut = true; return r; }
                    r.ResumeAfter = $"{Text(aId)}:{Text(bId)}";
                    var b = doc.GetElement(bid);
                    if (!intersects.PassesFilter(b) || JoinGeometryUtils.AreElementsJoined(doc, a, b)) continue;
                    if (!includeSteel && (IsSteel(a) || IsSteel(b))) { r.SkippedSteel++; continue; }
                    bool sameCategory = a.Category.Id == b.Category.Id;
                    var pair = new Pair { A = a, B = b, Volume = computeVolume || sameCategory ? Geo.OverlapVolume(a, b) : null };
                    if (pair.Volume is double v && v < 1e-6) continue;   // touching only
                    if ((computeVolume || sameCategory) && pair.Volume == null)
                        pair.Kind = "unmeasured";
                    else if (sameCategory && pair.Volume is double ov)
                        pair.Kind = Classify(ov, ElementVolume(a), ElementVolume(b));
                    r.Pairs.Add(pair);
                }
            }
        }
        r.ResumeAfter = null;
        return r;
    }

    private static string Classify(double overlap, double aVolume, double bVolume)
    {
        double smaller = Math.Min(aVolume, bVolume), larger = Math.Max(aVolume, bVolume);
        if (smaller <= 0 || overlap < 0.95 * smaller) return "overlap";
        if (Math.Abs(overlap - smaller) <= 1e-6 * smaller)
            return Math.Abs(overlap - larger) <= 1e-6 * larger ? "coincident_geometry" : "contained";
        return "near_coincident";
    }

    public static object ComparisonSide(Element e)
    {
        var dependents = e.GetDependentElements(null).Where(id => id != e.Id).ToList();
        return new
        {
            id = e.Id.Value, typeId = e.GetTypeId().Value, label = Describe.Label(e.Document, e),
            createdPhaseId = e.CreatedPhaseId.Value, demolishedPhaseId = e.DemolishedPhaseId.Value,
            designOptionId = e.DesignOption?.Id.Value,
            dependentCount = dependents.Count, dependentIds = dependents.Take(20).Select(id => id.Value).ToList(),
        };
    }

    /// <summary>Rank of an element in the cut priority (0 = cuts everything after it).</summary>
    public static int Rank(Element e, List<BuiltInCategory> priority)
    {
        if (e.Category == null) return int.MaxValue;
        int i = priority.IndexOf((BuiltInCategory)(int)e.Category.Id.Value);
        return i < 0 ? int.MaxValue : i;
    }
}
