namespace RevitMcpBridge;

/// <summary>
/// Phase 16 documentation depth: colour fill schemes (and their legends), linear / ordinate dimension types for the
/// dimension chains, sheet collections and STEP export (Revit 2027), and parameters from the Autodesk (ACC)
/// parameters service. Colours, captions, values and parameter ids come from the user.
/// </summary>
internal static class DocExtraOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["color_fill_scheme_set"] = ColorFillSchemeSet;
        ops["sheet_collection_set"] = SheetCollectionSet;
        ops["export_step"] = ExportStep;
        ops["acc_parameter_add"] = AccParameterAdd;
    }

    // ---------------------------------------------------------------- dimension type (linear or ordinate)

    /// <summary>
    /// A dimension along 'line' with the linear dimension type named in 'type' (an Ordinate type gives an ordinate
    /// dimension), or Revit's default type.
    /// </summary>
    public static Dimension LinearDim(Document doc, Obj p, View view, Line line, ReferenceArray refs)
    {
        if (!(Str(p, "type") is string name)) return doc.Create.NewDimension(view, line, refs);
        var types = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
            .Where(t => t.StyleType == DimensionStyleType.Linear || t.StyleType == DimensionStyleType.LinearFixed).ToList();
        var dt = types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                 ?? throw new ArgumentException($"No linear dimension type '{name}'. Linear types: {string.Join(", ", types.Select(t => t.Name).Distinct().OrderBy(x => x))}.");
        return doc.Create.NewDimension(view, line, refs, dt);
    }

    // ---------------------------------------------------------------- colour fill schemes

    private static Color Hex(string v, string what)
    {
        string h = (v ?? "").Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            throw new ArgumentException($"'{what}' must be a colour like #1F4E79.");
        return new Color((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
    }

    private static string ParamName(Document doc, ElementId id)
    {
        if (id.Value < 0)
        {
            try { return LabelUtils.GetLabelFor((BuiltInParameter)(int)id.Value); } catch { return id.Value.ToString(CultureInfo.InvariantCulture); }
        }
        return (doc.GetElement(id) as ParameterElement)?.Name ?? id.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A colour fill scheme of a category (rooms, areas, spaces, ducts, pipes ...): pick an existing one or copy one under a
    /// new name, colour by a parameter, set each value's colour and caption, apply it to views and place a legend.
    /// </summary>
    private static object ColorFillSchemeSet(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var cat = new ElementId(Find.Cat(doc, NeedStr(p, "category")));
        var schemes = new FilteredElementCollector(doc).OfClass(typeof(ColorFillScheme)).Cast<ColorFillScheme>().Where(s => s.CategoryId == cat).ToList();
        if (schemes.Count == 0) throw new ArgumentException($"The model has no colour fill scheme for '{Str(p, "category")}' to start from (make one in Revit first).");
        ColorFillScheme scheme;
        string name = NeedStr(p, "scheme");
        scheme = schemes.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        bool made = false;
        if (scheme == null)
        {
            string from = Str(p, "from") ?? throw new ArgumentException($"No scheme '{name}'. Schemes: {string.Join(", ", schemes.Select(s => s.Name))}. Give 'from' to copy one under this name.");
            var src = (from == "*" ? schemes.OrderBy(s => s.Name).FirstOrDefault() : schemes.FirstOrDefault(s => s.Name.Equals(from, StringComparison.OrdinalIgnoreCase)))
                      ?? throw new ArgumentException($"No scheme '{from}' to copy. Schemes: {string.Join(", ", schemes.Select(s => s.Name))}.");
            scheme = (ColorFillScheme)doc.GetElement(src.Duplicate(name));
            made = true;
        }
        if (Str(p, "title") is string title) scheme.Title = title;
        if (Str(p, "parameter") is string pname)
        {
            var ids = scheme.GetSupportedParameterIds();
            var pid = ids.FirstOrDefault(id => ParamName(doc, id).Equals(pname, StringComparison.OrdinalIgnoreCase));
            if (pid == null) throw new ArgumentException($"The scheme cannot colour by '{pname}'. Parameters: {string.Join(", ", ids.Select(id => ParamName(doc, id)).OrderBy(x => x))}.");
            scheme.ParameterDefinition = pid;
            doc.Regenerate();
        }
        var set = new List<object>();
        if (Has(p, "entries"))
        {
            var entries = scheme.GetEntries().ToList();
            foreach (var en in ObjList(p, "entries", 300))
            {
                string value = Convert.ToString(en.TryGetValue("value", out var v) ? v : null, CultureInfo.InvariantCulture)
                               ?? throw new ArgumentException("Each entry needs 'value'.");
                var entry = entries.FirstOrDefault(x => x.StorageType == StorageType.String && string.Equals(x.GetStringValue(), value, StringComparison.OrdinalIgnoreCase))
                            ?? entries.FirstOrDefault(x => string.Equals(x.Caption, value, StringComparison.OrdinalIgnoreCase));
                bool add = entry == null;
                if (add)
                {
                    if (scheme.StorageType != StorageType.String)
                        throw new ArgumentException($"No entry '{value}' in the scheme; new entries are added for text parameters only (this scheme's parameter is {scheme.StorageType}).");
                    entry = new ColorFillSchemeEntry(StorageType.String);
                    entry.SetStringValue(value);
                }
                if (Str(en, "color") is string col) entry.Color = Hex(col, "color");
                if (Str(en, "caption") is string cap) entry.Caption = cap;
                if (Bool(en, "visible") is bool vis) entry.IsVisible = vis;
                if (add) scheme.AddEntry(entry); else scheme.UpdateEntry(entry);
                set.Add(new { value, added = add });
            }
        }
        var applied = new List<string>();
        foreach (string vn in StrList(p, "views", 50, required: false) ?? new List<string>())
        {
            var view = Find.ViewOf(doc, vn);
            if (!view.CanApplyColorFillScheme(cat, scheme.Id)) throw new ArgumentException($"'{view.Name}' cannot show this scheme (a plan or section of the category's elements can).");
            view.SetColorFillSchemeId(cat, scheme.Id);
            applied.Add(view.Name);
            if (Has(p, "legend_at"))
            {
                var at = U.NeedPt(p, "legend_at");
                ColorFillLegend.Create(doc, view.Id, cat, at);
            }
        }
        doc.Regenerate();
        return new
        {
            scheme = scheme.Name, created = made, title = scheme.Title, parameter = ParamName(doc, scheme.ParameterDefinition),
            entries = scheme.GetEntries().Select(x => new { value = x.StorageType == StorageType.String ? x.GetStringValue() : x.Caption, caption = x.Caption,
                                                            color = $"#{x.Color.Red:X2}{x.Color.Green:X2}{x.Color.Blue:X2}", visible = x.IsVisible }).ToList(),
            set, views = applied, legends = Has(p, "legend_at") ? applied.Count : 0,
        };
    }

    // ---------------------------------------------------------------- sheet collections and STEP (Revit 2027)

    private static object SheetCollectionSet(EditContext c, Obj p)
    {
#if REVIT2027
        var doc = c.Doc;
        string name = NeedStr(p, "name");
        var col = new FilteredElementCollector(doc).OfClass(typeof(SheetCollection)).Cast<SheetCollection>()
            .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        bool made = col == null;
        if (made) col = SheetCollection.Create(doc, name);
        var moved = new List<string>();
        foreach (string s in StrList(p, "sheets", 1000, required: false) ?? new List<string>())
        {
            var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(v => v.SheetNumber.Equals(s, StringComparison.OrdinalIgnoreCase) || v.Name.Equals(s, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No sheet '{s}' (number or name).");
            sheet.SheetCollectionId = col.Id;
            moved.Add(sheet.SheetNumber);
        }
        return new { collection = col.Name, id = col.Id.Value, created = made, sheets = moved };
#else
        throw new NotSupportedException("Sheet collections are new in Revit 2027; this is Revit 2024.");
#endif
    }

    private static object ExportStep(EditContext c, Obj p)
    {
#if REVIT2027
        string folder = EditContext.Folder(p);
        string name = EditContext.SafeName(Str(p, "name") ?? IOPath.GetFileNameWithoutExtension(c.Doc.Title));
        c.GuardOverwrite(p, folder, name + ".stp");
        c.GuardOverwrite(p, folder, name + ".step");
        var options = new STEPExportOptions();
        // STEP exports what a 3D view shows: 'view', else the active view when it is 3D, else the default 3D view
        var v3 = (Has(p, "view") ? Find.ViewOf(c.Doc, NeedRef(p, "view")) : c.Doc.ActiveView) as View3D;
        v3 = v3 != null && !v3.IsTemplate ? v3 : new FilteredElementCollector(c.Doc).OfClass(typeof(View3D)).Cast<View3D>()
                 .Where(x => !x.IsTemplate && !x.IsPerspective).OrderBy(x => x.Name == "{3D}" ? 0 : 1).ThenBy(x => x.Name).FirstOrDefault()
             ?? throw new ArgumentException("STEP export needs a 3D view: give 'view' (a 3D view) or make one.");
        options.ViewId = v3.Id;
        if (Str(p, "protocol") is string proto)
        {
            if (!Enum.TryParse(proto, true, out STEPApplicationProtocol ap))
                throw new ArgumentException($"'protocol' must be one of: {string.Join(", ", Enum.GetNames(typeof(STEPApplicationProtocol)))}.");
            options.ApplicationProtocol = ap;
        }
        c.DeferOutput(folder, p, stage =>
        {
            if (!c.Doc.Export(stage, name, options)) throw new InvalidOperationException("STEP export failed.");
        });
        return new { folder, name, view = v3.Name, protocol = options.ApplicationProtocol.ToString(), status = "queued" };
#else
        throw new NotSupportedException("STEP export is new in Revit 2027; this is Revit 2024.");
#endif
    }

    // ---------------------------------------------------------------- Autodesk (ACC) parameters service

    /// <summary>
    /// Add a parameter from the Autodesk parameters service (ACC) to the model by its id, bound to the categories given.
    /// The user must be signed in to Revit with access to that parameter.
    /// </summary>
    private static object AccParameterAdd(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string id = NeedStr(p, "id");
        if (!id.StartsWith("parameters.", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("'id' is the parameter's id from the Autodesk parameters service, e.g. 'parameters.<collection>:<name>-1.0.0'.");
        var tid = new ForgeTypeId(id);
        var cats = new HashSet<ElementId>(Find.Cats(doc, p, "categories").Select(k => new ElementId(k)));
        if (cats.Count == 0) throw new ArgumentException("Give 'categories' to bind the parameter to.");
        ParameterDownloadOptions options;
#if REVIT2027
        string region = Str(p, "region") ?? "US";   // Revit 2027 asks for the hub's region (US, EMEA, AUS ...)
        try { options = ParameterUtils.DownloadParameterOptions(tid, region); }
#else
        string region = Str(p, "region");
        try { options = region != null ? ParameterUtils.DownloadParameterOptions(tid, region) : ParameterUtils.DownloadParameterOptions(tid); }
#endif
        catch (Exception ex) { throw new InvalidOperationException($"The parameters service did not give '{id}' ({ex.Message}). Check the id, and that Revit is signed in with access to it."); }
        options.SetCategories(cats);
        if (Bool(p, "instance") is bool inst) options.IsInstance = inst;
        if (Bool(p, "visible") is bool vis) options.Visible = vis;
        SharedParameterElement sp;
#if REVIT2027
        try { sp = ParameterUtils.DownloadParameter(doc, options, tid, region); }
#else
        try { sp = region != null ? ParameterUtils.DownloadParameter(doc, options, tid, region) : ParameterUtils.DownloadParameter(doc, options, tid); }
#endif
        catch (Exception ex) { throw new InvalidOperationException($"Revit could not add '{id}' from the parameters service: {ex.Message}"); }
        return new { name = sp.Name, guid = sp.GuidValue.ToString(), instance = options.IsInstance, categories = cats.Count };
    }
}
