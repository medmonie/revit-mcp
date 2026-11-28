using Autodesk.Revit.DB.Electrical;

namespace RevitMcpBridge;

/// <summary>Roadmap phase 18: panels, circuits and their connected loads as Revit computes them; devices not on a circuit.</summary>
internal static class ElectricalRead
{
    internal static readonly BuiltInCategory[] LoadCats =
    {
        BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices,
        BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_FireAlarmDevices,
        BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_SecurityDevices,
        BuiltInCategory.OST_NurseCallDevices, BuiltInCategory.OST_TelephoneDevices, BuiltInCategory.OST_PlumbingFixtures,
    };

    /// <summary>A parameter as Revit shows it (with its unit), or null.</summary>
    internal static string Shown(Element e, string bip)
    {
        var prm = Find.Builtin(e, bip);
        if (prm == null || !prm.HasValue) return null;
        try { return prm.StorageType == StorageType.String ? prm.AsString() : prm.AsValueString(); } catch { return null; }
    }

    internal static IEnumerable<Connector> ElecConns(Element e) =>
        ((e as FamilyInstance)?.MEPModel?.ConnectorManager?.Connectors.Cast<Connector>() ?? Enumerable.Empty<Connector>())
        .Where(x => x.Domain == Domain.DomainElectrical);

    /// <summary>The circuits an element is on (Revit keeps electrical circuits on the element's MEP model, not on its connectors).</summary>
    internal static List<ElectricalSystem> CircuitsOf(Element e)
    {
        try { return (e as FamilyInstance)?.MEPModel?.GetElectricalSystems()?.ToList() ?? new List<ElectricalSystem>(); }
        catch { return new List<ElectricalSystem>(); }
    }

    internal static bool IsPanel(Element e) =>
        e is FamilyInstance fi && fi.Category?.BuiltInCategory == BuiltInCategory.OST_ElectricalEquipment && fi.MEPModel is ElectricalEquipment;

    internal static Obj CircuitRow(Document doc, ElectricalSystem s, int maxElements = 50)
    {
        var els = s.Elements.Cast<Element>().ToList();
        return new Obj
        {
            ["id"] = s.Id.Value, ["circuit"] = s.CircuitNumber, ["panel"] = s.PanelName, ["panelId"] = s.BaseEquipment?.Id.Value,
            ["kind"] = s.SystemType.ToString(), ["loadName"] = s.LoadName, ["poles"] = Shown(s, "RBS_ELEC_NUMBER_OF_POLES"),
            ["voltage"] = Shown(s, "RBS_ELEC_VOLTAGE"), ["apparentLoad"] = Shown(s, "RBS_ELEC_APPARENT_LOAD"), ["trueLoad"] = Shown(s, "RBS_ELEC_TRUE_LOAD"),
            ["current"] = Shown(s, "RBS_ELEC_APPARENT_CURRENT_PARAM"), ["rating"] = Shown(s, "RBS_ELEC_CIRCUIT_RATING_PARAM"),
            ["wireType"] = Shown(s, "RBS_ELEC_CIRCUIT_WIRE_TYPE_PARAM"), ["wireSize"] = Shown(s, "RBS_ELEC_CIRCUIT_WIRE_SIZE_PARAM"),
            ["length"] = Shown(s, "RBS_ELEC_CIRCUIT_LENGTH_PARAM"), ["elementCount"] = els.Count,
            ["elements"] = els.Take(maxElements).Select(x => x.Id.Value).ToList(),
        };
    }

    /// <summary>
    /// Panels (name, distribution system, mains, connected and demand load, circuits) and circuits (number, panel, kind,
    /// load name, poles, voltage, apparent / true load, current, rating, wire, length, elements), all as Revit computes and
    /// shows them; plus the electrical loads not on any circuit. Host or a loaded link.
    /// </summary>
    public static object ElectricalCircuits(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        int limit = Int(p, "limit", 200, 1, 5000);
        string panelFilter = Str(p, "panel");
        var systems = new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
            .Where(s => panelFilter == null || string.Equals(s.PanelName, panelFilter, StringComparison.OrdinalIgnoreCase)
                        || s.BaseEquipment?.Id.Value.ToString(CultureInfo.InvariantCulture) == panelFilter)
            .OrderBy(s => s.PanelName).ThenBy(s => s.CircuitNumber, StringComparer.OrdinalIgnoreCase).ToList();
        var panels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment).WhereElementIsNotElementType()
            .Where(IsPanel).Cast<FamilyInstance>()
            .Where(f => panelFilter == null || string.Equals(Shown(f, "RBS_ELEC_PANEL_NAME"), panelFilter, StringComparison.OrdinalIgnoreCase)
                        || f.Id.Value.ToString(CultureInfo.InvariantCulture) == panelFilter)
            .Select(f =>
            {
                var ds = doc.GetElement(Find.Builtin(f, "RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM")?.AsElementId() ?? ElementId.InvalidElementId);
                return new Obj
                {
                    ["id"] = f.Id.Value, ["name"] = Shown(f, "RBS_ELEC_PANEL_NAME"), ["type"] = Describe.Label(doc, f),
                    ["distributionSystem"] = ds?.Name, ["mainsRating"] = Shown(f, "RBS_ELEC_MAINS"), ["mcbRating"] = Shown(f, "RBS_ELEC_PANEL_MCB_RATING_PARAM"),
                    ["connectedLoad"] = Shown(f, "RBS_ELEC_PANEL_TOTALLOAD_PARAM"), ["demandLoad"] = Shown(f, "RBS_ELEC_PANEL_TOTALESTLOAD_PARAM"),
                    ["connectedCurrent"] = Shown(f, "RBS_ELEC_PANEL_TOTAL_CONNECTED_CURRENT_PARAM"), ["demandCurrent"] = Shown(f, "RBS_ELEC_PANEL_TOTAL_DEMAND_CURRENT_PARAM"),
                    ["circuits"] = systems.Count(s => s.BaseEquipment?.Id == f.Id),
                };
            }).OrderBy(o => (string)o["name"]).ToList();
        // loads (devices, fixtures, equipment) with an electrical connector that is on no circuit
        var loose = new List<Obj>();
        int looseCount = 0;
        foreach (var e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(LoadCats)).WhereElementIsNotElementType())
        {
            if (IsPanel(e) || !ElecConns(e).Any()) continue;
            if (CircuitsOf(e).Count > 0) continue;
            looseCount++;
            if (loose.Count < limit)
                loose.Add(new Obj { ["id"] = e.Id.Value, ["category"] = e.Category?.Name, ["type"] = Describe.Label(doc, e), ["level"] = (doc.GetElement(e.LevelId) as Level)?.Name });
        }
        return new
        {
            model = doc.Title, link, panels, panelCount = panels.Count, circuitCount = systems.Count,
            circuits = systems.Take(limit).Select(s => CircuitRow(doc, s)).ToList(),
            notOnACircuit = looseCount, unassigned = loose, truncated = systems.Count > limit || looseCount > limit,
            note = "Values as Revit computes and shows them (with their units). Loads, ratings and demand factors come from the families and the engineer; nothing is sized here.",
        };
    }
}
