namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 18: plumbing fixtures, sprinklers and electrical / lighting / low-voltage devices of the engineer's types
/// at the engineer's points: on a level, on a ceiling plane (face-based types facing down) or on a wall face (host or link),
/// optionally each connected to a main pipe or duct (as mep_connect). Nothing is laid out, spaced or sized here.
/// </summary>
internal static class FixtureOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["mep_fixture_place"] = FixturePlace;
    }

    private static readonly BuiltInCategory[] DeviceCats =
    {
        BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices,
        BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_DataDevices,
        BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_NurseCallDevices, BuiltInCategory.OST_TelephoneDevices,
        BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingEquipment,
    };

    /// <summary>One id for every point, or a list with one id (or null) per point.</summary>
    private static List<ElementId> PerPointIds(Obj p, string name, int count)
    {
        if (!p.TryGetValue(name, out var raw) || raw == null) return Enumerable.Repeat((ElementId)null, count).ToList();
        if (!(raw is List<object>)) { var one = NeedId(p, name); return Enumerable.Repeat(one, count).ToList(); }
        var list = Arr(p, name, 2000).Select((v, i) => v == null ? null : new ElementId((long)Number(v, $"'{name}' item {i + 1}"))).ToList();
        if (list.Count != count) throw new ArgumentException($"'{name}' needs one id, or one per point ({count}).");
        return list;
    }

    /// <summary>The wall side face (host or link) nearest a point: the point moved onto it, its reference and a horizontal direction along it.</summary>
    private static (Reference r, XYZ at, XYZ along, string side) WallFace(Document hdoc, Transform tf, RevitLinkInstance link, ElementId wallId, XYZ wanted, string what)
    {
        if (!(hdoc.GetElement(wallId) is Wall wall)) throw new ArgumentException($"{what}: {wallId.Value} is not a wall in {hdoc.Title}.");
        Reference best = null; XYZ bestAt = null, bestN = null; double bestD = double.MaxValue; string bestSide = null;
        foreach (var side in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
        {
            IList<Reference> refs;
            try { refs = HostObjectUtils.GetSideFaces(wall, side); } catch { continue; }
            foreach (var r in refs)
            {
                if (!(wall.GetGeometryObjectFromReference(r) is PlanarFace f)) continue;
                var n = tf.OfVector(f.FaceNormal).Normalize(); var o = tf.OfPoint(f.Origin);
                double d = (wanted - o).DotProduct(n);
                var on = wanted - n * d;
                if (f.Project(tf.Inverse.OfPoint(on)) == null) continue;   // beyond this face (another segment of the wall)
                if (Math.Abs(d) < bestD) { bestD = Math.Abs(d); best = r; bestAt = on; bestN = n; bestSide = side.ToString().ToLowerInvariant(); }
            }
        }
        if (best == null) throw new ArgumentException($"{what}: the point is not in front of a side face of wall {wallId.Value} (check the point and the height).");
        if (link != null) best = best.CreateLinkReference(link);
        return (best, bestAt, XYZ.BasisZ.CrossProduct(bestN).Normalize(), bestSide);
    }

    /// <summary>
    /// Fixtures / devices of a given type at points: level-based types on the level at 'height', face-based types on a
    /// downward-facing plane at 'height' (ceiling sprinklers, downlights) or, with 'walls', on the nearer side face of the
    /// wall (receptacles, switches, wall lights; walls of a link with wall_link). Optional rotation, parameter values and a
    /// connection to a main (sprinkler drops, fixture branches).
    /// </summary>
    private static object FixturePlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var symbol = (FamilySymbol)Find.TypeOf(doc, NeedStr(p, "type"), null, typeof(FamilySymbol));
        var cat = symbol.Category?.BuiltInCategory;
        if (cat == null || !DeviceCats.Contains(cat.Value))
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is a {symbol.Category?.Name}; mep_fixture_place takes plumbing fixtures and equipment, sprinklers, "
                + "electrical fixtures / equipment, lighting fixtures / devices, fire alarm, communication, data, security, nurse call, telephone devices and mechanical equipment "
                + "(air terminals: air_terminal_place).");
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        var level = Find.NeedLevel(doc, p);
        double height = U.Len(p, "height", 0);
        var pts = U.PtList(p, "points", 1, 500);
        var rotations = MepOps.PerPoint(p, "rotation", pts.Count);
        var walls = PerPointIds(p, "walls", pts.Count);
        var values = Has(p, "set") ? (p["set"] as Obj ?? throw new ArgumentException("'set' must be an object of parameter name: value.")) : null;
        Document wdoc = doc; Transform wtf = Transform.Identity; RevitLinkInstance link = null;
        if (Has(p, "wall_link"))
        {
            link = LinkScope.Resolve(doc, NeedRef(p, "wall_link"));
            wdoc = link.GetLinkDocument() ?? throw new ArgumentException($"Link '{link.Name}' is not loaded.");
            wtf = link.GetTotalTransform();
        }
        var placement = symbol.Family.FamilyPlacementType;
        bool up = Choice(p, "face", "down", "down", "up") == "up";
        bool faceBased = placement == FamilyPlacementType.WorkPlaneBased;
        if (placement != FamilyPlacementType.OneLevelBased && !faceBased)
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is a {placement} family; mep_fixture_place takes level-based and face / work-plane-based families.");
        if (walls.Any(w => w != null) && !faceBased)
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is level-based: it cannot sit on a wall face. Leave 'walls' out (it goes on the level at 'height').");

        MEPCurve main = null;
        var mainPieces = new List<ElementId>();
        if (Has(p, "main"))
        {
            main = c.Editable(Find.Elem(doc, NeedId(p, "main"))) as MEPCurve ?? throw new ArgumentException("'main' must be a straight pipe or duct.");
            mainPieces.Add(main.Id);
        }
        var connectArgs = new Obj();
        foreach (var (from, to) in new[] { ("classification", "classification"), ("branch_type", "type"), ("branch_diameter", "diameter"), ("branch_width", "width"), ("branch_height", "height") })
            if (p.TryGetValue(from, out var v)) connectArgs[to] = v;

        ReferencePlane plane = null;
        var rows = new List<Obj>();
        for (int i = 0; i < pts.Count; i++)
        {
            string what = $"Point {i + 1}";
            var at = new XYZ(pts[i].X, pts[i].Y, level.ProjectElevation + height);
            FamilyInstance inst;
            string host;
            if (walls[i] != null)
            {
                var (r, on, along, side) = WallFace(wdoc, wtf, link, walls[i], at, what);
                inst = doc.Create.NewFamilyInstance(r, on, along, symbol);
                host = $"wall {walls[i].Value} {side} face" + (link != null ? $" (link {link.Name})" : "");
                at = on;
            }
            else if (faceBased)
            {
                plane ??= MepOps.DownPlane(c, level, level.ProjectElevation + height, "fixtures", up);
                inst = doc.Create.NewFamilyInstance(plane.GetReference(), at, XYZ.BasisX, symbol);
                host = $"plane '{plane.Name}' (facing {(up ? "up" : "down")})";
            }
            else
            {
                inst = doc.Create.NewFamilyInstance(at, symbol, level, RStruct.StructuralType.NonStructural);
                var ep = inst.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                if (ep != null && !ep.IsReadOnly) ep.Set(height);
                host = $"level '{level.Name}'";
            }
            doc.Regenerate();
            if (rotations[i] is double deg && Math.Abs(deg) > 1e-9)
            {
                if (walls[i] != null) c.Warn($"{what}: 'rotation' is ignored on a wall face (the device follows the wall).");
                else { ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateUnbound(at, XYZ.BasisZ), deg * Math.PI / 180); doc.Regenerate(); }
            }
            if (values != null)
                foreach (var kv in values) Describe.SetValue(Find.Param(inst, kv.Key), kv.Value, $"{what} '{kv.Key}'");
            var row = new Obj { ["id"] = inst.Id.Value, ["point"] = U.Xyz((inst.Location as LocationPoint)?.Point ?? at), ["host"] = host };
            if (main != null)
            {
                // a tee splits the main: each fixture connects to the piece its connector points at
                Obj joined = null; string miss = null;
                foreach (var pid in mainPieces.ToList())
                {
                    if (!(doc.GetElement(pid) is MEPCurve piece)) continue;
                    try { joined = MepOps.ConnectCore(c, inst, piece, connectArgs); break; }
                    catch (ArgumentException ex) { miss = ex.Message; }   // checked before anything is drawn: try the next piece
                    catch (Exception ex) { throw new InvalidOperationException($"{what}: element {inst.Id.Value} was placed but not connected to main {main.Id.Value}: {ex.Message}"); }
                }
                if (joined == null) throw new ArgumentException($"{what}: element {inst.Id.Value} cannot be connected to main {main.Id.Value} or its pieces: {miss}");
                if (joined.TryGetValue("split", out var sp) && sp is ElementId spl) mainPieces.Add(spl);
                else if (sp is long spv) mainPieces.Add(new ElementId(spv));
                joined.Remove("split"); joined.Remove("element");
                row["connection"] = joined;
            }
            row["connectors"] = MepOps.Conns(inst).Where(x => x.ConnectorType == ConnectorType.End)
                .Select(x => $"{x.Domain.ToString().Replace("Domain", "")} {MepOps.ConnClass(x)}{(x.IsConnected ? " (connected)" : "")}").ToList();
            rows.Add(row);
        }
        return new
        {
            type = Find.TypeLabel(symbol), category = symbol.Category?.Name, placement = placement.ToString(), level = level.Name, height = U.Out(height), placed = rows.Count, items = rows,
            note = "Placed where given (positions, mounting height, spacing and types are the engineer's). Circuits: electrical_circuit_create; pipe systems: mep_system_create.",
        };
    }
}
