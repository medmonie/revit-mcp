using System.Text.RegularExpressions;

namespace RevitMcpBridge;

/// <summary>A column or framing type with its section size (feet).</summary>
internal sealed class SectionSize
{
    public FamilySymbol Symbol;
    public double B, H;        // b along the family's x (width), h along y (depth)
    public bool Round;         // sized by a diameter
    public bool Steel;         // the family's structural material is steel
    public string Source;      // where the size came from: "b/h" (two parameter names), "Diameter" or "type name"
    public int Instances;      // how many elements use it (the project's usual choice when sizes tie)

    /// <summary>The size is in two type parameters, so a new size can be made by duplicating the type.</summary>
    public bool Resizable => !Round && Source != null && Source.Contains("/");
}

/// <summary>
/// Section sizes of column and framing types: read from type parameters or from the type name ("650x1300"), and new
/// sizes made by duplicating a base type.
/// </summary>
internal static class Sections
{
    // Size parameter pairs, in the order they are tried: concrete families (b / h), steel (bf / d), generic.
    private static readonly string[][] SizeParams =
    {
        new[] { "b", "h" }, new[] { "bf", "d" }, new[] { "Width", "Depth" }, new[] { "B", "H" }, new[] { "Width", "Height" },
    };

    // The first "AxB" in a name, A and B in mm: "KTR15-S-C4 650x1300", "BU 2000x700x22x35" -> 2000 x 700.
    private static readonly Regex SizeInName = new Regex(@"(\d{2,4}(?:\.\d+)?)\s*[xX×*]\s*(\d{2,4}(?:\.\d+)?)");

    /// <summary>Size key as the user types it in a 'types' map: "650x1300" (mm, first value first).</summary>
    public static string Key(double w, double d) => $"{U.MmText(w)}x{U.MmText(d)}";

    /// <summary>The first "AxB" of a name, in feet; false when there is none.</summary>
    public static bool FromName(string name, out double b, out double h)
    {
        b = h = 0;
        var m = SizeInName.Match(name ?? "");
        if (!m.Success) return false;
        b = U.Mm(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        h = U.Mm(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Every type of the category that has a readable size.</summary>
    public static List<SectionSize> Of(Document doc, BuiltInCategory cat)
    {
        var counts = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType()
            .OfType<FamilyInstance>().GroupBy(f => f.Symbol.Id.Value).ToDictionary(g => g.Key, g => g.Count());
        var list = new List<SectionSize>();
        foreach (var s in new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsElementType().OfType<FamilySymbol>())
        {
            var t = new SectionSize { Symbol = s, Instances = counts.TryGetValue(s.Id.Value, out int k) ? k : 0 };
            try { t.Steel = s.Family.StructuralMaterialType == RStruct.StructuralMaterialType.Steel; } catch { /* not structural */ }
            double Len(string name)
            {
                var prm = s.LookupParameter(name);
                return prm != null && prm.StorageType == StorageType.Double && prm.AsDouble() > 1e-6 ? prm.AsDouble() : 0;
            }
            double dia = Len("Diameter"); if (dia == 0) dia = Len("D");
            if (dia > 0) { t.B = t.H = dia; t.Round = true; t.Source = "Diameter"; }
            else
                foreach (var pair in SizeParams)
                {
                    double b = Len(pair[0]), h = Len(pair[1]);
                    if (b > 0 && h > 0) { t.B = b; t.H = h; t.Source = pair[0] + "/" + pair[1]; break; }
                }
            if (t.B == 0 && FromName(s.Name, out double nb, out double nh)) { t.B = nb; t.H = nh; t.Source = "type name"; }
            if (t.B > 0) list.Add(t);
        }
        return list;
    }

    /// <summary>A type of the list by "Type" or "Family : Type".</summary>
    public static SectionSize Named(List<SectionSize> types, string name) =>
        types.FirstOrDefault(t => Find.TypeLabel(t.Symbol).Equals(name, StringComparison.OrdinalIgnoreCase)
                                 || t.Symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A base type for new sizes ('create_types'): it must exist and keep its size in two type parameters.
    /// </summary>
    public static SectionSize NeedResizable(List<SectionSize> types, string name, string what)
    {
        var t = Named(types, name) ?? throw new ArgumentException($"'create_types': no {what} type named '{name}' with a readable size.");
        if (!t.Resizable)
            throw new ArgumentException($"'create_types': the size of '{name}' comes from its {t.Source}, not from two size parameters, " +
                                        "so new sizes cannot be set. Pick a type whose family has b / h (or Width / Depth) type parameters.");
        return t;
    }

    /// <summary>
    /// The type of the base type's family with size b x h (h: null keeps the base depth), made by duplicating the base
    /// type when it does not exist yet. Name: 'template' with {b} and {h}, default the base name with its size replaced.
    /// </summary>
    public static (FamilySymbol symbol, bool created) Sized(Document doc, SectionSize baseType, double b, double? h, string template)
    {
        double depth = h ?? baseType.H;
        template = template ?? SizeInName.Replace(baseType.Symbol.Name, "{b}x{h}", 1);
        if (!template.Contains("{b}")) template += " {b}x{h}";
        string name = template.Replace("{b}", U.MmText(b)).Replace("{h}", U.MmText(depth));

        var family = baseType.Symbol.Family;
        var existing = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>()
            .FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return (existing, false);

        var names = baseType.Source.Split('/');
        var sym = (FamilySymbol)baseType.Symbol.Duplicate(name);
        sym.LookupParameter(names[0]).Set(b);
        if (h != null) sym.LookupParameter(names[1]).Set(h.Value);
        return (sym, true);
    }
}
