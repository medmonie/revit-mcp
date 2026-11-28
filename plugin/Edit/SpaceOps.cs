using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;

namespace RevitMcpBridge;

/// <summary>Roadmap phase 11 (edits): rooms and MEP spaces at given points, in every enclosed region of a level, and separation lines.</summary>
internal static class SpaceOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["room_create"] = RoomCreate;
        ops["room_separation"] = RoomSeparation;
        ops["spaces_from_rooms"] = SpacesFromRooms;
        ops["hvac_zone_create"] = ZoneCreate;
        ops["space_airflow_set"] = SpaceAirflow;
    }

    private static Phase PhaseOf(Document doc, Obj p)
    {
        string name = Str(p, "phase");
        if (name == null) return null;
        return doc.Phases.Cast<Phase>().FirstOrDefault(ph => string.Equals(ph.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No phase '{name}'. Phases: {string.Join(", ", doc.Phases.Cast<Phase>().Select(ph => ph.Name))}.");
    }

    private static Obj Row(SpatialElement e) => new Obj
    {
        ["id"] = e.Id.Value, ["number"] = e.Number, ["name"] = Find.Builtin(e, "ROOM_NAME")?.AsString() ?? e.Name, ["level"] = e.Level?.Name,
        ["areaM2"] = U.Area(e.Area), ["status"] = e.Location == null ? "unplaced" : e.Area > 1e-9 ? "ok" : "not enclosed",
    };

    /// <summary>
    /// Rooms or spaces at the points given (with the names and numbers given), or, with all_regions, one in every enclosed
    /// region of the level that has none yet.
    /// </summary>
    private static object RoomCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", "rooms", "rooms", "spaces");
        var level = Find.NeedLevel(doc, p);
        var phase = PhaseOf(doc, p);
        var made = new List<SpatialElement>();
        if (Flag(p, "all_regions", false))
        {
            if (Has(p, "points")) throw new ArgumentException("Give 'points' or all_regions, not both.");
            if (kind == "spaces") throw new ArgumentException("all_regions is for rooms; give 'points' for spaces.");
            // Only regions that have no room yet (NewRooms2 also fills regions that already hold one).
            var topology = phase == null ? doc.get_PlanTopology(level) : doc.get_PlanTopology(level, phase);
            foreach (PlanCircuit circuit in topology.Circuits)
                if (!circuit.IsRoomLocated && circuit.Area > 1e-6) made.Add(doc.Create.NewRoom(null, circuit));
        }
        else
        {
            var pts = U.PtList(p, "points", 1, 500);
            var names = Has(p, "names") ? StrList(p, "names", 500) : null;
            var numbers = Has(p, "numbers") ? StrList(p, "numbers", 500) : null;
            if (names != null && names.Count != pts.Count) throw new ArgumentException("'names' needs one name per point.");
            if (numbers != null && numbers.Count != pts.Count) throw new ArgumentException("'numbers' needs one number per point.");
            for (int i = 0; i < pts.Count; i++)
            {
                var uv = new UV(pts[i].X, pts[i].Y);
                SpatialElement e;
                if (kind == "rooms") e = phase == null ? doc.Create.NewRoom(level, uv) : throw new ArgumentException("'phase' with points is for spaces; for rooms use it with all_regions.");
                else e = phase == null ? doc.Create.NewSpace(level, uv) : doc.Create.NewSpace(level, phase, uv);
                if (e == null) throw new InvalidOperationException($"Revit did not create the {kind.TrimEnd('s')} at point {i + 1}.");
                if (names != null) e.Name = names[i];
                if (numbers != null) e.Number = numbers[i];
                made.Add(e);
            }
        }
        doc.Regenerate();
        var rows = made.Select(Row).ToList();
        int open = rows.Count(r => (string)r["status"] != "ok");
        if (open > 0) c.Warn($"{open} of the new {kind} are not enclosed (no bounding walls or separation lines around the point).");
        return new { kind, level = level.Name, created = rows.Count, notEnclosed = open, items = rows };
    }

    /// <summary>Room (or space) separation lines on a level's plan, from segments given.</summary>
    private static object RoomSeparation(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", "rooms", "rooms", "spaces");
        var level = Find.NeedLevel(doc, p);
        View view = Has(p, "view") ? Find.ViewOf(doc, NeedRef(p, "view"))
            : new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel?.Id == level.Id)
                .OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : v.ViewType == ViewType.EngineeringPlan ? 1 : 2).FirstOrDefault()
              ?? throw new ArgumentException($"No plan view of level '{level.Name}'. Give 'view' or create a plan first.");
        if (!(view is ViewPlan plan) || plan.GenLevel?.Id != level.Id) throw new ArgumentException($"'{view.Name}' is not a plan of level '{level.Name}'.");
        var curves = new CurveArray();
        int n = 0;
        foreach (var item in ObjList(p, "lines", 500))
            curves.Append(U.Curve(item, $"line {++n}", level.Elevation, flatten: true));
        var sketch = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));
        var made = kind == "rooms" ? doc.Create.NewRoomBoundaryLines(sketch, curves, view) : doc.Create.NewSpaceBoundaryLines(sketch, curves, view);
        var ids = new List<long>();
        foreach (ModelCurve mc in made) ids.Add(mc.Id.Value);
        return new { kind, level = level.Name, view = view.Name, created = ids.Count, ids };
    }

    // ---------------------------------------------------------------- phase 18: spaces from the architect's rooms, zones, design airflow

    /// <summary>
    /// One MEP space for every placed room of a linked architectural model, at the room's point, on the host level at the
    /// same elevation, with the room's name and number. The link is made room-bounding so the spaces take its walls.
    /// Regions that already hold a space are skipped.
    /// </summary>
    private static object SpacesFromRooms(EditContext c, Obj p)
    {
        var doc = c.Doc;
        if (!(Find.Elem(doc, NeedId(p, "link")) is RevitLinkInstance link)) throw new ArgumentException("'link' must be a Revit link instance (list_links lists them).");
        var ldoc = link.GetLinkDocument() ?? throw new ArgumentException($"Link {link.Id.Value} is not loaded.");
        var phase = PhaseOf(doc, p);
        if (Flag(p, "room_bounding", true))
        {
            var lt = c.Editable(doc.GetElement(link.GetTypeId()));
            var rb = lt.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
            if (rb != null && !rb.IsReadOnly && rb.AsInteger() != 1) { rb.Set(1); doc.Regenerate(); c.Warn($"Link type '{lt.Name}' was made room-bounding."); }
        }
        var only = Has(p, "levels") ? new HashSet<string>(StrList(p, "levels", 200), StringComparer.OrdinalIgnoreCase) : null;
        double tol = U.LenMm(p, "level_tolerance", 50);
        var t = link.GetTotalTransform();
        var levels = Find.Levels(doc).ToList();
        var rooms = new FilteredElementCollector(ldoc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
            .Cast<SpatialElement>().Where(r => r.Location is LocationPoint && r.Area > 1e-9).ToList();
        if (rooms.Count == 0) throw new ArgumentException($"The linked model {ldoc.Title} has no placed, enclosed rooms.");
        int max = Int(p, "max", 2000, 1, 5000);
        var targetPhase = phase ?? doc.Phases.Cast<Phase>().Last();
        var existing = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces).WhereElementIsNotElementType().OfType<Space>()
            .Where(s => s.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == targetPhase.Id).ToList();
        var made = new List<Obj>(); var skipped = new List<string>();
        bool truncated = false;
        // stable order (level, number); 'max' counts spaces made, after the level filter
        foreach (var room in rooms.OrderBy(r => r.Level?.ProjectElevation ?? 0).ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase))
        {
            if (made.Count >= max) { truncated = true; break; }
            string rname = Find.Builtin(room, "ROOM_NAME")?.AsString() ?? room.Name, label = $"{room.Number} {rname}".Trim();
            var at = t.OfPoint(((LocationPoint)room.Location).Point);
            double lz = t.OfPoint(new XYZ(0, 0, room.Level?.ProjectElevation ?? 0)).Z;
            var level = levels.Where(l => Math.Abs(l.ProjectElevation - lz) <= tol).OrderBy(l => Math.Abs(l.ProjectElevation - lz)).FirstOrDefault();
            if (level == null) { skipped.Add($"{label}: no level at {U.Out(lz):0}"); continue; }
            if (only != null && !only.Contains(level.Name)) continue;
            Space there = null;
            foreach (double dz in new[] { 300.0, 1000.0, 2000.0 })
            {
                var probe = new XYZ(at.X, at.Y, level.ProjectElevation + U.Mm(dz));
                there = phase == null ? doc.GetSpaceAtPoint(probe) : doc.GetSpaceAtPoint(probe, phase);
                if (there != null) break;
            }
            // a space with the room's number and name on that level counts too (a room point can sit where no space is found)
            there = there ?? existing.FirstOrDefault(s => s.LevelId == level.Id && s.Number == room.Number && (Find.Builtin(s, "ROOM_NAME")?.AsString() ?? s.Name) == rname);
            if (there != null) { skipped.Add($"{label}: space {there.Number} is already there"); continue; }
            var sp = phase == null ? doc.Create.NewSpace(level, new UV(at.X, at.Y)) : doc.Create.NewSpace(level, phase, new UV(at.X, at.Y));
            sp.Name = rname;
            try { sp.Number = room.Number; } catch (Exception ex) { c.Warn($"Space for room {label}: number not set ({ex.Message})."); }
            if (room is Autodesk.Revit.DB.Architecture.Room ar && ar.UnboundedHeight > U.Mm(100)) { sp.UpperLimit = level; sp.LimitOffset = ar.UnboundedHeight; }
            made.Add(new Obj { ["space"] = sp, ["room"] = room.Id.Value });
            existing.Add(sp);
        }
        doc.Regenerate();
        var rows = made.Select(m => { var r = Row((SpatialElement)m["space"]); r["room"] = m["room"]; return r; }).ToList();
        int open = rows.Count(r => (string)r["status"] != "ok");
        if (open > 0) c.Warn($"{open} of the new spaces are not enclosed (the link's walls do not bound them at that point).");
        if (truncated) c.Warn($"Stopped at max = {max} new spaces; run again to continue (rooms that have a space are skipped).");
        return new { link = ldoc.Title, rooms = rooms.Count, created = rows.Count, truncated, notEnclosed = open, skipped = skipped.Take(200).ToList(), skippedCount = skipped.Count, items = rows };
    }

    private static Space NeedSpace(EditContext c, ElementId id) =>
        c.Editable(Find.Elem(c.Doc, id)) as Space ?? throw new ArgumentException($"Element {id.Value} is not an MEP space.");

    /// <summary>An HVAC zone holding the spaces given (all on one level), with a name.</summary>
    private static object ZoneCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
#if REVIT2027
        {
            // Revit 2027: HVAC zones are system zones (GenericZone, space based) of a system zone type.
            var spaces27 = Ids(p, "spaces", 2000).Select(id => NeedSpace(c, id)).ToList();
            var level27 = spaces27[0].Level ?? throw new ArgumentException($"Space {spaces27[0].Id.Value} has no level.");
            var other27 = spaces27.Where(s => s.Level?.Id != level27.Id).Select(s => s.Id.Value).ToList();
            if (other27.Count > 0) throw new ArgumentException($"A zone is made on one level: spaces {string.Join(", ", other27)} are not on '{level27.Name}'.");
            var types = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Mechanical.SystemZoneElementType))
                .Cast<Autodesk.Revit.DB.Mechanical.SystemZoneElementType>()
                .Where(t => t.GeometricDefinition.ToString().IndexOf("Space", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Autodesk.Revit.DB.Mechanical.SystemZoneElementType zt;
            string tname = Str(p, "type");
            bool madeType = false;
            if (tname != null)
            {
                zt = types.FirstOrDefault(t => t.Name.Equals(tname, StringComparison.OrdinalIgnoreCase));
                if (zt == null)
                {
                    if (!Flag(p, "create_type", false))
                        throw new ArgumentException($"No space-based system zone type '{tname}'. Types: {(types.Count == 0 ? "none" : string.Join(", ", types.Select(t => t.Name)))}. Pass create_type: true to make it (its set points stay Revit's defaults; set them in Revit).");
                    zt = Autodesk.Revit.DB.Mechanical.SystemZoneElementType.CreateSpaceBased(doc, tname);
                    madeType = true;
                }
            }
            else
            {
                if (types.Count == 0) throw new ArgumentException("The model has no space-based system zone type. Give 'type' with create_type: true, or make one in Revit.");
                if (types.Count > 1) throw new ArgumentException($"Several system zone types: {string.Join(", ", types.Select(t => t.Name))}. Give 'type'.");
                zt = types[0];
            }
            var taken = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Analysis.GenericZone)).Cast<Autodesk.Revit.DB.Analysis.GenericZone>()
                .SelectMany(z => { try { return z.GetSpaceIds().Select(i => (zone: z, id: i)); } catch { return Enumerable.Empty<(Autodesk.Revit.DB.Analysis.GenericZone zone, ElementId id)>(); } })
                .Where(x => spaces27.Any(s => s.Id == x.id)).ToList();
            if (taken.Count > 0)
                throw new ArgumentException("Already in a system zone: " + string.Join(", ", taken.Select(x => $"space {x.id.Value} in '{x.zone.Name}'")) + ".");
            var ids = new HashSet<ElementId>(spaces27.Select(s => s.Id));
            string zname = Str(p, "name") ?? "Zone";
            var gz = Autodesk.Revit.DB.Analysis.GenericZone.CreateSpaceBased(doc, zt.Id, zname, Autodesk.Revit.DB.Mechanical.SystemZoneData.Create(), level27.Id, ids);
            doc.Regenerate();
            return new { id = gz.Id.Value, name = gz.Name, kind = "system zone (Revit 2027)", type = zt.Name, typeCreated = madeType, level = level27.Name,
                         spaces = gz.GetSpaceIds().Count, areaM2 = U.Area(gz.Area) };
        }
#else
        var spaces = Ids(p, "spaces", 2000).Select(id => NeedSpace(c, id)).ToList();
        var level = spaces[0].Level ?? throw new ArgumentException($"Space {spaces[0].Id.Value} has no level.");
        var other = spaces.Where(s => s.Level?.Id != level.Id).Select(s => s.Id.Value).ToList();
        if (other.Count > 0) throw new ArgumentException($"A zone is made on one level: spaces {string.Join(", ", other)} are not on '{level.Name}'.");
        var phase = doc.GetElement(spaces[0].get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase
                    ?? doc.Phases.Cast<Phase>().Last();
        var zone = doc.Create.NewZone(level, phase);
        var set = new SpaceSet();
        foreach (var s in spaces) set.Insert(s);
        if (!zone.AddSpaces(set)) throw new InvalidOperationException("Revit did not add the spaces to the new zone.");
        if (Str(p, "name") is string name) zone.Name = name;
        doc.Regenerate();
        return new { id = zone.Id.Value, name = zone.Name, level = level.Name, phase = phase.Name, spaces = zone.Spaces.Size,
                     areaM2 = U.Area(spaces.Sum(x => x.Area)) };
#endif
    }

    /// <summary>The design supply / return / exhaust airflow of spaces, as the engineer gives them (L/s).</summary>
    private static object SpaceAirflow(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var spaces = Ids(p, "spaces", 2000).Select(id => NeedSpace(c, id)).ToList();
        var which = new[] { ("supply", BuiltInParameter.ROOM_DESIGN_SUPPLY_AIRFLOW_PARAM), ("return", BuiltInParameter.ROOM_DESIGN_RETURN_AIRFLOW_PARAM),
                            ("exhaust", BuiltInParameter.ROOM_DESIGN_EXHAUST_AIRFLOW_PARAM) };
        if (!which.Any(w => Has(p, w.Item1))) throw new ArgumentException("Give 'supply', 'return' and / or 'exhaust' (L/s): one number, or one per space.");
        var values = which.Where(w => Has(p, w.Item1)).Select(w => (w.Item1, w.Item2, list: PerSpace(p, w.Item1, spaces.Count))).ToList();
        var rows = new List<Obj>();
        for (int i = 0; i < spaces.Count; i++)
        {
            var sp = spaces[i];
            var row = new Obj { ["id"] = sp.Id.Value, ["number"] = sp.Number };
            foreach (var (key, bip, list) in values)
            {
                var prm = sp.get_Parameter(bip);
                if (prm == null || prm.IsReadOnly) throw new InvalidOperationException($"Space {sp.Id.Value}: the design {key} airflow cannot be set.");
                prm.Set(UnitUtils.ConvertToInternalUnits(list[i], UnitTypeId.LitersPerSecond));
                row[key + "Lps"] = Math.Round(UnitUtils.ConvertFromInternalUnits(prm.AsDouble(), UnitTypeId.LitersPerSecond), 1);
            }
            rows.Add(row);
        }
        return new { changed = rows.Count, items = rows, note = "Design values as given; compare with the terminals by air_terminals." };
    }

    private static List<double> PerSpace(Obj p, string name, int count)
    {
        if (p.TryGetValue(name, out var raw) && raw is double one) { if (one < 0) throw new ArgumentException($"'{name}' must not be negative."); return Enumerable.Repeat(one, count).ToList(); }
        var list = Arr(p, name, 2000).Select((v, i) => Number(v, $"'{name}' item {i + 1}")).ToList();
        if (list.Count != count) throw new ArgumentException($"'{name}' needs one value, or one value per space ({count}).");
        if (list.Any(v => v < 0)) throw new ArgumentException($"'{name}' must not be negative.");
        return list;
    }
}
