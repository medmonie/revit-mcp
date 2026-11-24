namespace RevitMcpBridge;

/// <summary>Fix Revit warnings that have one safe, mechanical fix; everything else is only reported.</summary>
internal static class FixOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["fix_warnings"] = FixWarnings;
    }

    /// <summary>The warning kinds this operation can fix, with how.</summary>
    private static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["joined_disjoint"] = "'Highlighted elements are joined but do not intersect': unjoin the pair (no geometry or quantity changes).",
        ["duplicate_instances"] = "'There are identical instances in the same place': delete the copies, keeping the lowest id of each set "
                                  + "(the copies double-count quantities). Deletes elements: runs only when named in 'kinds'.",
        ["overlapping_lines"] = "'Highlighted lines overlap' (model / detail lines): delete a straight line lying completely within another "
                                + "collinear line. Deletes elements: runs only when named in 'kinds'.",
    };

    /// <summary>Kinds run when 'kinds' is not given: only those that change no element count.</summary>
    private static readonly string[] DefaultKinds = { "joined_disjoint" };

    private static bool Says(FailureMessage w, string text) =>
        (w.GetDescriptionText() ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>True when segment b lies completely on segment a (collinear, within 1 mm).</summary>
    private static bool Within(Line a, Line b)
    {
        double tol = U.Mm(1);
        foreach (var q in new[] { b.GetEndPoint(0), b.GetEndPoint(1) })
        {
            var r = a.Project(q);
            if (r == null || r.Distance > tol) return false;
            double t = (q - a.GetEndPoint(0)).DotProduct(a.Direction);
            if (t < -tol || t > a.Length + tol) return false;
        }
        return true;
    }

    private static bool IsJoinedDisjoint(FailureMessage w)
    {
        var id = w.GetFailureDefinitionId();
        return id == BuiltInFailures.JoinElementsFailures.JoiningDisjointWarn || id == BuiltInFailures.JoinElementsFailures.JoiningDisjoint
               || w.GetDescriptionText().IndexOf("joined but do not intersect", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static object FixWarnings(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var kinds = StrList(p, "kinds", 20, required: false);
        if (kinds.Count == 0) kinds = DefaultKinds.ToList();
        foreach (var k in kinds)
            if (!Kinds.ContainsKey(k)) throw new ArgumentException($"'kinds': unknown kind '{k}'. Supported: " + string.Join(", ", Kinds.Keys) + ".");
        var only = Has(p, "ids") ? new HashSet<long>(Ids(p, "ids").Select(i => i.Value)) : null;

        var warnings = doc.GetWarnings();
        int unjoined = 0, alreadyApart = 0;
        var pairs = new List<long[]>();
        var handled = new HashSet<FailureMessage>();
        if (kinds.Contains("joined_disjoint", StringComparer.OrdinalIgnoreCase))
        {
            var done = new HashSet<string>();
            foreach (var w in warnings.Where(IsJoinedDisjoint))
            {
                var ids = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct().ToList();
                if (only != null && !ids.Any(i => only.Contains(i.Value))) continue;
                handled.Add(w);
                for (int i = 0; i < ids.Count; i++)
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        Element a = doc.GetElement(ids[i]), b = doc.GetElement(ids[j]);
                        if (a == null || b == null) continue;
                        string key = Math.Min(a.Id.Value, b.Id.Value) + "-" + Math.Max(a.Id.Value, b.Id.Value);
                        if (!done.Add(key)) continue;
                        if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) { alreadyApart++; continue; }
                        JoinGeometryUtils.UnjoinGeometry(doc, a, b);
                        unjoined++;
                        if (pairs.Count < 200) pairs.Add(new[] { a.Id.Value, b.Id.Value });
                    }
            }
        }
        var deleted = new List<long>();
        int duplicateSets = 0, lineSkipped = 0;
        if (kinds.Contains("duplicate_instances", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var w in warnings.Where(w => Says(w, "identical instances")))
            {
                var ids = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct().Where(i => doc.GetElement(i) != null)
                    .OrderBy(i => i.Value).ToList();
                if (only != null && !ids.Any(i => only.Contains(i.Value))) continue;
                if (ids.Count < 2) continue;
                handled.Add(w);
                duplicateSets++;
                foreach (var id in ids.Skip(1))
                    if (doc.GetElement(id) != null) { doc.Delete(id); deleted.Add(id.Value); }
            }
        }
        if (kinds.Contains("overlapping_lines", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var w in warnings.Where(w => Says(w, "lines overlap")))
            {
                var lines = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct()
                    .Select(i => doc.GetElement(i) as CurveElement).Where(e => e != null && e.GeometryCurve is Line).ToList();
                if (only != null && !lines.Any(e => only.Contains(e.Id.Value))) continue;
                bool any = false;
                for (int i = 0; i < lines.Count; i++)
                    for (int j = 0; j < lines.Count; j++)
                    {
                        if (i == j || !lines[i].IsValidObject || !lines[j].IsValidObject) continue;
                        Line a = (Line)lines[i].GeometryCurve, b = (Line)lines[j].GeometryCurve;
                        // delete b when it lies within a; for two equal lines keep the lower id
                        if (!Within(a, b)) continue;
                        if (Within(b, a) && lines[j].Id.Value < lines[i].Id.Value) continue;
                        deleted.Add(lines[j].Id.Value);
                        doc.Delete(lines[j].Id);
                        any = true;
                    }
                if (any) handled.Add(w); else lineSkipped++;
            }
        }

        var remaining = warnings.Where(w => !handled.Contains(w)).GroupBy(w => w.GetDescriptionText())
            .OrderByDescending(g => g.Count()).Take(25)
            .Select(g => new { text = g.Key, count = g.Count(), sampleElements = g.First().GetFailingElements().Take(6).Select(i => i.Value).ToList() }).ToList();
        return new
        {
            warningsBefore = warnings.Count,
            fixedWarnings = handled.Count,
            unjoinedPairs = unjoined,
            pairsNotJoined = alreadyApart,
            pairs,
            kinds,
            duplicateSets,
            overlappingLinesNotContained = lineSkipped,
            deletedIds = deleted,
            notFixed = remaining,
            note = "Only warnings with one safe fix are changed. 'notFixed' lists the rest by message, to review in Revit (Manage > Warnings).",
        };
    }
}
