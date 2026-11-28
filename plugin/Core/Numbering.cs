using Autodesk.Revit.DB.Structure;

namespace RevitMcpBridge;

/// <summary>
/// Revit's structural numbering schemas (rebar, fabric, couplers): the numbers each partition uses. Shared by the read
/// list_numbering and the edit rebar_numbering. Uses only the calls both Revit 2024 and 2027 keep (2027 renamed the rest).
/// </summary>
internal static class Numbering
{
    public static List<NumberingSchema> Schemas(Document doc) =>
        NumberingSchema.GetSchemasInDocument(doc).Select(id => doc.GetElement(id) as NumberingSchema).Where(s => s != null).ToList();

    /// <summary>
    /// rebar | fabric | coupler, from the parameter the schema numbers (REBAR_NUMBER, FABRIC_NUMBER, COUPLER_NUMBER):
    /// Revit 2024 names every schema "NumberingSchema", and 2027 retires SchemaType; the numbered parameter is in both.
    /// </summary>
    public static string Kind(NumberingSchema s)
    {
        long pid = s.NumberingParameterId.Value;
        if (pid == (long)BuiltInParameter.REBAR_NUMBER) return "rebar";
        if (pid == (long)BuiltInParameter.FABRIC_NUMBER) return "fabric";
        if (pid == (long)BuiltInParameter.COUPLER_NUMBER || pid == (long)BuiltInParameter.COUPLER_MARK) return "coupler";
        string n = (s.Name ?? "").ToLowerInvariant();
        if (n.Contains("coupler")) return "coupler";
        if (n.Contains("fabric")) return "fabric";
        if (n.Contains("rebar") || n.Contains("reinforcement")) return "rebar";
        return n;
    }

    public static NumberingSchema Pick(Document doc, string spec)
    {
        var all = Schemas(doc);
        string want = (spec ?? "rebar").Trim();
        var hit = all.FirstOrDefault(s => string.Equals(s.Name, want, StringComparison.OrdinalIgnoreCase))
                  ?? all.FirstOrDefault(s => Kind(s) == want.ToLowerInvariant());
        return hit ?? throw new ArgumentException($"No numbering schema '{want}'. In this model: {string.Join(", ", all.Select(s => Kind(s)))}.");
    }

    /// <summary>The numbers a partition uses, as ranges ("1-5, 7"), with their count and the gaps between.</summary>
    public static Obj Partition(NumberingSchema s, string partition)
    {
        var ranges = s.GetNumbers(partition).OrderBy(r => r.Low).ToList();
        var used = ranges.Select(r => r.Low == r.High ? $"{r.Low}" : $"{r.Low}-{r.High}").ToList();
        var gaps = new List<string>();
        for (int i = 1; i < ranges.Count; i++)
        {
            int a = ranges[i - 1].High + 1, b = ranges[i].Low - 1;
            if (b >= a) gaps.Add(a == b ? $"{a}" : $"{a}-{b}");
        }
        return new Obj
        {
            ["partition"] = partition, ["numbers"] = string.Join(", ", used), ["count"] = ranges.Sum(r => r.High - r.Low + 1),
            ["first"] = ranges.Count > 0 ? ranges[0].Low : (int?)null, ["last"] = ranges.Count > 0 ? ranges[ranges.Count - 1].High : (int?)null,
            ["gaps"] = gaps,
        };
    }

    public static object ListNumbering(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var schemas = Schemas(doc).Select(s => new
        {
            id = s.Id.Value, name = s.Name, kind = Kind(s),
            numberParameter = doc.GetElement(s.NumberingParameterId)?.Name ?? (s.NumberingParameterId.Value < 0 ? ((BuiltInParameter)s.NumberingParameterId.Value).ToString() : null),
            partitions = s.GetNumberingSequences().OrderBy(x => x).Select(x => Partition(s, x)).ToList(),
        }).ToList();
        var o = new Obj
        {
            ["schemas"] = schemas,
            ["note"] = "A partition is the value of the elements' Partition parameter: change it with set_parameters (bip:NUMBER_PARTITION_PARAM) "
                     + "to move bars to another sequence. rebar_numbering removes gaps, shifts a partition's numbers or changes one number.",
        };
#if REVIT2024 || REVIT2025
        try { o["minimumDigits"] = NumberingSchema.GetMinimumNumberOfDigits(doc); } catch { /* optional */ }
#endif
        return o;
    }
}
