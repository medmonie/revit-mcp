namespace RevitMcpBridge;

/// <summary>Linking and importing CAD files, linking Revit models.</summary>
internal static class LinkOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["cad_link"] = CadLink;
        ops["revit_link"] = RevitLink;
    }

    private static T Named<T>(string name) where T : struct => (T)Enum.Parse(typeof(T), name, true);

    private static string NeedFile(Obj p, params string[] extensions)
    {
        string path = NeedStr(p, "path");
        if (!File.Exists(path)) throw new ArgumentException($"File not found: {path}");
        string ext = IOPath.GetExtension(path).ToLowerInvariant();
        if (!extensions.Contains(ext)) throw new ArgumentException($"'{IOPath.GetFileName(path)}': expected {string.Join(" / ", extensions)}.");
        return path;
    }

    private static object CadLink(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string path = NeedFile(p, ".dwg", ".dxf");
        bool link = Choice(p, "mode", "link", "link", "import") == "link";
        var view = Find.ViewOrActive(doc, p);

        var options = new DWGImportOptions
        {
            ThisViewOnly = Flag(p, "this_view_only", false),
            OrientToView = true,
            VisibleLayersOnly = Flag(p, "visible_layers_only", false),
            Placement = Named<ImportPlacement>(Choice(p, "placement", "origin", "origin", "centered", "shared")),
        };
        switch (Choice(p, "colors", "preserve", "preserve", "black_white", "invert"))
        {
            case "black_white": options.ColorMode = ImportColorMode.BlackAndWhite; break;
            case "invert": options.ColorMode = ImportColorMode.Inverted; break;
            default: options.ColorMode = ImportColorMode.Preserved; break;
        }
        // CAD units: 'auto' reads them from the file; give them when the DWG is unitless (very common).
        string unit = Choice(p, "cad_units", "auto", "auto", "mm", "cm", "m", "ft", "in");
        options.Unit = unit == "mm" ? ImportUnit.Millimeter : unit == "cm" ? ImportUnit.Centimeter : unit == "m" ? ImportUnit.Meter
            : unit == "ft" ? ImportUnit.Foot : unit == "in" ? ImportUnit.Inch : ImportUnit.Default;

        ElementId id;
        bool ok = link ? doc.Link(path, options, view, out id) : doc.Import(path, options, view, out id);
        if (!ok || id == null || id == ElementId.InvalidElementId)
            throw new InvalidOperationException($"Revit could not {(link ? "link" : "import")} '{IOPath.GetFileName(path)}'.");
        if (Flag(p, "pin", true)) { var e = doc.GetElement(id); if (e != null) e.Pinned = true; }
        return new
        {
            id = id.Value, mode = link ? "link" : "import", file = IOPath.GetFileName(path), view = view.Name,
            note = "Use get_cad_geometry with this id to read its lines, arcs and polylines by layer.",
        };
    }

    private static object RevitLink(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string path = NeedFile(p, ".rvt");
        if (string.Equals(path, doc.PathName, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A model cannot be linked into itself.");
        var placement = Named<ImportPlacement>(Choice(p, "placement", "origin", "origin", "centered", "shared"));
        var result = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new RevitLinkOptions(false));
        var instance = RevitLinkInstance.Create(doc, result.ElementId, placement);
        if (Flag(p, "pin", true)) instance.Pinned = true;
        return new { id = instance.Id.Value, typeId = result.ElementId.Value, file = IOPath.GetFileName(path), placement = placement.ToString() };
    }
}
