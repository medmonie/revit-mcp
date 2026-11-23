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
    }

    private sealed class KeepProjectValues : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = false; return true; }
    }

    /// <summary>Loadable structural families chosen by 'families' (names) or 'family_contains'; in-place ones are reported, not edited.</summary>
    private static (List<Family> editable, List<string> notEditable) Pick(Document doc, Obj p)
    {
        var cats = Find.Cats(doc, p, "category", BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation);
        var catIds = new HashSet<long>(cats.Select(k => (long)(int)k));
        var names = Has(p, "families") ? new HashSet<string>(StrList(p, "families", 500), StringComparer.OrdinalIgnoreCase) : null;
        string contains = Str(p, "family_contains");
        if (names == null && contains == null) throw new ArgumentException("Give 'families' (names) or 'family_contains' (e.g. 'S-STL').");
        var fams = Find.Families(doc)
            .Where(f => f.FamilyCategory != null && catIds.Contains(f.FamilyCategory.Id.Value)
                        && (names == null || names.Contains(f.Name))
                        && (contains == null || f.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(f => f.Name).ToList();
        if (names != null)
        {
            var missing = names.Except(fams.Select(f => f.Name), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0) throw new ArgumentException("No loadable structural family named: " + string.Join(", ", missing) + ".");
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
    private static Obj InFamilies(EditContext c, Obj p, string what, Func<Document, FamilyManager, Change> change)
    {
        var doc = c.Doc;
        var (fams, notEditable) = Pick(doc, p);
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
                    if (done.Changed > 0) { stage = "load the family back into the model"; fdoc.LoadFamily(doc, new KeepProjectValues()); }
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
}
