namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 8, modify and documentation depth: flip, end joins and extensions, face / work-plane hosted families,
/// model groups and assemblies (with assembly views), schedule filters / sorting / grouping / formatting, rotated, angular
/// and radial dimensions, spot coordinates. Nothing here designs: every value placed is the one given.
/// </summary>
internal static class DepthOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["flip"] = Flip;
        ops["end_join"] = EndJoin;
        ops["family_place_hosted"] = FamilyPlaceHosted;
        ops["group_create"] = GroupCreate;
        ops["group_place"] = GroupPlace;
        ops["ungroup"] = Ungroup;
        ops["assembly_create"] = AssemblyCreate;
        ops["assembly_views"] = AssemblyViews;
        ops["schedule_set"] = ScheduleSet;
        ops["dimension_rotated"] = DimensionRotated;
        ops["dimension_angular"] = DimensionAngular;
        ops["dimension_radial"] = DimensionRadial;
        ops["spot_coordinate"] = SpotCoordinate;
    }

    // ---------------------------------------------------------------- flip

    private static object Flip(EditContext c, Obj p)
    {
        if (!Has(p, "what")) throw new ArgumentException("flip needs 'what': facing | hand | work_plane (family instances) or wall (wall orientation).");
        string what = Choice(p, "what", "facing", "facing", "hand", "work_plane", "wall");
        var flipped = new List<long>();
        var skipped = new List<object>();
        foreach (var e in c.Elements(p))
        {
            if (what == "wall")
            {
                if (e is Wall w) { w.Flip(); flipped.Add(e.Id.Value); }
                else skipped.Add(new { id = e.Id.Value, reason = "not a wall" });
                continue;
            }
            if (!(e is FamilyInstance fi)) { skipped.Add(new { id = e.Id.Value, reason = "not a family instance (walls: what = wall)" }); continue; }
            bool ok;
            switch (what)
            {
                case "facing": ok = fi.CanFlipFacing && fi.flipFacing(); break;
                case "hand": ok = fi.CanFlipHand && fi.flipHand(); break;
                default:
                    ok = fi.CanFlipWorkPlane;
                    if (ok) fi.IsWorkPlaneFlipped = !fi.IsWorkPlaneFlipped;
                    break;
            }
            if (ok) flipped.Add(e.Id.Value);
            else skipped.Add(new { id = e.Id.Value, reason = $"the family does not allow flipping {what.Replace('_', ' ')}" });
        }
        if (flipped.Count == 0) throw new ArgumentException("Nothing was flipped: " + string.Join("; ", skipped.Take(5)));
        return new { what, flipped = flipped.Count, ids = flipped, skipped };
    }

    // ---------------------------------------------------------------- end joins and extensions

    private static object EndJoin(EditContext c, Obj p)
    {
        if (!Has(p, "end")) throw new ArgumentException("end_join needs 'end': start | end | both.");
        string end = Choice(p, "end", "both", "start", "end", "both");
        bool? allow = Bool(p, "allow");
        double? extension = Has(p, "extension") ? U.In(NeedDbl(p, "extension")) : (double?)null;
        if (allow == null && extension == null) throw new ArgumentException("end_join needs 'allow' (true / false) and / or 'extension' (framing only).");
        var ends = end == "both" ? new[] { 0, 1 } : end == "start" ? new[] { 0 } : new[] { 1 };
        var rows = new List<object>();
        var skipped = new List<object>();
        foreach (var e in c.Elements(p))
        {
            if (e is Wall w)
            {
                if (extension != null) skipped.Add(new { id = e.Id.Value, reason = "extension applies to structural framing only" });
                if (allow == null) continue;
                var before = ends.Select(i => WallUtils.IsWallJoinAllowedAtEnd(w, i)).ToList();
                foreach (int i in ends)
                    if (allow.Value) WallUtils.AllowWallJoinAtEnd(w, i); else WallUtils.DisallowWallJoinAtEnd(w, i);
                rows.Add(new { id = e.Id.Value, kind = "wall", ends = ends.Select(i => i == 0 ? "start" : "end").ToList(), joinAllowedBefore = before, joinAllowed = allow });
            }
            else if (e is FamilyInstance fi && fi.StructuralType == RStruct.StructuralType.Beam)
            {
                var row = new Obj { ["id"] = e.Id.Value, ["kind"] = "framing", ["ends"] = ends.Select(i => i == 0 ? "start" : "end").ToList() };
                if (allow != null)
                {
                    row["joinAllowedBefore"] = ends.Select(i => RStruct.StructuralFramingUtils.IsJoinAllowedAtEnd(fi, i)).ToList();
                    foreach (int i in ends)
                        if (allow.Value) RStruct.StructuralFramingUtils.AllowJoinAtEnd(fi, i); else RStruct.StructuralFramingUtils.DisallowJoinAtEnd(fi, i);
                    row["joinAllowed"] = allow;
                }
                if (extension != null)
                {
                    foreach (int i in ends) c.SetBuiltin(fi, i == 0 ? "START_EXTENSION" : "END_EXTENSION", extension.Value);
                    row["extension"] = U.Out(extension.Value);
                }
                rows.Add(row);
            }
            else skipped.Add(new { id = e.Id.Value, reason = "not a wall or structural framing" });
        }
        if (rows.Count == 0) throw new ArgumentException("No wall or structural framing among the ids: " + string.Join("; ", skipped.Take(5)));
        return new { changed = rows.Count, elements = rows, skipped,
                     note = extension != null ? "Extension is the length you gave, added beyond the chosen end(s); a join at that end may override it." : null };
    }

    // ---------------------------------------------------------------- face- and work-plane-based families

    private static object FamilyPlaceHosted(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var cat = Find.Cat(doc, NeedStr(p, "category"));
        var symbol = Find.Symbol(doc, NeedStr(p, "type"), cat);
        var placement = symbol.Family.FamilyPlacementType;
        if (placement != FamilyPlacementType.WorkPlaneBased)
            throw new ArgumentException($"'{Find.TypeLabel(symbol)}' is a {placement} family, not face- or work-plane-based; place it with "
                                        + (placement == FamilyPlacementType.OneLevelBased ? "family_place" : "the operation for its kind") + ".");
        bool onHost = Has(p, "host"), onPlane = Has(p, "work_plane");
        if (onHost == onPlane) throw new ArgumentException("Give either 'host' (an element whose face carries the family) or 'work_plane' (a level or reference plane name).");
        XYZ dir = Has(p, "direction") ? U.NeedPt(p, "direction") : XYZ.BasisX;
        if (dir.GetLength() < 1e-9) throw new ArgumentException("'direction' must not be zero.");
        dir = dir.Normalize();
        var placed = new List<object>();
        var pts = U.PtList(p, "points", 1, 2000);

        if (onPlane)
        {
            string name = NeedRef(p, "work_plane");
            Reference plane; XYZ origin, normal;
            var level = Find.Levels(doc).FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (level != null) { plane = level.GetPlaneReference(); origin = new XYZ(0, 0, level.ProjectElevation); normal = XYZ.BasisZ; }
            else
            {
                var rp = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No level or named reference plane '{name}'. Levels: {string.Join(", ", Find.Levels(doc).Select(l => l.Name))}.");
                plane = rp.GetReference(); origin = rp.BubbleEnd; normal = rp.Normal;
            }
            if (Math.Abs(dir.DotProduct(normal)) > 0.999) throw new ArgumentException("'direction' must lie in the work plane, not along its normal.");
            foreach (var pt in pts)
            {
                var at = pt - normal * normal.DotProduct(pt - origin);      // onto the plane
                var inst = doc.Create.NewFamilyInstance(plane, at, dir, symbol);
                placed.Add(new { id = inst.Id.Value, point = U.Xyz(at) });
            }
            return new { type = Find.TypeLabel(symbol), workPlane = name, placed = placed.Count, instances = placed };
        }

        var host = Find.Elem(doc, NeedId(p, "host"));
        string which = Choice(p, "face", "nearest", "nearest", "top", "bottom", "side");
        var faces = DocOps.Faces(host, null).Where(f =>
            which == "top" ? f.normal.Z > 0.99 : which == "bottom" ? f.normal.Z < -0.99 : which == "side" ? Math.Abs(f.normal.Z) < 0.01 : true).ToList();
        if (faces.Count == 0) throw new ArgumentException($"Element {host.Id.Value} has no planar {(which == "nearest" ? "" : which + " ")}face with a reference.");
        var box = host.get_BoundingBox(null);
        double tol = U.Mm(1);
        foreach (var pt in pts)
        {
            // the face nearest to the point whose plane passes through the element's box at the projected point
            var best = faces.Select(f => (f, at: pt - f.normal * f.normal.DotProduct(pt - f.origin)))
                .Where(x => box == null || (x.at.X >= box.Min.X - tol && x.at.X <= box.Max.X + tol && x.at.Y >= box.Min.Y - tol
                                            && x.at.Y <= box.Max.Y + tol && x.at.Z >= box.Min.Z - tol && x.at.Z <= box.Max.Z + tol))
                .OrderBy(x => x.at.DistanceTo(pt)).FirstOrDefault();
            if (best.f.reference == null) throw new ArgumentException($"Point {string.Join(", ", U.Xyz(pt))} projects onto no {which} face of element {host.Id.Value}.");
            var d = dir - best.f.normal * dir.DotProduct(best.f.normal);
            if (d.GetLength() < 1e-6) d = best.f.normal.CrossProduct(XYZ.BasisZ).GetLength() > 1e-6 ? best.f.normal.CrossProduct(XYZ.BasisZ) : XYZ.BasisX;
            var inst = doc.Create.NewFamilyInstance(best.f.reference, best.at, d.Normalize(), symbol);
            placed.Add(new { id = inst.Id.Value, point = U.Xyz(best.at), faceNormal = new[] { Math.Round(best.f.normal.X, 4), Math.Round(best.f.normal.Y, 4), Math.Round(best.f.normal.Z, 4) } });
        }
        return new { type = Find.TypeLabel(symbol), host = host.Id.Value, face = which, placed = placed.Count, instances = placed };
    }

    // ---------------------------------------------------------------- groups

    private static GroupType GroupTypeOf(Document doc, string spec)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
        return all.FirstOrDefault(g => g.Name.Equals(spec.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? (long.TryParse(spec, out long n) ? all.FirstOrDefault(g => g.Id.Value == n) : null)
               ?? throw new ArgumentException($"No group type '{spec}'. Group types: {string.Join(", ", all.Select(g => g.Name).OrderBy(x => x).Take(40))}. Use list_groups.");
    }

    private static object GroupCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var ids = c.Elements(p).Select(e => e.Id).ToList();
        var g = doc.Create.NewGroup(ids);
        if (Str(p, "name") is string name)
        {
            if (new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"A group type named '{name}' already exists.");
            g.GroupType.Name = name;
        }
        return new { id = g.Id.Value, groupType = g.GroupType.Name, groupTypeId = g.GroupType.Id.Value, members = g.GetMemberIds().Count };
    }

    private static object GroupPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var type = GroupTypeOf(doc, NeedRef(p, "group"));
        if (type.Category?.Id.Value == (long)BuiltInCategory.OST_IOSDetailGroups)
            throw new ArgumentException($"'{type.Name}' is a detail group: use detail_group_place.");
        var ids = U.PtList(p, "points", 1, 2000).Select(pt => doc.Create.PlaceGroup(pt, type).Id.Value).ToList();
        return new { groupType = type.Name, placed = ids.Count, ids };
    }

    private static object Ungroup(EditContext c, Obj p)
    {
        var rows = new List<object>();
        foreach (var e in c.Elements(p))
        {
            if (!(e is Group g)) throw new ArgumentException($"Element {e.Id.Value} is not a group instance.");
            string type = g.GroupType.Name;
            var members = g.UngroupMembers();
            rows.Add(new { group = e.Id.Value, groupType = type, members = members.Count });
        }
        return new { ungrouped = rows.Count, groups = rows, note = "The group types stay in the model (purge removes unused ones)." };
    }

    // ---------------------------------------------------------------- assemblies

    internal static AssemblyInstance AssemblyOf(Document doc, string spec)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>().ToList();
        return (long.TryParse(spec, out long n) ? all.FirstOrDefault(a => a.Id.Value == n) : null)
               ?? all.Where(a => a.AssemblyTypeName.Equals(spec.Trim(), StringComparison.OrdinalIgnoreCase)).ToList() switch
               {
                   var one when one.Count == 1 => one[0],
                   var many when many.Count > 1 => throw new ArgumentException($"Assembly '{spec}' is placed {many.Count} times; give the instance id: {string.Join(", ", many.Select(a => a.Id.Value))}."),
                   _ => throw new ArgumentException($"No assembly '{spec}'. Assemblies: {string.Join(", ", all.Select(a => $"{a.Id.Value} = {a.AssemblyTypeName}").Take(30))}. Use list_assemblies."),
               };
    }

    private static object AssemblyCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var elements = c.Elements(p);
        var ids = elements.Select(e => e.Id).ToList();
        ElementId naming = Has(p, "naming_category") ? new ElementId(Find.Cat(doc, NeedStr(p, "naming_category"))) : elements[0].Category?.Id
                           ?? throw new ArgumentException("The first element has no category; give 'naming_category'.");
        if (!AssemblyInstance.IsValidNamingCategory(doc, naming, ids))
            throw new ArgumentException("'naming_category' must be the category of one of the elements.");
        if (!AssemblyInstance.AreElementsValidForAssembly(doc, ids, ElementId.InvalidElementId))
            throw new ArgumentException("These elements cannot form an assembly (already in another assembly, or a category Revit does not allow).");
        var a = AssemblyInstance.Create(doc, ids, naming);
        // Revit creates the assembly type when the transaction commits: the name is set right after, in its own transaction.
        string name = Str(p, "name");
        if (name != null)
            c.Defer(() =>
            {
                using (var t = new Transaction(doc, "Claude: assembly name"))
                {
                    t.Start();
                    a.AssemblyTypeName = name;
                    t.Commit();
                }
            });
        return new { id = a.Id.Value, name, members = ids.Count,
                     note = "The name is applied after the commit (Revit creates the assembly type then); a dry run does not show it. "
                            + "Create views with assembly_views in a later edit." };
    }

    private static readonly string[] Orientations =
        { "ElevationTop", "ElevationBottom", "ElevationLeft", "ElevationRight", "ElevationFront", "ElevationBack", "DetailSectionA", "DetailSectionB", "HorizontalDetail" };

    private static object AssemblyViews(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var a = AssemblyOf(doc, NeedRef(p, "assembly"));
        if (!a.AllowsAssemblyViewCreation()) throw new InvalidOperationException($"Assembly '{a.AssemblyTypeName}' does not allow views (a newly created assembly needs the batch to commit first).");
        var want = StrList(p, "views", 20);
        var made = new List<object>();
        var created = new List<ElementId>();
        foreach (string v in want)
        {
            string k = v.Trim();
            View view;
            if (k.Equals("3d", StringComparison.OrdinalIgnoreCase)) view = AssemblyViewUtils.Create3DOrthographic(doc, a.Id);
            else if (k.Equals("parts_list", StringComparison.OrdinalIgnoreCase)) view = AssemblyViewUtils.CreatePartList(doc, a.Id);
            else if (k.Equals("material_takeoff", StringComparison.OrdinalIgnoreCase)) view = AssemblyViewUtils.CreateMaterialTakeoff(doc, a.Id);
            else
            {
                string name = Orientations.FirstOrDefault(o => o.Equals(k, StringComparison.OrdinalIgnoreCase) || o.Equals("Elevation" + k, StringComparison.OrdinalIgnoreCase))
                              ?? throw new ArgumentException($"Unknown assembly view '{v}'. Use 3d, parts_list, material_takeoff, top, bottom, left, right, front, back, DetailSectionA, DetailSectionB, HorizontalDetail.");
                view = AssemblyViewUtils.CreateDetailSection(doc, a.Id, (AssemblyDetailViewOrientation)Enum.Parse(typeof(AssemblyDetailViewOrientation), name));
            }
            created.Add(view.Id);
            made.Add(new { kind = k, id = view.Id.Value, name = view.Name });
        }
        object sheet = null;
        if (Has(p, "sheet_title_block"))
        {
            var tb = Find.TypeOf(doc, NeedStr(p, "sheet_title_block"), BuiltInCategory.OST_TitleBlocks);
            var s = AssemblyViewUtils.CreateSheet(doc, a.Id, tb.Id);
            sheet = new { id = s.Id.Value, number = s.SheetNumber, name = s.Name };
        }
        return new { assembly = a.AssemblyTypeName, views = made, sheet,
                     note = sheet != null ? "The views are created; place them on the sheet with viewport_place or sheet_layout." : null };
    }

    // ---------------------------------------------------------------- schedules

    private static ScheduleField FieldOf(ScheduleDefinition def, string name)
    {
        var all = Enumerable.Range(0, def.GetFieldCount()).Select(i => def.GetField(i)).ToList();
        return all.FirstOrDefault(f => f.GetName().Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(f => (f.ColumnHeading ?? "").Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"The schedule has no field '{name}'. Fields: {string.Join(", ", all.Select(f => f.GetName()))}.");
    }

    private static readonly Dictionary<string, string> FilterOps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["equals"] = "Equal", ["not_equals"] = "NotEqual", ["greater"] = "GreaterThan", ["greater_or_equal"] = "GreaterThanOrEqual",
        ["less"] = "LessThan", ["less_or_equal"] = "LessThanOrEqual", ["contains"] = "Contains", ["not_contains"] = "NotContains",
        ["begins_with"] = "BeginsWith", ["not_begins_with"] = "NotBeginsWith", ["ends_with"] = "EndsWith", ["not_ends_with"] = "NotEndsWith",
        ["has_value"] = "HasValue", ["has_no_value"] = "HasNoValue",
    };

    private static bool Is(ForgeTypeId a, ForgeTypeId b) => a != null && b != null && a.TypeId == b.TypeId;

    private static ScheduleFilter FilterFor(Document doc, ScheduleDefinition def, Obj f, int index)
    {
        string what = $"filters[{index}]";
        var field = FieldOf(def, NeedStr(f, "field"));
        string op = Str(f, "op") ?? "equals";
        if (!FilterOps.TryGetValue(op, out string typeName)) throw new ArgumentException($"{what}: unknown op '{op}'. Use: {string.Join(", ", FilterOps.Keys)}.");
        var type = (ScheduleFilterType)Enum.Parse(typeof(ScheduleFilterType), typeName);
        if (!def.CanFilter()) throw new ArgumentException("This schedule cannot be filtered.");
        if (op == "has_value" || op == "has_no_value") return new ScheduleFilter(field.FieldId, type);
        if (!Has(f, "value")) throw new ArgumentException($"{what}: 'value' is required for op '{op}'.");
        object v = f["value"];
        ForgeTypeId spec = null;
        try { spec = field.GetSpecTypeId(); } catch { /* not a parameter field */ }
        if (v is double d)
        {
            if (Is(spec, SpecTypeId.Int.Integer)) return new ScheduleFilter(field.FieldId, type, IntegerValue(d, what));
            double inner = U.InSpec(d, spec, U.ProjectUnit(doc, spec));
            return new ScheduleFilter(field.FieldId, type, inner);
        }
        if (v is bool b) return new ScheduleFilter(field.FieldId, type, b ? 1 : 0);
        string text = Text(v);
        if (spec != null && UnitUtils.IsMeasurableSpec(spec))
            throw new ArgumentException($"{what}: field '{field.GetName()}' is a number; give 'value' as a number (lengths in request units, m2, m3, degrees).");
        return new ScheduleFilter(field.FieldId, type, text);
    }

    private static ForgeTypeId UnitOf(string unit) => (unit ?? "").Trim().ToLowerInvariant() switch
    {
        "mm" => UnitTypeId.Millimeters, "cm" => UnitTypeId.Centimeters, "m" => UnitTypeId.Meters,
        "m2" => UnitTypeId.SquareMeters, "mm2" => UnitTypeId.SquareMillimeters, "cm2" => UnitTypeId.SquareCentimeters,
        "m3" => UnitTypeId.CubicMeters, "cm3" => UnitTypeId.CubicCentimeters, "ft" => UnitTypeId.Feet, "in" => UnitTypeId.Inches,
        "deg" => UnitTypeId.Degrees, "kg" => UnitTypeId.Kilograms,
        _ => throw new ArgumentException($"Unknown unit '{unit}'. Use mm, cm, m, m2, mm2, cm2, m3, cm3, ft, in, deg, kg."),
    };

    private static object ScheduleSet(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var schedule = Find.ViewOf(doc, NeedRef(p, "schedule")) as ViewSchedule
                       ?? throw new ArgumentException("'schedule' is not a schedule. Use list_schedules.");
        var def = schedule.Definition;
        var done = new List<string>();

        if (Has(p, "filters"))
        {
            var list = ObjList(p, "filters", 8, required: false);
            var filters = list.Select((f, i) => FilterFor(doc, def, f, i)).ToList();
            def.ClearFilters();
            foreach (var f in filters) def.AddFilter(f);
            done.Add($"filters: {filters.Count} (replaced)");
        }
        if (Has(p, "sort"))
        {
            var list = ObjList(p, "sort", 8, required: false);
            var fields = new List<ScheduleSortGroupField>();
            foreach (var s in list)
            {
                var field = FieldOf(def, NeedStr(s, "field"));
                if (!def.CanSortByField(field.FieldId)) throw new ArgumentException($"Revit cannot sort by '{field.GetName()}'.");
                var order = Choice(s, "order", "ascending", "ascending", "descending") == "descending" ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending;
                var sg = new ScheduleSortGroupField(field.FieldId, order) { ShowHeader = Flag(s, "header", false), ShowBlankLine = Flag(s, "blank_line", false) };
                // Revit refuses footer options (even false) while footers are off.
                if (Flag(s, "footer", false)) { sg.ShowFooter = true; sg.ShowFooterCount = Flag(s, "footer_count", false); }
                else if (Flag(s, "footer_count", false)) throw new ArgumentException($"sort '{field.GetName()}': footer_count needs footer: true.");
                fields.Add(sg);
            }
            def.ClearSortGroupFields();
            foreach (var f in fields) def.AddSortGroupField(f);
            done.Add($"sort / group: {fields.Count} (replaced)");
        }
        if (Bool(p, "itemize") is bool itemize) { def.IsItemized = itemize; done.Add($"itemize: {itemize}"); }
        if (Bool(p, "grand_total") is bool total) { def.ShowGrandTotal = total; done.Add($"grand total: {total}"); }
        if (Bool(p, "show_title") is bool title) { def.ShowTitle = title; done.Add($"title: {title}"); }
        if (Bool(p, "show_headers") is bool headers) { def.ShowHeaders = headers; done.Add($"headers: {headers}"); }

        if (Has(p, "fields"))
        {
            var spec = Sub(p, "fields");
            foreach (var kv in spec)
            {
                var field = FieldOf(def, kv.Key);
                if (!(kv.Value is Obj f)) throw new ArgumentException($"fields.{kv.Key} must be an object {{heading?, hidden?, align?, total?, unit?, decimals?}}.");
                var changes = new List<string>();
                if (Str(f, "heading") is string h) { field.ColumnHeading = h; changes.Add("heading"); }
                if (Bool(f, "hidden") is bool hid) { field.IsHidden = hid; changes.Add(hid ? "hidden" : "shown"); }
                if (Str(f, "align") is string al)
                {
                    field.HorizontalAlignment = Choice(f, "align", "left", "left", "center", "right") switch
                    {
                        "center" => ScheduleHorizontalAlignment.Center, "right" => ScheduleHorizontalAlignment.Right, _ => ScheduleHorizontalAlignment.Left,
                    };
                    changes.Add("align " + al);
                }
                if (Bool(f, "total") is bool tot)
                {
                    if (tot && !field.CanTotal()) throw new ArgumentException($"Revit cannot total the field '{field.GetName()}'.");
                    field.DisplayType = tot ? ScheduleFieldDisplayType.Totals : ScheduleFieldDisplayType.Standard;
                    changes.Add(tot ? "totals" : "no totals");
                }
                if (Has(f, "unit") || Has(f, "decimals"))
                {
                    var current = field.GetFormatOptions();
                    ForgeTypeId unit = Has(f, "unit") ? UnitOf(NeedStr(f, "unit"))
                        : (!current.UseDefault ? current.GetUnitTypeId() : doc.GetUnits().GetFormatOptions(field.GetSpecTypeId()).GetUnitTypeId());
                    var fo = new FormatOptions(unit);
                    if (Has(f, "decimals")) fo.Accuracy = Math.Pow(10, -Int(f, "decimals", 2, 0, 8));
                    field.SetFormatOptions(fo);
                    changes.Add("format");
                }
                done.Add($"{field.GetName()}: {string.Join(", ", changes)}");
            }
        }
        if (done.Count == 0) throw new ArgumentException("schedule_set needs at least one of: filters, sort, itemize, grand_total, show_title, show_headers, fields.");
        return new { schedule = schedule.Name, changed = done };
    }

    // ---------------------------------------------------------------- dimensions

    /// <summary>A linear dimension chain along any plan direction: outer faces of each element square to it, plus grids across it.</summary>
    private static object DimensionRotated(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        if (!(view is ViewPlan)) throw new ArgumentException("dimension_rotated works in plan views ('view').");
        double angle = U.Rad(NeedDbl(p, "angle"));
        var d = new XYZ(Math.Cos(angle), Math.Sin(angle), 0);
        var across = XYZ.BasisZ.CrossProduct(d);
        double z = view.GenLevel?.ProjectElevation ?? 0;
        var elements = c.Elements(p);
        var stations = new List<(double at, Reference r)>();
        double maxAcross = double.MinValue, minAcross = double.MaxValue;
        foreach (var e in elements)
        {
            var faces = DocOps.Faces(e, view).Where(f => Math.Abs(f.normal.DotProduct(d)) > 0.9999).ToList();
            if (faces.Count > 0)
            {
                var lo = faces.OrderBy(f => f.origin.DotProduct(d)).First();
                var hi = faces.OrderBy(f => f.origin.DotProduct(d)).Last();
                stations.Add((lo.origin.DotProduct(d), lo.reference));
                if (hi.origin.DotProduct(d) - lo.origin.DotProduct(d) > 0.003) stations.Add((hi.origin.DotProduct(d), hi.reference));
            }
            var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            if (bb != null)
                foreach (var corner in new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) })
                {
                    maxAcross = Math.Max(maxAcross, corner.DotProduct(across));
                    minAcross = Math.Min(minAcross, corner.DotProduct(across));
                }
        }
        if (Flag(p, "include_grids", true))
            foreach (var g in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
                if (g.Curve is Line gl && Math.Abs(gl.Direction.DotProduct(d)) < 0.0001)
                    stations.Add((gl.Origin.DotProduct(d), new Reference(g)));
        var refs = new ReferenceArray();
        double first = double.MaxValue, last = double.MinValue, prev = double.MinValue;
        foreach (var st in stations.OrderBy(s => s.at))
        {
            if (st.at - prev < 0.003) continue;
            refs.Append(st.r); prev = st.at;
            first = Math.Min(first, st.at); last = Math.Max(last, st.at);
        }
        if (refs.Size < 2)
            throw new ArgumentException($"Found fewer than two faces square to {NeedDbl(p, "angle")} degrees (or grids across it); check the angle against the elements' orientation.");
        double line = maxAcross + U.LenMm(p, "offset", 1500);
        XYZ a = d * first + across * line + XYZ.BasisZ * z, b = d * last + across * line + XYZ.BasisZ * z;
        var dim = doc.Create.NewDimension(view, Line.CreateBound(a, b), refs);
        return new { id = dim.Id.Value, view = view.Name, angle = NeedDbl(p, "angle"), references = refs.Size, length = U.Out(last - first),
                     note = "offset defaults to 1500 mm beyond the elements (a drawing convention)." };
    }

    /// <summary>A straight line element as (reference, line): grid, reference plane, model or detail line.</summary>
    private static (Reference r, Line line, string name) LinearRef(Document doc, string spec)
    {
        var grid = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().FirstOrDefault(g => g.Name.Equals(spec, StringComparison.OrdinalIgnoreCase));
        Element e = grid ?? (long.TryParse(spec, out long n) ? doc.GetElement(new ElementId(n)) : null)
                    ?? new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().FirstOrDefault(r => r.Name.Equals(spec, StringComparison.OrdinalIgnoreCase));
        switch (e)
        {
            case Grid g when g.Curve is Line gl: return (new Reference(g), gl, "grid " + g.Name);
            case ReferencePlane rp: return (rp.GetReference(), Line.CreateBound(rp.BubbleEnd, rp.FreeEnd), "reference plane " + rp.Name);
            case CurveElement ce when ce.GeometryCurve is Line cl: return (cl.Reference, cl, "line " + ce.Id.Value);
            case null: throw new ArgumentException($"No grid, reference plane or line '{spec}'.");
            default: throw new ArgumentException($"'{spec}' is not a straight grid, reference plane or model / detail line.");
        }
    }

    private static DimensionType DimTypeOf(Document doc, Obj p, DimensionStyleType style)
    {
        if (Has(p, "type")) return (DimensionType)Find.TypeOf(doc, NeedStr(p, "type"), null, typeof(DimensionType));
        return new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>().FirstOrDefault(t => t.StyleType == style)
               ?? throw new ArgumentException($"The model has no {style} dimension type; give 'type'.");
    }

    private static object DimensionAngular(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        if (!(view is ViewPlan) && !(view is ViewDrafting)) throw new ArgumentException("dimension_angular works in plan and drafting views ('view').");
        var refsIn = StrList(p, "between", 2);
        if (refsIn.Count != 2) throw new ArgumentException("'between' needs exactly two grids / reference planes / line ids.");
        var r1 = LinearRef(doc, refsIn[0]); var r2 = LinearRef(doc, refsIn[1]);
        XYZ d1 = new XYZ(r1.line.Direction.X, r1.line.Direction.Y, 0).Normalize(), d2 = new XYZ(r2.line.Direction.X, r2.line.Direction.Y, 0).Normalize();
        double cross = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(cross) < 1e-6) throw new ArgumentException($"{r1.name} and {r2.name} are parallel: there is no angle to dimension.");
        // intersection in plan
        XYZ p1 = r1.line.Origin, p2 = r2.line.Origin;
        double t = ((p2.X - p1.X) * d2.Y - (p2.Y - p1.Y) * d2.X) / cross;
        double z = view.GenLevel?.ProjectElevation ?? r1.line.Origin.Z;
        var center = new XYZ(p1.X + d1.X * t, p1.Y + d1.Y * t, z);
        double radius = U.LenMm(p, "radius", 1000);
        double ang = Math.Atan2(cross, d1.DotProduct(d2));
        XYZ u1 = d1, u2 = d2;
        if (ang < 0) { u1 = d2; u2 = d1; ang = -ang; }
        var arc = Arc.Create(center, radius, 0, ang, u1, XYZ.BasisZ.CrossProduct(u1));
        var dim = AngularDimension.Create(doc, view, arc, new List<Reference> { r1.r, r2.r }, DimTypeOf(doc, p, DimensionStyleType.Angular));
        return new { id = dim.Id.Value, view = view.Name, between = new[] { r1.name, r2.name }, angleDeg = U.Deg(ang), center = U.Xyz(center),
                     radius = U.Out(radius), note = "radius defaults to 1000 mm (a drawing convention)." };
    }

    private static object DimensionRadial(EditContext c, Obj p)
    {
#if REVIT2024
        throw new InvalidOperationException("Revit 2024's API cannot create radial or diameter dimensions (RadialDimension.Create arrived later). Use Revit 2027, or place them by hand.");
#else
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        bool diameter = Flag(p, "diameter", false);
        var made = new List<object>(); var skipped = new List<object>();
        foreach (var e in c.Elements(p))
        {
            Reference r = null;
            if (e is Grid g && g.Curve is Arc) r = new Reference(g);
            else if (e is CurveElement ce && ce.GeometryCurve is Arc ca) r = ca.Reference;
            else
            {
                var geo = e.get_Geometry(new Options { ComputeReferences = true, View = view });
                if (geo != null)
                    foreach (var o in geo)
                        if (o is Solid s)
                            foreach (Face f in s.Faces)
                                if (f is CylindricalFace cf && cf.Reference != null) { r = cf.Reference; break; }
            }
            if (r == null) { skipped.Add(new { id = e.Id.Value, reason = "no arc or cylindrical face found" }); continue; }
            var dim = RadialDimension.Create(doc, view, r, diameter);
            made.Add(new { id = dim.Id.Value, element = e.Id.Value });
        }
        if (made.Count == 0) throw new ArgumentException("Nothing to dimension: " + string.Join("; ", skipped.Take(5)));
        return new { view = view.Name, kind = diameter ? "diameter" : "radius", created = made.Count, dimensions = made, skipped };
#endif
    }

    private static object SpotCoordinate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        bool leader = Flag(p, "leader", true);
        var made = new List<object>(); var skipped = new List<object>();
        foreach (var e in c.Elements(p))
        {
            var top = DocOps.Faces(e, view).Where(f => f.normal.Z > 0.999).OrderByDescending(f => f.origin.Z).FirstOrDefault();
            if (top.face == null) top = DocOps.Faces(e, null).Where(f => f.normal.Z > 0.999).OrderByDescending(f => f.origin.Z).FirstOrDefault();
            if (top.face == null) { skipped.Add(new { id = e.Id.Value, reason = "no exposed horizontal top face" }); continue; }
            XYZ origin = DocOps.PointOnFace(top.face, top.origin - top.face.Origin, Has(p, "point") ? U.NeedPt(p, "point") : null, e.Id.Value);
            XYZ bend = origin + new XYZ(U.Mm(600), U.Mm(600), 0), end = origin + new XYZ(U.Mm(1200), U.Mm(600), 0);
            var spot = doc.Create.NewSpotCoordinate(view, top.reference, origin, bend, end, origin, leader);
            made.Add(new { id = spot.Id.Value, element = e.Id.Value, point = U.Xyz(origin) });
        }
        if (made.Count == 0) throw new ArgumentException("No spot coordinate placed: " + string.Join("; ", skipped.Take(5)));
        return new { view = view.Name, created = made.Count, spots = made, skipped,
                     note = "Values shown follow the spot coordinate type (project base point or survey point)." };
    }
}
