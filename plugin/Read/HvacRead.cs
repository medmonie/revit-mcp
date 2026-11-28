using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 18 (reads): what Revit itself computes for ducts and pipes (flow, velocity, friction, pressure drop),
/// connectivity QA (open ends, elements in no system), and air terminals against the airflow specified for their space.
/// Limits (maximum velocity, friction) come from the engineer; nothing is sized. Host or a loaded link. Reads only.
/// </summary>
internal static class HvacRead
{
    private static Parameter Bip(Element e, string name) => Find.Builtin(e, name);

    private static double? Val(Element e, string bip, ForgeTypeId unit)
    {
        var p = Bip(e, bip);
        if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
        return Math.Round(UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit), 3);
    }

    private static string S(Element e, string bip) { var p = Bip(e, bip); return p == null || !p.HasValue ? null : p.AsString() ?? p.AsValueString(); }

    private static ConnectorManager Connectors(Element e) =>
        e is MEPCurve c ? c.ConnectorManager : e is FamilyInstance fi ? fi.MEPModel?.ConnectorManager : null;

    // ---------------------------------------------------------------- flow, velocity, pressure

    /// <summary>Revit's computed flow, velocity, friction and pressure drop per duct or pipe, by system; flags over the given limits.</summary>
    public static object MepFlow(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        string kind = Choice(p, "kind", "duct", "duct", "pipe");
        string system = Str(p, "system");
        double? maxV = Dbl(p, "max_velocity"), maxF = Dbl(p, "max_friction");
        int limit = Int(p, "limit", 200, 1, 5000);
        bool duct = kind == "duct";
        var bic = duct ? BuiltInCategory.OST_DuctCurves : BuiltInCategory.OST_PipeCurves;
        string flowBip = duct ? "RBS_DUCT_FLOW_PARAM" : "RBS_PIPE_FLOW_PARAM";
        string velBip = duct ? "RBS_VELOCITY" : "RBS_PIPE_VELOCITY_PARAM";
        string fricBip = duct ? "RBS_FRICTION" : "RBS_PIPE_FRICTION_PARAM";
        string dropBip = duct ? "RBS_PRESSURE_DROP" : "RBS_PIPE_PRESSUREDROP_PARAM";
        var rows = new List<Obj>();
        var bySystem = new Dictionary<string, (int n, double len, double maxV, double drop, int noFlow)>();
        int over = 0, total = 0;
        foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
        {
            string sys = S(e, "RBS_SYSTEM_NAME_PARAM") ?? "(no system)";
            if (system != null && sys.IndexOf(system, StringComparison.OrdinalIgnoreCase) < 0) continue;
            total++;
            double? flow = Val(e, flowBip, UnitTypeId.LitersPerSecond), vel = Val(e, velBip, UnitTypeId.MetersPerSecond);
            double? fric = Val(e, fricBip, UnitTypeId.PascalsPerMeter), drop = Val(e, dropBip, UnitTypeId.Pascals);
            double len = e.Location is LocationCurve lc ? lc.Curve.Length : 0;
            var key = sys;
            var a = bySystem.TryGetValue(key, out var x) ? x : (0, 0.0, 0.0, 0.0, 0);
            bySystem[key] = (a.Item1 + 1, a.Item2 + len, Math.Max(a.Item3, vel ?? 0), a.Item4 + (drop ?? 0), a.Item5 + ((flow ?? 0) <= 0 ? 1 : 0));
            var flags = new List<string>();
            if (maxV is double mv && vel > mv) flags.Add($"velocity over {mv} m/s");
            if (maxF is double mf && fric > mf) flags.Add($"friction over {mf} Pa/m");
            if (flags.Count > 0) over++;
            bool onlyFlagged = maxV != null || maxF != null;
            if ((!onlyFlagged || flags.Count > 0) && rows.Count < limit)
                rows.Add(new Obj
                {
                    ["id"] = e.Id.Value, ["system"] = sys, ["size"] = MepRead.Size(e).text, ["lengthM"] = Math.Round(len * 0.3048, 2),
                    ["flowLps"] = flow, ["velocityMps"] = vel, ["frictionPaPerM"] = fric, ["pressureDropPa"] = drop,
                    ["flags"] = flags.Count > 0 ? flags : null,
                });
        }
        return new
        {
            model = doc.Title, link, kind, elements = total, overLimits = (maxV != null || maxF != null) ? over : (int?)null,
            systems = bySystem.OrderBy(k => k.Key).Select(k => new
            {
                system = k.Key, count = k.Value.n, lengthM = Math.Round(k.Value.len * 0.3048, 1), maxVelocityMps = Math.Round(k.Value.maxV, 2),
                sumPressureDropPa = Math.Round(k.Value.drop, 1), withoutFlow = k.Value.noFlow,
            }).ToList(),
            rows, truncated = rows.Count >= limit,
            note = "Values are what Revit calculates from the flows in the model (terminals, equipment, specified flows); with no flows they are 0. Pressure drop is per element; sumPressureDropPa adds the runs only, not fittings or a critical path.",
        };
    }

    // ---------------------------------------------------------------- connectivity

    private static readonly BuiltInCategory[] Connectable =
    {
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers,
    };

    /// <summary>Open connectors (ends connected to nothing) and terminals / equipment whose connectors are in no system.</summary>
    public static object MepConnectivity(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        int limit = Int(p, "limit", 100, 1, 5000);
        var cats = Has(p, "category") ? new[] { Find.Cat(doc, NeedStr(p, "category")) } : Connectable;
        var open = new List<Obj>();
        var noSystem = new List<Obj>();
        var openBy = new Dictionary<string, int>();
        var noSysBy = new Dictionary<string, int>();
        foreach (var bic in cats)
            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                var cm = Connectors(e);
                if (cm == null) continue;
                string cat = e.Category?.Name ?? bic.ToString();
                foreach (Connector c in cm.Connectors)
                {
                    if (c.ConnectorType != ConnectorType.End) continue;
                    if (c.Domain != Domain.DomainHvac && c.Domain != Domain.DomainPiping) continue;
                    if (!c.IsConnected)
                    {
                        openBy[cat] = openBy.TryGetValue(cat, out var n) ? n + 1 : 1;
                        if (open.Count < limit) open.Add(new Obj { ["id"] = e.Id.Value, ["category"] = cat, ["label"] = Describe.Label(doc, e), ["at"] = U.Xyz(c.Origin) });
                    }
                    if (e is FamilyInstance && c.MEPSystem == null)
                    {
                        noSysBy[cat] = noSysBy.TryGetValue(cat, out var m) ? m + 1 : 1;
                        if (noSystem.Count < limit) noSystem.Add(new Obj { ["id"] = e.Id.Value, ["category"] = cat, ["label"] = Describe.Label(doc, e), ["domain"] = c.Domain.ToString() });
                    }
                }
            }
        return new
        {
            model = doc.Title, link, openEnds = openBy.Values.Sum(), openEndsByCategory = openBy, openEndItems = open,
            notInSystem = noSysBy.Values.Sum(), notInSystemByCategory = noSysBy, notInSystemItems = noSystem,
            note = "Open ends: duct / pipe connectors joined to nothing (unfinished runs, capped ends count too). notInSystem: terminal, fitting or equipment connectors assigned to no system.",
        };
    }

    // ---------------------------------------------------------------- air terminals vs spaces

    public static object AirTerminals(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        int limit = Int(p, "limit", 200, 1, 5000);
        Level level = Has(p, "level") ? Find.NeedLevel(doc, p) : null;
        var rows = new List<Obj>();
        var bySpace = new Dictionary<ElementId, (Space space, double supply, double ret, double exh, int n)>();
        int noSpace = 0, count = 0;
        bool anySpaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType().Any();
        foreach (var e in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DuctTerminal).WhereElementIsNotElementType())
        {
            if (level != null && e.LevelId != level.Id) continue;
            count++;
            double flow = Val(e, "RBS_DUCT_FLOW_PARAM", UnitTypeId.LitersPerSecond) ?? 0;
            string cls = null;
            var cm = Connectors(e);
            if (cm != null) foreach (Connector c in cm.Connectors) { if (c.Domain == Domain.DomainHvac) { cls = c.DuctSystemType.ToString(); break; } }
            Space space = null;
            var pt = (e.Location as LocationPoint)?.Point;
            if (anySpaces && pt != null)
            {
                space = doc.GetSpaceAtPoint(pt) ?? doc.GetSpaceAtPoint(pt - new XYZ(0, 0, 0.5));
                if (space == null) noSpace++;
                else
                {
                    var a = bySpace.TryGetValue(space.Id, out var x) ? x : (space, 0.0, 0.0, 0.0, 0);
                    if (cls == "SupplyAir") a.Item2 += flow; else if (cls == "ReturnAir") a.Item3 += flow; else if (cls == "ExhaustAir") a.Item4 += flow;
                    a.Item5++;
                    bySpace[space.Id] = a;
                }
            }
            if (rows.Count < limit)
                rows.Add(new Obj
                {
                    ["id"] = e.Id.Value, ["label"] = Describe.Label(doc, e), ["system"] = S(e, "RBS_SYSTEM_NAME_PARAM"), ["classification"] = cls,
                    ["flowLps"] = Math.Round(flow, 1), ["space"] = space == null ? null : $"{space.Number} {Find.Builtin(space, "ROOM_NAME")?.AsString()}".Trim(),
                });
        }
        var spaces = bySpace.Values.Select(v =>
        {
            double? spec = Val(v.space, "ROOM_DESIGN_SUPPLY_AIRFLOW_PARAM", UnitTypeId.LitersPerSecond);
            return new Obj
            {
                ["space"] = $"{v.space.Number} {Find.Builtin(v.space, "ROOM_NAME")?.AsString()}".Trim(), ["id"] = v.space.Id.Value, ["terminals"] = v.n,
                ["supplyLps"] = Math.Round(v.supply, 1), ["returnLps"] = Math.Round(v.ret, 1), ["exhaustLps"] = Math.Round(v.exh, 1),
                ["specifiedSupplyLps"] = spec, ["supplyMinusSpecifiedLps"] = spec == null ? (double?)null : Math.Round(v.supply - spec.Value, 1),
            };
        }).OrderBy(o => (string)o["space"]).ToList();
        return new
        {
            model = doc.Title, link, terminals = count, withoutSpace = anySpaces ? noSpace : (int?)null, rows, truncated = rows.Count >= limit, spaces,
            note = anySpaces ? "Specified supply airflow is the engineer's value on the space; supply is the sum of its supply terminals."
                             : "This model has no MEP spaces: terminals are listed without the space check (create spaces with room_create kind: spaces).",
        };
    }

    // ---------------------------------------------------------------- connectors (phase 18, third part)

    private static string ConnSize(Connector c)
    {
        try
        {
            switch (c.Shape)
            {
                case ConnectorProfileType.Round: return $"{Math.Round(U.Out(c.Radius * 2))}ø";
                case ConnectorProfileType.Rectangular:
                case ConnectorProfileType.Oval: return $"{Math.Round(U.Out(c.Width))}x{Math.Round(U.Out(c.Height))}";
            }
        }
        catch { /* sizes are not readable on every connector */ }
        return null;
    }

    private static string Classification(Connector c)
    {
        try
        {
            if (c.Domain == Domain.DomainHvac) return c.DuctSystemType.ToString();
            if (c.Domain == Domain.DomainPiping) return c.PipeSystemType.ToString();
            if (c.Domain == Domain.DomainElectrical) return c.ElectricalSystemType.ToString();
        }
        catch { /* not set */ }
        return null;
    }

    /// <summary>Every connector of the elements given (or of a category): domain, shape, size, classification, flow direction,
    /// the system it is in and what it is connected to.</summary>
    public static object MepConnectors(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        int limit = Int(p, "limit", 100, 1, 2000);
        bool onlyOpen = Flag(p, "open_only", false);
        IEnumerable<Element> elems;
        if (Has(p, "ids")) elems = Ids(p, "ids", 2000).Select(id => doc.GetElement(id) ?? throw new ArgumentException($"No element {id.Value}."));
        else if (Has(p, "category")) elems = new FilteredElementCollector(doc).OfCategory(Find.Cat(doc, NeedStr(p, "category"))).WhereElementIsNotElementType();
        else throw new ArgumentException("Give 'ids' or 'category'.");
        var rows = new List<Obj>();
        int count = 0, withConnectors = 0;
        foreach (var e in elems)
        {
            count++;
            var cm = Connectors(e);
            if (cm == null) continue;
            var list = new List<Obj>();
            foreach (Connector c in cm.Connectors)
            {
                if (c.ConnectorType == ConnectorType.Logical) continue;
                if (onlyOpen && (c.IsConnected || c.ConnectorType != ConnectorType.End)) continue;
                var to = new List<object>();
                try
                {
                    foreach (Connector r in c.AllRefs)
                        if (r.Owner != null && r.Owner.Id != e.Id && !(r.Owner is MEPSystem))
                            to.Add(new { id = r.Owner.Id.Value, category = r.Owner.Category?.Name });
                }
                catch { /* no references */ }
                string dir = null; double? flow = null;
                try { if (c.Domain == Domain.DomainHvac || c.Domain == Domain.DomainPiping) dir = c.Direction.ToString(); } catch { }
                try { if (c.Domain == Domain.DomainHvac || c.Domain == Domain.DomainPiping) flow = Math.Round(UnitUtils.ConvertFromInternalUnits(c.Flow, UnitTypeId.LitersPerSecond), 2); } catch { }
                XYZ o = null, z = null, xa = null, ya = null;
                try { o = c.Origin; z = c.CoordinateSystem.BasisZ; xa = c.CoordinateSystem.BasisX; ya = c.CoordinateSystem.BasisY; } catch { }
                double[] V(XYZ v) => v == null ? null : new[] { Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3) };
                list.Add(new Obj
                {
                    ["index"] = c.Id, ["domain"] = c.Domain.ToString().Replace("Domain", ""), ["type"] = c.ConnectorType.ToString(),
                    ["shape"] = c.Shape.ToString(), ["size"] = ConnSize(c), ["classification"] = Classification(c), ["flowDirection"] = dir,
                    ["flowLps"] = flow, ["system"] = c.MEPSystem?.Name, ["connected"] = c.IsConnected, ["connectedTo"] = to,
                    ["origin"] = o == null ? null : U.Xyz(o), ["facing"] = V(z), ["widthAxis"] = c.Shape == ConnectorProfileType.Round ? null : V(xa), ["heightAxis"] = c.Shape == ConnectorProfileType.Round ? null : V(ya),
                });
            }
            if (list.Count == 0) continue;
            withConnectors++;
            if (rows.Count < limit)
                rows.Add(new Obj { ["id"] = e.Id.Value, ["category"] = e.Category?.Name, ["label"] = Describe.Label(doc, e), ["connectors"] = list });
        }
        return new { model = doc.Title, link, elements = count, withConnectors, items = rows, truncated = withConnectors > rows.Count,
                     note = "Sizes in mm; flowLps is the connector's flow (Revit's value). flowDirection In / Out / Bidirectional. facing is the direction the connector points; widthAxis / heightAxis are the directions of its width and height (rectangular): a run leaving a vertical connector turns cleanly along heightAxis." };
    }

    // ---------------------------------------------------------------- insulation and lining

    private static readonly BuiltInCategory[] Insulable =
    {
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_FlexPipeCurves,
    };

    /// <summary>
    /// Insulation and lining of ducts, pipes, fittings and accessories: type and thickness per element, the totals by type
    /// and thickness, and the elements with none (for QA against the engineer's specification). Host or a loaded link.
    /// </summary>
    public static object MepInsulation(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        string system = Str(p, "system");
        int limit = Int(p, "limit", 200, 1, 5000);
        bool missingOnly = Flag(p, "missing_only", false);
        IEnumerable<Element> elems;
        if (Has(p, "ids")) elems = Ids(p, "ids", 5000).Select(id => doc.GetElement(id) ?? throw new ArgumentException($"No element {id.Value}."));
        else
        {
            var cats = Has(p, "category") ? Find.Cats(doc, p, "category") : Insulable.ToList();
            elems = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType();
        }
        string One(ElementId id)
        {
            if (!(doc.GetElement(id) is InsulationLiningBase x)) return null;
            return $"{doc.GetElement(x.GetTypeId())?.Name} {U.Out(x.Thickness):0.#}";
        }
        var rows = new List<Obj>();
        var byType = new Dictionary<string, (int n, double len)>();
        int count = 0, insulated = 0, lined = 0, bare = 0;
        foreach (var e in elems)
        {
            string sys = S(e, "RBS_SYSTEM_NAME_PARAM");
            if (system != null && (sys == null || sys.IndexOf(system, StringComparison.OrdinalIgnoreCase) < 0)) continue;
            count++;
            // Revit answers lining only for the duct side (ducts, duct fittings and accessories); it throws for pipes
            bool ductSide = e is Duct || e is FlexDuct || e.Category?.BuiltInCategory == BuiltInCategory.OST_DuctFitting || e.Category?.BuiltInCategory == BuiltInCategory.OST_DuctAccessory;
            List<string> Ids(Func<ICollection<ElementId>> get) { try { return get().Select(One).Where(t => t != null).ToList(); } catch { return new List<string>(); } }
            var ins = Ids(() => InsulationLiningBase.GetInsulationIds(doc, e.Id));
            var lin = ductSide ? Ids(() => InsulationLiningBase.GetLiningIds(doc, e.Id)) : new List<string>();
            double len = e.Location is LocationCurve lc ? lc.Curve.Length : 0;
            if (ins.Count > 0) insulated++;
            if (lin.Count > 0) lined++;
            if (ins.Count == 0 && lin.Count == 0) bare++;
            foreach (var t in ins.Select(t => "insulation " + t).Concat(lin.Select(t => "lining " + t)))
            {
                var a = byType.TryGetValue(t, out var v) ? v : (0, 0.0);
                byType[t] = (a.Item1 + 1, a.Item2 + len);
            }
            if (missingOnly && ins.Count > 0) continue;
            if (rows.Count < limit)
                rows.Add(new Obj
                {
                    ["id"] = e.Id.Value, ["category"] = e.Category?.Name, ["system"] = sys, ["size"] = S(e, "RBS_CALCULATED_SIZE"),
                    ["insulation"] = ins.Count == 0 ? null : string.Join(", ", ins), ["lining"] = lin.Count == 0 ? null : string.Join(", ", lin),
                });
        }
        return new
        {
            model = doc.Title, link, units = U.Name, elements = count, insulated, lined, withNeither = bare,
            byTypeAndThickness = byType.OrderBy(k => k.Key).Select(k => new { type = k.Key, elements = k.Value.n, runLength = U.Out(k.Value.len) }).ToList(),
            items = rows, truncated = rows.Count >= limit,
            note = "Type names and thicknesses as modelled (thickness in request units). runLength counts ducts / pipes only (fittings and accessories have no length). missing_only lists elements without insulation.",
        };
    }
}
