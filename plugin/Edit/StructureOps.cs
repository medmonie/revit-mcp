namespace RevitMcpBridge;

/// <summary>Structural (and plain) model elements: columns, beams, walls, floors / slabs, foundations, any level-based family.</summary>
internal static class StructureOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["column_create"] = ColumnCreate;
        ops["beam_create"] = BeamCreate;
        ops["wall_create"] = WallCreate;
        ops["floor_create"] = FloorCreate;
        ops["footing_create"] = FootingCreate;
        ops["wall_foundation_create"] = WallFoundationCreate;
        ops["family_place"] = FamilyPlace;
    }

    private static void Rotate(Document doc, Element e, XYZ at, double radians)
    {
        if (Math.Abs(radians) < 1e-12) return;
        ElementTransformUtils.RotateElement(doc, e.Id, Line.CreateBound(at, at + XYZ.BasisZ), radians);
    }

    /// <summary>One column at a plan point: from 'level' (+ baseOff) to 'top' (+ topOff), or by heightFeet; rotated about its point.</summary>
    internal static FamilyInstance PlaceColumn(EditContext c, FamilySymbol symbol, Level level, Level top, double? heightFeet,
                                               double baseOff, double topOff, XYZ pt, double rotation, bool structural = true)
    {
        var doc = c.Doc;
        if (!symbol.IsActive) symbol.Activate();
        var at = new XYZ(pt.X, pt.Y, level.ProjectElevation);
        var col = doc.Create.NewFamilyInstance(at, symbol, level,
            structural ? RStruct.StructuralType.Column : RStruct.StructuralType.NonStructural);
        if (top != null)
        {
            c.SetBuiltin(col, "FAMILY_TOP_LEVEL_PARAM", top.Id);
            c.SetBuiltin(col, "FAMILY_TOP_LEVEL_OFFSET_PARAM", topOff);
        }
        else
        {
            // Height from the base level: top level = base level, top offset = base offset + height.
            c.SetBuiltin(col, "FAMILY_TOP_LEVEL_OFFSET_PARAM", baseOff + heightFeet.Value);
            c.SetBuiltin(col, "FAMILY_TOP_LEVEL_PARAM", level.Id);
        }
        if (Math.Abs(baseOff) > 1e-12) c.SetBuiltin(col, "FAMILY_BASE_LEVEL_OFFSET_PARAM", baseOff);
        Rotate(doc, col, at, rotation);
        return col;
    }

    private static object ColumnCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        bool structural = Flag(p, "structural", true);
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), structural ? BuiltInCategory.OST_StructuralColumns : BuiltInCategory.OST_Columns);
        var level = Find.NeedLevel(doc, p);
        var top = Has(p, "top_level") ? Find.LevelOf(doc, Ref(p, "top_level")) : null;
        double? height = Dbl(p, "height");
        if (top == null && height == null) throw new ArgumentException("column_create needs 'top_level' or 'height'.");
        if (height != null && height <= 0) throw new ArgumentException("'height' must be positive.");
        double baseOff = U.Len(p, "base_offset", 0), topOff = U.Len(p, "top_offset", 0);
        double rotation = U.Rad(Dbl(p, "rotation") ?? 0);
        if (top != null && top.ProjectElevation + topOff <= level.ProjectElevation + baseOff)
            throw new ArgumentException("The column top is not above its base (check top_level and the offsets).");

        var ids = new List<long>();
        foreach (var pt in U.PtList(p, "points", 1, 2000))
            ids.Add(PlaceColumn(c, symbol, level, top, height == null ? (double?)null : U.In(height.Value), baseOff, topOff, pt, rotation, structural).Id.Value);
        return new { created = ids.Count, type = Find.TypeLabel(symbol), ids };
    }

    /// <summary>
    /// One beam along a curve. Families differ in their default justification (some hang the beam above the level or to
    /// one side of its line), so it is set explicitly unless null. Revit's codes: z Top 0, Center 1, Origin 2, Bottom 3;
    /// y Left 0, Center 1, Origin 2, Right 3.
    /// </summary>
    internal static FamilyInstance PlaceBeam(EditContext c, Curve curve, FamilySymbol symbol, Level level, int? zCode = 0, int? yCode = 1)
    {
        if (!symbol.IsActive) { symbol.Activate(); c.Doc.Regenerate(); }
        var beam = c.Doc.Create.NewFamilyInstance(curve, symbol, level, RStruct.StructuralType.Beam);
        if (zCode is int zc) { c.SetBuiltinInt(beam, "Z_JUSTIFICATION", zc); c.SetBuiltin(beam, "Z_OFFSET_VALUE", 0.0); }
        if (yCode is int yc) { c.SetBuiltinInt(beam, "Y_JUSTIFICATION", yc); c.SetBuiltin(beam, "Y_OFFSET_VALUE", 0.0); }
        return beam;
    }

    /// <summary>One wall along a curve, from the level (+ baseOff) up to 'top' (+ topOff) or by heightFeet.</summary>
    internal static Wall PlaceWall(EditContext c, Curve curve, WallType type, Level level, Level top, double heightFeet,
                                   double baseOff, double topOff, bool structural, bool flip = false)
    {
        var wall = Wall.Create(c.Doc, curve, type.Id, level.Id, heightFeet, baseOff, flip, structural);
        if (top != null)
        {
            c.SetBuiltin(wall, "WALL_HEIGHT_TYPE", top.Id);
            c.SetBuiltin(wall, "WALL_TOP_OFFSET", topOff);
        }
        return wall;
    }

    private static object BeamCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), BuiltInCategory.OST_StructuralFraming);
        var level = Find.NeedLevel(doc, p);
        double z = level.ProjectElevation + U.Len(p, "z_offset", 0);
        // Justification is set explicitly (see PlaceBeam); 'family' keeps the family's own.
        string zj = Choice(p, "z_justification", "top", "top", "center", "origin", "bottom", "family");
        string yj = Choice(p, "y_justification", "center", "center", "left", "origin", "right", "family");
        int zCode = zj == "top" ? 0 : zj == "center" ? 1 : zj == "origin" ? 2 : 3;
        int yCode = yj == "left" ? 0 : yj == "center" ? 1 : yj == "origin" ? 2 : 3;
        var ids = new List<long>();
        var beams = ObjList(p, "beams", 2000);
        for (int i = 0; i < beams.Count; i++)
        {
            var curve = U.Curve(beams[i], $"beam {i + 1}", z);   // straight or arc; a z per point makes it sloped
            ids.Add(PlaceBeam(c, curve, symbol, level, zj == "family" ? (int?)null : zCode, yj == "family" ? (int?)null : yCode).Id.Value);
        }
        return new { created = ids.Count, type = Find.TypeLabel(symbol), zJustification = zj, yJustification = yj, ids };
    }

    private static object WallCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var type = (WallType)Find.TypeOf(doc, NeedStr(p, "type"), BuiltInCategory.OST_Walls, typeof(WallType));
        var level = Find.NeedLevel(doc, p);
        var top = Has(p, "top_level") ? Find.LevelOf(doc, Ref(p, "top_level")) : null;
        double baseOff = U.Len(p, "base_offset", 0), topOff = U.Len(p, "top_offset", 0);
        double height = top != null
            ? top.ProjectElevation + topOff - (level.ProjectElevation + baseOff)
            : U.In(Dbl(p, "height") ?? throw new ArgumentException("wall_create needs 'top_level' or 'height'."));
        if (height <= 0) throw new ArgumentException("The wall top is not above its base (check height / top_level and the offsets).");
        bool structural = Flag(p, "structural", true), flip = Flag(p, "flip", false);

        var ids = new List<long>();
        var walls = ObjList(p, "walls", 2000);
        for (int i = 0; i < walls.Count; i++)
        {
            var curve = U.Curve(walls[i], $"wall {i + 1}", level.ProjectElevation, flatten: true);
            ids.Add(PlaceWall(c, curve, type, level, top, height, baseOff, topOff, structural, flip).Id.Value);
        }
        return new { created = ids.Count, type = type.Name, ids };
    }

    private static object FloorCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        // FloorType covers floors, structural slabs and foundation slabs (the latter are in Structural Foundations).
        var type = (FloorType)Find.TypeOf(doc, NeedStr(p, "type"), null, typeof(FloorType));
        var level = Find.NeedLevel(doc, p);
        double z = level.ProjectElevation;
        if (!Has(p, "boundary")) throw new ArgumentException("floor_create needs 'boundary': a list of [x, y] corner points.");
        var loops = new List<CurveLoop> { EditContext.Loop(p["boundary"], "boundary", z) };
        var holes = Arr(p, "holes", 200);
        for (int i = 0; i < holes.Count; i++) loops.Add(EditContext.Loop(holes[i], $"hole {i + 1}", z));

        // optional slope arrow: a line in plan from the low to the high side; slope in percent (rise / run x 100)
        Line arrow = null; double slope = 0;
        if (Has(p, "slope_arrow"))
        {
            var a = Sub(p, "slope_arrow");
            arrow = U.Segment(U.NeedPt(a, "start", z), U.NeedPt(a, "end", z), "slope_arrow");
            arrow = Line.CreateBound(new XYZ(arrow.GetEndPoint(0).X, arrow.GetEndPoint(0).Y, z), new XYZ(arrow.GetEndPoint(1).X, arrow.GetEndPoint(1).Y, z));
            slope = (Dbl(p, "slope") ?? throw new ArgumentException("slope_arrow needs 'slope' in percent (e.g. 2 for 2%).")) / 100.0;
        }
        var floor = Floor.Create(doc, loops, type.Id, level.Id, Flag(p, "structural", true), arrow, slope);
        if (Dbl(p, "offset") is double off) c.SetBuiltin(floor, "FLOOR_HEIGHTABOVELEVEL_PARAM", U.In(off));
        return new { id = floor.Id.Value, type = type.Name, category = type.Category?.Name, openings = holes.Count, slopePercent = arrow != null ? slope * 100 : (double?)null };
    }

    private static object FootingCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), BuiltInCategory.OST_StructuralFoundation);
        var level = Find.NeedLevel(doc, p);
        double rotation = U.Rad(Dbl(p, "rotation") ?? 0);
        double? offset = Dbl(p, "offset");
        var ids = new List<long>();
        foreach (var pt in U.PtList(p, "points", 1, 2000))
        {
            var at = new XYZ(pt.X, pt.Y, level.ProjectElevation);
            var f = doc.Create.NewFamilyInstance(at, symbol, level, RStruct.StructuralType.Footing);
            if (offset != null) c.SetBuiltin(f, "INSTANCE_FREE_HOST_OFFSET_PARAM", U.In(offset.Value));
            Rotate(doc, f, at, rotation);
            ids.Add(f.Id.Value);
        }
        return new { created = ids.Count, type = Find.TypeLabel(symbol), ids };
    }

    private static object WallFoundationCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var type = Find.TypeOf(doc, NeedStr(p, "type"), BuiltInCategory.OST_StructuralFoundation, typeof(WallFoundationType));
        var ids = new List<long>();
        foreach (var wallId in Ids(p, "wall_ids", 2000))
        {
            if (!(Find.Elem(doc, wallId) is Wall)) throw new ArgumentException($"Element {wallId.Value} is not a wall.");
            ids.Add(WallFoundation.Create(doc, type.Id, wallId).Id.Value);
        }
        return new { created = ids.Count, type = type.Name, ids };
    }

    /// <summary>Any level-based loadable family (generic models, equipment, furniture, ...).</summary>
    private static object FamilyPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), Find.Cat(doc, NeedStr(p, "category")));
        var level = Find.NeedLevel(doc, p);
        double rotation = U.Rad(Dbl(p, "rotation") ?? 0);
        double offset = U.Len(p, "offset", 0);
        var ids = new List<long>();
        foreach (var pt in U.PtList(p, "points", 1, 2000))
        {
            var at = new XYZ(pt.X, pt.Y, level.ProjectElevation + offset);
            var inst = doc.Create.NewFamilyInstance(at, symbol, level, RStruct.StructuralType.NonStructural);
            Rotate(doc, inst, at, rotation);
            ids.Add(inst.Id.Value);
        }
        return new { created = ids.Count, type = Find.TypeLabel(symbol), ids };
    }
}
