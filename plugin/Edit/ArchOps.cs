using Autodesk.Revit.DB.Architecture;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 17 (architecture): doors and windows in walls at the positions the architect gives, and wall / floor /
/// roof / ceiling types from the layers the architect gives. Sizes, finishes and positions are the architect's.
/// </summary>
internal static class ArchOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["door_window_place"] = DoorWindowPlace;
        ops["layered_type_create"] = LayeredTypeCreate;
    }

    // ---------------------------------------------------------------- doors and windows

    /// <summary>
    /// Doors or windows of the type given, each in a wall: at a point (projected onto the wall's location line) or at a
    /// distance along the wall from its start; sill or head height, flips and mark as given. Without 'wall' the wall whose
    /// location line passes nearest the point (within its half width plus the tolerance) on the level given is used.
    /// </summary>
    private static object DoorWindowPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var symbol = Find.TypeOf(doc, NeedStr(p, "type"), null, typeof(FamilySymbol)) as FamilySymbol
                     ?? throw new ArgumentException("'type' must be a door or window type (list_types category doors / windows).");
        var cat = symbol.Category?.BuiltInCategory;
        if (cat != BuiltInCategory.OST_Doors && cat != BuiltInCategory.OST_Windows)
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is a {symbol.Category?.Name}; door_window_place takes doors and windows.");
        if (symbol.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBasedHosted)
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is a {symbol.Family.FamilyPlacementType} family, not a wall-hosted one.");
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        string kind = cat == BuiltInCategory.OST_Doors ? "door" : "window";
        Level defaultLevel = Has(p, "level") ? Find.NeedLevel(doc, p) : null;
        double tol = U.LenMm(p, "tolerance", 50);
        double? sillAll = Has(p, "sill_height") ? U.Len(p, "sill_height", 0) : (double?)null;
        double? headAll = Has(p, "head_height") ? U.Len(p, "head_height", 0) : (double?)null;
        if (sillAll != null && headAll != null) throw new ArgumentException("Give sill_height or head_height, not both (the type's height sets the other).");

        var rows = new List<Obj>();
        var items = ObjList(p, "items", 1000);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            string what = $"Item {i + 1}";
            Wall wall = null;
            XYZ given = Has(it, "at") ? U.NeedPt(it, "at") : null;
            if (Has(it, "wall"))
                wall = Find.Elem(doc, NeedId(it, "wall")) as Wall ?? throw new ArgumentException($"{what}: 'wall' must be a wall id.");
            else if (given != null) wall = NearestWall(doc, given, Has(it, "level") ? Find.NeedLevel(doc, it) : defaultLevel, tol, what);
            else throw new ArgumentException($"{what}: give 'wall' with 'at' or 'distance', or 'at' alone (with 'level').");
            c.Editable(wall);
            if (!(wall.Location is LocationCurve lc) || lc.Curve == null) throw new ArgumentException($"{what}: wall {wall.Id.Value} has no location line.");
            if (wall.WallType.Kind == WallKind.Curtain) throw new ArgumentException($"{what}: wall {wall.Id.Value} is a curtain wall; doors go in as curtain panels there.");
            var curve = lc.Curve;
            double len = curve.Length;
            double along;
            if (Has(it, "distance"))
            {
                if (given != null) throw new ArgumentException($"{what}: give 'at' or 'distance', not both.");
                along = U.Len(it, "distance", 0);
            }
            else
            {
                if (given == null) throw new ArgumentException($"{what}: give 'at' or 'distance'.");
                var flat = new XYZ(given.X, given.Y, curve.GetEndPoint(0).Z);
                var proj = curve.Project(flat) ?? throw new ArgumentException($"{what}: the point does not project onto wall {wall.Id.Value}.");
                double off = proj.Distance;
                if (off > wall.Width / 2 + tol)
                    throw new ArgumentException($"{what}: the point is {U.Out(off)} from the line of wall {wall.Id.Value} (more than half its width plus the tolerance).");
                along = curve.ComputeNormalizedParameter(proj.Parameter) * len;
            }
            if (along <= 0 || along >= len)
                throw new ArgumentException($"{what}: {U.Out(along)} along wall {wall.Id.Value} is outside the wall (its length is {U.Out(len)}).");
            var at = curve.Evaluate(along / len, true);
            var level = doc.GetElement(wall.LevelId) as Level ?? throw new ArgumentException($"{what}: wall {wall.Id.Value} has no base level.");
            var fi = doc.Create.NewFamilyInstance(at, symbol, wall, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            doc.Regenerate();
            bool itS = Has(it, "sill_height"), itH = Has(it, "head_height");
            if (itS && itH) throw new ArgumentException($"{what}: give sill_height or head_height, not both.");
            double? sill = itS || itH ? (itS ? U.Len(it, "sill_height", 0) : (double?)null) : sillAll;
            double? head = itS || itH ? (itH ? U.Len(it, "head_height", 0) : (double?)null) : headAll;
            if (sill != null) SetLen(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, sill.Value, what, "sill_height");
            if (head != null) SetLen(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM, head.Value, what, "head_height");
            if (Flag(it, "flip_facing", false)) { if (!fi.CanFlipFacing) throw new ArgumentException($"{what}: this {kind} cannot flip its facing."); fi.flipFacing(); }
            if (Flag(it, "flip_hand", false)) { if (!fi.CanFlipHand) throw new ArgumentException($"{what}: this {kind} cannot flip its hand."); fi.flipHand(); }
            if (Has(it, "mark")) fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(NeedStr(it, "mark"));
            doc.Regenerate();
            var row = new Obj
            {
                ["id"] = fi.Id.Value, ["wall"] = wall.Id.Value, ["level"] = level.Name, ["at"] = U.Xy(at), ["distance"] = U.Out(along),
                ["sillHeight"] = LenOut(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM), ["headHeight"] = LenOut(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM),
                ["mark"] = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
            };
            if (kind == "door") { row["fromRoom"] = RoomLabel(fi.FromRoom); row["toRoom"] = RoomLabel(fi.ToRoom); }
            else row["room"] = RoomLabel(fi.ToRoom ?? fi.FromRoom);
            rows.Add(row);
        }
        return new
        {
            kind, type = Find.TypeLabel(symbol), placed = rows.Count, items = rows,
            note = "Positions, heights and marks as given; sizes come from the type. Revit reports overlapping inserts as warnings.",
        };
    }

    private static Wall NearestWall(Document doc, XYZ at, Level level, double tol, string what)
    {
        if (level == null) throw new ArgumentException($"{what}: without 'wall', give 'level' (on the item or the operation) to find the wall at the point.");
        Wall best = null; double bestD = double.MaxValue;
        foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.LevelId == level.Id && w.WallType.Kind != WallKind.Curtain))
        {
            if (!(w.Location is LocationCurve lc) || lc.Curve == null) continue;
            var proj = lc.Curve.Project(new XYZ(at.X, at.Y, lc.Curve.GetEndPoint(0).Z));
            if (proj == null) continue;
            double d = proj.Distance;
            if (d <= w.Width / 2 + tol && d < bestD) { bestD = d; best = w; }
        }
        return best ?? throw new ArgumentException($"{what}: no wall on level '{level.Name}' passes through the point (within half its width plus the tolerance). Give 'wall'.");
    }

    private static void SetLen(FamilyInstance fi, BuiltInParameter b, double feet, string what, string name)
    {
        var pr = fi.get_Parameter(b) ?? throw new ArgumentException($"{what}: this family has no {name}.");
        if (pr.IsReadOnly) throw new ArgumentException($"{what}: {name} is read-only for this family.");
        pr.Set(feet);
    }

    private static double? LenOut(Element e, BuiltInParameter b) { var pr = e.get_Parameter(b); return pr == null || !pr.HasValue ? (double?)null : U.Out(pr.AsDouble()); }

    private static string RoomLabel(Room r) => r == null ? null : $"{r.Number} {Find.Builtin(r, "ROOM_NAME")?.AsString()}".Trim();

    // ---------------------------------------------------------------- layered types

    private static readonly Dictionary<string, MaterialFunctionAssignment> Functions = new Dictionary<string, MaterialFunctionAssignment>(StringComparer.OrdinalIgnoreCase)
    {
        ["structure"] = MaterialFunctionAssignment.Structure, ["substrate"] = MaterialFunctionAssignment.Substrate,
        ["insulation"] = MaterialFunctionAssignment.Insulation, ["thermal"] = MaterialFunctionAssignment.Insulation,
        ["finish1"] = MaterialFunctionAssignment.Finish1, ["finish2"] = MaterialFunctionAssignment.Finish2,
        ["membrane"] = MaterialFunctionAssignment.Membrane,
    };

    /// <summary>
    /// A wall, floor, roof or ceiling type with the layers given (exterior to interior for walls, top to bottom otherwise):
    /// function, material and thickness each; the core is the run of layers given by 'core' (first and last index), by
    /// default from the first to the last structure layer. Made by duplicating a base type of that kind; with if_exists
    /// update an existing type of that name gets the new layers.
    /// </summary>
    private static object LayeredTypeCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string kind = Choice(p, "kind", null, "wall", "floor", "roof", "ceiling");
        string name = NeedStr(p, "name");
        Type cls = kind == "wall" ? typeof(WallType) : kind == "floor" ? typeof(FloorType) : kind == "roof" ? typeof(RoofType) : typeof(CeilingType);
        string ifExists = Choice(p, "if_exists", "fail", "fail", "update");
        var candidates = new FilteredElementCollector(doc).OfClass(cls).Cast<HostObjAttributes>()
            .Where(t => t.GetCompoundStructure() != null && !(t is WallType wt && wt.Kind != WallKind.Basic)).ToList();
        var existing = candidates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        HostObjAttributes target;
        bool updated = false;
        if (existing != null)
        {
            if (ifExists != "update") throw new ArgumentException($"A {kind} type '{name}' exists. Use if_exists: update to give it these layers, or another name.");
            target = c.Editable(existing) as HostObjAttributes; updated = true;
        }
        else
        {
            HostObjAttributes baseType;
            if (Has(p, "base_type"))
            {
                baseType = Find.TypeOf(doc, NeedStr(p, "base_type"), null, cls) as HostObjAttributes
                           ?? throw new ArgumentException($"'base_type' must be a {kind} type.");
                if (baseType.GetCompoundStructure() == null || (baseType is WallType bw && bw.Kind != WallKind.Basic))
                    throw new ArgumentException($"'{baseType.Name}' has no layers (curtain / stacked walls, non-compound ceilings); give a layered {kind} type.");
            }
            else baseType = candidates.FirstOrDefault() ?? throw new ArgumentException($"This model has no layered {kind} type to start from.");
            target = (HostObjAttributes)baseType.Duplicate(name);
        }

        var given = ObjList(p, "layers", 30);
        var layers = new List<CompoundStructureLayer>();
        var rows = new List<Obj>();
        for (int i = 0; i < given.Count; i++)
        {
            var l = given[i];
            string what = $"Layer {i + 1}";
            string fn = NeedStr(l, "function");
            if (!Functions.TryGetValue(fn, out var f)) throw new ArgumentException($"{what}: function '{fn}' is not one of {string.Join(", ", Functions.Keys)}.");
            double t = f == MaterialFunctionAssignment.Membrane ? 0 : U.Len(l, "thickness", 0);
            if (f == MaterialFunctionAssignment.Membrane && Has(l, "thickness") && U.Len(l, "thickness", 0) > 1e-9)
                throw new ArgumentException($"{what}: a membrane layer has no thickness.");
            if (f != MaterialFunctionAssignment.Membrane && t <= 1e-9) throw new ArgumentException($"{what}: give a thickness above zero.");
            var mat = Has(l, "material") ? Materials.Named(doc, NeedStr(l, "material"), $"{what} material") : null;
            layers.Add(new CompoundStructureLayer(t, f, mat?.Id ?? ElementId.InvalidElementId));
            rows.Add(new Obj { ["function"] = f.ToString(), ["material"] = mat?.Name ?? "<By Category>", ["thickness"] = U.Out(t) });
        }
        if (layers.Count == 0) throw new ArgumentException("Give at least one layer.");
        int first, last;
        if (Has(p, "core"))
        {
            var core = NeedArr(p, "core", 2);
            if (core.Count != 2) throw new ArgumentException("'core' is [first, last] layer index (1-based).");
            first = IntegerValue(Number(core[0], "core first"), "core first") - 1; last = IntegerValue(Number(core[1], "core last"), "core last") - 1;
        }
        else
        {
            first = layers.FindIndex(x => x.Function == MaterialFunctionAssignment.Structure);
            last = layers.FindLastIndex(x => x.Function == MaterialFunctionAssignment.Structure);
            if (first < 0) throw new ArgumentException("No structure layer: give 'core' as [first, last] layer index.");
        }
        if (first < 0 || last >= layers.Count || first > last) throw new ArgumentException($"'core' must be within 1..{layers.Count}, first <= last.");
        var cs = CompoundStructure.CreateSimpleCompoundStructure(layers);
        var was = target.GetCompoundStructure();
        if (was != null)
        {
            // keep the kind's own settings (floors / roofs refuse the wall end caps a simple structure starts with)
            cs.EndCap = was.EndCap;
            if (kind == "wall") { cs.OpeningWrapping = was.OpeningWrapping; }
        }
        cs.SetNumberOfShellLayers(ShellLayerType.Exterior, first);
        cs.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - last);
        if (!cs.IsValid(doc, out IDictionary<int, CompoundStructureError> errs, out IDictionary<int, int> pairs))
            throw new ArgumentException("Revit refuses these layers: " + string.Join("; ", errs.Select(kv => $"layer {kv.Key + 1}: {kv.Value}"))
                                        + (pairs.Count > 0 ? "; " + string.Join("; ", pairs.Select(kv => $"layers {kv.Key + 1} / {kv.Value + 1}")) : "") + ".");
        target.SetCompoundStructure(cs);
        for (int i = 0; i < rows.Count; i++) rows[i]["core"] = i >= first && i <= last;
        return new
        {
            kind, type = target.Name, id = target.Id.Value, created = !updated, updated, totalThickness = U.Out(cs.GetWidth()),
            order = kind == "wall" ? "exterior to interior" : "top to bottom", layers = rows,
        };
    }
}
