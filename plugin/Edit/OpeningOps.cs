namespace RevitMcpBridge;

/// <summary>Openings in slabs, roofs, walls and beams, and shafts through several levels.</summary>
internal static class OpeningOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["opening_create"] = OpeningCreate;
        ops["shaft_create"] = ShaftCreate;
    }

    /// <summary>A closed profile from 'boundary' ([x,y] corners) or 'center' + 'radius' (a circle), at height zFeet.</summary>
    private static CurveArray Profile(Obj p, double zFeet)
    {
        var arr = new CurveArray();
        if (Has(p, "radius"))
        {
            var c = U.NeedPt(p, "center");
            double r = U.In(NeedDbl(p, "radius"));
            if (r <= 0.003) throw new ArgumentException("'radius' must be positive.");
            var center = new XYZ(c.X, c.Y, zFeet);
            arr.Append(Arc.Create(center, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            arr.Append(Arc.Create(center, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            return arr;
        }
        if (!Has(p, "boundary")) throw new ArgumentException("Give 'boundary' (list of [x, y] corners) or 'center' and 'radius'.");
        foreach (Curve curve in EditContext.Loop(p["boundary"], "boundary", zFeet)) arr.Append(curve);
        return arr;
    }

    private static object OpeningCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var host = Find.Elem(doc, NeedId(p, "host"));
        Opening opening;
        if (host is Wall wall)
        {
            // Rectangular wall opening between two corners in the wall's plane: [x, y, z] of the lower-left and upper-right corner,
            // or along the wall: 'start' and 'end' plan points with 'bottom' and 'top' heights.
            XYZ a, b;
            if (Has(p, "bottom") || Has(p, "top"))
            {
                var s = U.NeedPt(p, "start"); var e = U.NeedPt(p, "end");
                double lo = U.In(NeedDbl(p, "bottom")), hi = U.In(NeedDbl(p, "top"));
                if (hi <= lo) throw new ArgumentException("'top' must be above 'bottom'.");
                a = new XYZ(s.X, s.Y, lo); b = new XYZ(e.X, e.Y, hi);
            }
            else { a = U.NeedPt(p, "start"); b = U.NeedPt(p, "end"); }
            opening = doc.Create.NewOpening(wall, a, b);
        }
        else if (host is FamilyInstance fi && fi.StructuralType != RStruct.StructuralType.NonStructural
                 && fi.StructuralType != RStruct.StructuralType.Footing)
        {
            opening = MemberOpening(doc, fi, p);
        }
        else
        {
            // Slabs, roofs, ceilings: a profile cut perpendicular to the face (vertical for a slab).
            double z = host.get_BoundingBox(null)?.Max.Z ?? 0;
            opening = doc.Create.NewOpening(host, Profile(p, z), Flag(p, "perpendicular", true));
        }
        return new { id = opening.Id.Value, host = host.Id.Value, hostCategory = host.Category?.Name };
    }

    /// <summary>
    /// Opening in a beam, brace or column. face 'web' (default): a sleeve through the side, profile in the member's
    /// vertical centre plane: 'center' [x, y, z?] (z defaults to mid depth) with 'radius', or 'width' (along the member)
    /// and 'height'. face 'top': a vertical hole through the top, profile from 'boundary' or 'center' + 'radius' in plan.
    /// </summary>
    private static Opening MemberOpening(Document doc, FamilyInstance fi, Obj p)
    {
        string face = (Str(p, "face") ?? "web").ToLowerInvariant();
        var box = fi.get_BoundingBox(null) ?? throw new ArgumentException("The member has no geometry.");
        if (face == "top" || face == "flange")
            return doc.Create.NewOpening(fi, Profile(p, box.Max.Z), Autodesk.Revit.Creation.eRefFace.CenterZ);
        if (face != "web") throw new ArgumentException("'face' must be 'web' or 'top'.");

        if (!(fi.Location is LocationCurve lc)) throw new ArgumentException("A web opening needs a member with a location line (beam or brace).");
        var line = lc.Curve;
        var raw = p.TryGetValue("center", out var cv) ? cv as List<object> : null;
        if (raw == null || raw.Count < 2) throw new ArgumentException("Give 'center' [x, y] or [x, y, z] of the web opening.");
        var c2 = U.NeedPt(p, "center");
        var onAxis = line.Project(new XYZ(c2.X, c2.Y, line.Evaluate(0.5, true).Z)).XYZPoint;
        double z = raw.Count >= 3 ? c2.Z : (box.Min.Z + box.Max.Z) / 2;
        var center = new XYZ(onAxis.X, onAxis.Y, z);
        var along = line.ComputeDerivatives(line.Project(onAxis).Parameter, false).BasisX;
        along = new XYZ(along.X, along.Y, 0);
        if (along.GetLength() < 1e-9) throw new ArgumentException("Web openings are for beams and braces, not vertical members; use face 'top'.");
        along = along.Normalize();

        var arr = new CurveArray();
        if (Has(p, "radius"))
        {
            double r = U.In(NeedDbl(p, "radius"));
            if (r <= 0.003) throw new ArgumentException("'radius' must be positive.");
            arr.Append(Arc.Create(center, r, 0, Math.PI, along, XYZ.BasisZ));
            arr.Append(Arc.Create(center, r, Math.PI, 2 * Math.PI, along, XYZ.BasisZ));
        }
        else
        {
            double w = U.In(NeedDbl(p, "width")) / 2, h = U.In(NeedDbl(p, "height")) / 2;
            if (w <= 0.003 || h <= 0.003) throw new ArgumentException("'width' and 'height' must be positive.");
            var up = XYZ.BasisZ;
            var pts = new[] { center - along * w - up * h, center + along * w - up * h, center + along * w + up * h, center - along * w + up * h };
            for (int i = 0; i < 4; i++) arr.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
        }
        return doc.Create.NewOpening(fi, arr, Autodesk.Revit.Creation.eRefFace.CenterY);
    }

    private static object ShaftCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var bottom = Find.NeedLevel(doc, p, "bottom_level");
        var top = Find.NeedLevel(doc, p, "top_level");
        if (top.ProjectElevation <= bottom.ProjectElevation) throw new ArgumentException("'top_level' must be above 'bottom_level'.");
        var shaft = doc.Create.NewOpening(bottom, top, Profile(p, bottom.ProjectElevation));
        if (Dbl(p, "base_offset") is double bo) c.SetBuiltin(shaft, "WALL_BASE_OFFSET", U.In(bo));
        if (Dbl(p, "top_offset") is double to) c.SetBuiltin(shaft, "WALL_TOP_OFFSET", U.In(to));
        return new { id = shaft.Id.Value, bottomLevel = bottom.Name, topLevel = top.Name };
    }
}
