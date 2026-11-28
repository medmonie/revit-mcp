using Autodesk.Revit.DB.Structure;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 10 (edits): analytical members and panels, load cases, load combinations and loads, all from values the
/// engineer gives. Nothing is derived: no load takedown, no factors chosen, no sections sized.
/// </summary>
internal static class AnalysisOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["analytical_member_create"] = MemberCreate;
        ops["analytical_panel_create"] = PanelCreate;
        ops["load_case_create"] = LoadCaseCreate;
        ops["load_combination_create"] = CombinationCreate;
        ops["load_place"] = LoadPlace;
    }

    /// <summary>AnalyticalStructuralRole by a plain word (beam, column, ...), looked up by name so both years compile.</summary>
    private static AnalyticalStructuralRole Role(Obj p, string dflt, params string[] allowed)
    {
        string word = Choice(p, "role", dflt, allowed);
        foreach (var name in Enum.GetNames(typeof(AnalyticalStructuralRole)))
            if (name.Equals("StructuralRole" + word, StringComparison.OrdinalIgnoreCase) || name.Equals(word, StringComparison.OrdinalIgnoreCase))
                return (AnalyticalStructuralRole)Enum.Parse(typeof(AnalyticalStructuralRole), name);
        throw new ArgumentException($"This Revit has no analytical role '{word}'. Roles: {string.Join(", ", Enum.GetNames(typeof(AnalyticalStructuralRole)))}.");
    }

    private static void Associate(EditContext c, Element analytical, Obj p, List<string> notes)
    {
        if (!Has(p, "physical")) return;
        var physical = Find.Elem(c.Doc, NeedId(p, "physical"));
        var mgr = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(c.Doc);
        if (mgr.HasAssociation(physical.Id))
            throw new ArgumentException($"Element {physical.Id.Value} already has an analytical element ({mgr.GetAssociatedElementId(physical.Id).Value}).");
        mgr.AddAssociation(analytical.Id, physical.Id);
        notes.Add($"associated with {physical.Category?.Name} {physical.Id.Value}");
    }

    private static object MemberCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var curve = U.Curve(p, "analytical member", 0);
        var role = Role(p, "beam", "beam", "column", "girder", "member", "unset");
        var m = AnalyticalMember.Create(doc, curve);
        m.StructuralRole = role;
        if (Has(p, "section"))
        {
            var sym = Find.TypeOf(doc, NeedStr(p, "section"), null, typeof(FamilySymbol))
                ?? throw new ArgumentException($"No section type '{Str(p, "section")}'. Use list_types for structural framing or columns.");
            m.SectionTypeId = sym.Id;
        }
        if (Has(p, "material"))
        {
            var mat = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(x => string.Equals(x.Name, Str(p, "material"), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No material '{Str(p, "material")}'.");
            m.MaterialId = mat.Id;
        }
        var notes = new List<string>();
        Associate(c, m, p, notes);
        return new { id = m.Id.Value, role = m.StructuralRole.ToString(), length = U.Out(curve.Length), section = doc.GetElement(m.SectionTypeId)?.Name, notes };
    }

    private static object PanelCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var pts = U.PtList(p, "points", 3, 200);
        var loop = new CurveLoop();
        for (int i = 0; i < pts.Count; i++) loop.Append(U.Segment(pts[i], pts[(i + 1) % pts.Count], $"panel edge {i + 1}"));
        if (!loop.HasPlane()) throw new ArgumentException("The panel's points must lie in one plane.");
        var vertical = Math.Abs(loop.GetPlane().Normal.Z) < 0.1;
        var a = AnalyticalPanel.Create(doc, loop);
        a.StructuralRole = Role(p, vertical ? "wall" : "floor", "floor", "wall", "panel", "unset");
        if (Dbl(p, "thickness") is double t)
        {
            if (t <= 0) throw new ArgumentException("'thickness' must be positive.");
            a.Thickness = U.In(t);
        }
        var notes = new List<string>();
        Associate(c, a, p, notes);
        return new { id = a.Id.Value, role = a.StructuralRole.ToString(), thickness = U.Out(a.Thickness), areaM2 = U.Area(PolygonArea(loop)), notes };
    }

    private static double PolygonArea(CurveLoop loop)
    {
        // Area of a planar polygon (Newell's method), in square feet.
        var pts = loop.Select(cv => cv.GetEndPoint(0)).ToList();
        var n = XYZ.Zero;
        for (int i = 0; i < pts.Count; i++) n += pts[i].CrossProduct(pts[(i + 1) % pts.Count]);
        return n.GetLength() / 2;
    }

    // ---------------------------------------------------------------- load cases and combinations

    private static readonly Dictionary<string, (string category, string nature)> CaseKinds = new Dictionary<string, (string, string)>
    {
        ["dead"] = ("Dead", "Dead"), ["live"] = ("Live", "Live"), ["wind"] = ("Wind", "Wind"), ["snow"] = ("Snow", "Snow"),
        ["roof_live"] = ("RoofLive", "Roof Live"), ["accidental"] = ("Accidental", "Accidental"),
        ["temperature"] = ("Temperature", "Temperature"), ["seismic"] = ("Seismic", "Seismic"),
    };

    private static LoadCase CaseOf(Document doc, string name) =>
        new FilteredElementCollector(doc).OfClass(typeof(LoadCase)).Cast<LoadCase>().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    private static object LoadCaseCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string name = NeedStr(p, "name");
        string kind = Choice(p, "category", null, CaseKinds.Keys.ToArray());
        var existing = CaseOf(doc, name);
        if (existing != null)
        {
            if (Str(p, "if_exists") == "skip") return new { id = existing.Id.Value, name = existing.Name, skipped = true };
            throw new ArgumentException($"Load case '{name}' already exists. Pass if_exists: 'skip' to accept it.");
        }
        string natureName = Str(p, "nature") ?? CaseKinds[kind].nature;
        var nature = new FilteredElementCollector(doc).OfClass(typeof(LoadNature)).Cast<LoadNature>()
            .FirstOrDefault(x => string.Equals(x.Name, natureName, StringComparison.OrdinalIgnoreCase));
        bool natureMade = false;
        if (nature == null)
        {
            if (!Flag(p, "create_nature", false))
                throw new ArgumentException($"No load nature '{natureName}'. Natures: {string.Join(", ", new FilteredElementCollector(doc).OfClass(typeof(LoadNature)).Select(x => x.Name))}. Pass create_nature: true to add it.");
            nature = LoadNature.Create(doc, natureName);
            natureMade = true;
        }
        var cat = (LoadCaseCategory)Enum.Parse(typeof(LoadCaseCategory), CaseKinds[kind].category);
        var lc = LoadCase.Create(doc, name, nature.Id, cat);
        if (Dbl(p, "number") is double num) lc.Number = IntegerValue(num, "'number'");
        return new { id = lc.Id.Value, name = lc.Name, number = lc.Number, category = kind, nature = nature.Name, natureCreated = natureMade };
    }

    private static object CombinationCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string name = NeedStr(p, "name");
        if (new FilteredElementCollector(doc).OfClass(typeof(LoadCombination)).Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Load combination '{name}' already exists.");
        var type = Choice(p, "type", "combination", "combination", "envelope") == "envelope" ? LoadCombinationType.Envelope : LoadCombinationType.Combination;
        var state = Choice(p, "state", "ultimate", "ultimate", "serviceability") == "serviceability" ? LoadCombinationState.Serviceability : LoadCombinationState.Ultimate;
        var comps = new List<LoadComponent>();
        var echo = new List<object>();
        foreach (var item in ObjList(p, "components", 100))
        {
            string of = NeedStr(item, "case");
            Element target = (Element)CaseOf(doc, of) ?? new FilteredElementCollector(doc).OfClass(typeof(LoadCombination))
                .FirstOrDefault(x => string.Equals(x.Name, of, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No load case or combination '{of}'.");
            double f = NeedDbl(item, "factor");
            comps.Add(new LoadComponent(target.Id, f));
            echo.Add(new { of = target.Name, factor = f });
        }
        var combo = LoadCombination.Create(doc, name, type, state);
        combo.SetComponents(comps);
        return new { id = combo.Id.Value, name = combo.Name, type = type.ToString(), state = state.ToString(), components = echo };
    }

    // ---------------------------------------------------------------- loads

    private static XYZ Vector(Obj p, string name, ForgeTypeId unit, bool required)
    {
        if (!Has(p, name))
        {
            if (required) throw new ArgumentException($"Give '{name}' as [fx, fy, fz].");
            return XYZ.Zero;
        }
        if (!(p[name] is List<object> v) || v.Count != 3) throw new ArgumentException($"'{name}' must be [fx, fy, fz].");
        double C(int i) => UnitUtils.ConvertToInternalUnits(Number(v[i], $"'{name}'"), unit);
        return new XYZ(C(0), C(1), C(2));
    }

    /// <summary>The analytical element to host a load: the id given, or the analytical element associated with a physical one.</summary>
    private static Element LoadHost(Document doc, Obj p)
    {
        var e = Find.Elem(doc, NeedId(p, "host"));
        if (e is AnalyticalElement) return e;
        var mgr = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
        var id = mgr.HasAssociation(e.Id) ? mgr.GetAssociatedElementId(e.Id) : ElementId.InvalidElementId;
        return id != ElementId.InvalidElementId ? doc.GetElement(id)
            : throw new ArgumentException($"Element {e.Id.Value} is not analytical and has no associated analytical element (see analytical_member_create).");
    }

    /// <summary>
    /// A point, line or area load on an analytical element (Revit 2023+ loads are hosted): on a member's end, at a point on
    /// it, along it or part of it; on a panel, all of it or a region of it. The forces are the engineer's, in global axes.
    /// </summary>
    private static object LoadPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", null, "point", "line", "area");
        var lcase = CaseOf(doc, NeedStr(p, "load_case")) ?? throw new ArgumentException($"No load case '{Str(p, "load_case")}' (see list_loads or load_case_create).");
        var host = LoadHost(doc, p);
        LoadBase load;
        switch (kind)
        {
            case "point":
            {
                if (!(host is AnalyticalMember)) throw new ArgumentException("A point load goes on an analytical member.");
                var f = Vector(p, "force", UnitTypeId.Kilonewtons, true);
                var mo = Vector(p, "moment", UnitTypeId.KilonewtonMeters, false);
                if (Has(p, "point")) load = PointLoad.Create(doc, host.Id, U.NeedPt(p, "point"), f, mo, null);
                else
                {
                    var sel = Choice(p, "at_end", "start", "start", "end") == "start" ? AnalyticalElementSelector.StartOrBase : AnalyticalElementSelector.EndOrTop;
                    load = PointLoad.Create(doc, host.Id, sel, f, mo, null);
                }
                break;
            }
            case "line":
            {
                if (!(host is AnalyticalMember)) throw new ArgumentException("A line load goes on an analytical member (or give an area load on a panel).");
                var f = Vector(p, "force", UnitTypeId.KilonewtonsPerMeter, true);
                var mo = Vector(p, "moment", UnitTypeId.KilonewtonMetersPerMeter, false);
                load = Has(p, "start") || Has(p, "end")
                    ? LineLoad.Create(doc, host.Id, U.Segment(U.NeedPt(p, "start"), U.NeedPt(p, "end"), "line load"), f, mo, null)
                    : LineLoad.Create(doc, host.Id, f, mo, null);
                break;
            }
            default:
            {
                if (!(host is AnalyticalPanel)) throw new ArgumentException("An area load goes on an analytical panel.");
                var f = Vector(p, "force", UnitTypeId.KilonewtonsPerSquareMeter, true);
                if (Has(p, "points"))
                {
                    var pts = U.PtList(p, "points", 3, 200);
                    var loop = new CurveLoop();
                    for (int i = 0; i < pts.Count; i++) loop.Append(U.Segment(pts[i], pts[(i + 1) % pts.Count], $"load edge {i + 1}"));
                    load = AreaLoad.Create(doc, host.Id, new List<CurveLoop> { loop }, f, null);
                }
                else load = AreaLoad.Create(doc, host.Id, f, null);
                break;
            }
        }
        if (load == null) throw new InvalidOperationException("Revit did not create the load.");
        load.LoadCaseId = lcase.Id;
        return new { id = load.Id.Value, kind, loadCase = lcase.Name, host = host.Id.Value };
    }
}
