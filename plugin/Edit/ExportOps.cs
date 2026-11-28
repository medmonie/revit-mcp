namespace RevitMcpBridge;

/// <summary>Writing files: DWG, IFC, PDF, NWC, images, schedule CSV, and saving the model. All run after the commit.</summary>
internal static class ExportOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["export_dwg"] = ExportDwg;
        ops["export_ifc"] = ExportIfc;
        ops["export_pdf"] = ExportPdf;
        ops["export_nwc"] = ExportNwc;
        ops["export_image"] = ExportImage;
        ops["export_schedule"] = ExportSchedule;
        ops["export_dwf"] = ExportDwf;
        ops["export_fbx"] = ExportFbx;
        ops["export_gbxml"] = ExportGbxml;
        ops["export_dgn"] = ExportDgn;
        ops["save"] = Save;
        ops["save_as"] = SaveAs;
    }

    private const string Queued = "see postCommit for the execution outcome";

    /// <summary>
    /// The views to export: 'sheets' (wildcard on sheet number or name, e.g. "S-1*" or "*"), else 'views'
    /// (names, sheet numbers or ids), else the active view.
    /// </summary>
    private static List<View> Views(EditContext c, Obj p)
    {
        if (Has(p, "sheets"))
        {
            var patterns = (p["sheets"] is string one ? new List<string> { one } : StrList(p, "sheets", 200))
                .Select(w => new System.Text.RegularExpressions.Regex(
                    "^" + System.Text.RegularExpressions.Regex.Escape(w.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            var sheets = new FilteredElementCollector(c.Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsTemplate && patterns.Any(r => r.IsMatch(s.SheetNumber) || r.IsMatch(s.Name)))
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).Cast<View>().ToList();
            if (sheets.Count == 0) throw new ArgumentException("No sheet number or name matches 'sheets'. Use list_sheets.");
            return sheets;
        }
        if (!Has(p, "views")) return new List<View> { Find.ViewOrActive(c.Doc, p) };
        return StrList(p, "views", 500).Select(v => Find.ViewOf(c.Doc, v)).ToList();
    }

    /// <summary>
    /// A file name from 'name_template' for one view: {number} {name} {revision} {project_number} {model} {date}.
    /// Default: "{number} - {name}" for sheets and "{name}" for views.
    /// </summary>
    private static string FileNameFor(EditContext c, Obj p, View v)
    {
        var sheet = v as ViewSheet;
        string template = Str(p, "name_template") ?? (sheet != null ? "{number} - {name}" : "{name}");
        string revision = sheet != null ? Find.Builtin(sheet, "SHEET_CURRENT_REVISION")?.AsString() ?? "" : "";
        string result = template
            .Replace("{number}", sheet?.SheetNumber ?? "")
            .Replace("{name}", v.Name)
            .Replace("{revision}", revision)
            .Replace("{project_number}", c.Doc.ProjectInformation?.Number ?? "")
            .Replace("{model}", IOPath.GetFileNameWithoutExtension(c.Doc.Title))
            .Replace("{date}", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        result = EditContext.SafeName(result).Trim().Trim('-', '_', ' ');
        return string.IsNullOrEmpty(result) ? EditContext.SafeName(v.Name) : result;
    }

    /// <summary>File names per view; fails before anything is written when two views would get the same name.</summary>
    private static List<(View view, string file)> Named(EditContext c, Obj p, List<View> views, string folder, string ext)
    {
        var list = views.Select(v => (v, FileNameFor(c, p, v))).ToList();
        var dup = list.GroupBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new ArgumentException($"Two views would be exported as '{dup.Key}{ext}'. Add {{number}} or {{name}} to name_template.");
        foreach (var x in list) c.GuardOverwrite(p, folder, x.Item2 + ext);
        return list;
    }

    private static string BaseName(EditContext c, Obj p) =>
        EditContext.SafeName(Str(p, "name") ?? IOPath.GetFileNameWithoutExtension(c.Doc.Title));

    private static object ExportDwg(EditContext c, Obj p)
    {
        string folder = EditContext.Folder(p);
        var views = Views(c, p);
        var files = Named(c, p, views, folder, ".dwg");
        c.DeferOutput(folder, p, stage =>
        {
            var failed = new List<string>();
            foreach (var (view, file) in files)
                if (!c.Doc.Export(stage, file, new List<ElementId> { view.Id }, new DWGExportOptions())) failed.Add(file);
            if (failed.Count > 0) throw new InvalidOperationException("DWG export failed for: " + string.Join(", ", failed));
        });
        return new { folder, files = files.Select(f => f.file + ".dwg").ToList(), status = Queued };
    }

    private static object ExportIfc(EditContext c, Obj p)
    {
        string folder = EditContext.Folder(p), name = BaseName(c, p);
        c.GuardOverwrite(p, folder, name + ".ifc");
        var options = new IFCExportOptions();
        string version = Str(p, "version") ?? "IFC2x3CV2";
        if (!Enum.TryParse(version, true, out IFCVersion ifc) || long.TryParse(version, out _))
            throw new ArgumentException($"'version' must be one of: {string.Join(", ", Enum.GetNames(typeof(IFCVersion)))}.");
        options.FileVersion = ifc;
        string viewName = null;
        if (Has(p, "view")) { var v = Find.ViewOf(c.Doc, Ref(p, "view")); options.FilterViewId = v.Id; viewName = v.Name; }
        c.DeferOutput(folder, p, stage =>
        {
            // The IFC exporter needs an open transaction; nothing it changes is kept.
            using (var t = new Transaction(c.Doc, "IFC export"))
            {
                t.Start();
                bool ok = c.Doc.Export(stage, name, options);
                t.RollBack();
                if (!ok) throw new InvalidOperationException("IFC export failed.");
            }
        });
        return new { file = IOPath.Combine(folder, name + ".ifc"), version = ifc.ToString(), onlyView = viewName, status = Queued };
    }

    private static object ExportPdf(EditContext c, Obj p)
    {
        string folder = EditContext.Folder(p);
        var views = Views(c, p);
        bool combine = Flag(p, "combine", !Has(p, "name_template"));
        if (combine)
        {
            string name = BaseName(c, p);
            c.GuardOverwrite(p, folder, name + ".pdf");
            var options = new PDFExportOptions { FileName = name, Combine = true };
            c.DeferOutput(folder, p, stage =>
            {
                if (!c.Doc.Export(stage, views.Select(v => v.Id).ToList(), options))
                    throw new InvalidOperationException("PDF export failed.");
            });
            return new { folder, files = new[] { name + ".pdf" }, combine, views = views.Select(v => v.Name).ToList(), status = Queued };
        }
        var files = Named(c, p, views, folder, ".pdf");
        c.DeferOutput(folder, p, stage =>
        {
            var failed = new List<string>();
            foreach (var (view, file) in files)
                if (!c.Doc.Export(stage, new List<ElementId> { view.Id }, new PDFExportOptions { FileName = file, Combine = true }))
                    failed.Add(file);
            if (failed.Count > 0) throw new InvalidOperationException("PDF export failed for: " + string.Join(", ", failed));
        });
        return new { folder, files = files.Select(f => f.file + ".pdf").ToList(), combine, status = Queued };
    }

    private static object ExportNwc(EditContext c, Obj p)
    {
        if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            throw new InvalidOperationException("The Navisworks exporter for this Revit version is not installed (install 'Navisworks NWC Export Utility').");
        string folder = EditContext.Folder(p), name = BaseName(c, p);
        c.GuardOverwrite(p, folder, name + ".nwc");
        var options = new NavisworksExportOptions { ExportScope = NavisworksExportScope.Model };
        string viewName = null;
        if (Has(p, "view"))
        {
            var v = Find.ViewOf(c.Doc, Ref(p, "view"));
            if (!(v is View3D)) throw new ArgumentException("export_nwc 'view' must be a 3D view.");
            options.ExportScope = NavisworksExportScope.View;
            options.ViewId = v.Id;
            viewName = v.Name;
        }
        c.DeferOutput(folder, p, stage => c.Doc.Export(stage, name, options));
        return new { file = IOPath.Combine(folder, name + ".nwc"), scope = options.ExportScope.ToString(), view = viewName, status = Queued };
    }

    private static object ExportImage(EditContext c, Obj p)
    {
        string folder = EditContext.Folder(p), name = BaseName(c, p);
        var views = Views(c, p);
        c.GuardOverwrite(p, folder, name + "*.png");
        var options = new ImageExportOptions
        {
            FilePath = IOPath.Combine(folder, name),
            ExportRange = ExportRange.SetOfViews,
            ZoomType = ZoomFitType.FitToPage,
            FitDirection = FitDirectionType.Horizontal,
            PixelSize = Int(p, "width_px", 2000, 200, 15000),
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
        };
        options.SetViewsAndSheets(views.Select(v => v.Id).ToList());
        c.DeferOutput(folder, p, stage => { options.FilePath = IOPath.Combine(stage, name); c.Doc.ExportImage(options); });
        return new { folder, name, views = views.Select(v => v.Name).ToList(), status = Queued,
                     note = "One PNG per view; Revit appends the view type and name to the file name." };
    }

    private static object ExportSchedule(EditContext c, Obj p)
    {
        var schedule = Find.ViewOf(c.Doc, NeedRef(p, "schedule")) as ViewSchedule
                       ?? throw new ArgumentException("'schedule' is not a schedule. Use list_schedules.");
        string folder = EditContext.Folder(p);
        string name = EditContext.SafeName(Str(p, "name") ?? schedule.Name) + ".csv";
        c.GuardOverwrite(p, folder, name);
        var options = new ViewScheduleExportOptions { FieldDelimiter = "," };
        c.DeferOutput(folder, p, stage => schedule.Export(stage, name, options));
        return new { file = IOPath.Combine(folder, name), schedule = schedule.Name, status = Queued };
    }

    /// <summary>Some exporters need a modifiable document: run inside a transaction that is rolled back (nothing kept).</summary>
    private static bool Rolled(Document doc, string name, Func<bool> export)
    {
        using (var t = new Transaction(doc, name))
        {
            t.Start();
            try { return export(); }
            finally { t.RollBack(); }
        }
    }

    private static ViewSet SetOf(IEnumerable<View> views)
    {
        var set = new ViewSet();
        foreach (var v in views) set.Insert(v);
        return set;
    }

    private static object ExportDwf(EditContext c, Obj p)
    {
        string folder = EditContext.Folder(p);
        var views = Views(c, p);
        bool merged = Flag(p, "merged", true);
        if (merged)
        {
            string name = BaseName(c, p);
            c.GuardOverwrite(p, folder, name + ".dwf");
            c.DeferOutput(folder, p, stage =>
            {
                if (!Rolled(c.Doc, "DWF export", () => c.Doc.Export(stage, name, SetOf(views), new DWFExportOptions { MergedViews = true })))
                    throw new InvalidOperationException("DWF export failed.");
            });
            return new { folder, files = new[] { name + ".dwf" }, merged, views = views.Select(v => v.Name).ToList(), status = Queued };
        }
        var files = Named(c, p, views, folder, ".dwf");
        c.DeferOutput(folder, p, stage =>
        {
            var failed = files.Where(f => !Rolled(c.Doc, "DWF export", () => c.Doc.Export(stage, f.file, SetOf(new[] { f.view }), new DWFExportOptions()))).Select(f => f.file).ToList();
            if (failed.Count > 0) throw new InvalidOperationException("DWF export failed for: " + string.Join(", ", failed));
        });
        return new { folder, files = files.Select(f => f.file + ".dwf").ToList(), merged, status = Queued };
    }

    private static object ExportFbx(EditContext c, Obj p)
    {
        string folder = EditContext.Folder(p), name = BaseName(c, p);
        var view = Find.ViewOrActive(c.Doc, p) as View3D;
        if (view == null || view.IsTemplate) throw new ArgumentException("export_fbx needs a 3D view ('view', or make a 3D view active).");
        c.GuardOverwrite(p, folder, name + ".fbx");
        c.DeferOutput(folder, p, stage =>
        {
            if (!c.Doc.Export(stage, name, SetOf(new[] { view }), new FBXExportOptions()))
                throw new InvalidOperationException("FBX export failed.");
        });
        return new { file = IOPath.Combine(folder, name + ".fbx"), view = view.Name, status = Queued };
    }

    private static object ExportGbxml(EditContext c, Obj p)
    {
        string folder = EditContext.Folder(p), name = BaseName(c, p);
        c.GuardOverwrite(p, folder, name + ".xml");
        c.DeferOutput(folder, p, stage =>
        {
            // The exporter needs an energy analysis model; it is made for the export and rolled back with the transaction.
            // From the building elements first; when Revit refuses that, from the rooms / spaces.
            string usedModel = null; string lastError = null;
            foreach (var fromSpaces in new[] { false, true })
            {
                try
                {
                    bool ok = Rolled(c.Doc, "gbXML export", () =>
                    {
#if REVIT2024
                        var settings = Autodesk.Revit.DB.Analysis.EnergyDataSettings.GetFromDocument(c.Doc);
#else
                        var settings = Autodesk.Revit.DB.Analysis.EnergyDataSettings.GetEnergyDataSettings(c.Doc);
#endif
                        var mode = fromSpaces ? Autodesk.Revit.DB.Analysis.AnalysisMode.RoomsOrSpaces : Autodesk.Revit.DB.Analysis.AnalysisMode.BuildingElements;
                        if (settings != null && settings.AnalysisType != mode) settings.AnalysisType = mode;
                        if (settings != null)
                        {
                            // the lowest level as ground plane when none is set; rooms (or spaces when there are no rooms) for the spatial model
                            var ground = c.Doc.GetElement(settings.GroundPlane) as Level;
                            if (ground == null)
                            {
                                var low = new FilteredElementCollector(c.Doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).FirstOrDefault();
                                if (low != null) settings.GroundPlane = low.Id;
                            }
                            if (fromSpaces)
                            {
                                bool rooms = new FilteredElementCollector(c.Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().GetElementCount() > 0;
                                settings.ExportCategory = new ElementId(rooms ? BuiltInCategory.OST_Rooms : BuiltInCategory.OST_MEPSpaces);
                            }
                        }
                        var existing = Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(c.Doc);
                        if (existing != null) c.Doc.Delete(existing.Id);
                        c.Doc.Regenerate();
#if REVIT2024
                        Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel.Create(c.Doc, new Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModelOptions
                        {
                            Tier = Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModelTier.Final,
                            EnergyModelType = fromSpaces ? Autodesk.Revit.DB.Analysis.EnergyModelType.SpatialElement : Autodesk.Revit.DB.Analysis.EnergyModelType.BuildingElement,
                        });
#else
                        Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel.Create(c.Doc);   // follows AnalysisType, ground plane and export category set above
#endif
                        var options = new GBXMLExportOptions();
#if REVIT2024
                        options.ExportEnergyModelType = fromSpaces ? ExportEnergyModelType.SpatialElement : ExportEnergyModelType.BuildingElement;
#endif
                        c.Doc.Regenerate();
                        return c.Doc.Export(stage, name, options);
                    });
                    if (ok) { usedModel = fromSpaces ? "rooms / spaces" : "building elements"; break; }
                    lastError = "Revit reported the export as failed";
                }
                catch (Exception ex) { lastError = ex.Message; }
            }
            if (usedModel == null) throw new InvalidOperationException("gbXML export failed from the building elements and from the rooms / spaces: " + lastError + " Check Energy Settings (Analyze tab).");
            c.Warn($"gbXML: exported from the {usedModel}; the energy analysis model made for it was not kept.");
        });
        return new { file = IOPath.Combine(folder, name + ".xml"), status = Queued,
                     note = "gbXML from the building elements, with the model's Energy Settings (Analyze tab); the model is not changed." };
    }

    private static object ExportDgn(EditContext c, Obj p)
    {
        string folder = EditContext.Folder(p);
        var views = Views(c, p);
        string setup = Str(p, "setup");
        string seed = Has(p, "seed") ? EditContext.FullPath(NeedStr(p, "seed"), "seed") : null;
        string installedSeed = seed == null ? InstalledDgnSeed(c.Doc) : null;
        if (seed != null && !File.Exists(seed)) throw new ArgumentException($"DGN seed file '{seed}' does not exist.");
        if (setup != null && !DGNExportOptions.GetPredefinedSetupNames(c.Doc).Contains(setup))
            throw new ArgumentException($"No DGN export setup '{setup}'. Setups: {string.Join(", ", DGNExportOptions.GetPredefinedSetupNames(c.Doc))}.");
        var files = Named(c, p, views, folder, ".dgn");
        c.DeferOutput(folder, p, stage =>
        {
            // Without a setup, Revit's defaults are tried first, then each DGN setup saved in the model (they carry a seed file).
            var candidates = setup != null ? new List<string> { setup }
                : new List<string> { null }.Concat(DGNExportOptions.GetPredefinedSetupNames(c.Doc)).ToList();
            // last resort: Revit's own default options with a seed file that came with Revit
            if (setup == null && seed == null && installedSeed != null) candidates.Add("\u0001seed");
            var failed = new List<string>();
            foreach (var (view, file) in files)
            {
                string used = null; bool ok = false;
                foreach (var name in candidates)
                {
                    bool withSeed = name == "\u0001seed";
                    var options = name != null && !withSeed ? DGNExportOptions.GetPredefinedOptions(c.Doc, name) : new DGNExportOptions();
                    if (seed != null) options.SeedName = seed;
                    if (withSeed) options.SeedName = installedSeed;
                    if (Rolled(c.Doc, "DGN export", () => c.Doc.Export(stage, file, new List<ElementId> { view.Id }, options))) { ok = true; used = name; break; }
                }
                if (!ok) failed.Add(file);
                else if (used == "\u0001seed") c.Warn($"DGN '{file}': no DGN setup in the model; exported with Revit's seed file {installedSeed}.");
                else if (used != null && setup == null) c.Warn($"DGN '{file}': Revit's default options failed; exported with the model's setup '{used}'.");
            }
            if (failed.Count > 0)
                throw new InvalidOperationException("DGN export failed for: " + string.Join(", ", failed) + ". DGN setups in this model: "
                    + (DGNExportOptions.GetPredefinedSetupNames(c.Doc).Count == 0 ? "none (create one in Revit: File > Export > DGN > setup)" : string.Join(", ", DGNExportOptions.GetPredefinedSetupNames(c.Doc)))
                    + (seed == null ? "; or give 'seed' (a .dgn seed file)." : "."));
        });
        return new { folder, files = files.Select(f => f.file + ".dgn").ToList(), setup, seed, status = Queued };
    }

    private static object Save(EditContext c, Obj p)
    {
        if (string.IsNullOrEmpty(c.Doc.PathName)) throw new InvalidOperationException("The model has never been saved; use save_as with a path.");
        if (c.Doc.IsWorkshared) c.Warn("workshared model: this saves the local file only. Synchronise with central in Revit.");
        c.Defer(() => c.Doc.Save());
        return new { file = c.Doc.PathName, status = Queued };
    }

    private static object SaveAs(EditContext c, Obj p)
    {
        string path = EditContext.FullPath(NeedStr(p, "path"), "path");
        if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("'path' must be a full path ending in .rvt.");
        bool overwrite = Flag(p, "overwrite", false);
        string folder = IOPath.GetDirectoryName(path);
        c.GuardOverwrite(p, folder, IOPath.GetFileName(path));
        if (c.Doc.IsWorkshared) throw new InvalidOperationException("save_as is not available for workshared models (it would create a new central or detached copy); do that in Revit.");
        c.DeferSave(folder, () => c.Doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = overwrite }));
        return new { file = path, status = Queued };
    }

    /// <summary>A DGN seed file installed with this Revit (metric or imperial to match the model), or null.</summary>
    private static string InstalledDgnSeed(Document doc)
    {
        try
        {
            string ver = doc.Application.VersionNumber;
            var roots = new[]
            {
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + ver),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "Revit " + ver),
            };
            // walk folder by folder so one folder Revit's process may not read does not end the search
            var seeds = new List<string>();
            var stack = new Stack<(string dir, int depth)>(roots.Where(Directory.Exists).Select(r => (r, 0)));
            int visited = 0;
            while (stack.Count > 0 && visited < 3000)
            {
                var (dir, depth) = stack.Pop();
                visited++;
                try { seeds.AddRange(Directory.GetFiles(dir, "*.dgn").Where(f => IOPath.GetFileName(f).IndexOf("seed", StringComparison.OrdinalIgnoreCase) >= 0)); } catch { /* unreadable */ }
                if (depth < 6) { try { foreach (var d in Directory.GetDirectories(dir)) stack.Push((d, depth + 1)); } catch { /* unreadable */ } }
            }
            if (seeds.Count == 0) return null;
            bool metric = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId() != UnitTypeId.Feet
                          && doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId() != UnitTypeId.FeetFractionalInches
                          && doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId() != UnitTypeId.Inches
                          && doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId() != UnitTypeId.FractionalInches;
            return seeds.OrderByDescending(f => (IOPath.GetFileName(f).IndexOf(metric ? "metric" : "imperial", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 0)
                                                + (IOPath.GetFileName(f).IndexOf("3d", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1))
                        .First();
        }
        catch { return null; }
    }
}
