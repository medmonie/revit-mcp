using Autodesk.Revit.DB.Electrical;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 18: electrical circuits from the engineer's list (which loads on which circuit, on which panel, with the
/// rating and load name given) and panel set-up (distribution system, name). Nothing is grouped, balanced or sized here.
/// </summary>
internal static class ElectricalOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["electrical_circuit_create"] = CircuitCreate;
        ops["electrical_circuit_edit"] = CircuitEdit;
        ops["electrical_panel_set"] = PanelSet;
    }

    private static readonly Dictionary<string, ElectricalSystemType> Kinds = new Dictionary<string, ElectricalSystemType>(StringComparer.OrdinalIgnoreCase)
    {
        ["power"] = ElectricalSystemType.PowerCircuit, ["data"] = ElectricalSystemType.Data, ["telephone"] = ElectricalSystemType.Telephone,
        ["security"] = ElectricalSystemType.Security, ["fire_alarm"] = ElectricalSystemType.FireAlarm, ["nurse_call"] = ElectricalSystemType.NurseCall,
        ["controls"] = ElectricalSystemType.Controls, ["communication"] = ElectricalSystemType.Communication,
    };

    /// <summary>A panel by element id or by its panel name.</summary>
    private static FamilyInstance Panel(EditContext c, string spec, string what)
    {
        var doc = c.Doc;
        var panels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment).WhereElementIsNotElementType()
            .Where(ElectricalRead.IsPanel).Cast<FamilyInstance>().ToList();
        var hit = panels.FirstOrDefault(f => f.Id.Value.ToString(CultureInfo.InvariantCulture) == spec)
                  ?? panels.FirstOrDefault(f => string.Equals(ElectricalRead.Shown(f, "RBS_ELEC_PANEL_NAME"), spec, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"{what}: no panel '{spec}'. Panels: {string.Join(", ", panels.Select(f => $"{ElectricalRead.Shown(f, "RBS_ELEC_PANEL_NAME")} ({f.Id.Value})").Take(40))}"
                                                 + (panels.Count == 0 ? "none (place electrical equipment first)" : "") + ".");
        return (FamilyInstance)c.Editable(hit);
    }

    private static void Rating(ElectricalSystem s, double amps)
    {
        if (amps <= 0) throw new ArgumentException("'rating' must be positive (amperes).");
        var prm = s.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM) ?? throw new InvalidOperationException("The circuit has no rating parameter.");
        if (prm.IsReadOnly) throw new InvalidOperationException("The circuit's rating is read-only here.");
        prm.Set(UnitUtils.ConvertToInternalUnits(amps, UnitTypeId.Amperes));
    }

    private static ElementSet SetOf(IEnumerable<Element> els)
    {
        var set = new ElementSet();
        foreach (var e in els) set.Insert(e);
        return set;
    }

    private static string Refused(Element e)
    {
        var cons = ElectricalRead.ElecConns(e).ToList();
        if (cons.Count == 0) return $"{e.Id.Value} ({e.Category?.Name}) has no electrical connector";
        return null;
    }

    /// <summary>
    /// Circuits from the engineer's list: each circuit's loads (devices, fixtures, equipment), optionally its panel, kind
    /// (power, lighting loads are power circuits; data, fire alarm ...), load name and breaker rating in amperes.
    /// </summary>
    private static object CircuitCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var rows = new List<Obj>();
        var circuits = ObjList(p, "circuits", 500);
        for (int i = 0; i < circuits.Count; i++)
        {
            var it = circuits[i];
            string what = $"Circuit {i + 1}";
            string kindName = Str(it, "kind") ?? "power";
            if (!Kinds.TryGetValue(kindName, out var kind)) throw new ArgumentException($"{what}: 'kind' must be one of {string.Join(", ", Kinds.Keys)}.");
            var els = c.Elements(it, "elements", 500);
            var bad = els.Select(Refused).Where(x => x != null).ToList();
            if (bad.Count > 0) throw new ArgumentException($"{what}: {string.Join("; ", bad)}.");
            // the panel is found before anything is made, so a wrong name changes nothing
            var panel = Str(it, "panel") is string ps ? Panel(c, ps, what) : null;
            var onOne = els.Where(e => ElectricalRead.CircuitsOf(e).Any(s => s.SystemType == kind)).ToList();
            if (onOne.Count > 0)
                throw new ArgumentException($"{what}: already on a {kindName} circuit: {string.Join(", ", onOne.Select(e => $"{e.Id.Value} (circuit {string.Join("/", ElectricalRead.CircuitsOf(e).Where(s => s.SystemType == kind).Select(s => $"{s.PanelName} {s.CircuitNumber}".Trim()))})"))} (electrical_circuit_edit moves them).");
            ElectricalSystem sys;
            try { sys = ElectricalSystem.Create(doc, els.Select(e => e.Id).ToList(), kind); }
            catch (Exception ex) { throw new InvalidOperationException($"{what}: Revit made no {kindName} circuit of these elements: {ex.Message} (their connectors' system type and voltage must match)."); }
            doc.Regenerate();
            if (panel != null)
            {
                try { sys.SelectPanel(panel); }
                catch (Exception ex) { throw new InvalidOperationException($"{what}: panel '{Str(it, "panel")}' refused the circuit: {ex.Message} (check the panel's distribution system and voltage against the loads; electrical_panel_set)."); }
            }
            if (Str(it, "load_name") is string ln) sys.LoadName = ln;
            if (Dbl(it, "rating") is double amps) Rating(sys, amps);
            doc.Regenerate();
            rows.Add(ElectricalRead.CircuitRow(doc, sys));
        }
        return new
        {
            created = rows.Count, circuits = rows,
            note = "Circuits as listed (which loads, panel, rating and load name are the engineer's). Loads, voltage and current are Revit's values (with units).",
        };
    }

    /// <summary>Change a circuit: add / remove loads, move it to a panel or off its panel, load name, rating.</summary>
    private static object CircuitEdit(EditContext c, Obj p)
    {
        var doc = c.Doc;
        if (!(c.Editable(Find.Elem(doc, NeedId(p, "circuit"))) is ElectricalSystem sys))
            throw new ArgumentException("'circuit' must be the id of an electrical circuit (electrical_circuits lists them).");
        var done = new List<string>();
        if (Has(p, "remove"))
        {
            var els = c.Elements(p, "remove", 500);
            var members = new HashSet<long>(sys.Elements.Cast<Element>().Select(e => e.Id.Value));
            var notIn = els.Where(e => !members.Contains(e.Id.Value)).Select(e => e.Id.Value).ToList();
            if (notIn.Count > 0) throw new ArgumentException($"Not on circuit {sys.Id.Value}: {string.Join(", ", notIn)}.");
            if (els.Count >= members.Count) throw new ArgumentException("That would leave the circuit empty: delete the circuit instead (delete with its id).");
            sys.RemoveFromCircuit(SetOf(els));
            done.Add($"removed {els.Count}");
        }
        if (Has(p, "add"))
        {
            var els = c.Elements(p, "add", 500);
            var bad = els.Select(Refused).Where(x => x != null).ToList();
            if (bad.Count > 0) throw new ArgumentException(string.Join("; ", bad) + ".");
            try { if (!sys.AddToCircuit(SetOf(els))) throw new InvalidOperationException("Revit returned false"); }
            catch (Exception ex) { throw new InvalidOperationException($"Revit did not add them to circuit {sys.Id.Value}: {ex.Message} (system type and voltage must match)."); }
            done.Add($"added {els.Count}");
        }
        if (Flag(p, "disconnect_panel", false))
        {
            if (sys.BaseEquipment == null) throw new ArgumentException($"Circuit {sys.Id.Value} has no panel.");
            sys.DisconnectPanel();
            done.Add("off its panel");
        }
        if (Str(p, "panel") is string panelSpec)
        {
            var panel = Panel(c, panelSpec, "'panel'");
            try { sys.SelectPanel(panel); }
            catch (Exception ex) { throw new InvalidOperationException($"Panel '{panelSpec}' refused the circuit: {ex.Message}"); }
            done.Add($"on panel {ElectricalRead.Shown(panel, "RBS_ELEC_PANEL_NAME")}");
        }
        if (Str(p, "load_name") is string ln) { sys.LoadName = ln; done.Add("load name"); }
        if (Dbl(p, "rating") is double amps) { Rating(sys, amps); done.Add("rating"); }
        if (done.Count == 0) throw new ArgumentException("Nothing to change: give add, remove, panel, disconnect_panel, load_name or rating.");
        doc.Regenerate();
        return new { changed = done, circuit = ElectricalRead.CircuitRow(doc, sys) };
    }

    /// <summary>A panel's distribution system (by name) and / or panel name.</summary>
    private static object PanelSet(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var panel = Panel(c, NeedRef(p, "panel"), "'panel'");
        var done = new List<string>();
        if (Str(p, "distribution_system") is string dsName)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(DistributionSysType)).Cast<DistributionSysType>().ToList();
            var ds = all.FirstOrDefault(d => string.Equals(d.Name, dsName, StringComparison.OrdinalIgnoreCase) || d.Id.Value.ToString(CultureInfo.InvariantCulture) == dsName)
                     ?? throw new ArgumentException($"No distribution system '{dsName}'. Available: {string.Join(", ", all.Select(d => d.Name))}"
                                                    + (all.Count == 0 ? "none (Revit's Electrical Settings define them)" : "") + ".");
            var prm = Find.Builtin(panel, "RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM") ?? throw new InvalidOperationException("The panel has no distribution system parameter.");
            if (prm.IsReadOnly || !prm.Set(ds.Id)) throw new InvalidOperationException($"Revit did not take distribution system '{ds.Name}' for this panel.");
            done.Add($"distribution system {ds.Name}");
        }
        if (Str(p, "name") is string name)
        {
            var prm = Find.Builtin(panel, "RBS_ELEC_PANEL_NAME") ?? throw new InvalidOperationException("The panel has no panel name parameter.");
            prm.Set(name);
            done.Add($"name {name}");
        }
        if (done.Count == 0) throw new ArgumentException("Nothing to change: give distribution_system and / or name.");
        doc.Regenerate();
        return new
        {
            id = panel.Id.Value, changed = done, name = ElectricalRead.Shown(panel, "RBS_ELEC_PANEL_NAME"),
            distributionSystem = doc.GetElement(Find.Builtin(panel, "RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM")?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
        };
    }
}
