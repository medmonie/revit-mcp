namespace RevitMcpBridge;

/// <summary>Sheet sets: one sheet per view, numbered and named from templates, view placed in the middle.</summary>
internal static class SheetOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["sheets_from_views"] = SheetsFromViews;
    }

    /// <summary>"S-101" -> next "S-102"; "A9" -> "A10"; keeps zero padding.</summary>
    internal static string Next(string number)
    {
        var m = System.Text.RegularExpressions.Regex.Match(number, @"^(.*?)(\d+)(\D*)$");
        if (!m.Success) return number + "-1";
        string digits = m.Groups[2].Value;
        long n = long.Parse(digits, CultureInfo.InvariantCulture) + 1;
        return m.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0') + m.Groups[3].Value;
    }

    private static object SheetsFromViews(EditContext c, Obj p)
    {
        var doc = c.Doc;
        // Which views: by name, or every view of a type whose name contains a text (templates and sheets never).
        var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && !(v is ViewSheet) && !(v is ViewSchedule) && v.ViewType != ViewType.ProjectBrowser
                        && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Internal).ToList();
        List<View> views;
        if (Has(p, "views"))
            views = StrList(p, "views", 500).Select(n => Find.ViewOf(doc, n)).ToList();
        else
        {
            string vt = Str(p, "view_type"); string contains = Str(p, "name_contains");
            var excludes = StrList(p, "exclude_contains", 50, required: false);
            if (vt == null && contains == null) throw new ArgumentException("Give 'views' (names), or 'view_type' and / or 'name_contains'.");
            views = all.Where(v => (vt == null || v.ViewType.ToString().Equals(vt, StringComparison.OrdinalIgnoreCase)
                                                || (vt.Equals("plan", StringComparison.OrdinalIgnoreCase) && v is ViewPlan))
                                   && (contains == null || v.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                                   && !excludes.Any(x => v.Name.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0))
                       .OrderBy(v => (v as ViewPlan)?.GenLevel?.ProjectElevation ?? double.MaxValue).ThenBy(v => v.Name).ToList();
            if (views.Count == 0) throw new ArgumentException("No view matches 'view_type' / 'name_contains'. Use list_views.");
        }

        ElementId titleBlock = ElementId.InvalidElementId;
        string tb = Str(p, "title_block");
        if (tb == null)
        {
            // the title block used most on existing sheets, else the first one loaded
            var used = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .Cast<FamilyInstance>().GroupBy(f => f.Symbol.Id).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            titleBlock = used ?? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().FirstElementId();
            if (titleBlock == null || titleBlock == ElementId.InvalidElementId) { titleBlock = ElementId.InvalidElementId; c.Warn("the model has no title block; sheets are empty."); }
        }
        else titleBlock = Find.Symbol(doc, tb, BuiltInCategory.OST_TitleBlocks).Id;

        var numbers = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber),
                                          StringComparer.OrdinalIgnoreCase);
        string number = Str(p, "number") ?? "S-101";
        string nameTemplate = Str(p, "name_template") ?? "{view}";
        bool skipPlaced = Flag(p, "skip_placed", true);
        string template = Str(p, "view_template");
        View vtpl = template == null ? null : new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(v => v.IsTemplate && v.Name.Equals(template, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No view template named '{template}'.");

        // which views are on a sheet already: one pass over the viewports
        var placed = new Dictionary<long, Viewport>();
        foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()) placed[vp.ViewId.Value] = vp;
        var made = new List<object>(); var skipped = new List<object>();
        foreach (var v in views)
        {
            if (placed.TryGetValue(v.Id.Value, out var placedOn))
            {
                if (skipPlaced) { skipped.Add(new { view = v.Name, onSheet = (doc.GetElement(placedOn.SheetId) as ViewSheet)?.SheetNumber }); continue; }
                throw new InvalidOperationException($"View '{v.Name}' is already on a sheet (a view can be placed only once).");
            }
            while (numbers.Contains(number)) number = Next(number);
            var sheet = ViewSheet.Create(doc, titleBlock);
            sheet.SheetNumber = number; numbers.Add(number);
            string level = (v as ViewPlan)?.GenLevel?.Name ?? "";
            sheet.Name = nameTemplate.Replace("{view}", v.Name).Replace("{level}", level).Replace("{number}", number);
            if (vtpl != null) v.ViewTemplateId = vtpl.Id;
            if (Has(p, "scale") && Int(p, "scale", 100, 1, 100000) is int sc && v.ViewTemplateId == ElementId.InvalidElementId) v.Scale = sc;
            doc.Regenerate();
            var o = sheet.Outline;
            var at = new XYZ((o.Min.U + o.Max.U) / 2, (o.Min.V + o.Max.V) / 2, 0);
            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id)) throw new InvalidOperationException($"View '{v.Name}' cannot be placed on a sheet.");
            var vpt = Viewport.Create(doc, sheet.Id, v.Id, at);
            made.Add(new { sheet = sheet.SheetNumber, name = sheet.Name, view = v.Name, sheetId = sheet.Id.Value, viewportId = vpt.Id.Value });
            number = Next(number);
        }
        return new { created = made.Count, sheets = made, skippedAlreadyOnSheet = skipped.Count, skipped,
                     titleBlock = titleBlock == ElementId.InvalidElementId ? null : Find.TypeLabel((ElementType)doc.GetElement(titleBlock)) };
    }
}
