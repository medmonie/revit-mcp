using System.Text.RegularExpressions;

namespace RevitMcpBridge;

/// <summary>Model QA: materials and naming. Nothing here changes anything.</summary>
internal static class QaRead
{
    private static Regex Pattern(Obj p, string name, bool required)
    {
        string text = required ? NeedStr(p, name) : Str(p, name);
        if (text == null) return null;
        try { return new Regex(text, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex) { throw new ArgumentException($"'{name}' is not a valid regular expression: {ex.Message}"); }
    }

    private sealed class Row
    {
        public string Category, Problem, Material;
        public int Count;
        public double Volume;
        public readonly List<long> Ids = new List<long>();
    }

    /// <summary>
    /// Elements with no material, a Revit default material, a material outside the firm's naming pattern, or steel
    /// families carrying a non-metal material.
    /// </summary>
    public static object CheckMaterials(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var cats = Find.Cats(doc, p, "category", Find.Structural);
        var accepted = Pattern(p, "pattern", false);
        string familyContains = Str(p, "family_contains");
        var rows = new Dictionary<string, Row>();
        int checkedCount = 0;

        void Add(Element e, string problem, string material)
        {
            string key = e.Category.Name + "|" + problem + "|" + material;
            if (!rows.TryGetValue(key, out var r)) rows[key] = r = new Row { Category = e.Category.Name, Problem = problem, Material = material };
            r.Count++;
            var vol = Find.Builtin(e, "HOST_VOLUME_COMPUTED");
            if (vol != null && vol.HasValue) r.Volume += vol.AsDouble();
            if (r.Ids.Count < 25) r.Ids.Add(e.Id.Value);
        }

        foreach (Element e in Find.Scope(doc, p, cats))
        {
            checkedCount++;
            var mats = Materials.Of(doc, e);

            if (mats.Count == 0) { Add(e, "no material", "(none)"); continue; }
            bool steel = Materials.IsSteel(doc, e, familyContains);
            foreach (var m in mats)
            {
                if (steel && !Materials.IsMetal(m))
                    Add(e, "steel member with a non-metal material", m.Name);
                else if (accepted != null ? !accepted.IsMatch(m.Name) : Materials.Generic.IsMatch(m.Name))
                    Add(e, accepted != null ? "material name outside the pattern" : "Revit default material", m.Name);
            }
        }

        var list = rows.Values.OrderBy(r => r.Category).ThenBy(r => r.Problem).ThenBy(r => r.Material).Select(r => new
        {
            category = r.Category, problem = r.Problem, material = r.Material, elements = r.Count,
            volume_m3 = U.Volume(r.Volume), sampleIds = r.Ids,
        }).ToList();
        return new
        {
            checkedElements = checkedCount,
            elementsWithProblems = rows.Values.Sum(r => r.Count),
            rows = list,
            note = accepted != null
                ? "Flagged: no material, materials whose name does not match 'pattern', steel members with a non-metal material."
                : "Flagged: no material, Revit default materials (Default, Concrete Cast-in-Place / Precast ..., Generic ...), steel " +
                  "members with a non-metal material. Pass 'pattern' (e.g. '^KTR15-') to check against your own naming instead. " +
                  "An element can appear in more than one row.",
        };
    }

    /// <summary>Names that do not match a pattern (views, sheets, types, levels, grids, materials, families, view templates).</summary>
    public static object CheckNames(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        string kind = Choice(p, "kind", null, "views", "sheets", "types", "levels", "grids", "materials", "families", "view_templates");
        var pattern = Pattern(p, "pattern", true);
        string field = Choice(p, "field", "name", "name", "number");
        IEnumerable<(long id, string name, string extra)> items;
        switch (kind)
        {
            case "views":
                items = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && !(v is ViewSheet) && v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser
                                && v.ViewType != ViewType.Internal && v.ViewType != ViewType.Undefined)
                    .Select(v => (v.Id.Value, v.Name, v.ViewType.ToString()));
                break;
            case "view_templates":
                items = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate)
                    .Select(v => (v.Id.Value, v.Name, v.ViewType.ToString()));
                break;
            case "sheets":
                items = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate)
                    .Select(s => (s.Id.Value, field == "number" ? s.SheetNumber : s.Name, field == "number" ? s.Name : s.SheetNumber));
                break;
            case "types":
            {
                var col = new FilteredElementCollector(doc).WhereElementIsElementType();
                if (Has(p, "category")) col = col.OfCategory(Find.Cat(doc, Str(p, "category")));
                items = col.Cast<ElementType>().Where(t => t.Category != null && t.Category.CategoryType == CategoryType.Model)
                    .Select(t => (t.Id.Value, t.Name, t.FamilyName));
                break;
            }
            case "levels": items = Find.Levels(doc).Select(l => (l.Id.Value, l.Name, (string)null)); break;
            case "grids": items = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Select(g => (g.Id.Value, g.Name, (string)null)); break;
            case "materials": items = new FilteredElementCollector(doc).OfClass(typeof(Material)).Select(m => (m.Id.Value, m.Name, (string)null)); break;
            default:
                items = Find.Families(doc)
                    .Select(f => (f.Id.Value, f.Name, f.FamilyCategory?.Name));
                break;
        }
        var all = items.ToList();
        var bad = all.Where(i => !pattern.IsMatch(i.name ?? "")).OrderBy(i => i.name, StringComparer.OrdinalIgnoreCase)
            .Select(i => new { id = i.id, name = i.name, info = i.extra });
        var o = (Obj)Describe.Page(p, bad, "nonMatching", 300, 5000);
        o["kind"] = kind;
        o["checked"] = all.Count;
        o["pattern"] = pattern.ToString();
        return o;
    }
}
