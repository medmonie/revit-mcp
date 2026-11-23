using Autodesk.Revit.DB.Structure;

namespace RevitMcpBridge;

/// <summary>Bar-by-bar list for a bar bending schedule: one row per bar set (rebar, and bars of area / path systems).</summary>
internal static class RebarRead
{
    private static double? Mm(Element e, string bip) => Find.Builtin(e, bip) is Parameter p && p.HasValue ? Math.Round(p.AsDouble() * U.MmPerFoot) : (double?)null;

    public static object ListRebar(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        IEnumerable<Element> bars = Has(p, "view")
            ? new FilteredElementCollector(doc, Find.ViewOf(doc, NeedRef(p, "view")).Id)
            : new FilteredElementCollector(doc);
        bars = ((FilteredElementCollector)bars).OfCategory(BuiltInCategory.OST_Rebar).WhereElementIsNotElementType().ToElements();
        var hostFilter = Has(p, "hosts") ? new HashSet<long>(Ids(p, "hosts", 5000).Select(i => i.Value)) : null;
        string level = Str(p, "level");
        var rows = new List<Obj>();
        foreach (var e in bars)
        {
            ElementId hostId = e is Rebar r ? r.GetHostId() : e is RebarInSystem ris ? ris.GetHostId() : ElementId.InvalidElementId;
            if (hostFilter != null && !hostFilter.Contains(hostId.Value)) continue;
            var host = doc.GetElement(hostId);
            if (level != null && !string.Equals(host != null ? Describe.LevelName(doc, host) : null, level, StringComparison.OrdinalIgnoreCase)) continue;
            var type = doc.GetElement(e.GetTypeId()) as RebarBarType;
            double dia = type != null ? Math.Round(type.BarNominalDiameter * U.MmPerFoot, 1) : 0;
            int qty = Find.Builtin(e, "REBAR_ELEM_QUANTITY_OF_BARS")?.AsInteger() ?? (e is Rebar rb ? rb.Quantity : 1);
            double? length = Mm(e, "REBAR_ELEM_LENGTH"), total = Mm(e, "REBAR_ELEM_TOTAL_LENGTH");
            var shape = Find.Builtin(e, "REBAR_SHAPE") is Parameter sp ? doc.GetElement(sp.AsElementId())?.Name : null;
            var dims = new Obj();
            foreach (var letter in "ABCDEFGHJ")
                if (e.LookupParameter(letter.ToString()) is Parameter d && d.StorageType == StorageType.Double && d.HasValue && d.AsDouble() > 1e-9)
                    dims[letter.ToString()] = Math.Round(d.AsDouble() * U.MmPerFoot);
            double totalM = (total ?? (length ?? 0) * qty) / 1000;
            rows.Add(new Obj
            {
                ["id"] = e.Id.Value,
                ["mark"] = Find.Builtin(e, "REBAR_NUMBER")?.AsString() ?? e.LookupParameter("Mark")?.AsString(),
                ["host"] = hostId.Value,
                ["hostLabel"] = host != null ? Describe.Label(doc, host) : null,
                ["hostCategory"] = host?.Category?.Name,
                ["hostLevel"] = host != null ? Describe.LevelName(doc, host) : null,
                ["barType"] = type?.Name,
                ["diameterMm"] = dia,
                ["shape"] = shape,
                ["quantity"] = qty,
                ["barLengthMm"] = length,
                ["totalLengthM"] = Math.Round(totalM, 3),
                // unit weight from the nominal diameter: d^2 / 162 kg/m (steel 7850 kg/m3)
                ["weightKg"] = Math.Round(totalM * dia * dia / 162.2, 2),
                ["dims"] = dims,
                ["system"] = e is RebarInSystem,
            });
        }
        int limit = Int(p, "limit", 5000, 1, 50000);
        return new
        {
            count = rows.Count, truncated = rows.Count > limit,
            totalWeightKg = Math.Round(rows.Sum(r => (double)r["weightKg"]), 1),
            byDiameter = rows.GroupBy(r => (double)r["diameterMm"]).OrderBy(g => g.Key)
                .Select(g => new { diameterMm = g.Key, bars = g.Sum(r => (int)r["quantity"]), lengthM = Math.Round(g.Sum(r => (double)r["totalLengthM"]), 2), weightKg = Math.Round(g.Sum(r => (double)r["weightKg"]), 1) }).ToList(),
            bars = rows.Take(limit).ToList(),
        };
    }
}
