namespace RevitMcpBridge;

/// <summary>
/// Drafting and detailing: drafting views, detail lines and polylines with line styles, filled and masking regions,
/// detail components, detail groups, callouts, duplicated views, view settings (scale, detail level, crop, view
/// range), and drafting views or legends copied from another open model.
/// </summary>
internal static class DetailOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["drafting_view_create"] = DraftingViewCreate;
        ops["detail_lines_create"] = DetailLinesCreate;
        ops["region_create"] = RegionCreate;
        ops["detail_component_place"] = DetailComponentPlace;
        ops["detail_group_place"] = DetailGroupPlace;
        ops["callout_create"] = CalloutCreate;
        ops["view_duplicate"] = ViewDuplicate;
        ops["view_set"] = ViewSet;
        ops["views_copy_from"] = ViewsCopyFrom;
        ops["legend_create"] = LegendCreate;
        ops["repeating_detail_place"] = RepeatingDetailPlace;
    }

    // ---------------------------------------------------------------- shared

    /// <summary>A line style (a subcategory of Lines) by name; the error lists the styles of the model.</summary>
    public static GraphicsStyle LineStyle(Document doc, string name)
    {
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        var styles = lines.SubCategories.Cast<Category>().ToList();
        var hit = styles.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"No line style '{name}'. Available: " +
                                                 string.Join(", ", styles.Select(s => s.Name).OrderBy(s => s)) + ".");
        return hit.GetGraphicsStyle(GraphicsStyleType.Projection);
    }

    /// <summary>One drawn item: {start, end, mid?} (a line or an arc) or {points: [...], closed?} (a polyline).</summary>
    private static List<Curve> Shape(Obj item, string what)
    {
        if (!Has(item, "points")) return new List<Curve> { U.Curve(item, what, 0) };
        var pts = U.PtList(item, "points", 2, 5000);
        if (Flag(item, "closed", false) && pts[0].DistanceTo(pts[pts.Count - 1]) > 0.003) pts.Add(pts[0]);
        var curves = new List<Curve>();
        for (int i = 0; i + 1 < pts.Count; i++) curves.Add(U.Segment(pts[i], pts[i + 1], $"{what} segment {i + 1}"));
        return curves;
    }

    /// <summary>Boundary loops: 'boundary' (one outline), 'boundaries' (outline then holes) or 'circle' {center, radius}.</summary>
    private static List<CurveLoop> Loops(Obj p)
    {
        if (Has(p, "circle"))
        {
            var circle = Sub(p, "circle");
            XYZ c = U.NeedPt(circle, "center");
            double r = U.In(NeedDbl(circle, "radius"));
            if (r < 0.003) throw new ArgumentException("circle: radius must be positive.");
            var loop = new CurveLoop();
            loop.Append(Arc.Create(c, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            loop.Append(Arc.Create(c, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
            return new List<CurveLoop> { loop };
        }
        if (Has(p, "boundaries"))
            return NeedArr(p, "boundaries", 500).Select((b, i) => EditContext.Loop(b, i == 0 ? "boundary" : $"hole {i}", 0)).ToList();
        if (Has(p, "boundary")) return new List<CurveLoop> { EditContext.Loop(p["boundary"], "boundary", 0) };
        throw new ArgumentException("Give 'boundary' (list of [x, y] corners), 'boundaries' (outline, then holes) or 'circle' {center, radius}.");
    }

    private static string Kind(View v) => v.ViewType.ToString();

    // ---------------------------------------------------------------- drafting views and lines

    private static object DraftingViewCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting)
            ?? throw new InvalidOperationException("The model has no drafting view type.");
        var view = ViewDrafting.Create(doc, vft.Id);
        if (Str(p, "name") is string name) view.Name = name;
        view.Scale = Int(p, "scale", 20, 1, 24000);
        return new { id = view.Id.Value, name = view.Name, scale = view.Scale };
    }

    private static object DetailLinesCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        var defaultStyle = Str(p, "line_style") is string s ? LineStyle(doc, s) : null;
        var ids = new List<long>();
        var lines = ObjList(p, "lines", 5000);
        for (int i = 0; i < lines.Count; i++)
        {
            var style = Str(lines[i], "style") is string own ? LineStyle(doc, own) : defaultStyle;
            foreach (var curve in Shape(lines[i], $"line {i + 1}"))
            {
                var dc = doc.Create.NewDetailCurve(view, curve);
                if (style != null) dc.LineStyle = style;
                ids.Add(dc.Id.Value);
            }
        }
        return new { created = ids.Count, view = view.Name, ids };
    }

    // ---------------------------------------------------------------- regions

    private static object RegionCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        var loops = Loops(p);
        bool masking = Flag(p, "masking", false);
        FilledRegion region;
        string typeName;
        if (masking)
        {
            region = FilledRegion.CreateMaskingRegion(doc, view.Id, loops);
            typeName = "masking";
        }
        else
        {
            var type = Has(p, "type")
                ? (FilledRegionType)Find.TypeOf(doc, Str(p, "type"), null, typeof(FilledRegionType))
                : doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.FilledRegionType)) as FilledRegionType
                  ?? new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().FirstOrDefault()
                  ?? throw new InvalidOperationException("The model has no filled region type.");
            region = FilledRegion.Create(doc, type.Id, view.Id, loops);
            typeName = type.Name;
        }
        if (Str(p, "line_style") is string ls)
        {
            var style = LineStyle(doc, ls);
            if (!FilledRegion.GetValidLineStyleIdsForFilledRegion(doc).Contains(style.Id))
                throw new ArgumentException($"Line style '{ls}' cannot be used for a region boundary.");
            region.SetLineStyleId(style.Id);
        }
        return new { id = region.Id.Value, view = view.Name, type = typeName, loops = loops.Count };
    }

    // ---------------------------------------------------------------- detail components and groups

    private static object DetailComponentPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), BuiltInCategory.OST_DetailComponents);
        bool lineBased = symbol.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail;
        double rotation = U.Rad(Dbl(p, "rotation") ?? 0);
        var ids = new List<long>();
        if (lineBased)
        {
            var lines = ObjList(p, "lines", 2000);
            for (int i = 0; i < lines.Count; i++)
                ids.Add(doc.Create.NewFamilyInstance(U.Segment(U.NeedPt(lines[i], "start"), U.NeedPt(lines[i], "end"), $"line {i + 1}"), symbol, view).Id.Value);
        }
        else
        {
            // at points (one rotation), and / or repeated along lines at a spacing, each turned to its line
            var placements = new List<(XYZ pt, double angle)>();
            if (Has(p, "points")) placements.AddRange(U.PtList(p, "points", 1, 2000).Select(pt => (pt, rotation)));
            if (Has(p, "along"))
            {
                var runs = ObjList(p, "along", 200);
                for (int i = 0; i < runs.Count; i++)
                {
                    var line = U.Segment(U.NeedPt(runs[i], "start"), U.NeedPt(runs[i], "end"), $"along {i + 1}");
                    double spacing = U.In(NeedDbl(runs[i], "spacing"));
                    if (spacing < 0.003) throw new ArgumentException($"along {i + 1}: spacing must be positive.");
                    int count = (int)Math.Floor(line.Length / spacing + 1e-9) + 1;
                    if (count > 2000) throw new ArgumentException($"along {i + 1}: {count} components; increase the spacing.");
                    double angle = Math.Atan2(line.Direction.Y, line.Direction.X) + rotation;
                    // centred on the line: the leftover length is split between both ends
                    double start = (line.Length - (count - 1) * spacing) / 2;
                    for (int k = 0; k < count; k++)
                        placements.Add((line.GetEndPoint(0) + line.Direction * (start + k * spacing), angle));
                }
            }
            if (placements.Count == 0) throw new ArgumentException("Give 'points' and / or 'along' for a point-based detail component.");
            foreach (var (pt, angle) in placements)
            {
                var fi = doc.Create.NewFamilyInstance(pt, symbol, view);
                if (Math.Abs(angle) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(pt, pt + XYZ.BasisZ), angle);
                ids.Add(fi.Id.Value);
            }
        }
        return new { created = ids.Count, view = view.Name, type = Find.TypeLabel(symbol), lineBased, ids };
    }

    private static object DetailGroupPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        if (doc.ActiveView == null || doc.ActiveView.Id != view.Id)
            throw new ArgumentException($"Revit places detail groups in the active view only. Make '{view.Name}' active " +
                                        "(view_activate in an earlier edit, or open it), then place the group.");
        var group = (GroupType)Find.TypeOf(doc, NeedStr(p, "group"), BuiltInCategory.OST_IOSDetailGroups, typeof(GroupType));
        var ids = U.PtList(p, "points", 1, 500).Select(pt => doc.Create.PlaceGroup(pt, group).Id.Value).ToList();
        return new { created = ids.Count, view = view.Name, group = group.Name, ids };
    }

    // ---------------------------------------------------------------- views

    private static object CalloutCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var parent = Find.ViewOf(doc, NeedRef(p, "view"));
        var types = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
        ViewFamilyType vft;
        if (Str(p, "type") is string t)
            vft = types.FirstOrDefault(x => x.Name.Equals(t, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"No view type '{t}'. Available: {string.Join(", ", types.Select(x => x.Name))}.");
        else if (parent is ViewPlan)
            vft = (ViewFamilyType)doc.GetElement(parent.GetTypeId());
        else
            vft = types.FirstOrDefault(x => x.ViewFamily == ViewFamily.Detail)
                  ?? throw new InvalidOperationException("The model has no detail view type for callouts.");
        XYZ a = U.NeedPt(p, "min"), b = U.NeedPt(p, "max");
        var callout = ViewSection.CreateCallout(doc, parent.Id, vft.Id, a, b);
        if (Str(p, "name") is string name) callout.Name = name;
        if (Has(p, "scale")) callout.Scale = Int(p, "scale", 20, 1, 24000);
        return new { id = callout.Id.Value, name = callout.Name, parent = parent.Name, viewType = vft.Name };
    }

    private static object ViewDuplicate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOf(doc, NeedRef(p, "view"));
        string mode = Choice(p, "mode", "duplicate", "duplicate", "detailing", "dependent");
        var option = mode == "detailing" ? ViewDuplicateOption.WithDetailing
                   : mode == "dependent" ? ViewDuplicateOption.AsDependent : ViewDuplicateOption.Duplicate;
        if (!view.CanViewBeDuplicated(option))
            throw new ArgumentException($"View '{view.Name}' ({Kind(view)}) cannot be duplicated as '{mode}'.");
        int count = Int(p, "count", 1, 1, 50);
        var names = Has(p, "names") ? StrList(p, "names", 50) : null;
        if (names != null && names.Count != count && Has(p, "count"))
            throw new ArgumentException("'names' must have one name per copy.");
        if (names != null) count = names.Count;
        var made = new List<object>();
        for (int i = 0; i < count; i++)
        {
            var copy = (View)doc.GetElement(view.Duplicate(option));
            if (names != null) copy.Name = names[i];
            made.Add(new { id = copy.Id.Value, name = copy.Name });
        }
        return new { source = view.Name, mode, created = made.Count, views = made };
    }

    private static object ViewSet(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOf(doc, NeedRef(p, "view"));
        var changed = new List<string>();
        if (Str(p, "name") is string name) { view.Name = name; changed.Add("name"); }
        if (Has(p, "scale")) { view.Scale = Int(p, "scale", 100, 1, 24000); changed.Add("scale"); }
        if (Has(p, "detail_level"))
        {
            string level = Choice(p, "detail_level", "medium", "coarse", "medium", "fine");
            view.DetailLevel = level == "coarse" ? ViewDetailLevel.Coarse : level == "fine" ? ViewDetailLevel.Fine : ViewDetailLevel.Medium;
            changed.Add("detail_level");
        }
        if (Has(p, "discipline"))
        {
            string d = Choice(p, "discipline", "structural", "architectural", "structural", "mechanical", "electrical", "plumbing", "coordination");
            view.Discipline = (ViewDiscipline)Enum.Parse(typeof(ViewDiscipline), d, true);
            changed.Add("discipline");
        }
        if (Has(p, "crop"))
        {
            var crop = Sub(p, "crop");
            var box = view.CropBox;
            var toView = box.Transform.Inverse;
            XYZ a = toView.OfPoint(U.NeedPt(crop, "min")), b = toView.OfPoint(U.NeedPt(crop, "max"));
            box.Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), box.Min.Z);
            box.Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), box.Max.Z);
            view.CropBox = box;
            view.CropBoxActive = true;
            changed.Add("crop");
        }
        if (Has(p, "scope_box"))
        {
            string sb = Str(p, "scope_box");
            var prm = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)
                      ?? throw new ArgumentException($"View '{view.Name}' has no Scope Box parameter.");
            ElementId id = ElementId.InvalidElementId;
            if (!string.IsNullOrEmpty(sb) && !sb.Equals("none", StringComparison.OrdinalIgnoreCase))
                id = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                         .FirstOrDefault(e => e.Name.Equals(sb, StringComparison.OrdinalIgnoreCase))?.Id
                     ?? throw new ArgumentException($"No scope box named '{sb}'. list_documentation kind scope_boxes lists them.");
            if (!prm.Set(id)) throw new InvalidOperationException($"Revit refused scope box '{sb}' for view '{view.Name}'.");
            changed.Add("scope_box");
        }
        if (Bool(p, "crop_active") is bool active) { view.CropBoxActive = active; changed.Add("crop_active"); }
        if (Bool(p, "crop_visible") is bool visible) { view.CropBoxVisible = visible; changed.Add("crop_visible"); }
        if (Has(p, "view_range"))
        {
            if (!(view is ViewPlan plan)) throw new ArgumentException("view_range applies to plan views only.");
            var r = Sub(p, "view_range");
            var range = plan.GetViewRange();
            void Set(string key, PlanViewPlane plane)
            {
                if (Dbl(r, key) is double d) range.SetOffset(plane, U.In(d));
            }
            Set("top", PlanViewPlane.TopClipPlane);
            Set("cut", PlanViewPlane.CutPlane);
            Set("bottom", PlanViewPlane.BottomClipPlane);
            Set("depth", PlanViewPlane.ViewDepthPlane);
            try { plan.SetViewRange(range); }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                var now = plan.GetViewRange();
                string Plane(PlanViewPlane pl) => $"{pl}: {U.Out(now.GetOffset(pl))} from {doc.GetElement(now.GetLevelId(pl))?.Name ?? "(unlimited)"}";
                throw new ArgumentException("Revit refused that view range (it needs bottom <= cut <= top and depth <= bottom). Current: " +
                    string.Join("; ", new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane }.Select(Plane)) + ".");
            }
            changed.Add("view_range");
        }
        if (changed.Count == 0) throw new ArgumentException("Nothing to change: give name, scale, detail_level, discipline, scope_box, crop, crop_active, crop_visible or view_range.");
        return new { view = view.Name, changed };
    }

    // ---------------------------------------------------------------- copy from another model

    internal sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }

    /// <summary>Another model open in this Revit, by title (with or without .rvt) or path.</summary>
    public static Document OtherModel(Document active, string name)
    {
        var docs = active.Application.Documents.Cast<Document>().Where(d => !d.IsFamilyDocument && !d.Equals(active)).ToList();
        string bare = name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        return docs.FirstOrDefault(d => d.Title.Equals(bare, StringComparison.OrdinalIgnoreCase)
                                        || d.Title.Equals(name, StringComparison.OrdinalIgnoreCase)
                                        || d.PathName.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No other open model named '{name}'. Open it in this Revit first. Open: " +
                                              (docs.Count == 0 ? "none" : string.Join(", ", docs.Select(d => d.Title))) + ".");
    }

    /// <summary>Drafting views and legends of a model that a copy can take.</summary>
    public static List<View> CopyableViews(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && (v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.Legend))
            .OrderBy(v => v.Name).ToList();

    private static object ViewsCopyFrom(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var source = OtherModel(doc, NeedStr(p, "model"));
        var available = CopyableViews(source);
        string kind = Choice(p, "kind", "all", "all", "drafting", "legend");
        var picked = available.Where(v => kind == "all" || (kind == "drafting" ? v.ViewType == ViewType.DraftingView : v.ViewType == ViewType.Legend)).ToList();
        if (Has(p, "views"))
        {
            var names = StrList(p, "views", 500);
            var missing = names.Where(n => !picked.Any(v => v.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"Not in '{source.Title}' as a {kind} view: {string.Join(", ", missing)}.");
            picked = picked.Where(v => names.Contains(v.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        }
        else if (Str(p, "name_contains") is string part)
            picked = picked.Where(v => v.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        else throw new ArgumentException("Give 'views' (names) or 'name_contains' to choose what to copy (list_detailing with model shows them).");

        var existing = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        bool skipExisting = Flag(p, "skip_existing", true);
        var skipped = skipExisting ? picked.Where(v => existing.Contains(v.Name)).Select(v => v.Name).ToList() : new List<string>();
        picked = picked.Where(v => !skipped.Contains(v.Name)).ToList();
        if (picked.Count == 0) return new { model = source.Title, copied = 0, skippedExisting = skipped };

        var options = new CopyPasteOptions();
        options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
        var newIds = ElementTransformUtils.CopyElements(source, picked.Select(v => v.Id).ToList(), doc, Transform.Identity, options);
        var copied = newIds.Select(id => doc.GetElement(id)).OfType<View>()
            .Select(v => (object)new { id = v.Id.Value, name = v.Name, kind = Kind(v) }).ToList();
        return new
        {
            model = source.Title, copied = copied.Count, views = copied, skippedExisting = skipped,
            note = "Types with the same name (line styles, fill patterns, text, detail components) keep this model's definition.",
        };
    }

    // ---------------------------------------------------------------- legends and repeating details (no create in the API)

    /// <summary>
    /// A new, empty legend. Revit's API cannot create a legend view; duplicating an existing legend without its content
    /// gives an empty one, which is then renamed and scaled.
    /// </summary>
    private static object LegendCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var legends = CopyableViews(doc).Where(v => v.ViewType == ViewType.Legend).ToList();
        View source = Str(p, "from") is string from
            ? legends.FirstOrDefault(v => v.Name.Equals(from, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"No legend named '{from}'.")
            : legends.FirstOrDefault()
              ?? throw new InvalidOperationException("Revit can only make a legend by duplicating one, and this model has none. " +
                                                     "Copy one from another open model first (views_copy_from with kind legend).");
        var legend = (View)doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate));
        legend.Name = NeedStr(p, "name");
        if (Has(p, "scale")) legend.Scale = Int(p, "scale", 50, 1, 24000);
        return new { id = legend.Id.Value, name = legend.Name, scale = legend.Scale, madeFrom = source.Name };
    }

    /// <summary>
    /// Repeating details: Revit's API has no create method, so an existing repeating detail (in this model, or in
    /// another open model given by 'model') is copied into the view, reshaped to each line, and given the type.
    /// </summary>
    private static object RepeatingDetailPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        string typeName = Str(p, "type");
        Element Sample(Document d) => new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_RepeatingDetailLines)
            .WhereElementIsNotElementType()
            .Where(e => e.OwnerViewId != ElementId.InvalidElementId && (typeName == null || d.GetElement(e.GetTypeId())?.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true))
            .FirstOrDefault();
        var sourceDoc = Str(p, "model") is string m ? OtherModel(doc, m) : doc;
        var sample = Sample(sourceDoc)
                     ?? throw new InvalidOperationException(
                         "Revit's API cannot create repeating details, so one is copied from an existing sample, and " +
                         $"'{sourceDoc.Title}' has no placed repeating detail{(typeName != null ? $" of type '{typeName}'" : "")}. " +
                         "Open a model that has one and pass it as 'model', or use detail_component_place with 'along'.");
        var sampleView = (View)sourceDoc.GetElement(sample.OwnerViewId);
        var options = new CopyPasteOptions();
        options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
        var lines = ObjList(p, "lines", 500);
        var ids = new List<long>();
        for (int i = 0; i < lines.Count; i++)
        {
            var curve = U.Segment(U.NeedPt(lines[i], "start"), U.NeedPt(lines[i], "end"), $"line {i + 1}");
            var copyId = ElementTransformUtils.CopyElements(sampleView, new List<ElementId> { sample.Id }, view, Transform.Identity, options).Single();
            var copy = doc.GetElement(copyId);
            if (copy.Location is LocationCurve lc) lc.Curve = curve;
            else if (copy is CurveElement ce) ce.SetGeometryCurve(curve, true);
            else throw new InvalidOperationException($"The copied repeating detail ({copy.GetType().Name}) cannot be reshaped through the API.");
            ids.Add(copy.Id.Value);
        }
        return new
        {
            created = ids.Count, view = view.Name, ids, copiedFrom = sourceDoc.Title,
            type = doc.GetElement(doc.GetElement(new ElementId(ids[0]))?.GetTypeId() ?? ElementId.InvalidElementId)?.Name,
        };
    }
}
