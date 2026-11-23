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
        c.DeferOutput(folder, () =>
        {
            var failed = new List<string>();
            foreach (var (view, file) in files)
                if (!c.Doc.Export(folder, file, new List<ElementId> { view.Id }, new DWGExportOptions())) failed.Add(file);
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
        c.DeferOutput(folder, () =>
        {
            // The IFC exporter needs an open transaction; nothing it changes is kept.
            using (var t = new Transaction(c.Doc, "IFC export"))
            {
                t.Start();
                bool ok = c.Doc.Export(folder, name, options);
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
            c.DeferOutput(folder, () =>
            {
                if (!c.Doc.Export(folder, views.Select(v => v.Id).ToList(), options))
                    throw new InvalidOperationException("PDF export failed.");
            });
            return new { folder, files = new[] { name + ".pdf" }, combine, views = views.Select(v => v.Name).ToList(), status = Queued };
        }
        var files = Named(c, p, views, folder, ".pdf");
        c.DeferOutput(folder, () =>
        {
            var failed = new List<string>();
            foreach (var (view, file) in files)
                if (!c.Doc.Export(folder, new List<ElementId> { view.Id }, new PDFExportOptions { FileName = file, Combine = true }))
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
        c.DeferOutput(folder, () => c.Doc.Export(folder, name, options));
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
        c.DeferOutput(folder, () => c.Doc.ExportImage(options));
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
        c.DeferOutput(folder, () => schedule.Export(folder, name, options));
        return new { file = IOPath.Combine(folder, name), schedule = schedule.Name, status = Queued };
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
        c.DeferOutput(folder, () => c.Doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = overwrite }));
        return new { file = path, status = Queued };
    }
}
