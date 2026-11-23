namespace RevitMcpBridge;

/// <summary>Read actions about individual elements and the user's selection.</summary>
internal static class ElementRead
{
    private static Obj Row(Document doc, Element e, bool location, bool box, List<string> names, bool details = false)
    {
        var o = Describe.Brief(doc, e);
        if (location && Describe.Location(e) is object loc) o["location"] = loc;
        if (box && Describe.Box(e) is object b) o["box"] = b;
        if (names.Count > 0)
        {
            var info = details ? new Obj() : null;
            o["params"] = Describe.Values(doc, e, names, info);
            if (info != null) o["parameterInfo"] = info;
        }
        return o;
    }

    public static object GetElements(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        IEnumerable<Element> src;
        var missing = new List<string>();
        bool byReference = Has(p, "ids") || Has(p, "unique_ids");
        if (Has(p, "ids") && Has(p, "unique_ids")) throw new ArgumentException("Give either ids or unique_ids.");
        if (byReference)
        {
            var found = new List<Element>();
            bool allowMissing = Flag(p, "allow_missing", false);
            void Add(Element e, string key)
            {
                if (e != null) found.Add(e);
                else if (allowMissing) missing.Add(key);
                else throw new ArgumentException($"No element with id '{key}' in this model.");
            }

            if (Has(p, "ids"))
                foreach (var id in Ids(p, "ids").Distinct()) Add(doc.GetElement(id), Text(id.Value));
            else
                foreach (string id in StrList(p, "unique_ids", 5000).Distinct()) Add(doc.GetElement(id), id);
            src = found;
        }
        else
        {
            var cats = Find.Cats(doc, p, "category");
            if (cats.Count == 0) throw new ArgumentException("get_elements needs 'category' (one name or a list) or 'ids'. Use count_by_category to see what the model holds.");
            var col = Has(p, "view") ? new FilteredElementCollector(doc, Find.ViewOf(doc, Ref(p, "view")).Id) : new FilteredElementCollector(doc);
            src = cats.Count == 1
                ? col.WhereElementIsNotElementType().OfCategory(cats[0])
                : col.WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(cats));
        }

        string type = Str(p, "type"), level = Ref(p, "level");
        if (type != null)
            src = src.Where(e => doc.GetElement(e.GetTypeId()) is ElementType t &&
                                 (string.Equals(t.Name, type.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(Find.TypeLabel(t), type.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (level != null)
        {
            string levelName = Find.LevelOf(doc, level).Name;
            src = src.Where(e => Describe.LevelName(doc, e) == levelName);
        }

        bool location = Flag(p, "location", true), box = Flag(p, "box", false);
        bool details = Flag(p, "parameter_details", false);
        var names = StrList(p, "params", 40, required: false).Distinct().ToList();
        long after = Has(p, "after_id") ? NeedId(p, "after_id").Value : long.MinValue;
        var page = (Obj)Describe.Page(p, src.Where(e => e.Id.Value > after).OrderBy(e => e.Id.Value)
            .Select(e => Row(doc, e, location, box, names, details)), "elements", 100, 5000);
        if ((bool)page["truncated"] && page["elements"] is List<Obj> rows)
            page["nextAfterId"] = Text(rows[rows.Count - 1]["id"]);
        if (byReference) page["missingIds"] = missing;
        return page;
    }

    private static Obj AllParams(Document doc, Element e, string target, List<string> readOnly, out List<Obj> details)
    {
        var o = new Obj();
        details = new List<Obj>();
        var groups = e.Parameters.Cast<Parameter>().Where(x => !string.IsNullOrEmpty(x.Definition?.Name))
            .GroupBy(x => x.Definition.Name).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            bool ambiguous = group.Count() > 1;
            foreach (var prm in group)
            {
                var info = Describe.ParameterInfo(prm, target);
                try { info["value"] = Describe.Value(doc, prm); }
                catch (Exception ex) { info["error"] = ErrorText(ex); }
                details.Add(info);
                if (ambiguous || !info.TryGetValue("value", out var value)) continue;
                o[group.Key] = value;
                if (prm.IsReadOnly) readOnly?.Add(group.Key);
            }
        }
        return o;
    }

    public static object GetElement(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var e = Find.Elem(doc, NeedId(p, "id"));
        var o = Describe.Brief(doc, e);
        o["units"] = U.Name;
        o["class"] = e.GetType().Name;
        if (Describe.Location(e) is object loc) o["location"] = loc;
        if (Describe.Box(e) is object box) o["box"] = box;
        if (e is FamilyInstance fi && fi.Host != null) o["hostId"] = fi.Host.Id.Value;
        if (e.Pinned) o["pinned"] = true;
        if (e.OwnerViewId != null && e.OwnerViewId != ElementId.InvalidElementId) o["ownerView"] = doc.GetElement(e.OwnerViewId)?.Name;
        if (doc.IsWorkshared)
        {
            try { o["checkout"] = WorksharingUtils.GetCheckoutStatus(doc, e.Id).ToString(); } catch { /* optional */ }
        }

        var readOnly = new List<string>();
        o["parameters"] = AllParams(doc, e, e is ElementType ? "type" : "instance", readOnly, out var details);
        o["parameterDetails"] = details;
        o["readOnlyParameters"] = readOnly;
        if (Flag(p, "include_type", true) && doc.GetElement(e.GetTypeId()) is ElementType t)
        {
            o["typeId"] = t.Id.Value;
            o["typeParameters"] = AllParams(doc, t, "type", null, out var typeDetails);
            o["typeParameterDetails"] = typeDetails;
        }
        return o;
    }

    public static object GetSelection(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var ids = rc.UiDoc.Selection.GetElementIds();
        var names = StrList(p, "params", 40, required: false);
        var o = (Obj)Describe.Page(p, ids.Select(id => doc.GetElement(id)).Where(e => e != null).Select(e => Row(doc, e, true, false, names)), "elements", 200, 5000);
        o["selected"] = ids.Count;
        return o;
    }

    /// <summary>Changes only what the user sees (selection and zoom), never the model.</summary>
    public static object ShowElements(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var ids = Ids(p, "ids", 2000).Where(id => doc.GetElement(id) != null).ToList();
        if (ids.Count == 0) throw new ArgumentException("None of those ids exist in this model.");
        if (Flag(p, "select", true)) rc.UiDoc.Selection.SetElementIds(ids);
        if (Flag(p, "zoom", true)) rc.UiDoc.ShowElements(ids);
        return new { shown = ids.Count };
    }
}
