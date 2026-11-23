namespace RevitMcpBridge;

/// <summary>
/// Material fixer: give elements the right material by rule. Loadable families (columns, framing, footings) get their
/// Structural Material set (on the type when every element of that type is being fixed, else per element); system types
/// (slabs, walls, foundation slabs) get the material of the matching layer changed in the type, only when every element
/// of that type is being fixed (otherwise the type is reported as shared: duplicate it first).
/// </summary>
internal static class MaterialOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["fix_materials"] = FixMaterials;
    }

    private sealed class Rule
    {
        public Material Material;
        public string Problem = "any";            // steel | default | none | any
        public List<BuiltInCategory> Cats;
        public string Family, Type, Level, From;   // filters; From = current material name contains
        public int Index;
    }

    private static List<Rule> Rules(Document doc, Obj p)
    {
        var rules = new List<Rule>();
        if (Has(p, "steel_material"))
            rules.Add(new Rule { Material = Materials.Named(doc, NeedStr(p, "steel_material"), "steel_material"), Problem = "steel" });
        if (Has(p, "concrete_material"))
            rules.Add(new Rule { Material = Materials.Named(doc, NeedStr(p, "concrete_material"), "concrete_material"), Problem = "default" });
        var list = ObjList(p, "rules", 100, required: false);
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i]; string what = $"rule {i + 1}";
            rules.Add(new Rule
            {
                Material = Materials.Named(doc, NeedStr(r, "material"), what),
                Problem = Choice(r, "problem", "any", "any", "steel", "default", "none"),
                Cats = Has(r, "category") ? Find.Cats(doc, r, "category", Find.Structural) : null,
                Family = Str(r, "family_contains"), Type = Str(r, "type_contains"),
                Level = Has(r, "level") ? Find.LevelOf(doc, Ref(r, "level")).Name : null,
                From = Str(r, "from_contains"),
            });
        }
        if (rules.Count == 0) throw new ArgumentException("Give 'steel_material', 'concrete_material' and / or 'rules' [{material, problem?, category?, family_contains?, type_contains?, level?, from_contains?}].");
        for (int i = 0; i < rules.Count; i++) rules[i].Index = i + 1;
        return rules;
    }

    /// <summary>The material of 'mats' this rule replaces, or null if the rule does not apply ("(none)" for no material).</summary>
    private static string Applies(Rule r, Document doc, Element e, List<Material> mats, bool steel, string level)
    {
        if (r.Cats != null && (e.Category == null || !r.Cats.Contains((BuiltInCategory)(int)e.Category.Id.Value))) return null;
        var t = doc.GetElement(e.GetTypeId()) as ElementType;
        if (r.Family != null && (t == null || t.FamilyName.IndexOf(r.Family, StringComparison.OrdinalIgnoreCase) < 0)) return null;
        if (r.Type != null && (t == null || t.Name.IndexOf(r.Type, StringComparison.OrdinalIgnoreCase) < 0)) return null;
        if (r.Level != null && level != r.Level) return null;
        if (mats.Count == 0) return r.Problem == "none" || r.Problem == "any" && r.From == null ? "(none)" : null;
        foreach (var m in mats)
        {
            if (m.Id == r.Material.Id) continue;
            if (r.From != null && m.Name.IndexOf(r.From, StringComparison.OrdinalIgnoreCase) < 0) continue;
            bool metal = Materials.IsMetal(m);
            if (r.Problem == "steel" && !(steel && !metal)) continue;
            if (r.Problem == "default" && (steel || !Materials.Generic.IsMatch(m.Name))) continue;
            if (r.Problem == "none") continue;
            return m.Name;
        }
        return null;
    }

    private static object FixMaterials(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var rules = Rules(doc, p);
        var cats = Find.Cats(doc, p, "category", Find.Structural);
        string familyContains = Str(p, "family_contains");
        string onlyLevel = Has(p, "level") ? Find.LevelOf(doc, Ref(p, "level")).Name : null;

        var els = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(cats))
            .Where(e => e.Category != null).ToList();
        var byType = els.GroupBy(e => e.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());

        // 1. decide: element -> (rule, material it replaces)
        var plan = new List<(Element e, Rule r, string from)>();
        foreach (var e in els)
        {
            string level = Describe.LevelName(doc, e);
            if (onlyLevel != null && level != onlyLevel) continue;
            var mats = Materials.Of(doc, e);
            bool steel = Materials.IsSteel(doc, e, familyContains);
            foreach (var r in rules)
            {
                string from = Applies(r, doc, e, mats, steel, level);
                if (from != null) { plan.Add((e, r, from)); break; }
            }
        }

        // 2. apply
        var done = new List<(string cat, string from, string to, string where, int n)>();
        var toVerify = new List<(Element e, string from)>();
        var skipped = new List<object>();
        void Count(Element e, string from, Material to, string where) => done.Add((e.Category.Name, from, to.Name, where, 1));

        // per (type, rule): how many planned elements; equal to the type's element count means the whole type is fixed
        var plannedPerType = plan.GroupBy(x => (x.e.GetTypeId().Value, x.r.Index)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var g in plan.GroupBy(x => (type: x.e.GetTypeId().Value, rule: x.r.Index, from: x.from)))
        {
            var items = g.ToList(); var r = items[0].r; string from = g.Key.from;
            var type = doc.GetElement(new ElementId(g.Key.type)) as ElementType;
            bool wholeType = type != null && byType.TryGetValue(g.Key.type, out int total) && total == plannedPerType[(g.Key.type, r.Index)];

            if (type is HostObjAttributes host)   // slabs, walls, roofs, foundation slabs: layer materials in the type
            {
                if (!wholeType)
                {
                    skipped.Add(new { type = Find.TypeLabel(type), elements = items.Count, reason = "type shared with elements outside this rule: duplicate the type for them first" });
                    continue;
                }
                var cs = host.GetCompoundStructure();
                if (cs == null) { skipped.Add(new { type = Find.TypeLabel(type), elements = items.Count, reason = "no layers (in-place or non-layered type)" }); continue; }
                bool changed = false;
                for (int i = 0; i < cs.LayerCount; i++)
                {
                    var lm = doc.GetElement(cs.GetMaterialId(i)) as Material;
                    bool match = from == "(none)" ? lm == null : lm != null && lm.Name == from;
                    if (match) { cs.SetMaterialId(i, r.Material.Id); changed = true; }
                }
                if (!changed) { skipped.Add(new { type = Find.TypeLabel(type), elements = items.Count, reason = $"'{from}' is not a layer material of the type (it may come from a paint or a sub-element)" }); continue; }
                host.SetCompoundStructure(cs);
                foreach (var x in items) Count(x.e, from, r.Material, "type layers");
                continue;
            }

            // Loadable families: the family's Structural Material is an instance parameter in most structural families
            // (and an unused built-in on the type), or a type parameter in some. Use the instance one when it is writable.
            var first = items[0].e;
            var ip0 = Find.Builtin(first, "STRUCTURAL_MATERIAL_PARAM") ?? first.LookupParameter("Structural Material");
            bool instanceParam = ip0 != null && !ip0.IsReadOnly && ip0.StorageType == StorageType.ElementId;
            var tp = type == null ? null : type.LookupParameter("Structural Material") ?? Find.Builtin(type, "STRUCTURAL_MATERIAL_PARAM");
            if (!instanceParam && wholeType && tp != null && !tp.IsReadOnly && tp.StorageType == StorageType.ElementId)
            {
                tp.Set(r.Material.Id);
                foreach (var x in items) { Count(x.e, from, r.Material, "type"); toVerify.Add((x.e, from)); }
                continue;
            }
            foreach (var x in items)
            {
                var ip = Find.Builtin(x.e, "STRUCTURAL_MATERIAL_PARAM") ?? x.e.LookupParameter("Structural Material");
                if (ip != null && !ip.IsReadOnly && ip.StorageType == StorageType.ElementId) { ip.Set(r.Material.Id); Count(x.e, from, r.Material, "element"); toVerify.Add((x.e, from)); }
                else skipped.Add(new { id = x.e.Id.Value, type = type == null ? null : Find.TypeLabel(type), reason = "no writable Structural Material parameter (the family's material is fixed inside the family)" });
            }
        }

        // 3. verify: the material must really have changed in the element's geometry
        doc.Regenerate();
        var notEffective = new List<(Element e, string from)>();
        foreach (var (e, from) in toVerify)
            if (Materials.Of(doc, e).Any(m => m.Name == from)) notEffective.Add((e, from));
        if (notEffective.Count > 0)
        {
            c.Warn($"{notEffective.Count} element(s) took the new Structural Material but their geometry still shows the old one: " +
                   "the family assigns its geometry material directly (not by Structural Material). family_material_set (or family_material_link) fixes those families.");
        }
        var summary = done.GroupBy(d => (d.cat, d.from, d.to, d.where)).OrderByDescending(g => g.Count())
            .Select(g => new { category = g.Key.cat, from = g.Key.from, to = g.Key.to, setOn = g.Key.where, elements = g.Count() }).ToList();
        var stillOld = notEffective.GroupBy(n => (n.e.GetTypeId().Value, n.from)).OrderByDescending(g => g.Count())
            .Select(g => new { type = doc.GetElement(new ElementId(g.Key.Item1)) is ElementType et ? Find.TypeLabel(et) : null, material = g.Key.from, elements = g.Count(),
                               sampleIds = g.Take(10).Select(n => n.e.Id.Value).ToList() }).ToList();
        if (skipped.Count > 0) c.Warn($"{skipped.Count} element / type group(s) were not changed (see skipped).");
        return new
        {
            elementsMatched = plan.Count, elementsChanged = done.Count - notEffective.Count, changes = summary,
            notEffective = notEffective.Count, notEffectiveByType = stillOld,
            skipped = skipped.Take(100).ToList(), skippedCount = skipped.Count,
            note = "Each changed element is re-read after the change; 'notEffective' lists those whose geometry still shows the old material (fixed inside their family).",
        };
    }
}
