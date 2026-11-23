namespace RevitMcpBridge;

/// <summary>Changing existing elements: parameters, type, position, joins, deletion; types and families.</summary>
internal static class ModifyOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["set_parameters"] = SetParameters;
        ops["change_type"] = ChangeType;
        ops["move"] = Move;
        ops["copy"] = Copy;
        ops["rotate"] = Rotate;
        ops["mirror"] = Mirror;
        ops["delete"] = Delete;
        ops["pin"] = Pin;
        ops["rename"] = Rename;
        ops["join"] = (c, p) => Joins(c, p, "join");
        ops["unjoin"] = (c, p) => Joins(c, p, "unjoin");
        ops["switch_join_order"] = (c, p) => Joins(c, p, "switch");
        ops["join_overlaps"] = JoinOverlaps;
        ops["type_duplicate"] = TypeDuplicate;
        ops["types_create"] = TypesCreate;
        ops["family_load"] = FamilyLoad;
    }

    private static List<ElementId> IdsOf(List<Element> elements) => elements.Select(e => e.Id).ToList();

    private static void Apply(Element target, Obj values, string what, Obj expected = null)
    {
        var resolved = values.Select(kv => (kv.Key, Parameter: Find.Param(target, kv.Key), kv.Value)).ToList();
        if (resolved.Select(x => x.Parameter.Id.Value).Distinct().Count() != resolved.Count)
            throw new ArgumentException($"{what}: more than one selector refers to the same parameter.");
        if (expected != null)
        {
            if (expected.Keys.Any(key => !values.ContainsKey(key))) throw new ArgumentException("expected keys must also appear in values.");
            foreach (var item in resolved)
                if (expected.TryGetValue(item.Key, out var before) && Json.Write(Describe.EditValue(item.Parameter)) != Json.Write(before))
                    throw new InvalidOperationException($"{what}: '{item.Parameter.Definition.Name}' changed since it was read.");
        }
        foreach (var item in resolved)
            Describe.SetValue(item.Parameter, item.Value, what);
    }

    private static object SetParameters(EditContext c, Obj p)
    {
        var values = Sub(p, "values") ?? throw new ArgumentException("set_parameters needs 'values': {\"Parameter name\": value, ...}.");
        if (values.Count == 0) throw new ArgumentException("'values' is empty.");
        var expected = Sub(p, "expected");
        bool onType = Choice(p, "target", "instance", "instance", "type") == "type";
        if (Has(p, "ids") && Has(p, "unique_ids")) throw new ArgumentException("Give either ids or unique_ids.");
        var elements = Has(p, "unique_ids")
            ? StrList(p, "unique_ids", 5000).Distinct().Select(id => c.Editable(c.Doc.GetElement(id)
                ?? throw new ArgumentException($"No element with unique id '{id}' in this model."))).ToList()
            : c.Elements(p);
        var done = new HashSet<long>();
        foreach (var e in elements)
        {
            Element target = e;
            if (onType)
            {
                if (e is ElementType) target = e;
                else target = c.Editable(c.Doc.GetElement(e.GetTypeId()) ?? throw new ArgumentException($"Element {e.Id.Value} has no type."));
            }
            if (!done.Add(target.Id.Value)) continue;   // several instances of one type: set the type once
            Apply(target, values, $"element {target.Id.Value}", expected);
        }
        return new { changed = done.Count, target = onType ? "type" : "instance", parameters = values.Keys.ToList() };
    }

    private static object ChangeType(EditContext c, Obj p)
    {
        var elements = c.Elements(p);
        var first = elements[0];
        BuiltInCategory cat = Has(p, "category") ? Find.Cat(c.Doc, Str(p, "category"))
            : first.Category != null ? (BuiltInCategory)(int)first.Category.Id.Value
            : throw new ArgumentException("Give 'category': the first element has none.");
        var type = Find.TypeOf(c.Doc, NeedStr(p, "type"), cat);
        if (type is FamilySymbol fs && !fs.IsActive) { fs.Activate(); c.Doc.Regenerate(); }
        foreach (var e in elements)
        {
            if (!e.IsValidType(type.Id)) throw new ArgumentException($"Element {e.Id.Value} ({e.Category?.Name}) cannot take the type '{Find.TypeLabel(type)}'.");
            e.ChangeTypeId(type.Id);
        }
        return new { changed = elements.Count, type = Find.TypeLabel(type) };
    }

    private static XYZ Displacement(Obj p)
    {
        var v = new XYZ(U.In(Dbl(p, "dx") ?? 0), U.In(Dbl(p, "dy") ?? 0), U.In(Dbl(p, "dz") ?? 0));
        if (v.GetLength() < 1e-9) throw new ArgumentException("Give at least one of dx, dy, dz (not zero).");
        return v;
    }

    private static object Move(EditContext c, Obj p)
    {
        var ids = IdsOf(c.Elements(p));
        ElementTransformUtils.MoveElements(c.Doc, ids, Displacement(p));
        return new { moved = ids.Count };
    }

    private static object Copy(EditContext c, Obj p)
    {
        var ids = IdsOf(c.Elements(p));
        var v = Displacement(p);
        int count = Int(p, "count", 1, 1, 200);
        var made = new List<long>();
        for (int k = 1; k <= count; k++)
            made.AddRange(EditContext.Values(ElementTransformUtils.CopyElements(c.Doc, ids, v.Multiply(k))));
        return new { copies = count, created = made.Count, ids = made };
    }

    private static object Rotate(EditContext c, Obj p)
    {
        var ids = IdsOf(c.Elements(p));
        var at = U.NeedPt(p, "center");
        ElementTransformUtils.RotateElements(c.Doc, ids, Line.CreateBound(at, at + XYZ.BasisZ), U.Rad(NeedDbl(p, "angle")));
        return new { rotated = ids.Count };
    }

    private static object Mirror(EditContext c, Obj p)
    {
        var ids = IdsOf(c.Elements(p));
        XYZ a = U.NeedPt(p, "axis_start"), b = U.NeedPt(p, "axis_end");
        var dir = new XYZ(b.X - a.X, b.Y - a.Y, 0);
        if (dir.GetLength() < 0.003) throw new ArgumentException("axis_start and axis_end are the same point.");
        var plane = Plane.CreateByNormalAndOrigin(dir.Normalize().CrossProduct(XYZ.BasisZ), a);
        bool copy = Flag(p, "copy", true);
        var made = ElementTransformUtils.MirrorElements(c.Doc, ids, plane, copy);
        return new { mirrored = ids.Count, copy, ids = copy ? EditContext.Values(made) : null };
    }

    private static object Delete(EditContext c, Obj p)
    {
        var ids = IdsOf(c.Elements(p));
        var gone = c.Doc.Delete(ids);
        if (gone.Count > ids.Count) c.Warn($"{gone.Count - ids.Count} dependent element(s) were deleted as well (hosted elements, tags, dimensions).");
        return new { requested = ids.Count, deleted = gone.Count };
    }

    private static object Pin(EditContext c, Obj p)
    {
        bool pinned = Flag(p, "pinned", true);
        var elements = c.Elements(p);
        foreach (var e in elements) e.Pinned = pinned;
        return new { changed = elements.Count, pinned };
    }

    private static object Rename(EditContext c, Obj p)
    {
        var e = Find.Elem(c.Doc, NeedId(p, "id"));
        string old = e.Name;
        try { e.Name = NeedStr(p, "name"); }
        catch (Exception ex) { throw new ArgumentException($"Cannot rename element {e.Id.Value} to '{Str(p, "name")}': {ex.Message}"); }
        return new { id = e.Id.Value, oldName = old, name = e.Name };
    }

    private static object Joins(EditContext c, Obj p, string mode)
    {
        var doc = c.Doc;
        int done = 0, skipped = 0;
        var pairs = NeedArr(p, "pairs", 5000);
        var seen = new HashSet<(long, long)>();
        for (int i = 0; i < pairs.Count; i++)
        {
            if (!(pairs[i] is List<object> pair) || pair.Count != 2) throw new ArgumentException($"'pairs' item {i + 1} must be [id, id].");
            var first = ToId(pair[0], "pairs");
            var second = ToId(pair[1], "pairs");
            if (first == second) throw new ArgumentException($"'pairs' item {i + 1} refers to the same element twice.");
            if (!seen.Add((Math.Min(first.Value, second.Value), Math.Max(first.Value, second.Value)))) { skipped++; continue; }
            var a = Find.Elem(doc, first);
            var b = Find.Elem(doc, second);
            bool joined = JoinGeometryUtils.AreElementsJoined(doc, a, b);
            if (mode == "join")
            {
                if (joined) { skipped++; continue; }
                JoinGeometryUtils.JoinGeometry(doc, a, b);
            }
            else
            {
                if (!joined) { skipped++; continue; }
                if (mode == "unjoin") JoinGeometryUtils.UnjoinGeometry(doc, a, b);
                else JoinGeometryUtils.SwitchJoinOrder(doc, a, b);
            }
            done++;
        }
        return new { mode, done, skipped, note = "Skipped repeated pairs and pairs where the requested change did not apply." };
    }

    /// <summary>
    /// The auto-join fix: join every overlapping, unjoined pair in scope and set which element cuts which by a category
    /// priority (default: foundations, columns, walls, beams, floors - an element earlier in the list keeps the shared volume).
    /// </summary>
    private static object JoinOverlaps(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var priority = Has(p, "priority") ? StrList(p, "priority", 20).Select(x => Find.Cat(doc, x)).ToList() : Overlaps.DefaultCategories.ToList();
        // Volumes are computed so that pairs which only touch are left alone (joining them only makes Revit warn).
        var r = Overlaps.Search(doc, p, computeVolume: true, maxPairs: Int(p, "max_pairs", 1000, 1, 5000));
        int joined = 0, switched = 0, failedCount = 0;
        var failed = new List<object>();
        foreach (var pair in r.Pairs)
        {
            // Failed volume measurements remain in the review list.
            if (pair.NeedsReview) continue;
            // first = the element that should cut (keep the shared volume)
            var (first, second) = Overlaps.Rank(pair.A, priority) <= Overlaps.Rank(pair.B, priority) ? (pair.A, pair.B) : (pair.B, pair.A);
            using (var part = new SubTransaction(doc))
            {
                if (part.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the join subtransaction.");
                try
                {
                    JoinGeometryUtils.JoinGeometry(doc, first, second);
                    bool switchOrder = !JoinGeometryUtils.IsCuttingElementInJoin(doc, first, second);
                    if (switchOrder) JoinGeometryUtils.SwitchJoinOrder(doc, first, second);
                    if (part.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit did not commit the join.");
                    c.Failures.JoinPairs.Add((Math.Min(first.Id.Value, second.Id.Value), Math.Max(first.Id.Value, second.Id.Value)));
                    joined++;
                    if (switchOrder) switched++;
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception ex)
                {
                    if (part.GetStatus() == TransactionStatus.Started && part.RollBack() != TransactionStatus.RolledBack) throw;
                    failedCount++;
                    if (failed.Count < 50) failed.Add(new { a = first.Id.Value, b = second.Id.Value, reason = ErrorText(ex) });
                }
            }
        }
        if (failedCount > 0) c.Warn($"{failedCount} pair(s) could not be joined. See 'failed' for examples.");
        int reviewPairCount = r.Pairs.Count(pair => pair.NeedsReview);
        return new
        {
            found = r.Pairs.Count, joined, orderSwitched = switched, failed, failedCount,
            reviewPairCount, unmeasuredPairs = r.Pairs.Count(pair => pair.Volume == null),
            priority = priority.Select(x => x.ToString().Substring(4)).ToList(),
            steelPairsSkipped = r.SkippedSteel,
            complete = !r.TimedOut, resumeAfter = r.ResumeAfter,
            note = (r.TimedOut ? $"Pass resume_after='{r.ResumeAfter}' with the same filters to continue. " : "") +
                   "Use find_overlaps to inspect coincidence, containment, and unmeasured pairs. See resolvedByRevit for detach resolutions.",
        };
    }

    private static object TypeDuplicate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        BuiltInCategory? cat = Has(p, "category") ? Find.Cat(doc, Str(p, "category")) : (BuiltInCategory?)null;
        var source = Find.TypeOf(doc, NeedStr(p, "type"), cat);
        string name = NeedStr(p, "new_name");
        ElementType copy;
        try { copy = source.Duplicate(name); }
        catch (Exception ex) { throw new ArgumentException($"Cannot duplicate '{Find.TypeLabel(source)}' as '{name}': {ex.Message}"); }
        if (Sub(p, "values") is Obj values) Apply(copy, values, $"new type '{name}'");
        return new { id = copy.Id.Value, family = copy.FamilyName, type = copy.Name };
    }

    /// <summary>Many types from one base type, e.g. a column schedule: [{name: "C1 400x800", values: {b: 400, h: 800}}, ...].</summary>
    private static object TypesCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        BuiltInCategory? cat = Has(p, "category") ? Find.Cat(doc, Str(p, "category")) : (BuiltInCategory?)null;
        var baseType = Find.TypeOf(doc, NeedStr(p, "base_type"), cat);
        string ifExists = Choice(p, "if_exists", "fail", "fail", "skip", "update");
        // Types of the same family (loadable) or the same system family class (walls, floors ...).
        var siblings = baseType is FamilySymbol fs
            ? fs.Family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<ElementType>().ToList()
            : new FilteredElementCollector(doc).OfClass(baseType.GetType()).Cast<ElementType>().ToList();

        var created = new List<object>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var items = ObjList(p, "types", 500);
        for (int i = 0; i < items.Count; i++)
        {
            string name = NeedStr(items[i], "name");
            var values = Sub(items[i], "values");
            var existing = siblings.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (ifExists == "fail") throw new ArgumentException($"types item {i + 1}: type '{name}' already exists. Use if_exists 'skip' or 'update'.");
                if (ifExists == "skip") { skipped.Add(name); continue; }
                if (values != null) Apply(existing, values, $"type '{name}'");
                updated.Add(name);
                continue;
            }
            ElementType copy;
            try { copy = baseType.Duplicate(name); }
            catch (Exception ex) { throw new ArgumentException($"types item {i + 1}: cannot create '{name}': {ex.Message}"); }
            if (values != null) Apply(copy, values, $"new type '{name}'");
            siblings.Add(copy);
            created.Add(new { id = copy.Id.Value, type = copy.Name });
        }
        return new { family = baseType.FamilyName, created = created.Count, types = created, updated, skipped };
    }

    private static object FamilyLoad(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string path = NeedStr(p, "path");
        if (!File.Exists(path)) throw new ArgumentException($"Family file not found: {path}");
        bool loaded = doc.LoadFamily(path, out Family family);
        if (!loaded || family == null)
        {
            string name = IOPath.GetFileNameWithoutExtension(path);
            family = Find.Families(doc).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (family == null) throw new InvalidOperationException($"Revit did not load the family '{name}'.");
        }
        var types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).Where(e => e != null)
            .Select(e => new { id = e.Id.Value, type = e.Name }).ToList();
        return new { family = family.Name, category = family.FamilyCategory?.Name, alreadyLoaded = !loaded, types };
    }
}
