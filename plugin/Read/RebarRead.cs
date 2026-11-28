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
            if (e is RebarContainer box)
            {
                var ch = doc.GetElement(box.GetHostId());
                if (hostFilter != null && !hostFilter.Contains(box.GetHostId().Value)) continue;
                if (level != null && !string.Equals(ch != null ? Describe.LevelName(doc, ch) : null, level, StringComparison.OrdinalIgnoreCase)) continue;
                rows.AddRange(ContainerRows(doc, box, ch));
                continue;
            }
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
            var (weight, source) = Weight(e, totalM, dia);
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
                ["weightKg"] = weight,
                ["weightSource"] = source,
                ["dims"] = dims,
                ["system"] = e is RebarInSystem,
                ["systemKind"] = e is RebarInSystem sys ? SystemKind(doc, sys) : null,
                ["systemId"] = e is RebarInSystem sys2 ? sys2.SystemId.Value : (long?)null,
            });
        }
        int limit = Int(p, "limit", 5000, 1, 50000);
        var inView = Has(p, "view") ? Find.ViewOf(doc, NeedRef(p, "view")) : null;
        var fabric = ReinforcementRead.FabricRows(doc, hostFilter, level, inView);
        var couplers = hostFilter == null && level == null ? ReinforcementRead.CouplerRows(doc, inView) : new List<Obj>();
        return new
        {
            count = rows.Count, truncated = rows.Count > limit,
            totalWeightKg = Math.Round(rows.Sum(r => (double)r["weightKg"]), 1),
            weightNote = "weightSource 'model': the mass Revit computes from the bar type's mass per unit length (Revit 2027); "
                       + "'d2/162': total length x d^2 / 162.2 kg/m (steel 7850 kg/m3), used in Revit 2024 or when the model gives no mass.",
            byDiameter = rows.GroupBy(r => (double)r["diameterMm"]).OrderBy(g => g.Key)
                .Select(g => new { diameterMm = g.Key, bars = g.Sum(r => (int)r["quantity"]), lengthM = Math.Round(g.Sum(r => (double)r["totalLengthM"]), 2), weightKg = Math.Round(g.Sum(r => (double)r["weightKg"]), 1) }).ToList(),
            bars = rows.Take(limit).ToList(),
            fabricSheets = fabric.Take(limit).ToList(), fabricSheetCount = fabric.Count,
            fabricMassKg = Math.Round(fabric.Sum(r => (double)r["massKg"]), 1),
            couplers = couplers.Take(limit).ToList(), couplerCount = couplers.Count,
        };
    }

    /// <summary>One row per item of a rebar container (its bar sets are not separate elements).</summary>
    private static IEnumerable<Obj> ContainerRows(Document doc, RebarContainer box, Element host)
    {
        foreach (RebarContainerItem item in box)
        {
            var type = doc.GetElement(item.BarTypeId) as RebarBarType;
            double dia = type != null ? Math.Round(type.BarNominalDiameter * U.MmPerFoot, 1) : 0;
            double totalM = item.TotalLength * U.MPerFoot;
            yield return new Obj
            {
                ["id"] = box.Id.Value, ["containerItem"] = item.ItemIndex, ["mark"] = box.ScheduleMark,
                ["host"] = box.GetHostId().Value, ["hostLabel"] = host != null ? Describe.Label(doc, host) : null,
                ["hostCategory"] = host?.Category?.Name, ["hostLevel"] = host != null ? Describe.LevelName(doc, host) : null,
                ["barType"] = type?.Name, ["diameterMm"] = dia, ["shape"] = doc.GetElement(item.RebarShapeId)?.Name,
                ["quantity"] = item.Quantity, ["barLengthMm"] = item.Quantity > 0 ? Math.Round(item.TotalLength / item.Quantity * U.MmPerFoot) : (double?)null,
                ["totalLengthM"] = Math.Round(totalM, 3), ["weightKg"] = Math.Round(totalM * dia * dia / 162.2, 2), ["weightSource"] = "d2/162",
                ["dims"] = new Obj(), ["system"] = false, ["systemKind"] = "container", ["systemId"] = box.Id.Value,
            };
        }
    }

    private static string SystemKind(Document doc, RebarInSystem bar)
    {
        var owner = doc.GetElement(bar.SystemId);
        return owner is AreaReinforcement ? "area" : owner is PathReinforcement ? "path" : owner?.GetType().Name;
    }

    /// <summary>Bar weight in kg: Revit's own mass where the API has it (2027), else from the nominal diameter.</summary>
    private static (double kg, string source) Weight(Element e, double totalM, double diaMm)
    {
#if !(REVIT2024 || REVIT2025 || REVIT2026)   // Rebar.Mass: checked in the 2027 API only
        try
        {
            double mass = e is Rebar r ? r.Mass : e is RebarInSystem ris ? ris.Mass : 0;
            if (mass > 1e-9) return (Math.Round(UnitUtils.ConvertFromInternalUnits(mass, UnitTypeId.Kilograms), 2), "model");
        }
        catch { /* no mass in this model: use the formula */ }
#endif
        return (Math.Round(totalM * diaMm * diaMm / 162.2, 2), "d2/162");
    }
}
