namespace RevitMcpBridge;

/// <summary>Read actions about the model as a whole. Nothing here changes anything.</summary>
internal static class ModelRead
{
    public static object Ping(Rc rc, Obj p)
    {
        var app = rc.App.Application;
        var ui = rc.App.ActiveUIDocument;
        string modelKey = DocumentTarget.ModelKey(ui?.Document);
        string view = null;
        try { view = ui?.ActiveView?.Name; } catch { /* no view yet */ }
        return new
        {
            bridgeVersion = BridgeInfo.Version,
            revitVersion = app.VersionNumber,
            revitName = app.VersionName,
            revitBuild = app.VersionBuild,
            activeModel = ui?.Document.Title,
            documentId = DocumentTarget.SessionId(modelKey), modelKey,
            activeView = view,
            editingAllowed = WriteGate.Granted,
            units = "lengths are " + U.Name + " (set params.units to mm, cm, m, ft or in); areas m2, volumes m3, angles degrees",
        };
    }

    public static object GetProjectInfo(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        string modelKey = DocumentTarget.ModelKey(doc);
        var o = new Obj
        {
            ["title"] = doc.Title, ["path"] = doc.PathName, ["isFamilyDocument"] = doc.IsFamilyDocument,
            ["documentId"] = DocumentTarget.SessionId(modelKey), ["modelKey"] = modelKey,
            ["isWorkshared"] = doc.IsWorkshared, ["isReadOnly"] = doc.IsReadOnly, ["hasUnsavedChanges"] = doc.IsModified,
            ["activeView"] = doc.ActiveView?.Name,
        };
        try { o["projectLengthUnit"] = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId; } catch { /* optional */ }
        if (doc.IsFamilyDocument) return o;

        var pi = doc.ProjectInformation;
        if (pi != null)
            o["project"] = new Obj
            {
                ["name"] = pi.Name, ["number"] = pi.Number, ["client"] = pi.ClientName, ["address"] = pi.Address,
                ["status"] = pi.Status, ["building"] = pi.BuildingName, ["author"] = pi.Author,
            };
        o["phases"] = doc.Phases.Cast<Phase>().Select(x => x.Name).ToList();

        var counts = new Obj();
        foreach (var cat in Find.Structural.Concat(new[] { BuiltInCategory.OST_Rebar, BuiltInCategory.OST_Grids, BuiltInCategory.OST_Levels }))
            counts[cat.ToString().Substring(4)] = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().GetElementCount();
        o["counts"] = counts;
        o["openModels"] = rc.App.Application.Documents.Cast<Document>()
            .Select(d => new { title = d.Title, isLinked = d.IsLinked, isFamily = d.IsFamilyDocument }).ToList();
        return o;
    }

    public static object ListLevels(Rc rc, Obj p) => new
    {
        units = U.Name,
        levels = Find.Levels(rc.Doc).Select(l => new { id = l.Id.Value, name = l.Name,
            elevation = U.Out(l.ProjectElevation), displayElevation = U.Out(l.Elevation) }).ToList(),
    };

    public static object ListGrids(Rc rc, Obj p)
    {
        var grids = new FilteredElementCollector(rc.Doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var o = new Obj { ["id"] = g.Id.Value, ["name"] = g.Name };
                var c = g.Curve;
                if (c != null && c.IsBound) foreach (var kv in U.CurveOut(c)) o[kv.Key] = kv.Value;
                return o;
            });
        return Describe.Page(p, grids, "grids", 500);
    }

    private static readonly HashSet<ViewType> HiddenViewTypes = new HashSet<ViewType>
    {
        ViewType.ProjectBrowser, ViewType.SystemBrowser, ViewType.Internal, ViewType.Undefined, ViewType.DrawingSheet,
    };

    public static object ListViews(Rc rc, Obj p)
    {
        string type = Str(p, "type");
        var views = new FilteredElementCollector(rc.Doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && !HiddenViewTypes.Contains(v.ViewType))
            .Where(v => type == null || string.Equals(v.ViewType.ToString(), type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var o = new Obj { ["id"] = v.Id.Value, ["name"] = v.Name, ["type"] = v.ViewType.ToString() };
                try { if (v.GenLevel != null) o["level"] = v.GenLevel.Name; } catch { /* not a plan */ }
                try { if (v.Scale > 0) o["scale"] = v.Scale; } catch { /* schedules have no scale */ }
                return o;
            });
        return Describe.Page(p, views, "views", 200);
    }

    public static object ListSheets(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => !s.IsTemplate).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .Select(s => new
            {
                id = s.Id.Value, number = s.SheetNumber, name = s.Name,
                views = s.GetAllPlacedViews().Select(id => doc.GetElement(id)?.Name).ToList(),
            });
        return Describe.Page(p, sheets, "sheets", 500);
    }

    /// <summary>
    /// True for elements that are not physical model content even though Revit files them under a model category:
    /// sketches, materials and their assets, sun path, base points, project information, legend components.
    /// </summary>
    private static bool NotPhysical(Element e) =>
        e is Sketch || e is Material || e is AppearanceAssetElement || e is PropertySetElement || e is SunAndShadowSettings
        || e is BasePoint || e is ProjectInfo || e is ElementType || e.ViewSpecific
        || (e.Category?.Name ?? "").StartsWith("<", StringComparison.Ordinal)
        || e.GetType().Name == "InternalOrigin";

    public static object CountByCategory(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        bool all = Flag(p, "include_annotation", false);
        var col = Has(p, "view") ? new FilteredElementCollector(doc, Find.ViewOf(doc, Ref(p, "view")).Id) : new FilteredElementCollector(doc);
        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int cad = 0;
        foreach (Element e in col.WhereElementIsNotElementType())
        {
            var c = e.Category;
            if (c == null) continue;
            if (e is ImportInstance) { cad++; continue; }   // every CAD file has its own category: count them together
            if (!all && (c.CategoryType != CategoryType.Model || NotPhysical(e))) continue;
            counts.TryGetValue(c.Name, out int n);
            counts[c.Name] = n + 1;
        }
        return new
        {
            total = counts.Values.Sum(),
            categories = counts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            cadFiles = cad,
            note = all ? null : "Physical model elements only; CAD files are counted separately (list_links).",
        };
    }

    public static object ListTypes(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var cat = Find.Cat(doc, NeedStr(p, "category"));
        var used = new Dictionary<long, int>();
        foreach (Element e in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType())
        {
            long t = e.GetTypeId().Value;
            used.TryGetValue(t, out int n);
            used[t] = n + 1;
        }
        var types = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsElementType().Cast<ElementType>()
            .OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new
            {
                id = t.Id.Value, family = t.FamilyName, type = t.Name, kind = t.GetType().Name,
                instances = used.TryGetValue(t.Id.Value, out int n) ? n : 0,
            });
        return Describe.Page(p, types, "types", 300);
    }

    public static object ListMaterials(Rc rc, Obj p)
    {
        var mats = new FilteredElementCollector(rc.Doc).OfClass(typeof(Material)).Cast<Material>()
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new { id = m.Id.Value, name = m.Name, materialClass = m.MaterialClass });
        return Describe.Page(p, mats, "materials", 300);
    }

    public static object ListWarnings(Rc rc, Obj p)
    {
        var all = rc.Doc.GetWarnings();
        var summary = all.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count())
            .Take(50).ToDictionary(g => g.Key, g => (object)g.Count());
        var o = (Obj)Describe.Page(p, all.Select(w => new
        {
            text = w.GetDescriptionText(), severity = w.GetSeverity().ToString(),
            elements = w.GetFailingElements().Select(id => id.Value).ToList(),
        }), "warnings", 100);
        o["total"] = all.Count;
        o["byText"] = summary;
        return o;
    }
}
