namespace RevitMcpBridge;

/// <summary>Tags and dimensions.</summary>
internal static class AnnotationOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["tag_elements"] = TagElements;
        ops["dimension_grids"] = DimensionGrids;
    }

    private static XYZ TagPoint(Element e, View view)
    {
        if (e.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
        if (e.Location is LocationPoint lp) return lp.Point;
        var b = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
        return b == null ? null : (b.Min + b.Max) / 2;
    }

    /// <summary>Tag elements in a view with the loaded tag of their category (or a given tag type); already tagged ones are skipped.</summary>
    private static object TagElements(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        if (view is View3D || view is ViewSchedule || view is ViewSheet) throw new ArgumentException($"Elements cannot be tagged in '{view.Name}': use a plan, section or elevation.");

        List<Element> targets;
        if (Has(p, "ids")) targets = Ids(p, "ids", 5000).Select(id => Find.Elem(doc, id)).ToList();
        else
        {
            var cats = Find.Cats(doc, p, "category");
            if (cats.Count == 0) throw new ArgumentException("tag_elements needs 'category' or 'ids'.");
            targets = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(cats)).ToElements().ToList();
        }

        var tagged = new HashSet<long>();
        if (Flag(p, "skip_tagged", true))
        {
            foreach (IndependentTag t in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)))
                foreach (var id in t.GetTaggedLocalElementIds()) tagged.Add(id.Value);
            // rooms, areas and spaces carry their own tag classes
            foreach (var st in new FilteredElementCollector(doc, view.Id).OfClass(typeof(SpatialElementTag)))
            {
                if (st is Autodesk.Revit.DB.Architecture.RoomTag rt && rt.TaggedLocalRoomId != null) tagged.Add(rt.TaggedLocalRoomId.Value);
                else if (st is AreaTag at && at.Area != null) tagged.Add(at.Area.Id.Value);
                else if (st is Autodesk.Revit.DB.Mechanical.SpaceTag sp && sp.Space != null) tagged.Add(sp.Space.Id.Value);
            }
        }

        ElementId tagType = Has(p, "tag_type") ? Find.TypeOf(doc, Str(p, "tag_type"), null, typeof(FamilySymbol)).Id : null;
        bool leader = Flag(p, "leader", false);
        var orientation = Choice(p, "orientation", "horizontal", "horizontal", "vertical") == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
        var ids = new List<long>();
        int skipped = 0;
        var failed = new Dictionary<string, int>();
        foreach (var e in targets)
        {
            if (tagged.Contains(e.Id.Value)) { skipped++; continue; }
            var at = TagPoint(e, view);
            if (at == null) { failed["no location"] = failed.TryGetValue("no location", out int n0) ? n0 + 1 : 1; continue; }
            try
            {
                if (e is SpatialElement spatial)
                {
                    ids.Add(SpatialTag(doc, view, spatial, at, tagType).Id.Value);
                    continue;
                }
                var tag = tagType != null
                    ? IndependentTag.Create(doc, tagType, view.Id, new Reference(e), leader, orientation, at)
                    : IndependentTag.Create(doc, view.Id, new Reference(e), leader, TagMode.TM_ADDBY_CATEGORY, orientation, at);
                ids.Add(tag.Id.Value);
            }
            catch (Exception ex)
            {
                string why = ex.Message.Length > 120 ? ex.Message.Substring(0, 120) : ex.Message;
                failed[why] = failed.TryGetValue(why, out int n) ? n + 1 : 1;
            }
        }
        if (ids.Count == 0 && failed.Count > 0)
            throw new InvalidOperationException("No element could be tagged: " + string.Join("; ", failed.Select(kv => $"{kv.Value} x {kv.Key}")) +
                                                ". Is a tag family for this category loaded (family_load)?");
        if (failed.Count > 0) c.Warn("not tagged: " + string.Join("; ", failed.Select(kv => $"{kv.Value} x {kv.Key}")));
        return new { view = view.Name, tagged = ids.Count, alreadyTagged = skipped, ids };
    }

    /// <summary>Rooms, areas and spaces take their own tags (room / area / space tag), at the element's point, in a plan (areas: their area plan).</summary>
    private static Element SpatialTag(Document doc, View view, SpatialElement e, XYZ at, ElementId tagType)
    {
        if (!(view is ViewPlan plan)) throw new ArgumentException("rooms, areas and spaces are tagged in plan views");
        if (e.Location is LocationPoint lp) at = lp.Point;
        var uv = new UV(at.X, at.Y);
        Element tag = e is Autodesk.Revit.DB.Architecture.Room room ? doc.Create.NewRoomTag(new LinkElementId(room.Id), uv, view.Id)
            : e is Area area ? doc.Create.NewAreaTag(plan, area, uv)
            : e is Autodesk.Revit.DB.Mechanical.Space space ? (Element)doc.Create.NewSpaceTag(space, uv, view)
            : throw new ArgumentException("not a room, area or space");
        if (tag == null) throw new InvalidOperationException("Revit made no tag (is a room / area / space tag family loaded, and is the element in this view?)");
        if (tagType != null) tag.ChangeTypeId(tagType);
        return tag;
    }

    /// <summary>A chain dimension across every set of parallel straight grids visible in a plan view.</summary>
    private static object DimensionGrids(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        if (!(view is ViewPlan)) throw new ArgumentException("dimension_grids needs a plan view ('view').");
        var names = Has(p, "grids") ? new HashSet<string>(StrList(p, "grids", 500), StringComparer.OrdinalIgnoreCase) : null;
        var grids = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(g => g.Curve is Line && (names == null || names.Contains(g.Name))).ToList();
        double offset = U.LenMm(p, "offset", 1500);
        double z = view.GenLevel?.ProjectElevation ?? 0;

        // Group parallel grids by direction (opposite directions are the same family).
        var sets = new List<(XYZ dir, List<Grid> members)>();
        foreach (var g in grids)
        {
            var d = ((Line)g.Curve).Direction;
            d = new XYZ(d.X, d.Y, 0).Normalize();
            var set = sets.FirstOrDefault(s => Math.Abs(s.dir.DotProduct(d)) > 0.9999);
            if (set.members == null) sets.Add((d, new List<Grid> { g })); else set.members.Add(g);
        }

        var made = new List<object>();
        foreach (var (dir, members) in sets.Where(s => s.members.Count >= 2))
        {
            var normal = dir.CrossProduct(XYZ.BasisZ);
            var refs = new ReferenceArray();
            double minN = double.MaxValue, maxN = double.MinValue, startAlong = double.MaxValue;
            foreach (var g in members.OrderBy(g => ((Line)g.Curve).Origin.DotProduct(normal)))
            {
                refs.Append(new Reference(g));
                var line = (Line)g.Curve;
                double n = line.GetEndPoint(0).DotProduct(normal);
                minN = Math.Min(minN, n); maxN = Math.Max(maxN, n);
                startAlong = Math.Min(startAlong, Math.Min(line.GetEndPoint(0).DotProduct(dir), line.GetEndPoint(1).DotProduct(dir)));
            }
            // The dimension line runs across the grids, 'offset' inside their start ends.
            double along = startAlong + offset;
            XYZ a = dir.Multiply(along) + normal.Multiply(minN), b = dir.Multiply(along) + normal.Multiply(maxN);
            var dimLine = Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
            var dim = doc.Create.NewDimension(view, dimLine, refs);
            made.Add(new { id = dim.Id.Value, grids = members.Select(g => g.Name).OrderBy(n => n).ToList() });
        }
        if (made.Count == 0)
        {
            var hidden = names == null ? new List<string>() : new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => names.Contains(g.Name) && !grids.Any(v => v.Id == g.Id)).Select(g => g.Name).ToList();
            throw new ArgumentException($"View '{view.Name}' shows no two parallel straight grids to dimension."
                + (hidden.Count > 0 ? $" Grids {string.Join(", ", hidden)} exist but do not reach this level: run grid_extents on them first." : ""));
        }
        return new { view = view.Name, dimensions = made.Count, created = made };
    }
}
