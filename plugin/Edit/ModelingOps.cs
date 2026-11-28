namespace RevitMcpBridge;

/// <summary>
/// Phase 15 edits: parts (create from layers, divide, merge), linear and radial arrays, reference planes and alignment
/// locks. Positions, counts, spacings and dividing lines come from the user; nothing is chosen here.
/// </summary>
internal static class ModelingOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["parts_create"] = PartsCreate;
        ops["parts_divide"] = PartsDivide;
        ops["parts_merge"] = PartsMerge;
        ops["array_create"] = ArrayCreate;
        ops["reference_plane_create"] = ReferencePlaneCreate;
        ops["align_lock"] = AlignLock;
    }

    // ---------------------------------------------------------------- parts

    private static List<long> PartsOf(Document doc, ElementId id) =>
        PartUtils.GetAssociatedParts(doc, id, false, true).Select(x => x.Value).OrderBy(x => x).ToList();

    /// <summary>Parts from the layers of walls, floors, roofs (and other elements Revit can divide into parts).</summary>
    private static object PartsCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var els = c.Elements(p);
        var ids = els.Select(e => e.Id).ToList();
        var already = ids.Where(id => PartUtils.HasAssociatedParts(doc, id)).Select(id => id.Value).ToList();
        if (already.Count > 0) throw new ArgumentException($"Already in parts: {string.Join(", ", already)}. Divide or merge those parts instead.");
        var bad = ids.Where(id => !PartUtils.AreElementsValidForCreateParts(doc, new List<ElementId> { id })).Select(id => id.Value).ToList();
        if (bad.Count > 0) throw new ArgumentException($"Revit cannot make parts of: {string.Join(", ", bad)} (walls, floors, roofs, ceilings, structural framing and the like can).");
        PartUtils.CreateParts(doc, ids);
        doc.Regenerate();
        return new { items = ids.Select(id => new { id = id.Value, parts = PartsOf(doc, id) }).ToList() };
    }

    /// <summary>The parts given, or the parts of the source elements given.</summary>
    private static List<ElementId> PartIds(Document doc, Obj p)
    {
        var result = new List<ElementId>();
        foreach (var id in Ids(p, "ids", 2000))
        {
            var e = Find.Elem(doc, id);
            if (e is Part) result.Add(id);
            else if (PartUtils.HasAssociatedParts(doc, id)) result.AddRange(PartUtils.GetAssociatedParts(doc, id, false, false));
            else throw new ArgumentException($"{id.Value} is neither a part nor an element made into parts (parts_create first).");
        }
        return result.Distinct().ToList();
    }

    /// <summary>
    /// Divide parts along lines drawn on a level (pour breaks, construction joints) and / or by datums that cross them
    /// (grids, levels, reference planes).
    /// </summary>
    private static object PartsDivide(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var parts = PartIds(doc, p);
        if (!PartUtils.ArePartsValidForDivide(doc, parts)) throw new ArgumentException("Revit cannot divide these parts (merged or excluded parts cannot be divided).");
        var refs = Has(p, "datums") ? Ids(p, "datums", 200).Select(id =>
        {
            var e = Find.Elem(doc, id);
            if (!(e is Grid || e is Level || e is ReferencePlane)) throw new ArgumentException($"{id.Value} is not a grid, level or reference plane.");
            return id;
        }).ToList() : new List<ElementId>();
        var curves = new List<Curve>();
        ElementId sketch = ElementId.InvalidElementId;
        if (Has(p, "lines"))
        {
            var level = Find.LevelOf(doc, NeedStr(p, "level"));
            double z = level.ProjectElevation;
            var lines = Arr(p, "lines", 500);
            for (int i = 0; i < lines.Count; i++)
            {
                if (!(lines[i] is List<object> seg) || seg.Count != 2) throw new ArgumentException($"lines item {i + 1} must be [[x, y], [x, y]].");
                var a = U.Pt(seg[0], $"line {i + 1} start");
                var b = U.Pt(seg[1], $"line {i + 1} end");
                curves.Add(U.Segment(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z), $"line {i + 1}"));
            }
            sketch = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z))).Id;
        }
        if (curves.Count == 0 && refs.Count == 0) throw new ArgumentException("Give 'lines' (with 'level') and / or 'datums' (grids, levels, reference planes) to divide by.");
        int before = parts.Count;
        PartUtils.DivideParts(doc, parts, refs, curves, sketch);
        doc.Regenerate();
        var sources = parts.Select(id => doc.GetElement(id) as Part).Where(x => x != null)
                           .SelectMany(x => x.GetSourceElementIds().Select(l => l.HostElementId)).Distinct().ToList();
        var now = sources.SelectMany(id => PartUtils.GetAssociatedParts(doc, id, false, true)).Distinct().Count();
        return new { dividedParts = before, lines = curves.Count, datums = refs.Count, partsNow = now,
                     note = "The divided parts stay in the model as the parents of the new ones (hidden); partsNow counts every part of the sources." };
    }

    private static object PartsMerge(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var parts = Ids(p, "ids", 500).Select(id => Find.Elem(doc, id) as Part ?? throw new ArgumentException($"{id.Value} is not a part.")).Select(x => x.Id).ToList();
        if (parts.Count < 2) throw new ArgumentException("Give at least two parts to merge.");
        if (!PartUtils.ArePartsValidForMerge(doc, parts))
            throw new ArgumentException("Revit cannot merge these parts (they must touch, share a source category and material, and not be divided further).");
        PartUtils.CreateMergedPart(doc, parts);
        doc.Regenerate();
        var merged = new FilteredElementCollector(doc).OfClass(typeof(Part)).Cast<Part>()
            .Where(x => PartUtils.IsMergedPart(x) && PartUtils.GetMergedParts(x).Any(parts.Contains)).Select(x => x.Id.Value).ToList();
        return new { merged = parts.Count, mergedPart = merged.FirstOrDefault() };
    }

    // ---------------------------------------------------------------- arrays

    /// <summary>Linear or radial array of the elements, grouped as a Revit array (default) or as plain copies.</summary>
    private static object ArrayCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        if (Dbl(p, "count") is double raw && (raw < 2 || raw > 200))
            throw new ArgumentException("'count' is the number of members including the original: 2 to 200.");
        int count = Int(p, "count", 2, 2, 200);
        var els = c.Elements(p);
        var ids = els.Select(e => e.Id).ToList();
        var notArrayable = ids.Where(id => !LinearArray.IsElementArrayable(doc, id)).Select(id => id.Value).ToList();
        if (notArrayable.Count > 0) throw new ArgumentException($"Revit cannot array: {string.Join(", ", notArrayable)} (an element already in an array group cannot be arrayed again).");
        string kind = Choice(p, "kind", null, "linear", "radial");
        bool group = Flag(p, "group", true);
        var anchor = Choice(p, "anchor", "second", "second", "last") == "last" ? ArrayAnchorMember.Last : ArrayAnchorMember.Second;
        var view = Find.ViewOrActive(doc, p);
        ICollection<ElementId> made;
        long arrayId = -1;
        if (kind == "linear")
        {
            var v = U.NeedPt(p, "vector");
            if (v.GetLength() < 1e-6) throw new ArgumentException("'vector' must not be zero.");
            if (group)
            {
                var arr = LinearArray.Create(doc, view, ids, count, v, anchor);
                arrayId = arr.Id.Value;
                made = arr.GetCopiedMemberIds();
            }
            else made = LinearArray.ArrayElementsWithoutAssociation(doc, view, ids, count, v, anchor);
        }
        else
        {
            var centre = U.NeedPt(p, "center");
            double angle = U.Rad(NeedDbl(p, "angle"));
            if (!RadialArray.IsRotationAngleValid(angle)) throw new ArgumentException("'angle' is not a valid array angle.");
            var axis = Line.CreateBound(new XYZ(centre.X, centre.Y, 0), new XYZ(centre.X, centre.Y, 1));
            if (group)
            {
                var arr = RadialArray.Create(doc, view, ids, count, axis, angle, anchor);
                arrayId = arr.Id.Value;
                made = arr.GetCopiedMemberIds();
            }
            else made = RadialArray.ArrayElementsWithoutAssociation(doc, view, ids, count, axis, angle, anchor);
        }
        doc.Regenerate();
        made = made.Where(x => !ids.Contains(x)).ToList();   // the plain-copy calls also return the originals
        return new { kind, count, grouped = group, array = arrayId >= 0 ? arrayId : (long?)null, copies = made.Select(x => x.Value).OrderBy(x => x).ToList() };
    }

    // ---------------------------------------------------------------- reference planes and alignment locks

    private static object ReferencePlaneCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        var a = U.NeedPt(p, "start");
        var b = U.NeedPt(p, "end");
        if (a.DistanceTo(b) < 0.003) throw new ArgumentException("'start' and 'end' are too close.");
        var cut = Has(p, "cut") ? U.NeedPt(p, "cut") : XYZ.BasisZ;
        if (cut.CrossProduct(b - a).GetLength() < 1e-9) throw new ArgumentException("'cut' must not run along the plane's line.");
        var rp = doc.Create.NewReferencePlane(a, b, cut, view);
        if (Str(p, "name") is string name) rp.Name = name;
        return new { id = rp.Id.Value, name = rp.Name, view = view.Name };
    }

    /// <summary>The datum's plane (grid or reference plane) and its reference.</summary>
    private static (Reference r, XYZ origin, XYZ normal, string name) Datum(Document doc, ElementId id)
    {
        var e = Find.Elem(doc, id);
        if (e is Grid g)
        {
            if (!(g.Curve is Line l)) throw new ArgumentException($"Grid '{g.Name}' is curved; alignment locks here need a straight grid.");
            return (new Reference(g), l.GetEndPoint(0), l.Direction.CrossProduct(XYZ.BasisZ).Normalize(), g.Name);
        }
        if (e is ReferencePlane rp)
        {
            var pl = rp.GetPlane();
            return (rp.GetReference(), pl.Origin, pl.Normal, rp.Name);
        }
        throw new ArgumentException($"{id.Value} is not a grid or a reference plane.");
    }

    /// <summary>
    /// Lock elements' faces to a grid or reference plane (the padlock of Revit's Align). The face parallel to the datum and
    /// nearest to it is used; it must already lie on the datum, or move: true moves the element onto it first.
    /// </summary>
    private static object AlignLock(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        if (!(view is ViewPlan || view is ViewSection)) throw new ArgumentException("align_lock works in a plan or section view ('view').");
        var (dr, o, n, dname) = Datum(doc, NeedId(p, "datum"));
        bool move = Flag(p, "move", false);
        double tol = U.LenMm(p, "tolerance", 1);
        var rows = new List<object>();
        foreach (var e in c.Elements(p))
        {
            var faces = DocOps.Faces(e, view).Where(f => Math.Abs(f.normal.DotProduct(n)) > 0.9999).ToList();
            if (faces.Count == 0) throw new ArgumentException($"{e.Id.Value} has no face parallel to '{dname}' in this view.");
            var best = faces.OrderBy(f => Math.Abs((f.origin - o).DotProduct(n))).First();
            double off = (best.origin - o).DotProduct(n);
            if (Math.Abs(off) > tol)
            {
                if (!move) throw new ArgumentException($"{e.Id.Value}: its nearest face is {U.Out(Math.Abs(off)):0.#} from '{dname}'. Move it onto the datum first, or pass move: true.");
                ElementTransformUtils.MoveElement(doc, e.Id, -off * n);
                doc.Regenerate();
                faces = DocOps.Faces(e, view).Where(f => Math.Abs(f.normal.DotProduct(n)) > 0.9999).ToList();
                best = faces.OrderBy(f => Math.Abs((f.origin - o).DotProduct(n))).First();
            }
            var dim = doc.Create.NewAlignment(view, dr, best.reference);
            dim.IsLocked = true;
            rows.Add(new { id = e.Id.Value, alignment = dim.Id.Value, moved = Math.Abs(off) > tol ? U.Out(Math.Abs(off)) : 0 });
        }
        return new { datum = dname, view = view.Name, locked = rows.Count, items = rows };
    }
}
