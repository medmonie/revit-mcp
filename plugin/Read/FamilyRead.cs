namespace RevitMcpBridge;

/// <summary>Look inside a loaded family (opened in memory, closed without saving): parameters, solids and their materials.</summary>
internal static class FamilyRead
{
    public static object FamilyInspect(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        string name = NeedStr(p, "family");
        var fam = Find.FamilyNamed(doc, name);
        if (fam.IsInPlace || !fam.IsEditable) throw new ArgumentException($"'{name}' is in-place or not editable.");
        Document fdoc = null;
        try
        {
            fdoc = doc.EditFamily(fam);
            var fm = fdoc.FamilyManager;
            var pars = new List<object>();
            foreach (FamilyParameter fp in fm.Parameters)
            {
                string kind;
                try { kind = fp.Definition.GetDataType().TypeId; } catch { kind = fp.StorageType.ToString(); }
                pars.Add(new
                {
                    name = fp.Definition.Name, instance = fp.IsInstance, builtIn = fp.Id.Value < 0, storage = fp.StorageType.ToString(),
                    kind, formula = string.IsNullOrEmpty(fp.Formula) ? null : fp.Formula,
                    associatedElements = fp.AssociatedParameters?.Size ?? 0,
                });
            }
            var forms = new List<object>(); var nested = new List<string>(); var classes = new Dictionary<string, int>();
            foreach (var el in new FilteredElementCollector(fdoc).WhereElementIsNotElementType())
            {
                string cls = el.GetType().Name;
                classes[cls] = classes.TryGetValue(cls, out int k) ? k + 1 : 1;
                if (el is FamilyInstance fi) { nested.Add(fi.Symbol.FamilyName + " : " + fi.Symbol.Name); continue; }
                if (!(el is GenericForm || el is FreeFormElement)) continue;
                var mp = el.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                forms.Add(new
                {
                    id = el.Id.Value, cls, solid = (el as GenericForm)?.IsSolid,
                    material = mp == null ? null : (fdoc.GetElement(mp.AsElementId()) as Material)?.Name ?? "<By Category>",
                    materialLinkedTo = mp == null ? null : fm.GetAssociatedFamilyParameter(mp)?.Definition.Name,
                    canLink = mp != null && fm.CanElementParameterBeAssociated(mp),
                });
            }
            return new
            {
                family = fam.Name, category = fam.FamilyCategory?.Name, types = fm.Types.Size,
                placement = fam.FamilyPlacementType.ToString(), hosting = Hosting(fdoc.OwnerFamily),
                parameters = pars, forms, nestedFamilies = nested.GroupBy(x => x).Select(g => new { family = g.Key, count = g.Count() }).ToList(),
                elementClasses = classes,
            };
        }
        finally { try { fdoc?.Close(false); } catch { } }
    }

    /// <summary>The family's own placement settings (Family Category and Parameters dialog).</summary>
    private static object Hosting(Family owner)
    {
        if (owner == null) return null;
        string Flag(BuiltInParameter b) { var pr = owner.get_Parameter(b); return pr == null || !pr.HasValue ? null : pr.AsInteger() != 0 ? "yes" : "no"; }
        var h = owner.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR);
        string host = h == null || !h.HasValue ? null : h.AsValueString() ?? h.AsInteger().ToString(CultureInfo.InvariantCulture);
        return new
        {
            host, workPlaneBased = Flag(BuiltInParameter.FAMILY_WORK_PLANE_BASED), alwaysVertical = Flag(BuiltInParameter.FAMILY_ALWAYS_VERTICAL),
            cutWithVoidsWhenLoaded = Flag(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS),
        };
    }
}
