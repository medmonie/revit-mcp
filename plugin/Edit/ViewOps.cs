namespace RevitMcpBridge;

/// <summary>Views, sheets, schedules and simple annotation.</summary>
internal static class ViewOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["view_plan_create"] = PlanCreate;
        ops["view_3d_create"] = View3dCreate;
        ops["section_create"] = SectionCreate;
        ops["elevation_create"] = ElevationCreate;
        ops["view_activate"] = Activate;
        ops["sheet_create"] = SheetCreate;
        ops["viewport_place"] = ViewportPlace;
        ops["schedule_create"] = ScheduleCreate;
        ops["text_note_create"] = TextNoteCreate;
        ops["model_lines_create"] = ModelLinesCreate;
    }

    private static ViewFamilyType FamilyType(Document doc, ViewFamily family) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == family)
        ?? throw new InvalidOperationException($"The model has no view type for {family}.");

    private static void Name(View view, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        try { view.Name = name; }
        catch (Exception ex) { throw new ArgumentException($"Cannot name the view '{name}': {ex.Message}"); }
    }

    public static ViewPlan CreatePlan(Document doc, Level level, string kind, string name)
    {
        var family = kind == "structural" ? ViewFamily.StructuralPlan : kind == "ceiling" ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan;
        var plan = ViewPlan.Create(doc, FamilyType(doc, family).Id, level.Id);
        Name(plan, name);
        return plan;
    }

    private static object PlanCreate(EditContext c, Obj p)
    {
        var plan = CreatePlan(c.Doc, Find.NeedLevel(c.Doc, p), Choice(p, "kind", "structural", "structural", "floor", "ceiling"), Str(p, "name"));
        return new { id = plan.Id.Value, name = plan.Name, type = plan.ViewType.ToString() };
    }

    private static object View3dCreate(EditContext c, Obj p)
    {
        var view = View3D.CreateIsometric(c.Doc, FamilyType(c.Doc, ViewFamily.ThreeDimensional).Id);
        Name(view, Str(p, "name"));
        return new { id = view.Id.Value, name = view.Name };
    }

    /// <summary>Lowest and highest level elevation in the model (feet), for default view heights.</summary>
    private static (double lo, double hi) LevelRange(Document doc)
    {
        var levels = Find.Levels(doc);
        return levels.Count == 0 ? (0.0, 10.0) : (levels[0].ProjectElevation, levels[levels.Count - 1].ProjectElevation);
    }

    private static string Compass(XYZ d)
    {
        // Looking direction in plan: the closest of the 8 compass directions (project north = +Y).
        double ang = Math.Atan2(d.Y, d.X) * 180 / Math.PI;   // 0 = east, 90 = north
        string[] names = { "east", "north-east", "north", "north-west", "west", "south-west", "south", "south-east" };
        int i = (int)Math.Round(((ang % 360) + 360) % 360 / 45.0) % 8;
        return names[i];
    }

    /// <summary>
    /// A section along a line (start/end), along a straight grid, or across an element's bounding box.
    /// The section looks to the LEFT of the direction start -> end (flip: true looks the other way).
    /// </summary>
    private static object SectionCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        XYZ a, b;
        double? autoLo = null, autoHi = null, autoDepth = null;
        if (Has(p, "grid"))
        {
            string gname = Ref(p, "grid");
            var grid = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .FirstOrDefault(g => string.Equals(g.Name, gname, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No grid named '{gname}'.");
            if (!(grid.Curve is Line gl)) throw new ArgumentException($"Grid '{gname}' is curved; give start and end instead.");
            a = gl.GetEndPoint(0); b = gl.GetEndPoint(1);
        }
        else if (Has(p, "element"))
        {
            var e = Find.Elem(doc, NeedId(p, "element"));
            var box = e.get_BoundingBox(null) ?? throw new ArgumentException($"Element {e.Id.Value} has no geometry to cut.");
            var mid = (box.Min + box.Max) * 0.5;
            if (e.Location is LocationCurve lc && lc.Curve is Line ll)
            {
                // Along a beam / wall: a longitudinal section cut on its centre line.
                a = ll.GetEndPoint(0); b = ll.GetEndPoint(1);
                autoDepth = U.Mm(1000);
            }
            else
            {
                // Otherwise along the longer plan side of the bounding box, through its centre.
                double dx = box.Max.X - box.Min.X, dy = box.Max.Y - box.Min.Y;
                a = dx >= dy ? new XYZ(box.Min.X, mid.Y, 0) : new XYZ(mid.X, box.Min.Y, 0);
                b = dx >= dy ? new XYZ(box.Max.X, mid.Y, 0) : new XYZ(mid.X, box.Max.Y, 0);
            }
            autoLo = box.Min.Z; autoHi = box.Max.Z;
            autoDepth = autoDepth ?? Math.Min(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) / 2 + U.Mm(300);
        }
        else { a = U.NeedPt(p, "start"); b = U.NeedPt(p, "end"); }
        a = new XYZ(a.X, a.Y, 0); b = new XYZ(b.X, b.Y, 0);
        if (Flag(p, "flip", false)) { var t = a; a = b; b = t; }
        if (a.DistanceTo(b) < 0.003) throw new ArgumentException("section_create: start and end are the same point.");

        var (lo, hi) = LevelRange(doc);
        double bottom = Dbl(p, "bottom") is double bt ? U.In(bt) : (autoLo ?? lo) - U.Mm(1000);
        double top = Dbl(p, "top") is double tp ? U.In(tp) : (autoHi ?? hi) + U.Mm(1000);
        if (top <= bottom) throw new ArgumentException("'top' must be above 'bottom'.");
        double depth = U.Len(p, "depth", autoDepth ?? U.Mm(1000));
        double extend = U.LenMm(p, "extend", 500);
        if (depth <= 0) throw new ArgumentException("'depth' must be positive.");

        var d = (b - a).Normalize();
        var viewDir = d.CrossProduct(XYZ.BasisZ);              // points towards the viewer; the view looks along -viewDir
        var tf = Transform.Identity;
        tf.Origin = (a + b) * 0.5;
        tf.BasisX = d;
        tf.BasisY = XYZ.BasisZ;
        tf.BasisZ = viewDir;
        double half = a.DistanceTo(b) / 2 + extend;
        var sectionBox = new BoundingBoxXYZ
        {
            Transform = tf,
            Min = new XYZ(-half, bottom, -depth),
            Max = new XYZ(half, top, 0),
        };
        var view = ViewSection.CreateSection(doc, FamilyType(doc, ViewFamily.Section).Id, sectionBox);
        Name(view, Str(p, "name"));
        if (Has(p, "scale")) view.Scale = Int(p, "scale", 100, 1, 10000);
        return new
        {
            id = view.Id.Value, name = view.Name, looking = Compass(viewDir.Negate()),
            length = U.Out(a.DistanceTo(b)), depth = U.Out(depth), bottom = U.Out(bottom), top = U.Out(top),
        };
    }

    /// <summary>Elevations from one marker in a plan view, in the compass directions asked for.</summary>
    private static object ElevationCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var plan = Find.ViewOrActive(doc, p, "plan") as ViewPlan
                   ?? throw new ArgumentException("elevation_create needs a plan view: give 'plan' or make a plan view active.");
        var at = U.NeedPt(p, "point");
        int scale = Int(p, "scale", 100, 1, 10000);
        var wanted = Has(p, "directions") ? StrList(p, "directions", 4).Select(x => x.Trim().ToLowerInvariant()).ToList()
                                          : new List<string> { "north", "east", "south", "west" };
        foreach (var w in wanted)
            if (!new[] { "north", "east", "south", "west" }.Contains(w)) throw new ArgumentException($"Unknown direction '{w}': use north, east, south, west.");

        var marker = ElevationMarker.CreateElevationMarker(doc, FamilyType(doc, ViewFamily.Elevation).Id, new XYZ(at.X, at.Y, 0), scale);
        string prefix = Str(p, "name");
        var made = new List<object>();
        // The marker's four slots face fixed directions; create each, read where it looks, keep the ones asked for.
        for (int i = 0; i < 4; i++)
        {
            if (!marker.IsAvailableIndex(i)) continue;
            var view = marker.CreateElevation(doc, plan.Id, i);
            string looking = Compass(view.ViewDirection.Negate());
            if (!wanted.Contains(looking)) { doc.Delete(view.Id); continue; }
            if (prefix != null) Name(view, prefix + " - " + char.ToUpperInvariant(looking[0]) + looking.Substring(1));
            made.Add(new { id = view.Id.Value, name = view.Name, looking });
        }
        return new { markerId = marker.Id.Value, plan = plan.Name, created = made.Count, views = made };
    }

    private static object Activate(EditContext c, Obj p)
    {
        var view = Find.ViewOf(c.Doc, NeedRef(p, "view"));
        c.Defer(() => c.UiDoc.ActiveView = view);
        return new { id = view.Id.Value, name = view.Name, note = "activated after the batch is committed" };
    }

    private static object SheetCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        ElementId titleBlock = ElementId.InvalidElementId;
        string tb = Str(p, "title_block");
        if (tb == null)
        {
            var first = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().FirstOrDefault();
            if (first != null) titleBlock = first.Id; else c.Warn("the model has no title block family; the sheet is empty.");
        }
        else if (!string.Equals(tb, "none", StringComparison.OrdinalIgnoreCase))
            titleBlock = Find.Symbol(doc, tb, BuiltInCategory.OST_TitleBlocks).Id;

        var sheet = ViewSheet.Create(doc, titleBlock);
        if (Ref(p, "number") is string number)
        {
            try { sheet.SheetNumber = number; }
            catch (Exception ex) { throw new ArgumentException($"Cannot use sheet number '{number}': {ex.Message}"); }
        }
        if (Str(p, "name") is string name) sheet.Name = name;
        return new { id = sheet.Id.Value, number = sheet.SheetNumber, name = sheet.Name };
    }

    private static object ViewportPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var sheet = Find.ViewOf(doc, NeedRef(p, "sheet")) as ViewSheet ?? throw new ArgumentException("'sheet' is not a sheet. Use list_sheets.");
        var view = Find.ViewOf(doc, NeedRef(p, "view"));
        XYZ at;
        if (Has(p, "position")) { var q = U.NeedPt(p, "position"); at = new XYZ(q.X, q.Y, 0); }
        else
        {
            var o = sheet.Outline;
            at = new XYZ((o.Min.U + o.Max.U) / 2, (o.Min.V + o.Max.V) / 2, 0);
        }
        if (view is ViewSchedule schedule)
        {
            var si = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, at);
            return new { id = si.Id.Value, sheet = sheet.SheetNumber, view = view.Name, schedule = true };
        }
        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
            throw new InvalidOperationException($"View '{view.Name}' cannot be placed on sheet {sheet.SheetNumber} (it may already be on a sheet).");
        var vp = Viewport.Create(doc, sheet.Id, view.Id, at);
        return new { id = vp.Id.Value, sheet = sheet.SheetNumber, view = view.Name };
    }

    /// <summary>Ready field lists for the architect's usual schedules (fields the category lacks are skipped and listed).</summary>
    private static readonly Dictionary<string, (string cat, string[] fields, string sort)> Presets = new Dictionary<string, (string, string[], string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["doors"] = ("doors", new[] { "Mark", "Family and Type", "Level", "Width", "Height", "From Room: Number", "To Room: Number", "Fire Rating", "Comments" }, "Mark"),
        ["windows"] = ("windows", new[] { "Mark", "Family and Type", "Level", "Width", "Height", "Sill Height", "Head Height", "Comments" }, "Mark"),
        ["rooms"] = ("rooms", new[] { "Number", "Name", "Level", "Area", "Perimeter", "Unbounded Height", "Department", "Occupancy" }, "Number"),
        ["room_finishes"] = ("rooms", new[] { "Number", "Name", "Level", "Floor Finish", "Base Finish", "Wall Finish", "Ceiling Finish", "Area" }, "Number"),
    };

    private static object ScheduleCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        (string cat, string[] fields, string sort)? preset = null;
        if (Has(p, "preset"))
        {
            string pn = NeedStr(p, "preset");
            if (!Presets.TryGetValue(pn, out var pr)) throw new ArgumentException($"'preset' must be one of: {string.Join(", ", Presets.Keys)}.");
            preset = pr;
            if (Has(p, "category") && Find.Cat(doc, NeedStr(p, "category")) != Find.Cat(doc, pr.cat))
                throw new ArgumentException($"preset '{pn}' schedules {pr.cat}; leave 'category' out.");
        }
        else if (!Has(p, "category")) throw new ArgumentException("Give 'category' (with 'fields') or a 'preset'.");
        var cat = Find.Cat(doc, preset?.cat ?? NeedStr(p, "category"));
        var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(cat));
        Name(schedule, Str(p, "name"));

        var def = schedule.Definition;
        var available = new Dictionary<string, SchedulableField>(StringComparer.OrdinalIgnoreCase);
        foreach (var sf in def.GetSchedulableFields())
        {
            string fieldName = sf.GetName(doc);
            if (!string.IsNullOrEmpty(fieldName) && !available.ContainsKey(fieldName)) available[fieldName] = sf;
        }
        var added = new List<string>(); var missing = new List<string>();
        var ids = new Dictionary<string, ScheduleFieldId>(StringComparer.OrdinalIgnoreCase);
        var wanted = Has(p, "fields") || preset == null ? StrList(p, "fields", 60) : preset.Value.fields.ToList();
        foreach (string field in wanted)
        {
            if (!available.TryGetValue(field, out var sf))
            {
                if (preset != null && !Has(p, "fields")) { missing.Add(field); continue; }
                throw new ArgumentException($"No schedulable field '{field}' for {cat}. Available: {string.Join(", ", available.Keys.OrderBy(k => k).Take(120))}");
            }
            ids[field] = def.AddField(sf).FieldId;
            added.Add(field);
        }
        if (added.Count == 0) throw new ArgumentException($"None of the fields exist for {cat}.");
        string sortBy = Str(p, "sort_by") ?? preset?.sort;
        if (sortBy != null)
        {
            if (!ids.TryGetValue(sortBy, out var fid)) throw new ArgumentException($"'sort_by' must be one of the schedule's fields: {string.Join(", ", added)}.");
            def.AddSortGroupField(new ScheduleSortGroupField(fid, ScheduleSortOrder.Ascending));
        }
        return new { id = schedule.Id.Value, name = schedule.Name, fields = added, sortedBy = sortBy, skippedFields = missing };
    }

    private static object TextNoteCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        ElementId type = Has(p, "type")
            ? Find.TypeOf(doc, Str(p, "type"), null, typeof(TextNoteType)).Id
            : doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        var note = TextNote.Create(doc, view.Id, U.NeedPt(p, "position"), NeedStr(p, "text"), type);
        return new { id = note.Id.Value, view = view.Name };
    }

    private static object ModelLinesCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var ids = new List<long>();
        var lines = ObjList(p, "lines", 5000);
        for (int i = 0; i < lines.Count; i++)
        {
            var curve = U.Curve(lines[i], $"line {i + 1}", 0);
            // The sketch plane must contain the curve: an arc's own plane; for a line, the vertical plane through it
            // (the YZ plane for a vertical line).
            Plane plane;
            if (curve is Arc arc) plane = Plane.CreateByNormalAndOrigin(arc.Normal, arc.Center);
            else
            {
                var dir = ((Line)curve).Direction;
                var normal = Math.Abs(dir.Z) > 0.999999 ? XYZ.BasisX : dir.CrossProduct(XYZ.BasisZ).Normalize();
                plane = Plane.CreateByNormalAndOrigin(normal, curve.GetEndPoint(0));
            }
            ids.Add(doc.Create.NewModelCurve(curve, SketchPlane.Create(doc, plane)).Id.Value);
        }
        return new { created = ids.Count, ids };
    }
}
