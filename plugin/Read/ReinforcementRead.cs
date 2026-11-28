using Autodesk.Revit.DB.Structure;

namespace RevitMcpBridge;

/// <summary>The reinforcement types a model offers, by the names the phase 7 edit operations take. Reads only.</summary>
internal static class ReinforcementRead
{
    private static double Kg(double internalMass) => Math.Round(UnitUtils.ConvertFromInternalUnits(internalMass, UnitTypeId.Kilograms), 3);

    public static object ListReinforcementTypes(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        int limit = Int(p, "limit", 300, 1, 2000);
        List<T> All<T>() where T : Element => new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().OrderBy(t => t.Name).ToList();

        var used = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rebar).WhereElementIsNotElementType()
            .GroupBy(e => e.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());
        var hooks = All<RebarHookType>();

        var bars = All<RebarBarType>().Take(limit).Select(b =>
        {
            var o = new Obj
            {
                ["id"] = b.Id.Value, ["name"] = b.Name,
                ["diameterMm"] = Math.Round(b.BarNominalDiameter * U.MmPerFoot, 2), ["modelDiameterMm"] = Math.Round(b.BarModelDiameter * U.MmPerFoot, 2),
                ["deformation"] = b.DeformationType.ToString(), ["barSets"] = used.TryGetValue(b.Id.Value, out int n) ? n : 0,
                ["hooksAllowed"] = hooks.Where(h => { try { return b.GetHookPermission(h.Id); } catch { return false; } }).Select(h => h.Name).ToList(),
            };
#if !(REVIT2024 || REVIT2025 || REVIT2026)
            try { if (b.BarMassPerUnitLength > 0) o["massKgPerM"] = Math.Round(UnitUtils.ConvertFromInternalUnits(b.BarMassPerUnitLength, UnitTypeId.KilogramsPerMeter), 4); } catch { /* none */ }
#endif
            return o;
        }).ToList();

        var fabricSheets = All<FabricSheetType>().Take(limit).Select(t => new
        {
            id = t.Id.Value, name = t.Name,
            overallLength = U.Out(t.OverallLength), overallWidth = U.Out(t.OverallWidth),
            majorSpacing = U.Out(t.MajorSpacing), minorSpacing = U.Out(t.MinorSpacing),
            majorWires = t.MajorNumberOfWires, minorWires = t.MinorNumberOfWires,
            majorWire = doc.GetElement(t.MajorDirectionWireType)?.Name, minorWire = doc.GetElement(t.MinorDirectionWireType)?.Name,
            majorLap = U.Out(t.MajorLapSpliceLength), minorLap = U.Out(t.MinorLapSpliceLength),
            sheetMassKg = Kg(t.SheetMass), custom = t.IsCustom(),
        }).ToList();

        var couplers = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Coupler)
            .Cast<FamilySymbol>().Take(limit).Select(s => new { id = s.Id.Value, family = s.FamilyName, name = s.Name }).ToList();

        return new
        {
            units = U.Name,
            barTypes = bars,
            hookTypes = hooks.Take(limit).Select(h => new { id = h.Id.Value, name = h.Name, angleDeg = U.Deg(h.HookAngle), extensionMultiplier = h.StraightLineMultiplier, style = h.Style.ToString() }).ToList(),
            pathReinforcementTypes = All<PathReinforcementType>().Select(t => new { id = t.Id.Value, name = t.Name }).ToList(),
            areaReinforcementTypes = All<AreaReinforcementType>().Select(t => new { id = t.Id.Value, name = t.Name }).ToList(),
            fabricAreaTypes = All<FabricAreaType>().Select(t => new { id = t.Id.Value, name = t.Name }).ToList(),
            fabricSheetTypes = fabricSheets,
            fabricWireTypes = All<FabricWireType>().Take(limit).Select(w => new { id = w.Id.Value, name = w.Name, diameterMm = Math.Round(w.WireDiameter * U.MmPerFoot, 2) }).ToList(),
            couplerTypes = couplers,
            annotationTypes = All<MultiReferenceAnnotationType>().Select(t => new
            {
                id = t.Id.Value, name = t.Name, tagType = doc.GetElement(t.TagTypeId)?.Name, dimensionStyle = doc.GetElement(t.DimensionStyleId)?.Name,
                referenceCategory = t.ReferenceCategoryId.Value == (long)BuiltInCategory.OST_Rebar ? "rebar" : Category.GetCategory(doc, t.ReferenceCategoryId)?.Name,
            }).ToList(),
            rebarShapes = All<RebarShape>().Count,
            spliceTypes = SpliceTypes(doc), crankTypes = CrankTypes(doc),
            note = "Names here are what path_reinforcement, fabric_area, fabric_sheet_place, rebar_hooks, coupler_place and rebar_annotate take. "
                 + "hooksAllowed: the hooks the bar type permits (Rebar Bar Type > Hook Lengths).",
        };
    }

#if REVIT2024 || REVIT2025 || REVIT2026
    private static object SpliceTypes(Document doc) => "Revit 2027 and later";
    private static object CrankTypes(Document doc) => "Revit 2027 and later";
#else
    private static object SpliceTypes(Document doc) => RebarSpliceTypeUtils.GetAllRebarSpliceTypes(doc).Select(id => new
    {
        id = id.Value, name = doc.GetElement(id)?.Name, lapD = RebarSpliceTypeUtils.GetLapLengthMultiplier(doc, id),
        staggerD = RebarSpliceTypeUtils.GetStaggerLengthMultiplier(doc, id), shift = RebarSpliceTypeUtils.GetShiftOption(doc, id).ToString(),
    }).ToList();

    private static object CrankTypes(Document doc) => RebarCrankTypeUtils.GetAllRebarCrankTypes(doc).Select(id => new
    {
        id = id.Value, name = doc.GetElement(id)?.Name, ratio = RebarCrankTypeUtils.GetCrankRatio(doc, id),
        lengthD = RebarCrankTypeUtils.GetCrankLengthMultiplier(doc, id), offsetD = RebarCrankTypeUtils.GetCrankOffsetMultiplier(doc, id),
    }).ToList();
#endif

    /// <summary>Fabric sheets for rebar_list: one row per sheet.</summary>
    public static List<Obj> FabricRows(Document doc, HashSet<long> hostFilter, string level, View view)
    {
        var col = view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
        var rows = new List<Obj>();
        foreach (var s in col.OfClass(typeof(FabricSheet)).Cast<FabricSheet>())
        {
            if (hostFilter != null && !hostFilter.Contains(s.HostId.Value)) continue;
            var host = doc.GetElement(s.HostId);
            if (level != null && !string.Equals(host != null ? Describe.LevelName(doc, host) : null, level, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(new Obj
            {
                ["id"] = s.Id.Value, ["type"] = doc.GetElement(s.GetTypeId())?.Name, ["host"] = s.HostId.Value,
                ["hostLevel"] = host != null ? Describe.LevelName(doc, host) : null,
                ["fabricArea"] = s.FabricAreaOwnerId != ElementId.InvalidElementId ? s.FabricAreaOwnerId.Value : (long?)null,
                ["number"] = s.FabricNumber, ["cutLengthMm"] = Math.Round(s.CutOverallLength * U.MmPerFoot),
                ["cutWidthMm"] = Math.Round(s.CutOverallWidth * U.MmPerFoot), ["massKg"] = Math.Round(Kg(s.CutSheetMass), 2),
            });
        }
        return rows;
    }

    /// <summary>Couplers for rebar_list: one row per coupler element.</summary>
    public static List<Obj> CouplerRows(Document doc, View view)
    {
        var col = view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
        return col.OfClass(typeof(RebarCoupler)).Cast<RebarCoupler>().Select(cp => new Obj
        {
            ["id"] = cp.Id.Value, ["type"] = doc.GetElement(cp.GetTypeId())?.Name,
            ["quantity"] = Find.Builtin(cp, "COUPLER_QUANTITY")?.AsInteger(),
            ["bars"] = cp.GetCoupledReinforcementData().OfType<RebarReinforcementData>().Select(d => (object)new { rebar = d.RebarId.Value, end = d.End == 0 ? "start" : "end" }).ToList(),
            ["linksTwoBars"] = cp.CouplerLinkTwoBars(),
        }).ToList();
    }
}
