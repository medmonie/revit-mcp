namespace RevitMcpBridge;

/// <summary>
/// Model management: shared / project parameters (add from a shared parameter file, bind to categories, remove),
/// purge unused elements, copy standards from another open model, worksets (create, move elements) and phases.
/// </summary>
internal static class ManageOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["parameter_add"] = ParameterAdd;
        ops["parameter_remove"] = ParameterRemove;
        ops["purge"] = Purge;
        ops["standards_copy"] = StandardsCopy;
        ops["workset_create"] = WorksetCreate;
        ops["workset_set"] = WorksetSet;
        ops["phase_set"] = PhaseSet;
    }

    // ---------------------------------------------------------------- parameters

    private static readonly Dictionary<string, Func<ForgeTypeId>> DataTypes = new Dictionary<string, Func<ForgeTypeId>>(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = () => SpecTypeId.String.Text, ["multiline"] = () => SpecTypeId.String.MultilineText, ["url"] = () => SpecTypeId.String.Url,
        ["number"] = () => SpecTypeId.Number, ["integer"] = () => SpecTypeId.Int.Integer, ["yes_no"] = () => SpecTypeId.Boolean.YesNo,
        ["length"] = () => SpecTypeId.Length, ["area"] = () => SpecTypeId.Area, ["volume"] = () => SpecTypeId.Volume,
        ["angle"] = () => SpecTypeId.Angle, ["material"] = () => SpecTypeId.Reference.Material,
    };

    /// <summary>The properties-palette group by its label ("Identity Data", "Data", "Constraints", ...).</summary>
    private static ForgeTypeId Group(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return GroupTypeId.Data;
        var all = ParameterUtils.GetAllBuiltInGroups();
        foreach (var g in all)
        {
            string name;
            try { name = LabelUtils.GetLabelForGroup(g); } catch { continue; }
            if (name.Equals(label.Trim(), StringComparison.OrdinalIgnoreCase)) return g;
        }
        throw new ArgumentException($"No parameter group '{label}'. Examples: Data, Identity Data, Constraints, Dimensions, Structural, Text, Other.");
    }

    /// <summary>The default shared parameter file for the bridge, created empty when no file is set.</summary>
    private static string DefaultSharedFile(bool dryRun)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMcpBridge");
        var path = Path.Combine(dir, "SharedParameters.txt");
        if (!dryRun && !File.Exists(path))
        {
            Directory.CreateDirectory(dir);
            using (File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { }
        }
        return path;
    }

    private static object ParameterAdd(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var app = doc.Application;
        var names = Has(p, "names") ? StrList(p, "names", 100) : new List<string> { NeedStr(p, "name") };
        var cats = Find.Cats(doc, p, "categories");
        if (cats.Count == 0) throw new ArgumentException("Give 'categories' (e.g. [\"Structural Columns\", \"Structural Framing\"]).");
        var requestedCategories = new List<Category>();
        foreach (var bic in cats)
        {
            var cat = Category.GetCategory(doc, bic) ?? throw new ArgumentException($"Category {bic} is not in this model.");
            if (!cat.AllowsBoundParameters) throw new ArgumentException($"Revit does not allow parameters on '{cat.Name}'.");
            requestedCategories.Add(cat);
        }
        bool instance = Choice(p, "binding", "instance", "instance", "type") == "instance";
        var group = Group(Str(p, "group_under"));
        string fileGroup = Str(p, "file_group") ?? "Revit MCP";
        string typeName = Str(p, "data_type") ?? "text";
        if (!DataTypes.TryGetValue(typeName, out var spec))
            throw new ArgumentException($"data_type '{typeName}' is not one of: {string.Join(", ", DataTypes.Keys)}.");
        bool create = Flag(p, "create_if_missing", true);

        string before = app.SharedParametersFilename;
        bool useDefault = !Has(p, "shared_file") && (string.IsNullOrEmpty(before) || !File.Exists(before));
        string path = useDefault ? DefaultSharedFile(c.DryRun)
            : Has(p, "shared_file") ? EditContext.FullPath(NeedStr(p, "shared_file"), "shared_file") : before;
        if (!File.Exists(path) && !(useDefault && c.DryRun)) throw new ArgumentException($"No shared parameter file at '{path}'.");
        // the shared parameter file is outside Revit's undo: a dry run works on a temporary copy of it
        string realPath = path;
        if (c.DryRun)
        {
            path = Path.Combine(Path.GetTempPath(), "RevitMcpBridge-dryrun-" + Guid.NewGuid().ToString("N") + ".txt");
            if (File.Exists(realPath)) File.Copy(realPath, path);
            else File.WriteAllText(path, "");
        }
        var results = new List<object>();
        try
        {
            app.SharedParametersFilename = path;
            var file = app.OpenSharedParameterFile() ?? throw new InvalidOperationException($"Revit could not read the shared parameter file '{path}'.");
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var catSet = app.Create.NewCategorySet();
                foreach (var category in requestedCategories) catSet.Insert(category);
                // the definition: anywhere in the file, else created in file_group
                var defs = file.Groups.Cast<DefinitionGroup>().SelectMany(g => g.Definitions.Cast<ExternalDefinition>())
                    .Where(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (defs.Count > 1) throw new ArgumentException($"Several shared definitions are named '{name}' in the file.");
                var def = defs.SingleOrDefault();
                // already bound in the model: add the categories to its binding
                var existing = Bound(doc, name);
                if (existing.def != null)
                {
                    if (def != null && doc.GetElement(existing.def.Id) is SharedParameterElement shared && shared.GuidValue != def.GUID)
                        throw new ArgumentException($"The shared parameter file and model use different GUIDs for '{name}'.");
                    foreach (var cat in existing.categories) catSet.Insert(cat);
                    ElementBinding merged = existing.binding is InstanceBinding ? (ElementBinding)app.Create.NewInstanceBinding(catSet) : app.Create.NewTypeBinding(catSet);
                    if (!doc.ParameterBindings.ReInsert(existing.def, merged, existing.def.GetGroupTypeId()))
                        throw new InvalidOperationException($"Revit refused to update the binding for '{name}'.");
                    results.Add(new { name, action = "categories added to the existing parameter", binding = existing.binding is InstanceBinding ? "instance" : "type",
                                      categories = merged.Categories.Cast<Category>().Select(x => x.Name).OrderBy(x => x).ToList() });
                    continue;
                }
                bool madeInFile = def == null;
                if (madeInFile)
                {
                    if (!create) throw new ArgumentException($"'{name}' is missing from the shared parameter file.");
                    var g = file.Groups.get_Item(fileGroup) ?? file.Groups.Create(fileGroup);
                    def = (ExternalDefinition)g.Definitions.Create(new ExternalDefinitionCreationOptions(name, spec()));
                    // The shared parameter file is outside Revit's undo: say so where a rolled-back batch reports it too.
                    if (!c.DryRun)
                        c.Warn($"'{name}' was written to the shared parameter file {realPath}; the file keeps it even if this batch is rolled back.");
                }
                ElementBinding binding = instance ? (ElementBinding)app.Create.NewInstanceBinding(catSet) : app.Create.NewTypeBinding(catSet);
                if (!doc.ParameterBindings.Insert(def, binding, group))
                    throw new InvalidOperationException($"Revit refused to add '{name}' (a parameter with this name may already exist with another GUID).");
                results.Add(new
                {
                    name, action = "added", guid = def.GUID.ToString(), dataType = ManageRead.SpecLabel(def), binding = instance ? "instance" : "type",
                    group = LabelUtils.GetLabelForGroup(group), addedToSharedFile = madeInFile ? $"{realPath} (group {fileGroup}){(c.DryRun ? " - dry run: not written" : "")}" : null,
                    categories = catSet.Cast<Category>().Select(x => x.Name).OrderBy(x => x).ToList(),
                });
            }
        }
        finally
        {
            app.SharedParametersFilename = before;
            if (c.DryRun) try { File.Delete(path); } catch { }
        }
        return new { sharedFile = realPath, parameters = results };
    }

    private static (InternalDefinition def, ElementBinding binding, List<Category> categories) Bound(Document doc, string name)
    {
        var it = doc.ParameterBindings.ForwardIterator();
        InternalDefinition found = null;
        ElementBinding binding = null;
        var categories = new List<Category>();
        while (it.MoveNext())
        {
            if (!(it.Key is InternalDefinition d) || !d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found != null && found.Id != d.Id) throw new ArgumentException($"Several bound parameters are named '{name}'.");
            found = d;
            binding = (ElementBinding)it.Current;
            categories.AddRange(binding.Categories.Cast<Category>());
        }
        return (found, binding, categories);
    }

    private static object ParameterRemove(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var names = Has(p, "names") ? StrList(p, "names", 100) : new List<string> { NeedStr(p, "name") };
        var removed = new List<string>();
        foreach (var name in names)
        {
            var (def, _, _) = Bound(doc, name);
            if (def == null) throw new ArgumentException($"No project parameter named '{name}' (list_parameters shows them).");
            if (!doc.ParameterBindings.Remove(def)) throw new InvalidOperationException($"Revit refused to remove '{name}'.");
            removed.Add(name);
        }
        return new { removed, note = "The values these parameters held are gone with them; Undo restores them." };
    }

    // ---------------------------------------------------------------- purge

    // Each attempted change may fail independently. Never continue after a regeneration or rollback failure.
    private static string TryChange(Document doc, Action change)
    {
        using (var part = new SubTransaction(doc))
        {
            if (part.Start() != TransactionStatus.Started) throw new InvalidOperationException("Could not start the subtransaction.");
            try { change(); }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex)
            {
                if (part.RollBack() != TransactionStatus.RolledBack) throw;
                return ErrorText(ex);
            }
            var status = part.Commit();
            if (status == TransactionStatus.RolledBack) return "Revit rolled back the subtransaction.";
            if (status != TransactionStatus.Committed) throw new InvalidOperationException($"Unexpected subtransaction status {status}.");
            return null;
        }
    }

    private static object Purge(EditContext c, Obj p)
        => Purge(c.Doc, p, c.DryRun);

    internal static object PreviewPurge(Document doc, Obj p) => Purge(doc, p, true);

    private static object Purge(Document doc, Obj p, bool preview)
    {
        foreach (var option in p)
            if (option.Value == null || (option.Key != "op" && option.Key != "categories" && option.Key != "keep_ids"
                && option.Key != "keep_names" && option.Key != "passes" && option.Key != "limit"))
                throw new ArgumentException($"Unknown or null purge option '{option.Key}'.");
        var catIds = new HashSet<ElementId>(Has(p, "categories") ? Find.Cats(doc, p, "categories").Select(b => new ElementId(b)) : Enumerable.Empty<ElementId>());
        var keep = new HashSet<long>(Has(p, "keep_ids") ? Ids(p, "keep_ids").Select(i => i.Value) : Enumerable.Empty<long>());
        var keepNames = Has(p, "keep_names") ? StrList(p, "keep_names", 500) : new List<string>();
        string Label(Element e) => e is ElementType t && !string.IsNullOrEmpty(t.FamilyName) ? t.FamilyName + " : " + t.Name : e.Name ?? "";
        int passes = Int(p, "passes", 3, 1, 10);
        int limit = Int(p, "limit", 200, 1, 5000);
        bool ProtectedName(string label) => keepNames.Any(k => label.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        if (preview)
        {
            var candidates = doc.GetUnusedElements(catIds).Where(id => !keep.Contains(id.Value))
                .OrderBy(id => id.Value).Select(id => doc.GetElement(id)).Where(e => e != null)
                .Select(e => new { id = e.Id.Value, kind = e.Category?.Name ?? e.GetType().Name, name = Label(e) })
                .Where(e => !ProtectedName(e.name)).ToList();
            return new
            {
                purged = 0, deletedIds = Array.Empty<long>(), candidateCount = candidates.Count,
                candidateIds = candidates.Take(limit).Select(e => e.id).ToList(),
                names = candidates.Take(limit).Select(e => $"{e.kind}: {e.name}").ToList(),
                byKind = candidates.GroupBy(e => e.kind).OrderByDescending(g => g.Count()).Select(g => new { kind = g.Key, count = g.Count() }).ToList(),
                truncated = candidates.Count > limit,
                note = "First-pass candidates only. Actual deletions depend on cascades, protected dependents and later passes. Subsequent preview operations see these elements still present."
            };
        }
        if (keepNames.Count > 0)
        {
            var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Concat(new FilteredElementCollector(doc).WhereElementIsElementType());
            foreach (var e in elements)
            {
                string label = Label(e);
                if (ProtectedName(label)) keep.Add(e.Id.Value);
            }
        }
        var deleted = new Dictionary<long, (string kind, string name)>();
        var refused = new HashSet<long>();
        var failed = new List<object>();
        for (int pass = 0; pass < passes; pass++)
        {
            var unused = doc.GetUnusedElements(catIds).Where(id => !keep.Contains(id.Value) && !refused.Contains(id.Value)).ToList();
            var batch = new Dictionary<long, (string kind, string name)>();
            foreach (var id in unused)
            {
                var e = doc.GetElement(id);
                if (e == null) continue;
                batch[id.Value] = (e.Category?.Name ?? e.GetType().Name, Label(e));
            }
            if (batch.Count == 0) break;
            int before = deleted.Count;
            foreach (var candidate in batch)
            {
                var id = new ElementId(candidate.Key);
                if (deleted.ContainsKey(candidate.Key)) continue;
                List<long> affected = null;
                string error = TryChange(doc, () =>
                {
                    affected = doc.Delete(id).Select(x => x.Value).ToList();
                    var protectedIds = affected.Where(keep.Contains).ToList();
                    if (protectedIds.Count > 0)
                        throw new InvalidOperationException("Would also remove protected element(s) " + string.Join(", ", protectedIds));
                });
                if (error != null)
                {
                    refused.Add(candidate.Key);
                    failed.Add(new { id = candidate.Key, name = candidate.Value.name, reason = error });
                    continue;
                }
                foreach (long removed in affected)
                    deleted[removed] = batch.TryGetValue(removed, out var label) ? label : ("Dependent", "id " + removed);
            }
            doc.Regenerate();
            if (deleted.Count == before) break;
        }
        return new
        {
            purged = deleted.Count,
            byKind = deleted.Values.GroupBy(d => d.kind).OrderByDescending(g => g.Count()).Select(g => new { kind = g.Key, count = g.Count() }).ToList(),
            names = deleted.Values.Select(d => $"{d.kind}: {d.name}").Take(limit).ToList(),
            deletedIds = deleted.Keys.Take(limit).ToList(), truncated = deleted.Count > limit,
            failed,
            note = "Run with dry_run first to see the list. Purge removes unused families, types, materials, patterns and styles; Undo brings them back.",
        };
    }

    // ---------------------------------------------------------------- copy standards

    private static readonly string[] StandardKinds =
    {
        "view_templates", "filters", "text_types", "dimension_types", "fill_patterns", "line_patterns", "materials",
        "wall_types", "floor_types", "title_blocks", "line_styles",
    };

    /// <summary>
    /// Line styles are sub-categories of Lines, not elements that copy between models; each missing one is recreated by
    /// name with the source's weight, colour and line pattern (the pattern is recreated too when this model lacks it).
    /// </summary>
    private static object CopyLineStyles(Document source, Document doc, List<string> only, string contains)
    {
        var srcLines = source.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        var dstLines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        var have = new HashSet<string>(dstLines.SubCategories.Cast<Category>().Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var picked = srcLines.SubCategories.Cast<Category>().Where(sc =>
            (only == null || only.Contains(sc.Name, StringComparer.OrdinalIgnoreCase))
            && (contains == null || sc.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        var copied = new List<string>(); var already = new List<string>(); var failed = new List<object>();
        foreach (var sc in picked.OrderBy(x => x.Name))
        {
            if (have.Contains(sc.Name) || sc.Name.StartsWith("<")) { already.Add(sc.Name); continue; }
            try
            {
                var made = doc.Settings.Categories.NewSubcategory(dstLines, sc.Name);
                var w = sc.GetLineWeight(GraphicsStyleType.Projection);
                if (w is int weight) made.SetLineWeight(weight, GraphicsStyleType.Projection);
                made.LineColor = sc.LineColor;
                var pid = sc.GetLinePatternId(GraphicsStyleType.Projection);
                if (pid != null && pid != ElementId.InvalidElementId && pid != LinePatternElement.GetSolidPatternId()
                    && source.GetElement(pid) is LinePatternElement lp)
                {
                    var mine = LinePatternElement.GetLinePatternElementByName(doc, lp.Name)
                               ?? LinePatternElement.Create(doc, lp.GetLinePattern());
                    made.SetLinePatternId(mine.Id, GraphicsStyleType.Projection);
                }
                copied.Add(sc.Name);
            }
            catch (Exception ex) { failed.Add(new { name = sc.Name, reason = ex.Message }); }
        }
        var notFound = only?.Where(n => !picked.Any(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        return new { kind = "line_styles", copied, alreadyInModel = already, notFound, failed };
    }

    private static List<Element> StandardsOf(Document doc, string kind)
    {
        IEnumerable<Element> all;
        switch (kind)
        {
            case "view_templates": all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate); break;
            case "filters": all = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)); break;
            case "text_types": all = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)); break;
            case "dimension_types": all = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Where(t => !string.IsNullOrEmpty(t.Name)); break;
            case "fill_patterns": all = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)); break;
            case "line_patterns": all = new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)); break;
            case "materials": all = new FilteredElementCollector(doc).OfClass(typeof(Material)); break;
            case "wall_types": all = new FilteredElementCollector(doc).OfClass(typeof(WallType)); break;
            case "floor_types": all = new FilteredElementCollector(doc).OfClass(typeof(FloorType)); break;
            case "title_blocks": all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_TitleBlocks); break;
            default: throw new ArgumentException($"kind '{kind}' is not one of: {string.Join(", ", StandardKinds)}.");
        }
        return all.ToList();
    }

    private static string StandardName(Element e) =>
        e is GraphicsStyle gs ? gs.GraphicsStyleCategory?.Name ?? e.Name
        : e is FamilySymbol fs ? fs.FamilyName + " : " + fs.Name : e.Name;

    private static object StandardsCopy(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var source = DetailOps.OtherModel(doc, NeedStr(p, "model"));
        var kinds = p.ContainsKey("kinds") ? StrList(p, "kinds", 20) : new List<string> { NeedStr(p, "kind") };
        var only = Has(p, "names") ? StrList(p, "names", 1000) : null;
        string contains = Str(p, "name_contains");
        var options = new CopyPasteOptions();
        options.SetDuplicateTypeNamesHandler(new DetailOps.UseDestinationTypes());
        var report = new List<object>();
        foreach (var kind in kinds.Select(k => k.Trim().ToLowerInvariant()))
        {
            if (kind == "line_styles") { report.Add(CopyLineStyles(source, doc, only, contains)); continue; }
            var have = new HashSet<string>(StandardsOf(doc, kind).Select(StandardName), StringComparer.OrdinalIgnoreCase);
            var picked = StandardsOf(source, kind).Where(e =>
            {
                string n = StandardName(e);
                if (only != null && !only.Contains(n, StringComparer.OrdinalIgnoreCase) && !only.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) return false;
                return contains == null || n.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();
            var already = picked.Where(e => have.Contains(StandardName(e))).Select(StandardName).OrderBy(n => n).ToList();
            var toCopy = picked.Where(e => !have.Contains(StandardName(e))).ToList();
            var copied = new List<string>();
            if (toCopy.Count > 0)
            {
                var newIds = ElementTransformUtils.CopyElements(source, toCopy.Select(e => e.Id).ToList(), doc, Transform.Identity, options);
                copied = newIds.Select(id => doc.GetElement(id)).Where(e => e != null).Select(StandardName).OrderBy(n => n).ToList();
            }
            report.Add(new { kind, copied, alreadyInModel = already, notFound = only?.Where(n => !picked.Any(e => StandardName(e).Equals(n, StringComparison.OrdinalIgnoreCase) || e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList() });
        }
        return new
        {
            from = source.Title, kinds = report,
            note = "Items with the same name already in this model are kept as they are (not overwritten). Copying a type also brings what it uses (materials, patterns).",
        };
    }

    // ---------------------------------------------------------------- worksets and phases

    private static void NeedWorkshared(Document doc)
    {
        if (!doc.IsWorkshared) throw new InvalidOperationException("This model is not workshared, so it has no worksets.");
    }

    private static Workset WorksetNamed(Document doc, string name) =>
        new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets()
            .FirstOrDefault(w => w.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No workset '{name}'. Worksets: " +
                                       string.Join(", ", new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Select(w => w.Name)) + ".");

    private static object WorksetCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        NeedWorkshared(doc);
        var names = Has(p, "names") ? StrList(p, "names", 100) : new List<string> { NeedStr(p, "name") };
        var made = new List<object>(); var skipped = new List<string>();
        foreach (var name in names)
        {
            if (!WorksetTable.IsWorksetNameUnique(doc, name)) { skipped.Add(name); continue; }
            var w = Workset.Create(doc, name);
            made.Add(new { id = w.Id.IntegerValue, name = w.Name });
        }
        return new { created = made, alreadyExist = skipped };
    }

    private static object WorksetSet(EditContext c, Obj p)
    {
        var doc = c.Doc;
        NeedWorkshared(doc);
        var w = WorksetNamed(doc, NeedStr(p, "workset"));
        var moved = new List<long>(); var failed = new List<object>();
        foreach (var id in Ids(p, "ids", 5000))
        {
            var e = c.Editable(Find.Elem(doc, id));
            var prm = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            if (prm == null || prm.IsReadOnly) { failed.Add(new { id = id.Value, reason = "its workset cannot be changed (types, views and some hosted elements follow their owner)" }); continue; }
            prm.Set(w.Id.IntegerValue);
            moved.Add(id.Value);
        }
        return new { workset = w.Name, moved = moved.Count, ids = moved, failed };
    }

    private static object PhaseSet(EditContext c, Obj p)
    {
        var doc = c.Doc;
        Phase PhaseNamed(string name) => doc.Phases.Cast<Phase>().FirstOrDefault(ph => ph.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No phase '{name}'. Phases: {string.Join(", ", doc.Phases.Cast<Phase>().Select(ph => ph.Name))}.");
        var created = Str(p, "created") is string cr ? PhaseNamed(cr) : null;
        string dem = Str(p, "demolished");
        var demolished = dem == null || dem.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : PhaseNamed(dem);
        if (created == null && dem == null) throw new ArgumentException("Give 'created' and/or 'demolished' (a phase name, or 'none' to clear demolished).");
        if (created != null && demolished != null &&
            doc.Phases.Cast<Phase>().ToList().FindIndex(ph => ph.Id == demolished.Id) < doc.Phases.Cast<Phase>().ToList().FindIndex(ph => ph.Id == created.Id))
            throw new ArgumentException("The demolished phase comes before the created phase.");
        var done = new List<long>(); var failed = new List<object>();
        foreach (var id in Ids(p, "ids", 5000).Distinct())
        {
            bool changed = false;
            string error = TryChange(doc, () =>
            {
                var e = c.Editable(Find.Elem(doc, id));
                var updates = new List<(Parameter parameter, ElementId value)>();
                void Plan(BuiltInParameter key, ElementId value)
                {
                    var parameter = e.get_Parameter(key);
                    if (parameter == null) throw new InvalidOperationException($"Element has no {key}.");
                    if (parameter.AsElementId() == value) return;
                    if (parameter.IsReadOnly) throw new InvalidOperationException($"{key} is read-only.");
                    updates.Add((parameter, value));
                }
                if (created != null) Plan(BuiltInParameter.PHASE_CREATED, created.Id);
                if (dem != null) Plan(BuiltInParameter.PHASE_DEMOLISHED, demolished?.Id ?? ElementId.InvalidElementId);
                foreach (var update in updates)
                    if (!update.parameter.Set(update.value)) throw new InvalidOperationException($"Revit refused to set {update.parameter.Definition.Name}.");
                changed = updates.Count > 0;
            });
            if (error != null) failed.Add(new { id = id.Value, reason = error });
            else if (changed) done.Add(id.Value);
        }
        return new { created = created?.Name, demolished = dem == null ? null : demolished?.Name ?? "none", changed = done.Count, failed };
    }
}
