using Autodesk.Revit.DB.Architecture;

namespace RevitMcpBridge;

/// <summary>Roadmap phase 17 (reads): type layers, room finishes measured from the room geometry, architectural QA. Nothing here changes anything.</summary>
internal static class ArchRead
{
    // ---------------------------------------------------------------- layers of wall / floor / roof / ceiling types

    public static object TypeLayers(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        IEnumerable<HostObjAttributes> types;
        if (Has(p, "types"))
            types = StrList(p, "types", 200).Select(t => Find.TypeOf(doc, t, null) as HostObjAttributes
                                                     ?? throw new ArgumentException($"'{t}' is not a wall, floor, roof or ceiling type."));
        else
        {
            string kind = Choice(p, "kind", "wall", "wall", "floor", "roof", "ceiling");
            Type cls = kind == "wall" ? typeof(WallType) : kind == "floor" ? typeof(FloorType) : kind == "roof" ? typeof(RoofType) : typeof(CeilingType);
            types = new FilteredElementCollector(doc).OfClass(cls).Cast<HostObjAttributes>().OrderBy(t => t.Name);
        }
        int limit = Int(p, "limit", 200, 1, 1000);
        var rows = new List<object>();
        foreach (var t in types.Take(limit))
        {
            var cs = t.GetCompoundStructure();
            if (cs == null) { rows.Add(new { type = t.Name, id = t.Id.Value, layered = false, kind = (t as WallType)?.Kind.ToString() }); continue; }
            int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
            var layers = cs.GetLayers().Select((l, i) => new
            {
                index = i + 1, function = l.Function.ToString(), material = (doc.GetElement(l.MaterialId) as Material)?.Name ?? "<By Category>",
                thickness = U.Out(l.Width), core = i >= first && i <= last, variable = cs.VariableLayerIndex == i,
            }).ToList();
            rows.Add(new { type = t.Name, id = t.Id.Value, layered = true, totalThickness = U.Out(cs.GetWidth()), layers });
        }
        return new { count = rows.Count, order = "walls: exterior to interior; floors, roofs, ceilings: top to bottom", types = rows };
    }

    // ---------------------------------------------------------------- room finishes

    private static Obj Opening(Document doc, FamilyInstance fi)
    {
        double W(Element e, params BuiltInParameter[] bs)
        {
            foreach (var b in bs) { var pr = e?.get_Parameter(b); if (pr != null && pr.HasValue && pr.AsDouble() > 1e-9) return pr.AsDouble(); }
            return 0;
        }
        var sym = fi.Symbol;
        double w = W(fi, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH);
        if (w == 0) w = W(sym, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH);
        double h = W(fi, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT);
        if (h == 0) h = W(sym, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT);
        return new Obj
        {
            ["id"] = fi.Id.Value, ["category"] = fi.Category?.Name, ["type"] = Find.TypeLabel(sym), ["width"] = w > 0 ? U.Out(w) : (double?)null,
            ["height"] = h > 0 ? U.Out(h) : (double?)null, ["areaM2"] = w > 0 && h > 0 ? U.Area(w * h) : (double?)null,
        };
    }

    /// <summary>
    /// Per room: the finish parameters, and the areas measured from the room's own geometry (finish faces): floor, ceiling,
    /// gross wall area per bounding element (category, type), the doors and windows in its walls with their sizes, and the
    /// net wall area (gross minus the openings whose width and height the family gives).
    /// </summary>
    public static object RoomFinishes(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        IEnumerable<Room> rooms;
        if (Has(p, "ids")) rooms = Ids(p, "ids", 2000).Select(id => Find.Elem(doc, id) as Room ?? throw new ArgumentException($"{id.Value} is not a room."));
        else
        {
            var q = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>();
            if (Has(p, "level")) { var lv = Find.NeedLevel(doc, p); q = q.Where(r => r.LevelId == lv.Id); }
            rooms = q.OrderBy(r => r.Level?.Elevation ?? 0).ThenBy(r => r.Number, StringComparer.OrdinalIgnoreCase);
        }
        int limit = Int(p, "limit", 500, 1, 2000);
        var calc = new SpatialElementGeometryCalculator(doc, new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish });
        var rows = new List<object>(); var skipped = new List<object>();
        int seen = 0; bool truncated = false;
        foreach (var room in rooms)
        {
            if (room.Location == null || room.Area <= 1e-9) { skipped.Add(new { id = room.Id.Value, number = room.Number, why = room.Location == null ? "unplaced" : "not enclosed or redundant" }); continue; }
            if (++seen > limit) { truncated = true; break; }
            SpatialElementGeometryResults res;
            try { res = calc.CalculateSpatialElementGeometry(room); }
            catch (Exception ex) { skipped.Add(new { id = room.Id.Value, number = room.Number, why = "Revit could not compute its geometry: " + ex.Message }); continue; }
            var solid = res.GetGeometry();
            double floor = 0, ceiling = 0, sides = 0;
            var byElem = new Dictionary<long, (string cat, string type, double area, bool host)>();
            var walls = new HashSet<long>();
            foreach (Face f in solid.Faces)
            {
                var n = f.ComputeNormal(new UV(0.5, 0.5));
                if (n.Z < -0.9) floor += f.Area; else if (n.Z > 0.9) ceiling += f.Area; else sides += f.Area;
                IList<SpatialElementBoundarySubface> subs;
                try { subs = res.GetBoundaryFaceInfo(f); } catch { continue; }
                foreach (var sub in subs)
                {
                    if (sub.SubfaceType != SubfaceType.Side) continue;
                    var le = sub.SpatialBoundaryElement;
                    bool host = le.LinkInstanceId == ElementId.InvalidElementId;
                    Element be = host ? doc.GetElement(le.HostElementId)
                        : (doc.GetElement(le.LinkInstanceId) as RevitLinkInstance)?.GetLinkDocument()?.GetElement(le.LinkedElementId);
                    long key = be?.Id.Value ?? -1;
                    double a = sub.GetSubface()?.Area ?? 0;
                    string type = be == null ? "separation line / unknown" : (be.Document.GetElement(be.GetTypeId()) is ElementType et ? Find.TypeLabel(et) : be.Name);
                    byElem[key] = byElem.TryGetValue(key, out var v) ? (v.cat, v.type, v.area + a, v.host) : (be?.Category?.Name ?? "-", type, a, host);
                    if (host && be is Wall) walls.Add(key);
                }
            }
            var openings = new List<Obj>();
            foreach (long wid in walls)
                foreach (var iid in ((Wall)doc.GetElement(new ElementId(wid))).FindInserts(true, false, false, false))
                    if (doc.GetElement(iid) is FamilyInstance fi && (fi.FromRoom?.Id == room.Id || fi.ToRoom?.Id == room.Id))
                    { var o = Opening(doc, fi); o["wall"] = wid; openings.Add(o); }
            double openM2 = openings.Sum(o => o["areaM2"] as double? ?? 0);
            double grossM2 = U.Area(sides);
            string P(BuiltInParameter b) => room.get_Parameter(b)?.AsString();
            rows.Add(new
            {
                id = room.Id.Value, number = room.Number, name = Find.Builtin(room, "ROOM_NAME")?.AsString(), level = room.Level?.Name,
                areaM2 = U.Area(room.Area), perimeter = U.Out(room.Perimeter), height = U.Out(room.UnboundedHeight),
                finishes = new { floor = P(BuiltInParameter.ROOM_FINISH_FLOOR), wall = P(BuiltInParameter.ROOM_FINISH_WALL), ceiling = P(BuiltInParameter.ROOM_FINISH_CEILING), @base = P(BuiltInParameter.ROOM_FINISH_BASE) },
                department = P(BuiltInParameter.ROOM_DEPARTMENT), occupancy = P(BuiltInParameter.ROOM_OCCUPANCY),
                measured = new
                {
                    floorM2 = U.Area(floor), ceilingM2 = U.Area(ceiling), wallGrossM2 = grossM2, openingsM2 = Math.Round(openM2, 4),
                    wallNetM2 = Math.Round(grossM2 - openM2, 4), openingsWithoutSize = openings.Count(o => o["areaM2"] == null),
                },
                bounding = byElem.OrderByDescending(kv => kv.Value.area).Select(kv => new { id = kv.Key < 0 ? (long?)null : kv.Key, category = kv.Value.cat, type = kv.Value.type, inLink = !kv.Value.host, areaM2 = U.Area(kv.Value.area) }).ToList(),
                openings,
            });
        }
        return new
        {
            count = rows.Count, truncated, rooms = rows, skipped,
            note = "Areas from the room's geometry at the finish faces (walls up to the room's height). Gross wall area includes "
                 + "door and window openings; net subtracts the openings whose family gives width and height (rough size first).",
        };
    }

    // ---------------------------------------------------------------- architectural QA

    /// <summary>Rooms unplaced / not enclosed / unnamed / with a duplicate number / untagged; doors and windows without a mark, with a duplicate mark, untagged, or (doors) between no rooms.</summary>
    public static object ArchQa(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        int limit = Int(p, "limit", 50, 1, 1000);
        Level lv = Has(p, "level") ? Find.NeedLevel(doc, p) : null;
        var issues = new List<object>();
        void Add(string what, string problem, IEnumerable<Element> els, Func<Element, object> describe)
        {
            var list = els.ToList();
            if (list.Count > 0) issues.Add(new { what, problem, count = list.Count, items = list.Take(limit).Select(describe).ToList() });
        }

        var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>()
            .Where(r => lv == null || r.LevelId == lv.Id).ToList();
        object R(Element e) { var r = (Room)e; return new { id = r.Id.Value, number = r.Number, name = Find.Builtin(r, "ROOM_NAME")?.AsString(), level = r.Level?.Name }; }
        Add("rooms", "unplaced", rooms.Where(r => r.Location == null), R);
        Add("rooms", "not enclosed or redundant", rooms.Where(r => r.Location != null && r.Area <= 1e-9), R);
        Add("rooms", "no name", rooms.Where(r => string.IsNullOrWhiteSpace(Find.Builtin(r, "ROOM_NAME")?.AsString())), R);
        Add("rooms", "no number", rooms.Where(r => string.IsNullOrWhiteSpace(r.Number)), R);
        Add("rooms", "duplicate number", rooms.Where(r => !string.IsNullOrWhiteSpace(r.Number)).GroupBy(r => r.Number, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).SelectMany(g => g), R);
        var tagged = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(SpatialElementTag)).OfType<RoomTag>()
            .Select(t => t.TaggedLocalRoomId?.Value ?? -1));
        Add("rooms", "not tagged in any view", rooms.Where(r => r.Location != null && r.Area > 1e-9 && !tagged.Contains(r.Id.Value)), R);

        var tagIds = new HashSet<long>();
        foreach (IndependentTag t in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)))
            foreach (var id in t.GetTaggedLocalElementIds()) tagIds.Add(id.Value);
        foreach (var (cat, label) in new[] { (BuiltInCategory.OST_Doors, "doors"), (BuiltInCategory.OST_Windows, "windows") })
        {
            var els = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(e => lv == null || e.LevelId == lv.Id).ToList();
            string Mark(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            object D(Element e) { var f = (FamilyInstance)e; return new { id = f.Id.Value, mark = Mark(f), type = Find.TypeLabel(f.Symbol), level = (doc.GetElement(f.LevelId) as Level)?.Name, host = f.Host?.Id.Value }; }
            Add(label, "no mark", els.Where(e => string.IsNullOrWhiteSpace(Mark(e))), D);
            Add(label, "duplicate mark", els.Where(e => !string.IsNullOrWhiteSpace(Mark(e))).GroupBy(Mark, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g), D);
            Add(label, "not tagged in any view", els.Where(e => !tagIds.Contains(e.Id.Value)), D);
            Add(label, "no host", els.Where(e => e.Host == null), D);
            if (cat == BuiltInCategory.OST_Doors && rooms.Count > 0)
                Add(label, "between no rooms (neither side has a room)", els.Where(e => e.FromRoom == null && e.ToRoom == null), D);
        }
        return new
        {
            checkedRooms = rooms.Count, issues = issues.Count, items = issues,
            note = "Tags count in any view of the model. Naming conventions: check_names; Revit's own warnings: list_warnings.",
        };
    }
}
