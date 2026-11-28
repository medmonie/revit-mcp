namespace RevitMcpBridge;

/// <summary>
/// Changes made inside loadable families (the Family Editor's work, done in memory): each family is opened, changed in
/// one family transaction, loaded back into the model keeping the project's type values, and closed without saving a
/// file. A family cannot be opened while the project has a transaction open, so this runs after the batch's commit;
/// a dry run only lists the families.
/// </summary>
internal static class FamilyOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["family_material_set"] = FamilyMaterialSet;
        ops["family_material_link"] = FamilyMaterialLink;
        ops["family_parameters"] = FamilyParameters;
    }

    /// <summary>Loading back after type values were set in the family: the family's values win for its types.</summary>
    private sealed class OverwriteValues : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }

    private sealed class KeepProjectValues : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = false; return true; }
    }

    /// <summary>Loadable structural families chosen by 'families' (names) or 'family_contains'; in-place ones are reported, not edited.</summary>
    private static (List<Family> editable, List<string> notEditable) Pick(Document doc, Obj p, bool anyCategory = false)
    {
        HashSet<long> catIds = null;
        if (!anyCategory || Has(p, "category"))
        {
            var cats = Find.Cats(doc, p, "category", BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation);
            catIds = new HashSet<long>(cats.Select(k => (long)(int)k));
        }
        var names = Has(p, "families") ? new HashSet<string>(StrList(p, "families", 500), StringComparer.OrdinalIgnoreCase) : null;
        string contains = Str(p, "family_contains");
        if (names == null && contains == null) throw new ArgumentException("Give 'families' (names) or 'family_contains' (e.g. 'S-STL').");
        var fams = Find.Families(doc)
            .Where(f => f.FamilyCategory != null && (catIds == null || catIds.Contains(f.FamilyCategory.Id.Value))
                        && (names == null || names.Contains(f.Name))
                        && (contains == null || f.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(f => f.Name).ToList();
        if (names != null)
        {
            var missing = names.Except(fams.Select(f => f.Name), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0) throw new ArgumentException((anyCategory ? "No loadable family named: " : "No loadable structural family named: ") + string.Join(", ", missing) + ".");
        }
        if (fams.Count == 0) throw new ArgumentException("No family matches.");
        return (fams.Where(f => !f.IsInPlace && f.IsEditable).ToList(), fams.Where(f => f.IsInPlace || !f.IsEditable).Select(f => f.Name).ToList());
    }

    private static string Why(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException) parts.Add(e.GetType().Name + ": " + e.Message);
        return string.Join(" <- ", parts);
    }

    /// <summary>What one family change did: how many items it changed (0: nothing to load back) and what to report.</summary>
    private sealed class Change
    {
        public int Changed;
        public Obj Report = new Obj();
    }

    /// <summary>
    /// Run 'change' inside each family and load the changed ones back. The result object is returned at once and filled
    /// in after the commit ('results': one entry per family with the step reached and Revit's messages on failure).
    /// </summary>
    private static Obj InFamilies(EditContext c, Obj p, string what, Func<Document, FamilyManager, Change> change,
                                  bool overwriteValues = false, bool anyCategory = false)
    {
        var doc = c.Doc;
        var (fams, notEditable) = Pick(doc, p, anyCategory);
        var result = new Obj
        {
            ["families"] = fams.Select(f => (object)f.Name).ToList(),
            ["notEditable"] = notEditable.Cast<object>().ToList(),
            ["status"] = c.DryRun ? $"dry run: these families would be opened, {what}, and loaded back (project type values kept)"
                                  : "queued: runs after the commit",
        };
        if (c.DryRun) return result;

        var report = new List<object>();
        result["results"] = report;
        c.Defer(() =>
        {
            int failures = 0;
            foreach (var fam in fams)
            {
                string name = fam.Name;   // the Family object is replaced when the family is loaded back
                Document fdoc = null; string stage = "open the family"; var fc = new FailureCollector();
                try
                {
                    fdoc = doc.EditFamily(fam);
                    Change done;
                    using (var t = new Transaction(fdoc, "Claude: " + what))
                    {
                        stage = "start the family transaction";
                        fc.Start(t);
                        stage = "regenerate the family as opened";
                        fdoc.Regenerate();
                        stage = "change the family";
                        done = change(fdoc, fdoc.FamilyManager);
                        stage = "regenerate after the change";
                        fdoc.Regenerate();
                        stage = "commit the family change";
                        if (t.Commit() != TransactionStatus.Committed)
                        {
                            failures++;
                            report.Add(new { family = name, error = "rolled back by Revit", stage, revitErrors = fc.Errors });
                            continue;
                        }
                    }
                    if (done.Changed > 0) { stage = "load the family back into the model"; fdoc.LoadFamily(doc, overwriteValues ? (IFamilyLoadOptions)new OverwriteValues() : new KeepProjectValues()); }
                    var entry = new Obj { ["family"] = name, ["reloaded"] = done.Changed > 0 };
                    foreach (var kv in done.Report) entry[kv.Key] = kv.Value;
                    if (fc.Warnings.Count > 0) entry["revitWarnings"] = fc.Warnings;
                    report.Add(entry);
                }
                catch (Exception ex)
                {
                    failures++;
                    report.Add(new { family = name, error = Why(ex), stage, revitErrors = fc.Errors, revitWarnings = fc.Warnings });
                }
                finally { try { fdoc?.Close(false); } catch { /* already closed */ } }
            }
            result["status"] = failures == 0 ? "completed" : "some families failed";
            if (failures > 0) throw new InvalidOperationException($"{failures} family change(s) failed. See the per-family results.");
        });
        return result;
    }

    /// <summary>The solids of a family document (extrusions, blends, sweeps ...) and how many nested families it has.</summary>
    private static (List<Element> forms, int nested) Forms(Document fdoc)
    {
        var forms = new List<Element>(); int nested = 0;
        foreach (var el in new FilteredElementCollector(fdoc).WhereElementIsNotElementType())
        {
            if (el is FamilyInstance) nested++;
            else if (el is GenericForm || el is FreeFormElement) forms.Add(el);
        }
        return (forms, nested);
    }

    /// <summary>
    /// Set the material of every solid in the families to one material (no parameter link): for a grade fixed per family.
    /// The material is made in the family under the project material's name, so the project's material is used on load.
    /// </summary>
    private static object FamilyMaterialSet(EditContext c, Obj p)
    {
        var pm = Materials.Named(c.Doc, NeedStr(p, "material"), "material");
        var result = InFamilies(c, p, "set the solids' material", (fdoc, fm) =>
        {
            var fmat = new FilteredElementCollector(fdoc).OfClass(typeof(Material)).Cast<Material>()
                .FirstOrDefault(m => m.Name.Equals(pm.Name, StringComparison.OrdinalIgnoreCase));
            if (fmat == null)
            {
                fmat = (Material)fdoc.GetElement(Material.Create(fdoc, pm.Name));
                fmat.MaterialClass = pm.MaterialClass;
                fmat.Color = pm.Color;
            }
            var (forms, nested) = Forms(fdoc);
            int set = 0, unlinked = 0;
            foreach (var el in forms)
            {
                var mp = el.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (mp == null) continue;
                if (fm.GetAssociatedFamilyParameter(mp) != null) { fm.AssociateElementParameterToFamilyParameter(mp, null); unlinked++; }
                mp.Set(fmat.Id);
                set++;
            }
            return new Change { Changed = set, Report = new Obj { ["solidsSet"] = set, ["linksRemoved"] = unlinked, ["nestedFamilies"] = nested } };
        });
        result["material"] = pm.Name;
        return result;
    }

    /// <summary>
    /// Link the material of every solid in the families to the family's Structural Material, so each element's material
    /// is what its geometry shows. (Revit can refuse the link with 'Document regeneration failed'; family_material_set
    /// is the fallback when the grade is fixed per family.)
    /// </summary>
    private static object FamilyMaterialLink(EditContext c, Obj p) =>
        InFamilies(c, p, "link the solids' material to Structural Material", (fdoc, fm) =>
        {
            var fp = fm.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM) ?? fm.get_Parameter("Structural Material")
                     ?? throw new InvalidOperationException("the family has no Structural Material parameter");
            var (forms, nested) = Forms(fdoc);
            int linked = 0, already = 0, cannot = 0;
            var previous = new HashSet<string>();
            foreach (var el in forms)
            {
                var mp = el.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (mp == null) continue;
                var cur = fm.GetAssociatedFamilyParameter(mp);
                if (cur != null && cur.Id == fp.Id) { already++; continue; }
                if (cur != null) previous.Add(cur.Definition.Name);
                if (!fm.CanElementParameterBeAssociated(mp)) { cannot++; continue; }
                fm.AssociateElementParameterToFamilyParameter(mp, fp);
                linked++;
            }
            return new Change
            {
                Changed = linked,
                Report = new Obj
                {
                    ["solidsLinked"] = linked, ["alreadyLinked"] = already, ["couldNotLink"] = cannot, ["nestedFamilies"] = nested,
                    ["previouslyLinkedTo"] = previous.Cast<object>().ToList(),
                },
            };
        });

    // ---------------------------------------------------------------- family parameters and formulas (no geometry)

    private static readonly Dictionary<string, ForgeTypeId> Specs = new Dictionary<string, ForgeTypeId>(StringComparer.OrdinalIgnoreCase)
    {
        ["length"] = SpecTypeId.Length, ["area"] = SpecTypeId.Area, ["volume"] = SpecTypeId.Volume, ["angle"] = SpecTypeId.Angle,
        ["number"] = SpecTypeId.Number, ["integer"] = SpecTypeId.Int.Integer, ["text"] = SpecTypeId.String.Text,
        ["yes_no"] = SpecTypeId.Boolean.YesNo, ["material"] = SpecTypeId.Reference.Material,
    };

    private static readonly Dictionary<string, ForgeTypeId> Groups = new Dictionary<string, ForgeTypeId>(StringComparer.OrdinalIgnoreCase)
    {
        ["dimensions"] = GroupTypeId.Geometry, ["construction"] = GroupTypeId.Construction, ["data"] = GroupTypeId.Data,
        ["identity_data"] = GroupTypeId.IdentityData, ["materials"] = GroupTypeId.Materials, ["structural"] = GroupTypeId.Structural,
        ["text"] = GroupTypeId.Text, ["other"] = GroupTypeId.General, ["graphics"] = GroupTypeId.Graphics,
    };

    private static FamilyParameter NeedParam(FamilyManager fm, string name) =>
        fm.get_Parameter(name) ?? throw new InvalidOperationException($"no parameter '{name}' in the family");

    /// <summary>A value as the engineer gives it, in request units (lengths), m2, m3, degrees; text, numbers, yes / no.</summary>
    private static void SetValue(Document fdoc, FamilyManager fm, FamilyParameter fp, object v, string where)
    {
        if (fp.IsDeterminedByFormula || !string.IsNullOrEmpty(fp.Formula)) throw new InvalidOperationException($"{where}: '{fp.Definition.Name}' is driven by a formula; change the formula instead");
        var spec = fp.Definition.GetDataType();
        switch (fp.StorageType)
        {
            case StorageType.String: fm.Set(fp, Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            case StorageType.Integer:
                if (spec == SpecTypeId.Boolean.YesNo) fm.Set(fp, v is bool b ? (b ? 1 : 0) : Convert.ToInt32(v, CultureInfo.InvariantCulture) != 0 ? 1 : 0);
                else fm.Set(fp, Convert.ToInt32(v, CultureInfo.InvariantCulture));
                break;
            case StorageType.Double:
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (spec == SpecTypeId.Length) d = U.In(d);
                else if (spec == SpecTypeId.Area) d = UnitUtils.ConvertToInternalUnits(d, UnitTypeId.SquareMeters);
                else if (spec == SpecTypeId.Volume) d = UnitUtils.ConvertToInternalUnits(d, UnitTypeId.CubicMeters);
                else if (spec == SpecTypeId.Angle) d = d * Math.PI / 180;
                else if (spec != SpecTypeId.Number) throw new InvalidOperationException($"{where}: '{fp.Definition.Name}' is a {spec.TypeId} parameter; only length, area, volume, angle and number values are set here");
                fm.Set(fp, d);
                break;
            case StorageType.ElementId:
                if (spec != SpecTypeId.Reference.Material) throw new InvalidOperationException($"{where}: '{fp.Definition.Name}' takes an element; only materials are set here");
                string mname = Convert.ToString(v, CultureInfo.InvariantCulture);
                var mat = new FilteredElementCollector(fdoc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name.Equals(mname, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException($"{where}: no material '{mname}' in the family");
                fm.Set(fp, mat.Id);
                break;
            default: throw new InvalidOperationException($"{where}: '{fp.Definition.Name}' cannot be set");
        }
    }

    private static object Shown(FamilyManager fm, FamilyType t, FamilyParameter fp)
    {
        if (!t.HasValue(fp)) return null;
        var spec = fp.Definition.GetDataType();
        switch (fp.StorageType)
        {
            case StorageType.String: return t.AsString(fp);
            case StorageType.Integer: return spec == SpecTypeId.Boolean.YesNo ? (object)(t.AsInteger(fp) == 1) : t.AsInteger(fp);
            case StorageType.Double:
                double d = t.AsDouble(fp) ?? 0;
                if (spec == SpecTypeId.Length) return U.Out(d);
                if (spec == SpecTypeId.Area) return Math.Round(UnitUtils.ConvertFromInternalUnits(d, UnitTypeId.SquareMeters), 4);
                if (spec == SpecTypeId.Volume) return Math.Round(UnitUtils.ConvertFromInternalUnits(d, UnitTypeId.CubicMeters), 4);
                if (spec == SpecTypeId.Angle) return Math.Round(d * 180 / Math.PI, 4);
                return Math.Round(d, 6);
            default: return t.AsValueString(fp);
        }
    }

    /// <summary>
    /// Parameters, formulas, types and type values inside loadable families, as the engineer gives them; loaded back with
    /// the family's values. Order: add, rename, new types, formulas, values, remove. Nothing about geometry.
    /// </summary>
    private static object FamilyParameters(EditContext c, Obj p)
    {
        var add = Has(p, "add") ? ObjList(p, "add", 50) : new List<Obj>();
        var rename = Has(p, "rename") ? Sub(p, "rename") : null;
        var newTypes = Has(p, "new_types") ? ObjList(p, "new_types", 100) : new List<Obj>();
        var formulas = Has(p, "formulas") ? Sub(p, "formulas") : null;
        var values = Has(p, "values") ? Sub(p, "values") : null;
        var remove = Has(p, "remove") ? StrList(p, "remove", 50) : new List<string>();
        if (add.Count == 0 && rename == null && newTypes.Count == 0 && formulas == null && values == null && remove.Count == 0)
            throw new ArgumentException("family_parameters needs at least one of: add, rename, new_types, formulas, values, remove.");
        foreach (var a in add)
        {
            NeedStr(a, "name");
            Choice(a, "type", null, Specs.Keys.ToArray());
            Choice(a, "group", "other", Groups.Keys.ToArray());
        }
        foreach (var t in newTypes) NeedStr(t, "name");
        return InFamilies(c, p, "change family parameters", (fdoc, fm) =>
        {
            int n = 0;
            var report = new Obj();
            var added = new List<object>();
            foreach (var a in add)
            {
                string name = NeedStr(a, "name");
                if (fm.get_Parameter(name) != null)
                {
                    if (Str(a, "if_exists") == "skip") { added.Add(new { name, skipped = true }); continue; }
                    throw new InvalidOperationException($"the family already has a parameter '{name}' (if_exists: 'skip' to accept it)");
                }
                var fp = fm.AddParameter(name, Groups[Choice(a, "group", "other", Groups.Keys.ToArray())], Specs[Choice(a, "type", null, Specs.Keys.ToArray())], Flag(a, "instance", false));
                added.Add(new { name, instance = fp.IsInstance });
                n++;
            }
            if (added.Count > 0) report["added"] = added;
            if (rename != null)
            {
                foreach (var kv in rename) { fm.RenameParameter(NeedParam(fm, kv.Key), Convert.ToString(kv.Value, CultureInfo.InvariantCulture)); n++; }
                report["renamed"] = rename.Count;
            }
            var types = new List<object>();
            foreach (var t in newTypes)
            {
                string name = NeedStr(t, "name");
                var existing = fm.Types.Cast<FamilyType>().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (Str(t, "if_exists") == "skip") { types.Add(new { name, skipped = true }); continue; }
                    throw new InvalidOperationException($"the family already has a type '{name}' (if_exists: 'skip' to accept it)");
                }
                if (Str(t, "from") is string from)
                    fm.CurrentType = fm.Types.Cast<FamilyType>().FirstOrDefault(x => x.Name.Equals(from, StringComparison.OrdinalIgnoreCase))
                                     ?? throw new InvalidOperationException($"no type '{from}' to copy");
                fm.NewType(name);   // a copy of the current type's values
                types.Add(new { name, from = Str(t, "from") });
                n++;
            }
            if (types.Count > 0) report["newTypes"] = types;
            if (formulas != null)
            {
                foreach (var kv in formulas)
                {
                    var fp = NeedParam(fm, kv.Key);
                    string f = kv.Value == null ? null : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                    fm.SetFormula(fp, string.IsNullOrEmpty(f) ? null : f);
                    n++;
                }
                report["formulas"] = formulas.Keys.ToList();
            }
            if (values != null)
            {
                int set = 0;
                foreach (var kv in values)
                {
                    if (!(kv.Value is Obj pv)) throw new InvalidOperationException($"values.{kv.Key} must be an object {{parameter: value}}");
                    var targets = kv.Key == "*" ? fm.Types.Cast<FamilyType>().ToList()
                        : new List<FamilyType> { fm.Types.Cast<FamilyType>().FirstOrDefault(x => x.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                                                 ?? throw new InvalidOperationException($"no type '{kv.Key}' in the family (types: {string.Join(", ", fm.Types.Cast<FamilyType>().Select(x => x.Name))})") };
                    foreach (var ft in targets)
                    {
                        fm.CurrentType = ft;
                        foreach (var pvk in pv) { SetValue(fdoc, fm, NeedParam(fm, pvk.Key), pvk.Value, $"type '{ft.Name}'"); set++; }
                    }
                }
                report["valuesSet"] = set;
                n += set;
            }
            if (remove.Count > 0)
            {
                foreach (var r in remove) { fm.RemoveParameter(NeedParam(fm, r)); n++; }
                report["removed"] = remove;
            }
            fdoc.Regenerate();
            // what the family now says, for the parameters this call touched
            var touched = new HashSet<string>(add.Select(a => NeedStr(a, "name")), StringComparer.OrdinalIgnoreCase);
            if (rename != null) foreach (var kv in rename) touched.Add(Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
            if (formulas != null) foreach (var k in formulas.Keys) touched.Add(k);
            if (values != null) foreach (var pv in values.Values.OfType<Obj>()) foreach (var k in pv.Keys) touched.Add(k);
            touched.ExceptWith(remove);
            var now = new List<object>();
            foreach (var name in touched.OrderBy(x => x))
            {
                var fp = fm.get_Parameter(name);
                if (fp == null) continue;
                now.Add(new
                {
                    name, instance = fp.IsInstance, formula = fp.Formula,
                    types = fm.Types.Cast<FamilyType>().OrderBy(t => t.Name).ToDictionary(t => t.Name, t => Shown(fm, t, fp)),
                });
            }
            report["parameters"] = now;
            return new Change { Changed = n, Report = report };
        }, overwriteValues: true, anyCategory: true);
    }
}
