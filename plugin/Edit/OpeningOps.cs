namespace RevitMcpBridge;

/// <summary>Openings in slabs, roofs, walls and beams, and shafts through several levels.</summary>
internal static class OpeningOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["opening_create"] = OpeningCreate;
        ops["shaft_create"] = ShaftCreate;
        ops["crossing_opening"] = CrossingOpening;
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

    // ---------------------------------------------------------------- openings where an MEP run crosses structure (0.22.0)

    /// <summary>The run's cross-section: round (radius) or rectangular (half sizes along two axes), in host coordinates.</summary>
    private sealed class Section
    {
        public bool Round; public double R, HalfW, HalfH; public XYZ Wx, Wy; public string Text;
        /// <summary>Half extent of the section, with the clearance, along a direction square to the run.</summary>
        public double Half(XYZ axis, double clear) => Round ? R + clear
            : Math.Abs(Wx.DotProduct(axis)) * HalfW + Math.Abs(Wy.DotProduct(axis)) * HalfH + clear;
    }

    private static Section SectionOf(Element m, Transform tf, XYZ dir)
    {
        var s = new Section { Text = Find.Builtin(m, "RBS_CALCULATED_SIZE")?.AsString() };
        if (!(m is MEPCurve mc)) throw new ArgumentException($"Element {m.Id.Value} is not a duct, pipe, cable tray or conduit.");
        double d = 0;
        try { d = mc.Diameter; } catch { /* not round */ }
        if (d > 1e-6) { s.Round = true; s.R = d / 2; return s; }
        double w = mc.Width, h = mc.Height;
        XYZ bx = null;
        foreach (Connector con in mc.ConnectorManager.Connectors)
        {
            try { bx = tf.OfVector(con.CoordinateSystem.BasisX); break; } catch { /* no coordinate system */ }
        }
        if (bx == null || Math.Abs(bx.DotProduct(dir)) > 0.1)
        {
            // Fall back: width horizontal, square to the run (or along X for a vertical run).
            bx = Math.Abs(dir.Z) > 0.9 ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(dir).Normalize();
        }
        bx = (bx - dir * bx.DotProduct(dir)).Normalize();
        s.Wx = bx; s.Wy = dir.CrossProduct(bx).Normalize(); s.HalfW = w / 2; s.HalfH = h / 2;
        return s;
    }

    /// <summary>
    /// An opening in a host slab, roof, wall or beam where a duct, pipe, tray or conduit (host or link) passes through it,
    /// sized as the run's cross-section plus the clearance the engineer gives on each side. Openings are square to the
    /// face; the run must be close to square to it (within 15 degrees).
    /// </summary>
    private static object CrossingOpening(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var host = c.Editable(Find.Elem(doc, NeedId(p, "structure")));
        double clear = U.In(NeedDbl(p, "clearance"));
        if (clear < 0) throw new ArgumentException("'clearance' must be zero or more.");
        string shape = Choice(p, "shape", "auto", "auto", "round", "rectangular");

        Document mdoc = doc; Transform mtf = Transform.Identity;
        if (Has(p, "mep_link"))
        {
            var inst = LinkScope.Resolve(doc, NeedRef(p, "mep_link"));
            mdoc = inst.GetLinkDocument() ?? throw new ArgumentException($"Link '{inst.Name}' is not loaded.");
            mtf = inst.GetTotalTransform();
        }
        var m = mdoc.GetElement(NeedId(p, "mep")) ?? throw new ArgumentException($"No element {NeedRef(p, "mep")} in {mdoc.Title}.");
        if (!(m.Location is LocationCurve lc) || !(lc.Curve is Line run)) throw new ArgumentException($"Element {m.Id.Value} is not a straight run.");
        XYZ a = mtf.OfPoint(run.GetEndPoint(0)), b = mtf.OfPoint(run.GetEndPoint(1));
        var dir = (b - a).Normalize();

        // Where the centreline is inside the host (its solids already include any opening cut in it).
        var segs = new List<(XYZ p0, XYZ p1)>();
        var opt = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
        foreach (var solid in Geo.Solids(host))
        {
            SolidCurveIntersection hit;
            try { hit = solid.IntersectWithCurve(Line.CreateBound(a, b), opt); } catch { continue; }
            for (int i = 0; i < hit.SegmentCount; i++)
            {
                var seg = hit.GetCurveSegment(i);
                if (seg.Length > 0.003) segs.Add((seg.GetEndPoint(0), seg.GetEndPoint(1)));
            }
        }
        if (segs.Count == 0)
            throw new ArgumentException($"Run {m.Id.Value} does not pass through element {host.Id.Value} (or an opening there already clears its centreline).");
        var near = Has(p, "at") ? U.NeedPt(p, "at") : null;
        var pick = near == null ? segs[0] : segs.OrderBy(sg => ((sg.p0 + sg.p1) / 2).DistanceTo(near)).First();
        var center = (pick.p0 + pick.p1) / 2;

        var sec = SectionOf(m, mtf, dir);
        bool round = shape == "auto" ? sec.Round : shape == "round";
        if (round && !sec.Round)
        {   // a round opening around a rectangular run: the circle through its corners
            sec = new Section { Round = true, R = Math.Sqrt(sec.HalfW * sec.HalfW + sec.HalfH * sec.HalfH), Text = sec.Text };
        }
        const double square = 0.966;   // cos 15 degrees
        Opening opening;
        string note = null;
        var o = new Obj();

        if (host is Wall wall)
        {
            if (!(wall.Location is LocationCurve wl) || !(wl.Curve is Line wline)) throw new ArgumentException("Openings in curved walls are not supported; use opening_create.");
            var along = wline.Direction.Normalize();
            var normal = XYZ.BasisZ.CrossProduct(along).Normalize();
            if (Math.Abs(dir.DotProduct(normal)) < square) throw new ArgumentException("The run is not square to the wall (more than 15 degrees off); use opening_create with the size you want.");
            double hw = sec.Half(along, clear), hh = sec.Half(XYZ.BasisZ, clear);
            opening = doc.Create.NewOpening(wall, center - along * hw - XYZ.BasisZ * hh, center + along * hw + XYZ.BasisZ * hh);
            if (round) note = "Wall openings are rectangular: the opening is the square around the round size.";
            o["width"] = U.Out(2 * hw); o["height"] = U.Out(2 * hh);
        }
        else if (host is FamilyInstance member && (member.StructuralType == RStruct.StructuralType.Beam || member.StructuralType == RStruct.StructuralType.Brace))
        {
            if (!(member.Location is LocationCurve ml)) throw new ArgumentException("The member has no location line.");
            var axis = ml.Curve.ComputeDerivatives(ml.Curve.Project(center).Parameter, false).BasisX;
            axis = new XYZ(axis.X, axis.Y, 0).Normalize();
            var side = XYZ.BasisZ.CrossProduct(axis).Normalize();
            if (Math.Abs(dir.DotProduct(side)) < square) throw new ArgumentException("The run is not square to the beam web (more than 15 degrees off); use opening_create.");
            var q = new Obj { ["face"] = "web", ["center"] = new List<object> { U.Out(center.X), U.Out(center.Y), U.Out(center.Z) } };
            if (round) { q["radius"] = U.Out(sec.R + clear); o["diameter"] = U.Out(2 * (sec.R + clear)); }
            else { double hw = sec.Half(axis, clear), hh = sec.Half(XYZ.BasisZ, clear); q["width"] = U.Out(2 * hw); q["height"] = U.Out(2 * hh); o["width"] = q["width"]; o["height"] = q["height"]; }
            opening = MemberOpening(doc, member, q);
        }
        else if (host is Floor || host is RoofBase || host is Ceiling)
        {
            if (Math.Abs(dir.Z) < square) throw new ArgumentException("The run is not vertical where it crosses (more than 15 degrees off); use opening_create.");
            double z = host.get_BoundingBox(null)?.Max.Z ?? center.Z;
            var c0 = new XYZ(center.X, center.Y, z);
            var arr = new CurveArray();
            if (round)
            {
                double r = sec.R + clear;
                arr.Append(Arc.Create(c0, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                arr.Append(Arc.Create(c0, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                o["diameter"] = U.Out(2 * r);
            }
            else
            {
                XYZ ux = new XYZ(sec.Wx.X, sec.Wx.Y, 0).Normalize(), uy = XYZ.BasisZ.CrossProduct(ux);
                double hx = sec.HalfW + clear, hy = sec.HalfH + clear;
                var pts = new[] { c0 - ux * hx - uy * hy, c0 + ux * hx - uy * hy, c0 + ux * hx + uy * hy, c0 - ux * hx + uy * hy };
                for (int i = 0; i < 4; i++) arr.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                o["width"] = U.Out(2 * hx); o["height"] = U.Out(2 * hy);
            }
            opening = doc.Create.NewOpening(host, arr, true);
        }
        else throw new ArgumentException($"Openings from crossings are made in walls, slabs, roofs, ceilings, beams and braces, not in {host.Category?.Name}.");

        o["id"] = opening.Id.Value; o["structure"] = host.Id.Value; o["structureCategory"] = host.Category?.Name;
        o["mep"] = m.Id.Value; o["mepModel"] = mdoc.Title; o["mepSize"] = sec.Text; o["shape"] = round && !(host is Wall) ? "round" : "rectangular";
        o["clearance"] = U.Out(clear); o["at"] = U.Xyz(center);
        if (segs.Count > 1) o["crossingsInThisElement"] = segs.Count;
        if (note != null) o["note"] = note;
        return o;
    }
}
