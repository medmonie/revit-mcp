namespace RevitMcpBridge;

/// <summary>Roadmap phase 9 reads: global parameters, a material's assets, keynotes and assembly codes.</summary>
internal static class DataRead
{
    private static string SpecName(ForgeTypeId spec) =>
        DataOps.Specs.FirstOrDefault(kv => DataOps.Is(kv.Value, spec)).Key ?? spec?.TypeId;

    private static object ValueOut(Document doc, ForgeTypeId spec, ParameterValue v)
    {
        switch (v)
        {
            case StringParameterValue s: return s.Value;
            case IntegerParameterValue i: return DataOps.Is(spec, SpecTypeId.Boolean.YesNo) ? (object)(i.Value != 0) : i.Value;
            case ElementIdParameterValue e: return e.Value == ElementId.InvalidElementId ? null : new { id = e.Value.Value, name = doc.GetElement(e.Value)?.Name };
            case DoubleParameterValue d:
                if (DataOps.Is(spec, SpecTypeId.Length)) return U.Out(d.Value);
                if (DataOps.Is(spec, SpecTypeId.Area)) return U.Area(d.Value);
                if (DataOps.Is(spec, SpecTypeId.Volume)) return U.Volume(d.Value);
                if (DataOps.Is(spec, SpecTypeId.Angle)) return U.Deg(d.Value);
                if (spec != null && UnitUtils.IsMeasurableSpec(spec))
                    return Math.Round(UnitUtils.ConvertFromInternalUnits(d.Value, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId()), 6);
                return Math.Round(d.Value, 9);
            default: return null;
        }
    }

    public static object ListGlobalParameters(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        if (!GlobalParametersManager.AreGlobalParametersAllowed(doc)) return new { allowed = false, globalParameters = new object[0] };
        var rows = GlobalParametersManager.GetGlobalParametersOrdered(doc).Select(id => (GlobalParameter)doc.GetElement(id)).Select(gp =>
        {
            var spec = gp.GetDefinition().GetDataType();
            var o = new Obj { ["id"] = gp.Id.Value, ["name"] = gp.Name, ["spec"] = SpecName(spec) };
            try { o["value"] = ValueOut(doc, spec, gp.GetValue()); } catch (Exception ex) { o["error"] = ErrorText(ex); }
            string formula = gp.GetFormula();
            if (!string.IsNullOrEmpty(formula)) o["formula"] = formula;
            if (gp.IsReporting) o["reporting"] = true;
            if (gp.IsDrivenByDimension) o["drivenByDimension"] = true;
            o["affectedElements"] = gp.GetAffectedElements().Count;
            return o;
        });
        var page = (Obj)Describe.Page(p, rows, "globalParameters", 300, 5000);
        page["allowed"] = true;
        return page;
    }

    // ---------------------------------------------------------------- materials

    private static double Out(double v, ForgeTypeId unit, int digits = 4) => Math.Round(UnitUtils.ConvertFromInternalUnits(v, unit), digits);

    private static object Xyz(XYZ v, ForgeTypeId unit, int digits = 4) =>
        unit == null ? new[] { Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6) }
                     : new[] { Out(v.X, unit, digits), Out(v.Y, unit, digits), Out(v.Z, unit, digits) };

    private static string ColorText(Color c) => c == null || !c.IsValid ? null : $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    private static string Pattern(Document doc, ElementId id) => id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;

    public static object GetMaterial(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var m = DataOps.MaterialOf(doc, NeedRef(p, "material"));
        var o = new Obj
        {
            ["id"] = m.Id.Value, ["name"] = m.Name, ["class"] = m.MaterialClass, ["category"] = m.MaterialCategory,
            ["graphics"] = new
            {
                shadingColor = ColorText(m.Color), transparency = m.Transparency, shininess = m.Shininess, smoothness = m.Smoothness,
                useRenderAppearance = m.UseRenderAppearanceForShading,
                surfacePattern = Pattern(doc, m.SurfaceForegroundPatternId), surfacePatternColor = ColorText(m.SurfaceForegroundPatternColor),
                cutPattern = Pattern(doc, m.CutForegroundPatternId), cutPatternColor = ColorText(m.CutForegroundPatternColor),
            },
        };
        foreach (var name in new[] { "ALL_MODEL_DESCRIPTION", "ALL_MODEL_MANUFACTURER", "ALL_MODEL_MARK", "KEYNOTE_PARAM" })
            if (Find.Builtin(m, name)?.AsString() is string s && s.Length > 0) o[name.Replace("ALL_MODEL_", "").Replace("_PARAM", "").ToLowerInvariant()] = s;

        if (doc.GetElement(m.StructuralAssetId) is PropertySetElement sp)
        {
            var a = sp.GetStructuralAsset();
            var s = new Obj
            {
                ["asset"] = sp.Name, ["class"] = a.StructuralAssetClass.ToString(), ["behavior"] = a.Behavior.ToString(),
                ["density_kg_m3"] = Out(a.Density, UnitTypeId.KilogramsPerCubicMeter, 2),
                ["youngModulus_MPa"] = Xyz(a.YoungModulus, UnitTypeId.Megapascals, 1),
                ["poissonRatio"] = Xyz(a.PoissonRatio, null),
                ["shearModulus_MPa"] = Xyz(a.ShearModulus, UnitTypeId.Megapascals, 1),
                ["thermalExpansion_per_C"] = Xyz(a.ThermalExpansionCoefficient, UnitTypeId.InverseDegreesCelsius, 9),
            };
            if (a.StructuralAssetClass == StructuralAssetClass.Concrete)
            {
                s["concreteCompression_MPa"] = Out(a.ConcreteCompression, UnitTypeId.Megapascals, 2);
                s["lightweight"] = a.Lightweight;
            }
            if (a.StructuralAssetClass == StructuralAssetClass.Metal)
            {
                s["yieldStress_MPa"] = Out(a.MinimumYieldStress, UnitTypeId.Megapascals, 2);
                s["tensileStrength_MPa"] = Out(a.MinimumTensileStrength, UnitTypeId.Megapascals, 2);
            }
            s["sharedWith"] = DataOps.Sharing(doc, m, x => x.StructuralAssetId);
            o["structural"] = s;
        }
        else o["structural"] = null;

        if (doc.GetElement(m.ThermalAssetId) is PropertySetElement tp)
        {
            var a = tp.GetThermalAsset();
            o["thermal"] = new Obj
            {
                ["asset"] = tp.Name, ["type"] = a.ThermalMaterialType.ToString(),
                ["conductivity_W_mK"] = Out(a.ThermalConductivity, UnitTypeId.WattsPerMeterKelvin, 4),
                ["specificHeat_J_kgC"] = Out(a.SpecificHeat, UnitTypeId.JoulesPerKilogramDegreeCelsius, 2),
                ["density_kg_m3"] = Out(a.Density, UnitTypeId.KilogramsPerCubicMeter, 2),
                ["emissivity"] = Math.Round(a.Emissivity, 4), ["porosity"] = Math.Round(a.Porosity, 4), ["reflectivity"] = Math.Round(a.Reflectivity, 4),
                ["sharedWith"] = DataOps.Sharing(doc, m, x => x.ThermalAssetId),
            };
        }
        else o["thermal"] = null;

        if (doc.GetElement(m.AppearanceAssetId) is AppearanceAssetElement ae)
        {
            var ap = new Obj { ["asset"] = ae.Name, ["sharedWith"] = DataOps.Sharing(doc, m, x => x.AppearanceAssetId) };
            try
            {
                var asset = ae.GetRenderingAsset();
                ap["schema"] = asset.Name;
                if (asset.FindByName("generic_diffuse") is Autodesk.Revit.DB.Visual.AssetPropertyDoubleArray4d diffuse) ap["color"] = ColorText(diffuse.GetValueAsColor());
            }
            catch { /* the rendering asset is not always readable */ }
            o["appearance"] = ap;
        }
        else o["appearance"] = null;
        o["note"] = "Structural values in kg/m3, MPa and 1/°C; thermal values in W/(m·K), J/(kg·°C), kg/m3. Change them with material_asset_set.";
        return o;
    }

    // ---------------------------------------------------------------- keynotes and assembly codes

    private static string FilePath(Document doc, Element table)
    {
        try
        {
            var r = ExternalFileUtils.GetExternalFileReference(doc, table.Id);
            return r == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetAbsolutePath());
        }
        catch { return null; }
    }

    private static string TypeValue(ElementType t, string bip, string name) =>
        (Find.Builtin(t, bip) ?? Find.ParamOrNull(t, name))?.AsString();

    public static object ListKeynotes(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var keynotes = KeynoteTable.GetKeynoteTable(doc);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<object>();
        int limit = Int(p, "entries_limit", 100, 0, 20000);
        foreach (KeyBasedTreeEntry e in keynotes.GetKeyBasedTreeEntries())
        {
            string text = (e as KeynoteEntry)?.KeynoteText;
            texts[e.Key] = text;
            if (entries.Count < limit) entries.Add(new { key = e.Key, parent = e.ParentKey, text });
        }
        var codes = AssemblyCodeTable.GetAssemblyCodeTable(doc);
        int codeCount = codes.GetKeyBasedTreeEntries().Count();

        var cats = Find.Cats(doc, p, "category", Find.Structural);
        var types = new FilteredElementCollector(doc).WhereElementIsElementType().WherePasses(new ElementMulticategoryFilter(cats))
            .Cast<ElementType>().ToList();
        var used = new HashSet<long>(Find.Scope(doc, new Obj(), cats).Select(e => e.GetTypeId().Value));
        bool usedOnly = Flag(p, "used_only", true);
        var rows = types.Where(t => !usedOnly || used.Contains(t.Id.Value))
            .OrderBy(t => t.Category?.Name).ThenBy(t => Find.TypeLabel(t), StringComparer.OrdinalIgnoreCase)
            .Select(t =>
            {
                string key = TypeValue(t, "KEYNOTE_PARAM", "Keynote");
                return new
                {
                    id = t.Id.Value, category = t.Category?.Name, type = Find.TypeLabel(t),
                    keynote = string.IsNullOrEmpty(key) ? null : key,
                    keynoteText = string.IsNullOrEmpty(key) ? null : texts.TryGetValue(key, out var tx) ? tx : "(key not in the keynote file)",
                    assemblyCode = TypeValue(t, "UNIFORMAT_CODE", "Assembly Code") is string ac && ac.Length > 0 ? ac : null,
                    assemblyDescription = TypeValue(t, "UNIFORMAT_DESCRIPTION", "Assembly Description") is string ad && ad.Length > 0 ? ad : null,
                };
            }).ToList();
        var page = (Obj)Describe.Page(p, rows, "types", 300, 5000);
        page["keynoteFile"] = FilePath(doc, keynotes);
        page["keynoteEntries"] = texts.Count;
        page["entries"] = entries;
        page["assemblyCodeFile"] = FilePath(doc, codes);
        page["assemblyCodeEntries"] = codeCount;
        page["typesWithoutKeynote"] = rows.Count(r => r.keynote == null);
        page["typesWithoutAssemblyCode"] = rows.Count(r => r.assemblyCode == null);
        return page;
    }
}
