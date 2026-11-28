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
            documentId = DocumentTarget.SessionId(ui?.Document), modelKey,
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
            ["documentId"] = DocumentTarget.SessionId(doc), ["modelKey"] = modelKey,
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

    public static object ListLevels(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        var o = new Obj
        {
            ["units"] = U.Name,
            ["levels"] = Find.Levels(doc).Select(l =>
            {
                var row = new Obj { ["id"] = l.Id.Value, ["name"] = l.Name };
                if (link == null) row["elevation"] = U.Out(l.ProjectElevation);
                else
                {
                    row["elevation"] = U.Out(link.Z(l.ProjectElevation));   // in the reported coordinates
                    row["linkElevation"] = U.Out(l.ProjectElevation);       // in the linked model
                }
                row["displayElevation"] = U.Out(l.Elevation);
                return row;
            }).ToList(),
        };
        if (link != null) o["link"] = link.Info();
        return o;
    }

    public static object ListGrids(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        var tf = link?.Tf;
        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var o = new Obj { ["id"] = g.Id.Value, ["name"] = g.Name };
                var c = g.Curve;
                if (c != null && c.IsBound) foreach (var kv in U.CurveOut(tf == null || tf.IsIdentity ? c : c.CreateTransformed(tf))) o[kv.Key] = kv.Value;
                return o;
            });
        var page = (Obj)Describe.Page(p, grids, "grids", 500);
        if (link != null) page["link"] = link.Info();
        return page;
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
        var host = rc.Doc;
        var doc = LinkScope.DocOf(host, p, out var link);
        bool all = Flag(p, "include_annotation", false);
        var col = !Has(p, "view") ? new FilteredElementCollector(doc)
            : link == null ? new FilteredElementCollector(doc, Find.ViewOf(doc, Ref(p, "view")).Id)
            : new FilteredElementCollector(host, Find.ViewOf(host, Ref(p, "view")).Id, link.Instance.Id);
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
        var o = new Obj
        {
            ["total"] = counts.Values.Sum(),
            ["categories"] = counts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            ["cadFiles"] = cad,
            ["note"] = all ? null : "Physical model elements only; CAD files are counted separately (list_links).",
        };
        if (link != null) o["link"] = new Obj { ["id"] = link.Instance.Id.Value, ["name"] = link.Instance.Name, ["document"] = link.Doc.Title };
        return o;
    }

    public static object ListTypes(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
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
        var page = (Obj)Describe.Page(p, types, "types", 300);
        if (link != null) page["link"] = new Obj { ["id"] = link.Instance.Id.Value, ["name"] = link.Instance.Name, ["document"] = link.Doc.Title };
        return page;
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

    /// <summary>
    /// What this Revit has loaded: add-in applications (from their .addin manifests) with their files, Revit's optional
    /// exporters, and whether the analytical model, loads and steel connection types are available in the active model.
    /// For checking that separately installed tools (structural analysis, steel connections, exporters) are really there.
    /// </summary>
    public static object RevitAddins(Rc rc, Obj p)
    {
        var apps = new List<Obj>();
        try
        {
            foreach (var a in rc.App.LoadedApplications)
            {
                var t = a.GetType();
                string file = null;
                try { file = t.Assembly.Location; } catch { /* dynamic */ }
                apps.Add(new Obj { ["application"] = t.FullName, ["assembly"] = t.Assembly.GetName().Name,
                                   ["version"] = t.Assembly.GetName().Version?.ToString(), ["file"] = file });
            }
        }
        catch (Exception ex) { apps.Add(new Obj { ["error"] = ex.Message }); }
        var doc = rc.App.ActiveUIDocument?.Document;
        object model = null;
        if (doc != null && !doc.IsFamilyDocument)
        {
            int Count(Type t) { try { return new FilteredElementCollector(doc).OfClass(t).GetElementCount(); } catch { return -1; } }
            model = new
            {
                title = doc.Title,
                analyticalMembers = Count(typeof(Autodesk.Revit.DB.Structure.AnalyticalMember)),
                analyticalPanels = Count(typeof(Autodesk.Revit.DB.Structure.AnalyticalPanel)),
                loadCases = Count(typeof(Autodesk.Revit.DB.Structure.LoadCase)),
                boundaryConditions = Count(typeof(Autodesk.Revit.DB.Structure.BoundaryConditions)),
                steelConnectionTypes = Count(typeof(Autodesk.Revit.DB.Structure.StructuralConnectionHandlerType)),
                steelConnections = Count(typeof(Autodesk.Revit.DB.Structure.StructuralConnectionHandler)),
            };
        }
        bool Has(Func<bool> f) { try { return f(); } catch { return false; } }
        List<string> Matching(params string[] words) => apps
            .Where(a => words.Any(w => (((string)a["application"] ?? "") + " " + ((string)a["file"] ?? "")).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
            .Select(a => (string)a["application"]).OrderBy(x => x).ToList();
        return new
        {
            revit = rc.App.Application.VersionName, build = rc.App.Application.VersionBuild,
            count = apps.Count,
            exporters = new
            {
                navisworks = Has(OptionalFunctionalityUtils.IsNavisworksExporterAvailable),
                ifc = Has(OptionalFunctionalityUtils.IsIFCAvailable),
                dgn = Has(OptionalFunctionalityUtils.IsDGNExportAvailable),
                dwg = Has(OptionalFunctionalityUtils.IsDWGExportAvailable),
                dxf = Has(OptionalFunctionalityUtils.IsDXFExportAvailable),
            },
            structuralAnalysis = Matching("StructuralAnalysis", "Structural Analysis", "Robot", "AnalysisInCloud", "AnalyzeInCloud",
                                          "Analytical", "LoadsAnalysis", "StructuralLoads", "RSA.", "ResultsExplorer", "ResultsBuilder",
                                          "MemberForces", "SectionProperties", "StructuralRibbon").Where(n => n.IndexOf("Electrical", StringComparison.OrdinalIgnoreCase) < 0).ToList(),
            steelConnections = Matching("SteelConnection", "Steel Connection", "AdvanceSteel", "Advance Steel", "SteelDesign"),
            applications = apps.OrderBy(a => (string)a["application"]).ToList(),
            activeModel = model,
            note = "The analytical model itself (members, panels, loads, supports) is part of Revit's API; analysis and steel connection tools come from separately installed add-ins listed here.",
        };
    }
}
