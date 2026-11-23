namespace RevitMcpBridge;

/// <summary>The auto-join check (read only): overlapping elements that are not joined, and the volume counted twice.</summary>
internal static class OverlapRead
{
    public static object FindOverlaps(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        bool volumes = Flag(p, "volumes", true);
        int limit = Int(p, "limit", 200, 1, 5000);
        var r = Overlaps.Search(doc, p, volumes, Int(p, "max_pairs", 20000, 1, 100000));

        string Label(Element e) => Describe.Label(doc, e);
        var review = r.Pairs.Where(x => x.NeedsReview).ToList();
        var normal = r.Pairs.Where(x => !x.NeedsReview).ToList();
        var groups = normal.GroupBy(x =>
        {
            string ca = x.A.Category.Name, cb = x.B.Category.Name;
            return string.CompareOrdinal(ca, cb) <= 0 ? ca + " / " + cb : cb + " / " + ca;
        }).OrderByDescending(g => g.Sum(x => x.Volume ?? 0)).Select(g => new
        {
            categories = g.Key, pairs = g.Count(),
            overlap_m3 = volumes ? U.Volume(g.Sum(x => x.Volume ?? 0)) : (double?)null,
        }).ToList();

        var reviewPairs = review.Take(limit).Select(x => new
        {
            a = Overlaps.ComparisonSide(x.A), b = Overlaps.ComparisonSide(x.B), kind = x.Kind,
            overlap_m3 = x.Volume is double v ? U.Volume(v) : (double?)null,
        }).ToList();

        var pairs = normal.OrderByDescending(x => x.Volume ?? 0).Take(limit).Select(x => new
        {
            a = x.A.Id.Value, aLabel = Label(x.A), b = x.B.Id.Value, bLabel = Label(x.B),
            overlap_m3 = x.Volume is double v ? U.Volume(v) : (double?)null,
            level = Describe.LevelName(doc, x.A),
        }).ToList();

        return new
        {
            scannedElements = r.Scanned, elementsInScope = r.Candidates, unjoinedPairs = normal.Count,
            doubleCounted_m3 = volumes ? U.Volume(normal.Sum(x => x.Volume ?? 0)) : (double?)null,
            byCategories = groups, pairs, pairsTruncated = normal.Count > limit,
            reviewPairCount = review.Count, reviewPairs, reviewTruncated = review.Count > limit,
            steelPairsSkipped = r.SkippedSteel, geometrySkipped = r.SkippedGeometry,
            unmeasuredPairs = volumes ? r.Pairs.Count(x => x.Volume == null) : (int?)null,
            complete = !r.TimedOut,
            resumeAfter = r.ResumeAfter,
            note = (r.TimedOut ? $"Pass resume_after='{r.ResumeAfter}' with the same filters to continue this scan. " : "") +
                   "Volumes sum measured pairs. Multiple intersections can share the same volume. " +
                   "Review coincidence, containment and failed volume measurements using the element metadata. Use include_steel to include steel pairs.",
        };
    }
}
