using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Structure;

namespace RevitMcpBridge;

/// <summary>
/// Reads for roadmap phases 10 (analytical model and loads), 11 (rooms, areas, spaces) and 14 (steel connections), in the
/// host or a loaded link. Values as Revit holds them; nothing is derived or designed. Reads only.
/// </summary>
internal static class AnalysisRead
{
    // ---------------------------------------------------------------- rooms, areas, spaces (phase 11)

    public static object ListSpaces(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        string kind = Choice(p, "kind", "rooms", "rooms", "areas", "spaces");
        var cat = kind == "rooms" ? BuiltInCategory.OST_Rooms : kind == "areas" ? BuiltInCategory.OST_Areas : BuiltInCategory.OST_MEPSpaces;
        string level = Str(p, "level");
        int limit = Int(p, "limit", 500, 1, 20000);
        var rows = new List<Obj>();
        int unplaced = 0, notEnclosed = 0;
        foreach (var e in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<SpatialElement>())
        {
            string lv = e.Level?.Name;
            if (level != null && !string.Equals(lv, level, StringComparison.OrdinalIgnoreCase)) continue;
            bool placed = e.Location != null;
            bool enclosed = e.Area > 1e-9;
            if (!placed) unplaced++;
            else if (!enclosed) notEnclosed++;
            var o = new Obj
            {
                ["id"] = e.Id.Value, ["number"] = e.Number, ["name"] = Find.Builtin(e, "ROOM_NAME")?.AsString() ?? e.Name, ["level"] = lv,
                ["areaM2"] = U.Area(e.Area), ["perimeter"] = U.Out(e.Perimeter),
                ["status"] = !placed ? "unplaced" : !enclosed ? "not enclosed" : "ok",
            };
            if (e is Room r) { o["volumeM3"] = U.Volume(r.Volume); o["department"] = Find.Builtin(r, "ROOM_DEPARTMENT")?.AsString(); o["unboundedHeight"] = U.Out(r.UnboundedHeight); }
            if (e is Space s) { o["volumeM3"] = U.Volume(s.Volume); o["occupiable"] = s.Occupiable; o["plenum"] = s.Plenum; }
            if (e is Area a) { o["areaScheme"] = a.AreaScheme?.Name; }
            rows.Add(o);
        }
        return new
        {
            model = doc.Title, kind, units = U.Name, count = rows.Count, unplaced, notEnclosed, truncated = rows.Count > limit,
            totalAreaM2 = Math.Round(rows.Sum(r => (double)r["areaM2"]), 2),
            byLevel = rows.GroupBy(r => (string)r["level"] ?? "(none)").Select(g => new { level = g.Key, count = g.Count(), areaM2 = Math.Round(g.Sum(r => (double)r["areaM2"]), 2) }).ToList(),
            items = rows.OrderBy(r => (string)r["level"]).ThenBy(r => (string)r["number"]).Take(limit).ToList(),
        };
    }

    // ---------------------------------------------------------------- analytical model (phase 10)

    private static string Release(AnalyticalMember m, bool start)
    {
        try { return m.GetReleaseType(start).ToString(); } catch { return null; }
    }

    public static object AnalyticalModel(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        var tf = link?.Tf ?? Transform.Identity;
        int limit = Int(p, "limit", 500, 1, 20000);
        var assoc = AnalyticalToPhysicalAssociationManager.GetAnalyticalToPhysicalAssociationManager(doc);
        long Physical(Element e) { try { var id = assoc?.GetAssociatedElementId(e.Id); return id == null || id == ElementId.InvalidElementId ? -1 : id.Value; } catch { return -1; } }

        var members = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalMember)).Cast<AnalyticalMember>().Select(m =>
        {
            Curve c = null;
            try { c = m.GetCurve(); } catch { /* none */ }
            return new Obj
            {
                ["id"] = m.Id.Value, ["role"] = m.StructuralRole.ToString(), ["section"] = doc.GetElement(m.SectionTypeId)?.Name,
                ["material"] = doc.GetElement(m.MaterialId)?.Name, ["start"] = c != null ? U.Xyz(tf.OfPoint(c.GetEndPoint(0))) : null,
                ["end"] = c != null ? U.Xyz(tf.OfPoint(c.GetEndPoint(1))) : null, ["length"] = c != null ? U.Out(c.Length) : (double?)null,
                ["releaseStart"] = Release(m, true), ["releaseEnd"] = Release(m, false), ["physical"] = Physical(m),
            };
        }).ToList();
        var panels = new FilteredElementCollector(doc).OfClass(typeof(AnalyticalPanel)).Cast<AnalyticalPanel>().Select(a => new Obj
        {
            ["id"] = a.Id.Value, ["role"] = a.StructuralRole.ToString(), ["thickness"] = U.Out(a.Thickness),
            ["material"] = doc.GetElement(a.MaterialId)?.Name, ["openings"] = a.GetAnalyticalOpeningsIds().Count, ["physical"] = Physical(a),
        }).ToList();
        var bcList = new FilteredElementCollector(doc).OfClass(typeof(BoundaryConditions)).Cast<BoundaryConditions>().ToList();
        var supports = bcList.GroupBy(b => b.GetBoundaryConditionsType().ToString()).Select(g => new { type = g.Key, count = g.Count() }).ToList();
        string Dofs(BoundaryConditions b, string kind)
        {
            // each direction as Revit holds it: F = fixed, R = released, S = spring
            var keys = kind == "Point" ? new[] { "X", "Y", "Z", "ROT_X", "ROT_Y", "ROT_Z" }
                     : kind == "Line" ? new[] { "X", "Y", "Z", "ROT_X" }
                     : new[] { "X", "Y", "Z" };
            var sb = new StringBuilder();
            foreach (var k in keys)
            {
                if (!Enum.TryParse("BOUNDARY_DIRECTION_" + k, out BuiltInParameter bip)) { sb.Append('?'); continue; }
                var v = b.get_Parameter(bip)?.AsInteger();
                sb.Append(v == 0 ? 'F' : v == 1 ? 'R' : v == 2 ? 'S' : '?');
            }
            return sb.ToString();
        }
        var supportItems = bcList.Take(limit).Select(b =>
        {
            string kind = b.GetBoundaryConditionsType().ToString();
            return new Obj { ["id"] = b.Id.Value, ["kind"] = kind, ["host"] = b.HostElementId?.Value,
                             ["directions"] = Dofs(b, kind) };
        }).ToList();
        return new
        {
            model = doc.Title, units = U.Name,
            members = new { count = members.Count, withoutPhysical = members.Count(m => (long)m["physical"] < 0), byRole = members.GroupBy(m => (string)m["role"]).Select(g => new { role = g.Key, count = g.Count() }).ToList(), items = members.Take(limit).ToList() },
            panels = new { count = panels.Count, withoutPhysical = panels.Count(m => (long)m["physical"] < 0), items = panels.Take(limit).ToList() },
            boundaryConditions = supports,
            supports = supportItems,
            note = "physical = the associated physical element id (-1: none). Releases as Revit's release type. Support directions in the "
                 + "order x y z rx ry rz (line: x y z rx; area: x y z): F fixed, R released, S spring, ? unknown.",
        };
    }

    // ---------------------------------------------------------------- loads (phase 10)

    private static double[] Vec(XYZ v, ForgeTypeId unit) =>
        new[] { v.X, v.Y, v.Z }.Select(x => Math.Round(UnitUtils.ConvertFromInternalUnits(x, unit), 3)).ToArray();

    public static object ListLoads(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        int limit = Int(p, "limit", 500, 1, 20000);
        var cases = new FilteredElementCollector(doc).OfClass(typeof(LoadCase)).Cast<LoadCase>().Select(c => new
        {
            id = c.Id.Value, name = c.Name, number = c.Number, nature = doc.GetElement(c.NatureId)?.Name,
            category = Category.GetCategory(doc, c.SubcategoryId)?.Name,
        }).OrderBy(c => c.number).ToList();
        var combos = new FilteredElementCollector(doc).OfClass(typeof(LoadCombination)).Cast<LoadCombination>().Select(c => new
        {
            id = c.Id.Value, name = c.Name, type = c.Type.ToString(), state = c.State.ToString(),
            components = c.GetComponents().Select(x => new { factor = x.Factor, of = doc.GetElement(x.LoadCaseOrCombinationId)?.Name }).ToList(),
            usages = c.GetUsageIds().Select(u => doc.GetElement(u)?.Name).ToList(),
        }).ToList();
        var loads = new List<Obj>();
        foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(LoadBase)).Cast<LoadBase>())
        {
            var o = new Obj { ["id"] = l.Id.Value, ["kind"] = l.GetType().Name, ["case"] = l.LoadCaseName, ["nature"] = l.LoadNatureName,
                              ["host"] = l.IsHosted ? l.HostElementId.Value : (long?)null, ["reaction"] = l.IsReaction };
            if (l is PointLoad pl) { o["forceKN"] = Vec(pl.ForceVector, UnitTypeId.Kilonewtons); o["momentKNm"] = Vec(pl.MomentVector, UnitTypeId.KilonewtonMeters); o["at"] = U.Xyz(pl.Point); }
            else if (l is LineLoad ll) { o["force1KNm"] = Vec(ll.ForceVector1, UnitTypeId.KilonewtonsPerMeter); o["force2KNm"] = Vec(ll.ForceVector2, UnitTypeId.KilonewtonsPerMeter); o["uniform"] = ll.IsUniform; o["start"] = U.Xyz(ll.StartPoint); o["end"] = U.Xyz(ll.EndPoint); }
            else if (l is AreaLoad al) { o["force1KNm2"] = Vec(al.ForceVector1, UnitTypeId.KilonewtonsPerSquareMeter); o["areaM2"] = U.Area(al.Area); o["projected"] = al.IsProjected; }
            loads.Add(o);
        }
        return new
        {
            model = doc.Title, units = U.Name, loadCases = cases, combinations = combos,
            loads = new { count = loads.Count, byCase = loads.GroupBy(x => (string)x["case"] ?? "?").Select(g => new { loadCase = g.Key, count = g.Count() }).ToList(), items = loads.Take(limit).ToList() },
            note = "Forces in kN, line loads kN/m, area loads kN/m2, moments kN·m, as entered in Revit (global axes unless OrientTo says otherwise). Read only: no load is derived.",
        };
    }

    // ---------------------------------------------------------------- steel connections (phase 14)

    public static object SteelConnections(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        var tf = link?.Tf ?? Transform.Identity;
        int limit = Int(p, "limit", 500, 1, 20000);
        var rows = new FilteredElementCollector(doc).OfClass(typeof(StructuralConnectionHandler)).Cast<StructuralConnectionHandler>().Select(h =>
        {
            XYZ o = null;
            try { o = h.GetOrigin(); } catch { /* none */ }
            return new Obj
            {
                ["id"] = h.Id.Value, ["type"] = doc.GetElement(h.GetTypeId())?.Name, ["members"] = h.GetConnectedElementIds().Select(i => (object)i.Value).ToList(),
                ["approval"] = doc.GetElement(h.ApprovalTypeId)?.Name, ["codeChecking"] = h.CodeCheckingStatus.ToString(),
                ["custom"] = h.IsCustom(), ["detailed"] = h.IsDetailed(), ["origin"] = o != null ? U.Xyz(tf.OfPoint(o)) : null,
            };
        }).ToList();
        return new
        {
            model = doc.Title, count = rows.Count, truncated = rows.Count > limit,
            byType = rows.GroupBy(r => (string)r["type"] ?? "?").Select(g => new { type = g.Key, count = g.Count() }).ToList(),
            items = rows.Take(limit).ToList(),
            note = "Connections as placed in Revit (type, connected members, approval, code checking). Plates, bolts and welds are not read yet.",
        };
    }
}
