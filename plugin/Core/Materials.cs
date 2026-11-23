using System.Text.RegularExpressions;

namespace RevitMcpBridge;

/// <summary>Materials of elements: what an element is made of, whether it is steel, whether a material is a Revit default.</summary>
internal static class Materials
{
    /// <summary>Revit's out-of-the-box materials, which usually mean nobody chose one.</summary>
    public static readonly Regex Generic = new Regex(
        @"^(Default|Default (Wall|Floor|Roof|Form)|Concrete, Cast-in-Place.*|Concrete, Precast.*|Generic.*|<By Category>)$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// The materials an element shows: those of its geometry (including category materials of 'By Category' solids)
    /// plus, for family instances, its Structural Material.
    /// </summary>
    public static List<Material> Of(Document doc, Element e)
    {
        var mats = new List<Material>();
        try { mats.AddRange(e.GetMaterialIds(false).Select(id => doc.GetElement(id)).OfType<Material>()); } catch { /* no geometry */ }
        if (e is FamilyInstance fi && doc.GetElement(fi.StructuralMaterialId) is Material sm && mats.All(m => m.Id != sm.Id)) mats.Add(sm);
        return mats;
    }

    /// <summary>Material class Metal or Steel (Revit's own steel grades use class 'Steel').</summary>
    public static bool IsMetal(Material m) =>
        string.Equals(m.MaterialClass, "Metal", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(m.MaterialClass, "Steel", StringComparison.OrdinalIgnoreCase);

    public static string QuantityKind(Document doc, Material material)
    {
        if (material == null) return "unclassified";
        string materialClass = material.MaterialClass?.Trim();
        if (doc.GetElement(material.StructuralAssetId) is PropertySetElement asset)
        {
            using (var structural = asset.GetStructuralAsset())
                if (structural != null && structural.StructuralAssetClass != StructuralAssetClass.Generic)
                    return structural.StructuralAssetClass == StructuralAssetClass.Concrete ? "concrete" : "non_concrete";
        }
        if (string.Equals(materialClass, "Concrete", StringComparison.OrdinalIgnoreCase)) return "concrete";
        return string.IsNullOrWhiteSpace(materialClass) || string.Equals(materialClass, "Generic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(materialClass, "Default", StringComparison.OrdinalIgnoreCase) ? "unclassified" : "non_concrete";
    }

    /// <summary>
    /// A steel member: structural material type Steel, a metal Structural Material, or a family name containing one of
    /// the comma-separated words in familyContains.
    /// </summary>
    public static bool IsSteel(Document doc, Element e, string familyContains)
    {
        if (e is FamilyInstance fi)
        {
            try { if (fi.StructuralMaterialType == RStruct.StructuralMaterialType.Steel) return true; } catch { /* not structural */ }
            if (doc.GetElement(fi.StructuralMaterialId) is Material m && IsMetal(m)) return true;
        }
        if (!string.IsNullOrEmpty(familyContains) && doc.GetElement(e.GetTypeId()) is ElementType t)
            foreach (string word in familyContains.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (word.Trim().Length > 0 && t.FamilyName.IndexOf(word.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>A material by exact name (case-insensitive); the error lists similar names.</summary>
    public static Material Named(Document doc, string name, string what)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
        var m = all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (m != null) return m;
        var near = all.Where(x => x.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                                  || name.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0).Select(x => x.Name).Take(10).ToList();
        throw new ArgumentException($"{what}: no material named '{name}'." + (near.Count > 0 ? " Similar: " + string.Join(", ", near) + "." : " Use list_materials."));
    }
}
