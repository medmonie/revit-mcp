namespace RevitMcpBridge;

/// <summary>Modelling beyond the basic elements: slab shape points (sloped and warped slabs), beam systems, braces.</summary>
internal static class ModelOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["slab_shape"] = SlabShape;
        ops["beam_system_create"] = BeamSystemCreate;
        ops["brace_create"] = BraceCreate;
    }

    /// <summary>
    /// Raise or lower points of a slab: an existing corner within 'tolerance' of a given point is moved, otherwise a new
    /// point is added there. Offsets are from the slab's own top (positive up). 'reset' returns the slab to flat.
    /// </summary>
    private static object SlabShape(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var floor = c.Editable(Find.Elem(doc, NeedId(p, "id"))) as Floor ?? throw new ArgumentException("'id' is not a floor / slab.");
        var editor = floor.GetSlabShapeEditor() ?? throw new InvalidOperationException($"Slab {floor.Id.Value} cannot be shape-edited.");
        if (Flag(p, "reset", false)) { editor.ResetSlabShape(); return new { slab = floor.Id.Value, reset = true }; }
        editor.Enable();
        doc.Regenerate();   // the corner vertices are listed only after shape editing is switched on and regenerated
        if (editor.SlabShapeVertices.Size == 0)
            throw new InvalidOperationException($"Revit offers no shape editing for slab {floor.Id.Value} (it lists no corners after shape editing is " +
                "switched on; slabs hosting reinforcement or joined in some ways behave like this). Edit it in Revit, or use a separate slab.");
        double tol = U.LenMm(p, "tolerance", 50);
        var moved = new List<object>();
        foreach (var item in ObjList(p, "points", 500))
        {
            var at = U.NeedPt(item, "at");
            double offset = U.In(NeedDbl(item, "offset"));
            SlabShapeVertex vertex = null;
            foreach (SlabShapeVertex v in editor.SlabShapeVertices)
                if (new XYZ(v.Position.X - at.X, v.Position.Y - at.Y, 0).GetLength() <= tol) { vertex = v; break; }
            bool added = vertex == null;
            if (added)
            {
                var top = floor.get_BoundingBox(null)?.Max.Z ?? 0;
                try { vertex = editor.AddPoints(new List<XYZ> { new XYZ(at.X, at.Y, top) }).FirstOrDefault(); }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { vertex = null; }
                if (vertex == null)
                {
                    var corners = editor.SlabShapeVertices.Cast<SlabShapeVertex>().Take(12).Select(v => $"[{U.Out(v.Position.X)}, {U.Out(v.Position.Y)}]");
                    throw new InvalidOperationException($"Revit would not add a shape point at [{U.Out(at.X)}, {U.Out(at.Y)}]: it must be inside the slab, " +
                        "not on an edge or an opening (Revit also refuses some slabs that host reinforcement). Corners within 'tolerance' are moved instead; " +
                        $"this slab's corners: {string.Join(" ", corners)}.");
                }
            }
            editor.ModifySubElement(vertex, offset);
            moved.Add(new { at = U.Xy(at), offset = U.Out(offset), added });
        }
        return new { slab = floor.Id.Value, points = moved.Count, items = moved };
    }

    private static object BeamSystemCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), BuiltInCategory.OST_StructuralFraming);
        double z = level.ProjectElevation;
        var loop = EditContext.Loop(p["boundary"] ?? throw new ArgumentException("Give 'boundary'."), "boundary", z).ToList();
        var d = Has(p, "direction") ? U.NeedPt(p, "direction") : XYZ.BasisX;
        d = new XYZ(d.X, d.Y, 0);
        if (d.GetLength() < 1e-9) throw new ArgumentException("'direction' must be a non-zero [x, y] vector.");
        var system = BeamSystem.Create(doc, loop, level, d.Normalize(), false);
        system.BeamType = symbol;
        double spacing = U.In(NeedDbl(p, "spacing"));                       // joist spacing is design input: no default
        if (spacing <= 0) throw new ArgumentException("'spacing' must be positive.");
        string just = Choice(p, "justification", "center", "center", "beginning", "end", "directionline");
        var justify = just == "beginning" ? BeamSystemJustifyType.Beginning
                    : just == "end" ? BeamSystemJustifyType.End
                    : just == "directionline" ? BeamSystemJustifyType.DirectionLine : BeamSystemJustifyType.Center;
        system.LayoutRule = new LayoutRuleFixedDistance(spacing, justify);
        doc.Regenerate();
        return new { id = system.Id.Value, beams = system.GetBeamIds().Count, type = Find.TypeLabel(symbol), spacing = U.Out(spacing), justification = just };
    }

    private static object BraceCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var level = Find.NeedLevel(doc, p);
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), BuiltInCategory.OST_StructuralFraming);
        double z = level.ProjectElevation;
        var ids = new List<long>();
        var braces = ObjList(p, "braces", 1000);
        for (int i = 0; i < braces.Count; i++)
        {
            // points without z sit on the level; give z (absolute) for the upper end of a brace
            var line = U.Segment(U.NeedPt(braces[i], "start", z), U.NeedPt(braces[i], "end", z), $"brace {i + 1}");
            ids.Add(doc.Create.NewFamilyInstance(line, symbol, level, RStruct.StructuralType.Brace).Id.Value);
        }
        return new { created = ids.Count, type = Find.TypeLabel(symbol), ids };
    }
}
