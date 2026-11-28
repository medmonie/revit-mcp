namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 9, standards and data: global parameters (create, set, associate) and material assets (structural,
/// thermal, appearance colour). Every engineering value is the user's; nothing is filled in.
/// </summary>
internal static class DataOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["global_parameter_create"] = GlobalParameterCreate;
        ops["global_parameter_set"] = GlobalParameterSet;
        ops["global_parameter_associate"] = GlobalParameterAssociate;
        ops["material_asset_set"] = MaterialAssetSet;
    }

    // ---------------------------------------------------------------- global parameters

    internal static readonly Dictionary<string, ForgeTypeId> Specs = new Dictionary<string, ForgeTypeId>(StringComparer.OrdinalIgnoreCase)
    {
        ["length"] = SpecTypeId.Length, ["area"] = SpecTypeId.Area, ["volume"] = SpecTypeId.Volume, ["angle"] = SpecTypeId.Angle,
        ["number"] = SpecTypeId.Number, ["integer"] = SpecTypeId.Int.Integer, ["text"] = SpecTypeId.String.Text,
        ["yes_no"] = SpecTypeId.Boolean.YesNo, ["material"] = SpecTypeId.Reference.Material,
    };

    internal static bool Is(ForgeTypeId a, ForgeTypeId b) => a != null && b != null && a.TypeId == b.TypeId;

    internal static GlobalParameter GlobalOf(Document doc, string name)
    {
        var id = GlobalParametersManager.FindByName(doc, name);
        if (id == null || id == ElementId.InvalidElementId)
            throw new ArgumentException($"No global parameter '{name}'. Global parameters: " +
                string.Join(", ", GlobalParametersManager.GetAllGlobalParameters(doc).Select(i => doc.GetElement(i)?.Name).Where(n => n != null).OrderBy(n => n).Take(60)) + ".");
        return (GlobalParameter)doc.GetElement(id);
    }

    /// <summary>A JSON value as a global parameter value of this spec (lengths in request units, m2, m3, degrees, other measurable values in the project unit).</summary>
    private static ParameterValue ValueFor(Document doc, ForgeTypeId spec, object v, string what)
    {
        if (Is(spec, SpecTypeId.String.Text)) return new StringParameterValue(Text(v));
        if (Is(spec, SpecTypeId.Boolean.YesNo))
        {
            if (v is bool b) return new IntegerParameterValue(b ? 1 : 0);
            throw new ArgumentException($"{what}: a yes / no value must be true or false.");
        }
        if (Is(spec, SpecTypeId.Reference.Material))
        {
            string name = Text(v);
            var m = new FilteredElementCollector(doc).OfClass(typeof(Material)).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"{what}: no material '{name}'. Use list_materials.");
            return new ElementIdParameterValue(m.Id);
        }
        double d = Number(v, what);
        if (Is(spec, SpecTypeId.Int.Integer)) return new IntegerParameterValue(IntegerValue(d, what));
        return new DoubleParameterValue(U.InSpec(d, spec, U.ProjectUnit(doc, spec)));
    }

    private static object Apply(Document doc, GlobalParameter gp, Obj p)
    {
        var spec = gp.GetDefinition().GetDataType();
        var changed = new List<string>();
        if (Has(p, "value") && Has(p, "formula")) throw new ArgumentException("Give 'value' or 'formula', not both.");
        if (Has(p, "value"))
        {
            if (gp.IsDrivenByFormula) gp.SetFormula("");
            gp.SetValue(ValueFor(doc, spec, p["value"], $"global parameter '{gp.Name}'"));
            changed.Add("value");
        }
        if (Str(p, "formula") is string formula)
        {
            if (!gp.IsValidFormula(formula)) throw new ArgumentException($"'{formula}' is not a valid formula for global parameter '{gp.Name}'.");
            gp.SetFormula(formula);
            changed.Add("formula");
        }
        if (Bool(p, "reporting") is bool reporting)
        {
            if (reporting != gp.IsReporting)
            {
                if (!gp.CanChangeReporting()) throw new ArgumentException($"Global parameter '{gp.Name}' cannot change its reporting state.");
                gp.IsReporting = reporting;
            }
            changed.Add("reporting");
        }
        return changed;
    }

    private static object GlobalParameterCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        if (!GlobalParametersManager.AreGlobalParametersAllowed(doc)) throw new InvalidOperationException("This document does not allow global parameters.");
        string name = NeedStr(p, "name");
        string specName = NeedStr(p, "spec");
        if (!Specs.TryGetValue(specName, out var spec)) throw new ArgumentException($"Unknown spec '{specName}'. Use: {string.Join(", ", Specs.Keys)}.");
        if (!GlobalParametersManager.IsUniqueName(doc, name))
        {
            if (Choice(p, "if_exists", "fail", "fail", "update") == "fail")
                throw new ArgumentException($"A global parameter named '{name}' already exists (if_exists: update sets it).");
            var existing = GlobalOf(doc, name);
            if (!Is(existing.GetDefinition().GetDataType(), spec)) throw new ArgumentException($"Global parameter '{name}' exists with another spec.");
            return new { id = existing.Id.Value, name, created = false, changed = Apply(doc, existing, p) };
        }
        var gp = GlobalParameter.Create(doc, name, spec);
        var changed = Apply(doc, gp, p);
        if (!Has(p, "value") && !Has(p, "formula")) c.Warn($"global parameter '{name}' was created without a value; set it with global_parameter_set.");
        return new { id = gp.Id.Value, name, spec = specName, created = true, changed };
    }

    private static object GlobalParameterSet(EditContext c, Obj p)
    {
        var gp = GlobalOf(c.Doc, NeedStr(p, "name"));
        if (!Has(p, "value") && !Has(p, "formula") && !Has(p, "reporting")) throw new ArgumentException("global_parameter_set needs 'value', 'formula' or 'reporting'.");
        return new { id = gp.Id.Value, name = gp.Name, changed = Apply(c.Doc, gp, p), affectedElements = gp.GetAffectedElements().Count };
    }

    private static object GlobalParameterAssociate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        bool clear = Flag(p, "clear", false);
        GlobalParameter gp = clear ? null : GlobalOf(doc, NeedStr(p, "name"));
        string param = NeedStr(p, "parameter");
        bool onType = Choice(p, "target", "instance", "instance", "type") == "type";
        var done = new List<long>(); var skipped = new List<object>();
        var seen = new HashSet<long>();
        foreach (var e0 in c.Elements(p))
        {
            var e = onType ? doc.GetElement(e0.GetTypeId()) : e0;
            if (e == null) { skipped.Add(new { id = e0.Id.Value, reason = "no type" }); continue; }
            if (!seen.Add(e.Id.Value)) continue;
            var prm = Find.ParamOrNull(e, param);
            if (prm == null) { skipped.Add(new { id = e.Id.Value, reason = $"no parameter '{param}'" }); continue; }
            if (clear)
            {
                if (prm.GetAssociatedGlobalParameter() != ElementId.InvalidElementId) prm.DissociateFromGlobalParameter();
                done.Add(e.Id.Value); continue;
            }
            if (!prm.CanBeAssociatedWithGlobalParameter(gp.Id))
            { skipped.Add(new { id = e.Id.Value, reason = "the parameter cannot take this global parameter (read-only, formula-driven or another spec)" }); continue; }
            prm.AssociateWithGlobalParameter(gp.Id);
            done.Add(e.Id.Value);
        }
        if (done.Count == 0) throw new ArgumentException("No parameter was " + (clear ? "cleared" : "associated") + ": " + string.Join("; ", skipped.Take(5)));
        return new { globalParameter = gp?.Name, parameter = param, target = onType ? "type" : "instance", cleared = clear, elements = done.Count, ids = done, skipped };
    }

    // ---------------------------------------------------------------- material assets

    internal static Material MaterialOf(Document doc, string spec)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
        return all.FirstOrDefault(m => m.Name.Equals(spec.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? (long.TryParse(spec, out long n) ? all.FirstOrDefault(m => m.Id.Value == n) : null)
               ?? throw new ArgumentException($"No material '{spec}'. Use list_materials.");
    }

    /// <summary>Other materials using the same asset element (changing it changes them too).</summary>
    internal static List<string> Sharing(Document doc, Material m, Func<Material, ElementId> asset)
    {
        var id = asset(m);
        if (id == null || id == ElementId.InvalidElementId) return new List<string>();
        return new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
            .Where(x => x.Id != m.Id && asset(x) == id).Select(x => x.Name).OrderBy(x => x).ToList();
    }

    private static double In(Obj s, string key, ForgeTypeId unit) => UnitUtils.ConvertToInternalUnits(NeedDbl(s, key), unit);

    /// <summary>
    /// Store edited asset values. Revit 2024 refuses PropertySetElement.SetStructuralAsset / SetThermalAsset on existing assets
    /// ("internal error"), so the edited asset always becomes a new asset element (PropertySetElement.Create):
    /// copied (newName given): only this material uses the copy; the other materials keep the original.
    /// in place (newName null): every material that used the old asset is moved to the new one, the old one is deleted and
    /// the new one takes the old name, so nothing else changes.
    /// </summary>
    private static string Save(Document doc, Material m, PropertySetElement old, List<string> others, string newName, string kind, Obj report,
                               Func<string, PropertySetElement> create, Func<Material, ElementId> get, Action<Material, ElementId> assign)
    {
        if (newName != null)
        {
            PropertySetElement copy = null;
            Step($"creating the {kind} asset", () => { copy = create(newName); assign(m, copy.Id); });
            report[kind + "AssetCopied"] = new { from = old.Name, to = copy.Name, keptBy = others };
            return copy.Name;
        }
        string oldName = old.Name;
        var oldId = old.Id;
        var users = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Where(x => get(x) == oldId).ToList();
        var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(PropertySetElement)).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        string temp = oldName + " (edited)";
        for (int i = 2; names.Contains(temp); i++) temp = $"{oldName} (edited {i})";
        PropertySetElement made = null;
        Step($"creating the edited {kind} asset", () => { made = create(temp); foreach (var u in users) assign(u, made.Id); });
        Step($"removing the old {kind} asset", () => doc.Delete(oldId));
        string final = temp;
        try { made.Name = oldName; final = oldName; } catch { /* keep the temporary name; reported below */ }
        report[kind + "AssetReplaced"] = new { asset = final, materials = users.Select(u => u.Name).OrderBy(x => x).ToList(),
                                               note = final == oldName ? null : $"Revit kept the name '{final}'." };
        return final;
    }

    /// <summary>Run one step; a Revit failure names the step (Revit's own messages for assets are often just "internal error").</summary>
    private static void Step(string what, Action work)
    {
        try { work(); }
        catch (ArgumentException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException($"{what}: Revit refused it ({ErrorText(ex)}).", ex); }
    }

    private static object MaterialAssetSet(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var m = MaterialOf(doc, NeedStr(p, "material"));
        bool shareOk = Flag(p, "change_shared", false);
        var report = new Obj { ["material"] = m.Name };
        var st = Sub(p, "structural"); var th = Sub(p, "thermal"); var ap = Sub(p, "appearance");
        if (st == null && th == null && ap == null) throw new ArgumentException("material_asset_set needs 'structural', 'thermal' and / or 'appearance' values.");

        // The asset element to change, and whether it is shared: a shared asset is not changed in place (unless
        // change_shared) but copied, with the new values, into a new asset element for this material only.
        (PropertySetElement pse, List<string> others, string newName) Own(ElementId id, string kind)
        {
            if (id == null || id == ElementId.InvalidElementId)
                throw new ArgumentException($"Material '{m.Name}' has no {kind} asset to change; assign one in Revit's Material Browser first.");
            var pse = (PropertySetElement)doc.GetElement(id);
            var others = Sharing(doc, m, x => kind == "structural" ? x.StructuralAssetId : x.ThermalAssetId);
            if (others.Count == 0) return (pse, others, null);
            if (shareOk) { report[kind + "AlsoChanges"] = others; return (pse, others, null); }
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(PropertySetElement)).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            string name = m.Name + " - " + kind;
            for (int i = 2; names.Contains(name); i++) name = $"{m.Name} - {kind} {i}";
            return (pse, others, name);
        }

        if (st != null)
        {
            var (pse, others, newName) = Own(m.StructuralAssetId, "structural");
            var a = pse.GetStructuralAsset();
            var set = new List<string>();
            if (Has(st, "density")) { Step("structural.density", () => { a.Density = In(st, "density", UnitTypeId.KilogramsPerCubicMeter); }); set.Add("density"); }
            if (Has(st, "young_modulus")) { Step("structural.young_modulus", () => { a.SetYoungModulus(In(st, "young_modulus", UnitTypeId.Megapascals)); }); set.Add("young_modulus"); }
            if (Has(st, "poisson_ratio")) { Step("structural.poisson_ratio", () => { a.SetPoissonRatio(NeedDbl(st, "poisson_ratio")); }); set.Add("poisson_ratio"); }
            if (Has(st, "shear_modulus")) { Step("structural.shear_modulus", () => { a.SetShearModulus(In(st, "shear_modulus", UnitTypeId.Megapascals)); }); set.Add("shear_modulus"); }
            if (Has(st, "thermal_expansion")) { Step("structural.thermal_expansion", () => { a.SetThermalExpansionCoefficient(In(st, "thermal_expansion", UnitTypeId.InverseDegreesCelsius)); }); set.Add("thermal_expansion"); }
            if (Has(st, "concrete_compression")) { Step("structural.concrete_compression", () => { a.ConcreteCompression = In(st, "concrete_compression", UnitTypeId.Megapascals); }); set.Add("concrete_compression"); }
            if (Has(st, "yield_stress")) { Step("structural.yield_stress", () => { a.MinimumYieldStress = In(st, "yield_stress", UnitTypeId.Megapascals); }); set.Add("yield_stress"); }
            if (Has(st, "tensile_strength")) { Step("structural.tensile_strength", () => { a.MinimumTensileStrength = In(st, "tensile_strength", UnitTypeId.Megapascals); }); set.Add("tensile_strength"); }
            if (Bool(st, "lightweight") is bool lw) { Step("structural.lightweight", () => { a.Lightweight = lw; }); set.Add("lightweight"); }
            if (set.Count == 0) throw new ArgumentException("'structural' needs at least one of: density (kg/m3), young_modulus, shear_modulus (MPa), poisson_ratio, thermal_expansion (1/°C), concrete_compression, yield_stress, tensile_strength (MPa), lightweight.");
            string assetName = Save(doc, m, pse, others, newName, "structural", report,
                name => { a.Name = name; return PropertySetElement.Create(doc, a); }, x => x.StructuralAssetId, (x, id) => x.StructuralAssetId = id);
            report["structural"] = new { asset = assetName, set };
        }
        if (th != null)
        {
            var (pse, others, newName) = Own(m.ThermalAssetId, "thermal");
            var a = pse.GetThermalAsset();
            var set = new List<string>();
            if (Has(th, "conductivity")) { Step("thermal.conductivity", () => { a.ThermalConductivity = In(th, "conductivity", UnitTypeId.WattsPerMeterKelvin); }); set.Add("conductivity"); }
            if (Has(th, "specific_heat")) { Step("thermal.specific_heat", () => { a.SpecificHeat = In(th, "specific_heat", UnitTypeId.JoulesPerKilogramDegreeCelsius); }); set.Add("specific_heat"); }
            if (Has(th, "density")) { Step("thermal.density", () => { a.Density = In(th, "density", UnitTypeId.KilogramsPerCubicMeter); }); set.Add("density"); }
            if (Has(th, "emissivity")) { Step("thermal.emissivity", () => { a.Emissivity = NeedDbl(th, "emissivity"); }); set.Add("emissivity"); }
            if (Has(th, "porosity")) { Step("thermal.porosity", () => { a.Porosity = NeedDbl(th, "porosity"); }); set.Add("porosity"); }
            if (Has(th, "reflectivity")) { Step("thermal.reflectivity", () => { a.Reflectivity = NeedDbl(th, "reflectivity"); }); set.Add("reflectivity"); }
            if (set.Count == 0) throw new ArgumentException("'thermal' needs at least one of: conductivity (W/(m·K)), specific_heat (J/(kg·°C)), density (kg/m3), emissivity, porosity, reflectivity.");
            string assetName = Save(doc, m, pse, others, newName, "thermal", report,
                name => { a.Name = name; return PropertySetElement.Create(doc, a); }, x => x.ThermalAssetId, (x, id) => x.ThermalAssetId = id);
            report["thermal"] = new { asset = assetName, set };
        }
        if (ap != null)
        {
            var set = new List<string>();
            if (Has(ap, "color"))
            {
                var id = m.AppearanceAssetId;
                if (id == null || id == ElementId.InvalidElementId) throw new ArgumentException($"Material '{m.Name}' has no appearance asset.");
                var others = Sharing(doc, m, x => x.AppearanceAssetId);
                if (others.Count > 0 && !shareOk)
                {
                    string dupName = m.Name + " - appearance";
                    for (int i = 2; AppearanceAssetElement.GetAppearanceAssetElementByName(doc, dupName) != null; i++) dupName = $"{m.Name} - appearance {i}";
                    AppearanceAssetElement dup = null;
                    var source = (AppearanceAssetElement)doc.GetElement(id);
                    Step("duplicating the appearance asset", () => { dup = source.Duplicate(dupName); m.AppearanceAssetId = dup.Id; });
                    id = dup.Id;
                    report["appearanceAssetDuplicated"] = new { name = dupName, sharedWith = others };
                }
                var color = DocOps.ColorOf(ap["color"], "appearance.color");
                var assetId = id;
                Step("appearance.color", () =>
                {
                    using (var scope = new Autodesk.Revit.DB.Visual.AppearanceAssetEditScope(doc))
                    {
                        var asset = scope.Start(assetId);
                        var prop = asset.FindByName("generic_diffuse") as Autodesk.Revit.DB.Visual.AssetPropertyDoubleArray4d
                                   ?? throw new ArgumentException($"The appearance asset of '{m.Name}' is not a Generic one (no diffuse colour); change it in Revit.");
                        prop.SetValueAsColor(color);
                        scope.Commit(true);
                    }
                });
                set.Add("render colour");
            }
            if (Has(ap, "shading_color")) { Step("appearance.shading colour", () => { m.Color = DocOps.ColorOf(ap["shading_color"], "appearance.shading_color"); }); set.Add("shading colour"); }
            if (Has(ap, "transparency")) { Step("appearance.transparency", () => { m.Transparency = Int(ap, "transparency", 0, 0, 100); }); set.Add("transparency"); }
            if (set.Count == 0) throw new ArgumentException("'appearance' needs color (render), shading_color and / or transparency (0-100).");
            report["appearance"] = set;
        }
        return report;
    }
}
