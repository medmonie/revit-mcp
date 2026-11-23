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
    };

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
        if (kinds.Count == 0) kinds = Kinds.Keys.ToList();
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
            notFixed = remaining,
            note = "Only warnings with one safe fix are changed. 'notFixed' lists the rest by message, to review in Revit (Manage > Warnings).",
        };
    }
}
