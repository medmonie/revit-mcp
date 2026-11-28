namespace RevitMcpBridge;

/// <summary>Linking and importing CAD files, linking Revit models.</summary>
internal static class LinkOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["cad_link"] = CadLink;
        ops["revit_link"] = RevitLink;
        ops["link_reload"] = LinkReload;
        ops["link_unload"] = LinkUnload;
        ops["link_set"] = LinkSet;
    }

    // ---------------------------------------------------------------- link management (phase 6)

    /// <summary>A Revit link (instance id, type id or file name, as LinkScope) or a linked CAD file (its id).</summary>
    private sealed class Target
    {
        public RevitLinkInstance Revit;
        public RevitLinkType RevitType;
        public ImportInstance Cad;
        public CADLinkType CadType;
        public string Name;
    }

    private static Target Resolve(Document doc, Obj p)
    {
        string given = NeedRef(p, "link").Trim();
        if (long.TryParse(given, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            && doc.GetElement(new ElementId(n)) is ImportInstance imp)
        {
            if (!imp.IsLinked) throw new ArgumentException($"CAD file {n} was imported, not linked: an import cannot be reloaded or unloaded.");
            var type = doc.GetElement(imp.GetTypeId()) as CADLinkType
                       ?? throw new ArgumentException($"CAD file {n} has no link type.");
            return new Target { Cad = imp, CadType = type, Name = type.Name };
        }
        var inst = LinkScope.Resolve(doc, given);
        var rt = doc.GetElement(inst.GetTypeId()) as RevitLinkType
                 ?? throw new ArgumentException($"Link {inst.Id.Value} has no link type.");
        if (rt.IsNestedLink) throw new ArgumentException($"'{rt.Name}' is a nested link: reload, unload or change it in the model that links it.");
        return new Target { Revit = inst, RevitType = rt, Name = rt.Name };
    }

    private static Obj Info(Target t) => t.Revit != null
        ? new Obj { ["kind"] = "revit", ["instanceId"] = t.Revit.Id.Value, ["typeId"] = t.RevitType.Id.Value, ["file"] = t.Name,
                    ["status"] = t.RevitType.GetLinkedFileStatus().ToString() }
        : new Obj { ["kind"] = "cad", ["id"] = t.Cad.Id.Value, ["typeId"] = t.CadType.Id.Value, ["file"] = t.Name };

    private static string LoadText(object r) => r is LinkLoadResult l ? l.LoadResult.ToString() : r?.ToString() ?? "done";

    /// <summary>Unloading a link asks whether to save moved shared positions back to it: never, from the bridge.</summary>
    private sealed class NoSave : ISaveSharedCoordinatesCallback, ISaveSharedCoordinatesCallbackForUnloadLocally
    {
        public SaveModifiedLinksOptions GetSaveModifiedLinksOption(RevitLinkType link) => SaveModifiedLinksOptions.DoNotSaveLinks;
        public SaveModifiedLinksOptionsForUnloadLocally GetSaveModifiedLinksOptionForUnloadLocally(RevitLinkType link) =>
            SaveModifiedLinksOptionsForUnloadLocally.DoNotSaveLinks;
    }

    // Revit reloads and unloads links in its own transaction, so these run after the batch commits (a dry run only checks).
    private static object LinkReload(EditContext c, Obj p)
    {
        var t = Resolve(c.Doc, p);
        string path = Str(p, "path");
        if (path != null)
        {
            path = EditContext.FullPath(path, "path");
            if (!File.Exists(path)) throw new ArgumentException($"File not found: {path}");
            string ext = IOPath.GetExtension(path).ToLowerInvariant();
            if (t.Revit != null && ext != ".rvt") throw new ArgumentException("A Revit link reloads from an .rvt file.");
            if (t.Cad != null && ext != ".dwg" && ext != ".dxf") throw new ArgumentException("A CAD link reloads from a .dwg or .dxf file.");
            if (t.Revit != null && string.Equals(path, c.Doc.PathName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A model cannot be linked into itself.");
        }
        var result = Info(t);
        result["action"] = path == null ? "reload" : "reload_from";
        if (path != null) result["path"] = path;
        if (c.DryRun) { result["note"] = "Dry run: checked only. Reloading runs after the commit of a real run."; return result; }
        c.Defer(() =>
        {
            object r;
            if (t.Revit != null)
            {
                bool loaded = t.RevitType.GetLinkedFileStatus() == LinkedFileStatus.Loaded;
                r = path != null ? t.RevitType.LoadFrom(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new WorksetConfiguration())
                    : loaded ? t.RevitType.Reload() : t.RevitType.Load();
                result["statusAfter"] = t.RevitType.GetLinkedFileStatus().ToString();
                if (r is LinkLoadResult l && !LinkLoadResult.IsCodeSuccess(l.LoadResult))
                    throw new InvalidOperationException($"Revit did not load '{t.Name}': {l.LoadResult}.");
            }
            else
            {
                // unlike Revit links, a CAD link reloads inside a transaction of its own
                using (var tr = new Transaction(c.Doc, "Claude: reload CAD link"))
                {
                    tr.Start();
                    r = path != null ? t.CadType.LoadFrom(path) : t.CadType.Reload();
                    if (tr.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit did not commit the CAD reload.");
                }
            }
            result["loadResult"] = LoadText(r);
        });
        result["note"] = "Reloads after the commit; postCommit and loadResult say how it went.";
        return result;
    }

    private static object LinkUnload(EditContext c, Obj p)
    {
        var t = Resolve(c.Doc, p);
        if (t.Cad != null)
            throw new ArgumentException("Revit's API cannot unload a CAD link (only reload it). Unload it in Manage Links, or delete it.");
        bool locally = Choice(p, "for", "everyone", "everyone", "me") == "me";
        if (locally && !c.Doc.IsWorkshared) throw new ArgumentException("for='me' (unload locally) needs a workshared model.");
        if (t.RevitType.GetLinkedFileStatus() != LinkedFileStatus.Loaded)
            throw new ArgumentException($"'{t.Name}' is not loaded ({t.RevitType.GetLinkedFileStatus()}).");
        var result = Info(t);
        result["action"] = locally ? "unload_locally" : "unload";
        if (c.DryRun) { result["note"] = "Dry run: checked only. Unloading runs after the commit of a real run."; return result; }
        c.Defer(() =>
        {
            if (locally) t.RevitType.UnloadLocally(new NoSave()); else t.RevitType.Unload(new NoSave());
            result["statusAfter"] = t.RevitType.GetLinkedFileStatus().ToString();
        });
        result["note"] = "Unloads after the commit. Moved shared positions are not saved back to the linked file."
                         + (c.Doc.IsWorkshared && !locally ? " In a workshared model this unloads it for everyone (for='me' unloads it only for you)." : "");
        return result;
    }

    private static object LinkSet(EditContext c, Obj p)
    {
        var t = Resolve(c.Doc, p);
        var changed = new Obj();
        if (Has(p, "pinned"))
        {
            Element inst = (Element)t.Revit ?? t.Cad;
            bool pin = Bool(p, "pinned").Value;
            if (inst.Pinned != pin) { inst.Pinned = pin; changed["pinned"] = pin; }
        }
        if (Has(p, "attachment"))
        {
            if (t.Revit == null) throw new ArgumentException("attachment applies to Revit links only.");
            var a = Choice(p, "attachment", null, "overlay", "attachment") == "overlay" ? AttachmentType.Overlay : AttachmentType.Attachment;
            if (t.RevitType.AttachmentType != a) { t.RevitType.AttachmentType = a; changed["attachment"] = a.ToString(); }
        }
        if (Has(p, "path_type"))
        {
            if (t.Revit == null) throw new ArgumentException("path_type applies to Revit links only.");
            var pt = Choice(p, "path_type", null, "relative", "absolute") == "relative" ? PathType.Relative : PathType.Absolute;
            if (t.RevitType.PathType != pt) { t.RevitType.PathType = pt; changed["pathType"] = pt.ToString(); }
        }
        if (!Has(p, "pinned") && !Has(p, "attachment") && !Has(p, "path_type"))
            throw new ArgumentException("Give at least one of pinned, attachment, path_type.");
        var result = Info(t);
        result["changed"] = changed;
        if (changed.Count == 0) result["note"] = "Already as asked; nothing changed.";
        return result;
    }

    private static T Named<T>(string name) where T : struct => (T)Enum.Parse(typeof(T), name, true);

    private static string NeedFile(Obj p, params string[] extensions)
    {
        string path = EditContext.FullPath(NeedStr(p, "path"), "path");
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
