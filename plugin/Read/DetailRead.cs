namespace RevitMcpBridge;

/// <summary>What detailing can use: line styles, filled region types, detail components, detail groups, repeating
/// detail types, drafting views and legends, in the active model or in another open model (the source of a copy).</summary>
internal static class DetailRead
{
    private static readonly string[] Kinds =
        { "line_styles", "region_types", "detail_components", "detail_groups", "repeating_details", "drafting_views", "legends" };

    public static object ListDetailing(Rc rc, Obj p)
    {
        var doc = Str(p, "model") is string m ? DetailOps.OtherModel(rc.Doc, m) : rc.Doc;
        var kinds = Has(p, "kind") ? new List<string> { Choice(p, "kind", "line_styles", Kinds) } : Kinds.ToList();
        int limit = Int(p, "limit", 300, 1, 5000);
        var result = new Obj { ["model"] = doc.Title };
        foreach (var kind in kinds)
        {
            List<object> items;
            switch (kind)
            {
                case "line_styles":
                    items = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines).SubCategories.Cast<Category>()
                        .OrderBy(s => s.Name).Select(s => (object)new { name = s.Name, weight = s.GetLineWeight(GraphicsStyleType.Projection) }).ToList();
                    break;
                case "region_types":
                    items = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                        .OrderBy(t => t.Name).Select(t => (object)new
                        {
                            id = t.Id.Value, name = t.Name,
                            pattern = (doc.GetElement(t.ForegroundPatternId) as FillPatternElement)?.Name,
                        }).ToList();
                    break;
                case "detail_components":
                    items = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_DetailComponents)
                        .Cast<FamilySymbol>().OrderBy(Find.TypeLabel).Select(s => (object)new
                        {
                            id = s.Id.Value, type = Find.TypeLabel(s),
                            lineBased = s.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail,
                        }).ToList();
                    break;
                case "detail_groups":
                    items = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).OfCategory(BuiltInCategory.OST_IOSDetailGroups)
                        .Cast<GroupType>().OrderBy(g => g.Name).Select(g => (object)new { id = g.Id.Value, name = g.Name, placed = g.Groups.Size }).ToList();
                    break;
                case "repeating_details":
                    items = new FilteredElementCollector(doc).WhereElementIsElementType().OfCategory(BuiltInCategory.OST_RepeatingDetailLines)
                        .Cast<ElementType>().OrderBy(t => t.Name).Select(t => (object)new { id = t.Id.Value, name = t.Name }).ToList();
                    break;
                default:
                    var type = kind == "legends" ? ViewType.Legend : ViewType.DraftingView;
                    items = DetailOps.CopyableViews(doc).Where(v => v.ViewType == type)
                        .Select(v => (object)new { id = v.Id.Value, name = v.Name, scale = v.Scale }).ToList();
                    break;
            }
            result[kind] = items.Take(limit).ToList();
            if (items.Count > limit) result[kind + "Truncated"] = items.Count;
        }
        result["note"] = "Repeating details cannot be created through the Revit API; they are listed for reference.";
        return result;
    }

    private static readonly string[] DocKinds = { "view_templates", "filters", "revisions", "scope_boxes", "title_blocks" };

    /// <summary>What documentation work refers to by name: view templates, filters, revisions, scope boxes, title blocks.</summary>
    public static object ListDocumentation(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var kinds = Has(p, "kind") ? new List<string> { Choice(p, "kind", "view_templates", DocKinds) } : DocKinds.ToList();
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
        var result = new Obj();
        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case "view_templates":
                    result[kind] = views.Where(v => v.IsTemplate).OrderBy(v => v.Name).Select(t => (object)new
                    {
                        id = t.Id.Value, name = t.Name, viewType = t.ViewType.ToString(),
                        usedBy = views.Count(v => !v.IsTemplate && v.ViewTemplateId == t.Id),
                    }).ToList();
                    break;
                case "filters":
                    result[kind] = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>()
                        .OrderBy(f => f.Name).Select(f => (object)new
                        {
                            id = f.Id.Value, name = f.Name,
                            categories = f.GetCategories().Select(id => Category.GetCategory(doc, id)?.Name).ToList(),
                            usedIn = views.Count(v => { try { return v.GetFilters().Contains(f.Id); } catch { return false; } }),
                        }).ToList();
                    break;
                case "revisions":
                    result[kind] = Revision.GetAllRevisionIds(doc).Select(id => (Revision)doc.GetElement(id)).Select(r => (object)new
                    {
                        id = r.Id.Value, sequence = r.SequenceNumber, number = DocOps.NumberOf(r), description = r.Description,
                        date = r.RevisionDate, issued = r.Issued, issuedBy = r.IssuedBy, issuedTo = r.IssuedTo,
                    }).ToList();
                    break;
                case "scope_boxes":
                    result[kind] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                        .OrderBy(e => e.Name).Select(e => (object)new { id = e.Id.Value, name = e.Name }).ToList();
                    break;
                default:
                    result[kind] = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType()
                        .Cast<ElementType>().OrderBy(Find.TypeLabel).Select(t => (object)new { id = t.Id.Value, type = Find.TypeLabel(t) }).ToList();
                    break;
            }
        }
        return result;
    }
}
