namespace RevitMcpBridge;

/// <summary>
/// Model management reads: project and shared parameters, worksets and element ownership, phases and design options,
/// and a model health report (file, warnings, families, CAD, links, views, groups, purgeable elements).
/// </summary>
internal static class ManageRead
{
    // ---------------------------------------------------------------- parameters

    public static object ListParameters(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var project = new List<(string name, object row)>();
        var it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            if (!(it.Key is Definition def) || !(it.Current is ElementBinding binding)) continue;
            var spe = def is InternalDefinition idef ? doc.GetElement(idef.Id) as SharedParameterElement : null;
            project.Add((def.Name, new
            {
                name = def.Name, shared = spe != null, guid = spe?.GuidValue.ToString(),
                dataType = SpecLabel(def), group = GroupLabel(def),
                binding = binding is InstanceBinding ? "instance" : "type",
                categories = binding.Categories.Cast<Category>().Select(cat => cat.Name).OrderBy(n => n).ToList(),
            }));
        }
        var result = new Obj
        {
            ["model"] = doc.Title,
            ["projectParameters"] = project.OrderBy(o => o.name).Select(o => o.row).ToList(),
            ["sharedParameterFile"] = doc.Application.SharedParametersFilename,
        };
        if (Flag(p, "shared_file_contents", false) || Has(p, "shared_file"))
            result["sharedFile"] = SharedFileContents(doc.Application, Str(p, "shared_file"));
        return result;
    }

    internal static string SpecLabel(Definition def)
    {
        try { return LabelUtils.GetLabelForSpec(def.GetDataType()); } catch { return def.GetDataType()?.TypeId; }
    }

    internal static string GroupLabel(Definition def)
    {
        try { return LabelUtils.GetLabelForGroup(def.GetGroupTypeId()); } catch { return null; }
    }

    /// <summary>The groups and definitions of a shared parameter file (the application's current file by default).</summary>
    private static object SharedFileContents(Autodesk.Revit.ApplicationServices.Application app, string path)
    {
        string before = app.SharedParametersFilename;
        try
        {
            if (path != null)
            {
                if (!File.Exists(path)) throw new ArgumentException($"No shared parameter file at '{path}'.");
                app.SharedParametersFilename = path;
            }
            var file = app.OpenSharedParameterFile();
            if (file == null) return new { path = app.SharedParametersFilename, error = "No shared parameter file is set or it could not be read." };
            return new
            {
                path = app.SharedParametersFilename,
                groups = file.Groups.Cast<DefinitionGroup>().OrderBy(g => g.Name).Select(g => new
                {
                    group = g.Name,
                    parameters = g.Definitions.Cast<ExternalDefinition>().OrderBy(d => d.Name)
                                  .Select(d => new { name = d.Name, guid = d.GUID.ToString(), dataType = SpecLabel(d) }).ToList(),
                }).ToList(),
            };
        }
        finally { if (path != null) app.SharedParametersFilename = before; }
    }

    // ---------------------------------------------------------------- worksets

    public static object ListWorksets(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        if (!doc.IsWorkshared) return new { model = doc.Title, workshared = false, note = "This model is not workshared, so it has no worksets." };
        var active = doc.GetWorksetTable().GetActiveWorksetId();
        bool counts = Flag(p, "counts", false);
        var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name).Select(w => new
        {
            id = w.Id.IntegerValue, name = w.Name, isOpen = w.IsOpen, editable = w.IsEditable, owner = w.Owner,
            active = w.Id == active, visibleByDefault = w.IsVisibleByDefault,
            elements = counts ? new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(w.Id)).WhereElementIsNotElementType().GetElementCount() : (int?)null,
        }).ToList();
        var result = new Obj { ["model"] = doc.Title, ["workshared"] = true, ["worksets"] = worksets };
        if (Has(p, "ids"))
        {
            result["elements"] = Ids(p, "ids", 2000).Select(id =>
            {
                var e = doc.GetElement(id);
                if (e == null) return (object)new { id = id.Value, missing = true };
                string owner = "";
                var status = WorksharingUtils.GetCheckoutStatus(doc, e.Id, out owner);
                var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, e.Id);
                return new
                {
                    id = id.Value, workset = doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name, status = status.ToString(),
                    owner = string.IsNullOrEmpty(owner) ? null : owner, lastChangedBy = info.LastChangedBy, creator = info.Creator,
                };
            }).ToList();
        }
        return result;
    }

    // ---------------------------------------------------------------- phases and design options

    public static object ListPhases(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var options = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().ToList();
        return new
        {
            model = doc.Title,
            phases = doc.Phases.Cast<Phase>().Select((ph, i) => new { order = i + 1, id = ph.Id.Value, name = ph.Name }).ToList(),
            phaseFilters = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Select(f => new { id = f.Id.Value, name = f.Name }).OrderBy(f => f.name).ToList(),
            designOptionSets = options.GroupBy(o => o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId() ?? ElementId.InvalidElementId)
                .Select(g => new
                {
                    set = doc.GetElement(g.Key)?.Name ?? "(unknown set)",
                    options = g.Select(o => new
                    {
                        id = o.Id.Value, name = o.Name, primary = o.IsPrimary,
                        elements = new FilteredElementCollector(doc).WherePasses(new ElementDesignOptionFilter(o.Id)).WhereElementIsNotElementType().GetElementCount(),
                    }).ToList(),
                }).ToList(),
        };
    }

    // ---------------------------------------------------------------- health

    public static object ModelHealth(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var flags = new List<string>();
        long? sizeMb = null;
        try { if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName)) sizeMb = new FileInfo(doc.PathName).Length / (1024 * 1024); } catch { }
        if (sizeMb > 300) flags.Add($"File is {sizeMb} MB (over 300 MB): purge, check CAD imports and in-place families.");

        var warnings = doc.GetWarnings();
        if (warnings.Count > 500) flags.Add($"{warnings.Count} warnings (over 500).");

        var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
        var inPlace = families.Where(f => f.IsInPlace).ToList();
        if (inPlace.Count > 10) flags.Add($"{inPlace.Count} in-place families (over 10).");

        var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
        int imported = imports.Count(i => !i.IsLinked);
        if (imported > 0) flags.Add($"{imported} CAD file(s) imported rather than linked.");

        var linkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
        int unloaded = linkTypes.Count(t => !RevitLinkType.IsLoaded(doc, t.Id));
        if (unloaded > 0) flags.Add($"{unloaded} Revit link(s) not loaded.");

        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.CanBePrinted && !(v is ViewSheet)).ToList();
        var placed = new HashSet<long>(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(vp => vp.ViewId.Value));
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()) placed.Add(s.ScheduleId.Value);
        var notOnSheets = views.Where(v => !placed.Contains(v.Id.Value) && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser
                                           && v.ViewType != ViewType.Internal && v.ViewType != ViewType.Undefined).ToList();
        var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
        var usedTemplates = new HashSet<long>(views.Select(v => v.ViewTemplateId.Value));
        int sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).GetElementCount();

        var groupTypes = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
        int unusedGroups = groupTypes.Count(g => g.Groups.Size == 0);

        int? purgeable = null; string purgeNote = null;
        if (Flag(p, "purgeable", true))
        {
            try { purgeable = doc.GetUnusedElements(new HashSet<ElementId>()).Count; }
            catch (Exception ex) { purgeNote = ex.Message; }
            if (purgeable > 200) flags.Add($"{purgeable} purgeable elements (first pass; purge removes more).");
        }

        int worksets = doc.IsWorkshared ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Count : 0;
        int options = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).GetElementCount();
        int elements = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();

        return new
        {
            model = doc.Title, path = doc.PathName, sizeMb, workshared = doc.IsWorkshared, cloud = doc.IsModelInCloud,
            elements,
            warnings = new
            {
                total = warnings.Count,
                top = warnings.GroupBy(w => w.GetDescriptionText()).OrderByDescending(g => g.Count()).Take(10)
                              .Select(g => new { text = g.Key, count = g.Count() }).ToList(),
            },
            families = new { total = families.Count, inPlace = inPlace.Count, inPlaceNames = inPlace.Select(f => f.Name).OrderBy(n => n).Take(50).ToList() },
            cad = new
            {
                linked = imports.Count(i => i.IsLinked), imported,
                viewSpecific = imports.Count(i => i.ViewSpecific),
                rasterImages = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RasterImages).WhereElementIsNotElementType().GetElementCount(),
            },
            revitLinks = new { types = linkTypes.Count, notLoaded = unloaded },
            views = new
            {
                total = views.Count, notOnSheets = notOnSheets.Count, sheets,
                templates = templates.Count, unusedTemplates = templates.Count(t => !usedTemplates.Contains(t.Id.Value)),
            },
            groups = new { types = groupTypes.Count, unused = unusedGroups },
            purgeable, purgeNote, worksets, designOptions = options,
            attention = flags,
        };
    }
}
