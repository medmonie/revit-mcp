namespace RevitMcpBridge;

/// <summary>Looking things up in the model by the names Claude and the user use.</summary>
internal static class Find
{
    public static Element Elem(Document doc, ElementId id) =>
        doc.GetElement(id) ?? throw new ArgumentException($"No element with id {id.Value} in this model.");

    private static bool Same(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsId(string s, out ElementId id)
    {
        id = ElementId.InvalidElementId;
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return false;
        id = new ElementId(n);
        return true;
    }

    // ---------------------------------------------------------------- levels and views

    public static List<Level> Levels(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();

    public static Level LevelOf(Document doc, string nameOrId)
    {
        var levels = Levels(doc);
        var byName = levels.FirstOrDefault(l => Same(l.Name, nameOrId));
        if (byName != null) return byName;
        if (IsId(nameOrId, out var id) && doc.GetElement(id) is Level byId) return byId;
        throw new ArgumentException($"No level named '{nameOrId}'. Levels: {string.Join(", ", levels.Select(l => l.Name))}");
    }

    public static Level NeedLevel(Document doc, Obj p, string name = "level") => LevelOf(doc, NeedRef(p, name));

    /// <summary>A view or sheet by name (sheets also by number) or id; templates are never returned.</summary>
    public static View ViewOf(Document doc, string nameOrId)
    {
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        var hit = views.FirstOrDefault(v => Same(v.Name, nameOrId))
                  ?? views.FirstOrDefault(v => v is ViewSheet s && Same(s.SheetNumber, nameOrId));
        if (hit != null) return hit;
        if (IsId(nameOrId, out var id) && doc.GetElement(id) is View byId && !byId.IsTemplate) return byId;
        throw new ArgumentException($"No view or sheet named '{nameOrId}'. Use list_views / list_sheets.");
    }

    /// <summary>The view named by the parameter, or the active view when it is absent.</summary>
    public static View ViewOrActive(Document doc, Obj p, string name = "view")
    {
        string given = Ref(p, name);
        if (!string.IsNullOrEmpty(given)) return ViewOf(doc, given);
        return doc.ActiveView ?? throw new InvalidOperationException("There is no active view; give the 'view' parameter.");
    }

    // ---------------------------------------------------------------- categories

    private static readonly Dictionary<string, BuiltInCategory> Aliases = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
    {
        ["columns"] = BuiltInCategory.OST_StructuralColumns, ["structural_columns"] = BuiltInCategory.OST_StructuralColumns,
        ["architectural_columns"] = BuiltInCategory.OST_Columns,
        ["beams"] = BuiltInCategory.OST_StructuralFraming, ["framing"] = BuiltInCategory.OST_StructuralFraming,
        ["structural_framing"] = BuiltInCategory.OST_StructuralFraming,
        ["foundations"] = BuiltInCategory.OST_StructuralFoundation, ["structural_foundations"] = BuiltInCategory.OST_StructuralFoundation,
        ["footings"] = BuiltInCategory.OST_StructuralFoundation,
        ["walls"] = BuiltInCategory.OST_Walls, ["floors"] = BuiltInCategory.OST_Floors, ["slabs"] = BuiltInCategory.OST_Floors,
        ["roofs"] = BuiltInCategory.OST_Roofs, ["ceilings"] = BuiltInCategory.OST_Ceilings, ["stairs"] = BuiltInCategory.OST_Stairs,
        ["ramps"] = BuiltInCategory.OST_Ramps, ["doors"] = BuiltInCategory.OST_Doors, ["windows"] = BuiltInCategory.OST_Windows,
        ["rebar"] = BuiltInCategory.OST_Rebar, ["rooms"] = BuiltInCategory.OST_Rooms,
        ["grids"] = BuiltInCategory.OST_Grids, ["levels"] = BuiltInCategory.OST_Levels,
        ["generic_models"] = BuiltInCategory.OST_GenericModel, ["title_blocks"] = BuiltInCategory.OST_TitleBlocks,
        ["pipes"] = BuiltInCategory.OST_PipeCurves, ["pipe_fittings"] = BuiltInCategory.OST_PipeFitting,
        ["pipe_accessories"] = BuiltInCategory.OST_PipeAccessory, ["ducts"] = BuiltInCategory.OST_DuctCurves,
        ["mechanical_equipment"] = BuiltInCategory.OST_MechanicalEquipment, ["plumbing_fixtures"] = BuiltInCategory.OST_PlumbingFixtures,
        ["furniture"] = BuiltInCategory.OST_Furniture, ["curtain_panels"] = BuiltInCategory.OST_CurtainWallPanels,
        ["openings"] = BuiltInCategory.OST_ShaftOpening, ["sheets"] = BuiltInCategory.OST_Sheets,
    };

    public static string CategoryHelp => "an alias (" + string.Join(", ", Aliases.Keys.Take(14)) + ", ...), a Revit category name " +
                                         "as shown in the UI (e.g. 'Structural Columns') or a BuiltInCategory (e.g. OST_Walls)";

    public static BuiltInCategory Cat(Document doc, string name)
    {
        name = (name ?? "").Trim();
        if (Aliases.TryGetValue(name.Replace(' ', '_'), out var alias)) return alias;
        if (name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(name, true, out BuiltInCategory bic)) return bic;
        foreach (Category c in doc.Settings.Categories)
            if (Same(c.Name, name)) return (BuiltInCategory)(int)c.Id.Value;
        throw new ArgumentException($"Unknown category '{name}'. Give {CategoryHelp}.");
    }

    public static List<BuiltInCategory> Cats(Document doc, Obj p, string name, params BuiltInCategory[] dflt)
    {
        if (!Has(p, name)) return dflt.ToList();
        if (p[name] is string one) return new List<BuiltInCategory> { Cat(doc, one) };
        return StrList(p, name, 50).Select(s => Cat(doc, s)).ToList();
    }

    /// <summary>
    /// The model elements of these categories, limited to what the 'view' parameter shows and to the 'level' parameter
    /// when they are given: the scope of take-offs, checks and fixes.
    /// </summary>
    public static IEnumerable<Element> Scope(Document doc, Obj p, List<BuiltInCategory> cats)
    {
        var col = Has(p, "view") ? new FilteredElementCollector(doc, ViewOf(doc, Ref(p, "view")).Id) : new FilteredElementCollector(doc);
        IEnumerable<Element> src = cats.Count == 1
            ? col.WhereElementIsNotElementType().OfCategory(cats[0])
            : col.WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(cats));
        src = src.Where(e => e.Category != null);
        if (Has(p, "level"))
        {
            string levelName = LevelOf(doc, Ref(p, "level")).Name;
            src = src.Where(e => Describe.LevelName(doc, e) == levelName);
        }
        return src;
    }

    public static readonly BuiltInCategory[] Structural =
    {
        BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFoundation,
    };

    // ---------------------------------------------------------------- types

    public static string TypeLabel(ElementType t) => string.IsNullOrEmpty(t.FamilyName) ? t.Name : t.FamilyName + " : " + t.Name;

    /// <summary>
    /// A type by "Type name", "Family : Type name" or id, optionally limited to a category and/or a class
    /// (FamilySymbol, WallType, FloorType, ...). An ambiguous or unknown name fails and lists the choices.
    /// </summary>
    public static ElementType TypeOf(Document doc, string spec, BuiltInCategory? cat, Type cls = null)
    {
        if (IsId(spec, out var id) && doc.GetElement(id) is ElementType byId)
        {
            if ((cls != null && !cls.IsInstanceOfType(byId)) || (cat != null && byId.Category?.Id.Value != (long)cat.Value))
                throw new ArgumentException($"Type id {id.Value} is outside the expected category or type class.");
            return byId;
        }

        var col = new FilteredElementCollector(doc).WhereElementIsElementType();
        if (cls != null) col = col.OfClass(cls);
        if (cat != null) col = col.OfCategory(cat.Value);
        var all = col.Cast<ElementType>().ToList();

        string family = null, type = spec.Trim();
        int colon = spec.IndexOf(':');
        if (colon > 0) { family = spec.Substring(0, colon).Trim(); type = spec.Substring(colon + 1).Trim(); }

        var hits = all.Where(t => Same(t.Name, type) && (family == null || Same(t.FamilyName, family))).ToList();
        if (hits.Count == 0 && family != null) hits = all.Where(t => Same(t.Name, spec)).ToList();   // a type name containing ':'
        if (hits.Count == 1) return hits[0];

        string where = cat != null ? " in category " + cat.Value : "";
        if (hits.Count > 1)
            throw new ArgumentException($"Type '{spec}' is ambiguous{where}; write it as 'Family : Type'. Matches: {string.Join("; ", hits.Select(TypeLabel))}");
        string some = string.Join("; ", all.Select(TypeLabel).OrderBy(s => s).Take(40));
        throw new ArgumentException($"No type '{spec}'{where}. " + (all.Count == 0
            ? "The model has no types there: load a family first (family_load) or use a template that has them."
            : $"Available ({all.Count}): {some}{(all.Count > 40 ? "; ..." : "")}"));
    }

    /// <summary>A loadable-family type, activated so that instances can be placed.</summary>
    public static FamilySymbol Symbol(Document doc, string spec, BuiltInCategory cat)
    {
        var symbol = (FamilySymbol)TypeOf(doc, spec, cat, typeof(FamilySymbol));
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        return symbol;
    }

    // ---------------------------------------------------------------- families

    public static List<Family> Families(Document doc) => new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();

    public static Family FamilyNamed(Document doc, string name) =>
        Families(doc).FirstOrDefault(f => Same(f.Name, name)) ?? throw new ArgumentException($"No family named '{name}'.");

    // ---------------------------------------------------------------- parameters

    private static readonly Dictionary<string, BuiltInParameter?> BipCache = new Dictionary<string, BuiltInParameter?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A BuiltInParameter by its enum name, resolved at run time so a name missing in one Revit release cannot break the build.</summary>
    public static BuiltInParameter? Bip(string name)
    {
        if (!BipCache.TryGetValue(name, out var value))
        {
            value = Enum.TryParse(name, true, out BuiltInParameter b) && !long.TryParse(name, out _) ? b : (BuiltInParameter?)null;
            BipCache[name] = value;
        }
        return value;
    }

    /// <summary>The element's built-in parameter of that enum name, or null.</summary>
    public static Parameter Builtin(Element e, string bipName) => Bip(bipName) is BuiltInParameter b ? e.get_Parameter(b) : null;

    public static string ParameterKey(Parameter p)
    {
        if (p.IsShared) return "guid:" + p.GUID.ToString("D");
        if (p.Definition is InternalDefinition d && d.BuiltInParameter != BuiltInParameter.INVALID)
            return "bip:" + d.BuiltInParameter;
        return "id:" + p.Id.Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Resolve a stable identifier or an unambiguous display name.</summary>
    public static Parameter ParamOrNull(Element e, string name)
    {
        if (name.StartsWith("bip:", StringComparison.OrdinalIgnoreCase))
            return Builtin(e, name.Substring(4));
        if (name.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            return Guid.TryParse(name.Substring(5), out var guid) ? e.get_Parameter(guid)
                : throw new ArgumentException($"Invalid shared parameter selector '{name}'.");
        if (name.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(name.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? e.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id.Value == id)
                : throw new ArgumentException($"Invalid parameter id selector '{name}'.");

        if (Bip(name) is BuiltInParameter builtin) return e.get_Parameter(builtin);
        var matches = e.GetParameters(name);
        if (matches.Count <= 1) return matches.FirstOrDefault();
        if (matches.Count == 2)
        {
            var first = matches[0];
            var second = matches[1];
            Parameter Pair(BuiltInParameter primary, BuiltInParameter reporting)
            {
                if (first.Id.Value == (long)primary && second.Id.Value == (long)reporting) return first;
                if (second.Id.Value == (long)primary && first.Id.Value == (long)reporting) return second;
                return null;
            }
            if (e is ViewSheet && Pair(BuiltInParameter.SHEET_NUMBER, BuiltInParameter.VIEWER_SHEET_NUMBER) is Parameter sheetNumber)
                return sheetNumber;
            long? category = e.Category?.Id.Value;
            if ((category == (long)BuiltInCategory.OST_StructuralColumns || category == (long)BuiltInCategory.OST_Columns)
                && SameValue(first, second))
            {
                var family = Pair(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.SCHEDULE_BASE_LEVEL_PARAM)
                    ?? Pair(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, BuiltInParameter.SCHEDULE_BASE_LEVEL_OFFSET_PARAM)
                    ?? Pair(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, BuiltInParameter.SCHEDULE_TOP_LEVEL_PARAM)
                    ?? Pair(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, BuiltInParameter.SCHEDULE_TOP_LEVEL_OFFSET_PARAM);
                if (family != null) return family.IsReadOnly ? matches.FirstOrDefault(m => !m.IsReadOnly) ?? family : family;
            }
        }
        throw new ArgumentException($"Element {e.Id.Value} has several parameters named '{name}'. Use one of: " +
            string.Join(", ", matches.Select(ParameterKey)) + ".");
    }

    private static bool SameValue(Parameter a, Parameter b)
    {
        if (a.StorageType != b.StorageType || a.HasValue != b.HasValue) return false;
        switch (a.StorageType)
        {
            case StorageType.Double: return Math.Abs(a.AsDouble() - b.AsDouble()) < 1e-9;
            case StorageType.Integer: return a.AsInteger() == b.AsInteger();
            case StorageType.ElementId: return a.AsElementId() == b.AsElementId();
            case StorageType.String: return string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal);
            default: return true;
        }
    }

    public static Parameter Param(Element e, string name)
    {
        var p = ParamOrNull(e, name);
        if (p != null) return p;
        var names = e.Parameters.Cast<Parameter>().Select(x => x.Definition?.Name).Where(n => n != null).Distinct().OrderBy(n => n).Take(60);
        throw new ArgumentException($"Element {e.Id.Value} has no parameter '{name}'. It has: {string.Join(", ", names)}");
    }
}
