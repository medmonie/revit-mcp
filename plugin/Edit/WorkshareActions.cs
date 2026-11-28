using System.Diagnostics;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 12: worksharing. Synchronise with central, reload latest, relinquish and check out. These talk to the
/// central model (on a server or in Autodesk Docs), cannot run inside a transaction and have no dry run, so each is its
/// own action, never an operation of an edit batch. They need the edit permission; sync and relinquish also need
/// confirm: true in the request, so every one is a deliberate, separate step. worksharing_status reads only.
/// </summary>
internal static class WorkshareActions
{
    private static Document Workshared(Rc rc)
    {
        var doc = rc.Doc;
        if (!doc.IsWorkshared) throw new InvalidOperationException($"'{doc.Title}' is not workshared: there is no central model.");
        if (doc.IsModifiable) throw new InvalidOperationException("Revit has a transaction open (a command is in progress). Finish it and try again.");
        if (doc.IsReadOnly) throw new InvalidOperationException("The active model is read-only.");
        return doc;
    }

    private static void Confirmed(Obj p, string what)
    {
        if (!Flag(p, "confirm", false))
            throw new ArgumentException($"{what} changes the central model and cannot be undone or previewed. Ask the user, then send it again with confirm: true.");
    }

    private static object Denied(string reason) => new Obj { ["applied"] = false, ["permission"] = "not granted", ["reason"] = reason };

    private static string Central(Document doc)
    {
        try
        {
            var path = doc.IsModelInCloud ? doc.GetCloudModelPath() : doc.GetWorksharingCentralModelPath();
            return path == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
        }
        catch { return null; }
    }

    // Asking the central model about every element keeps Revit's window frozen: stop after this long and say so.
    private const double HeldScanSeconds = 5;

    /// <summary>Elements and worksets the current user holds in this model. The element scan is time-limited.</summary>
    private static Obj Held(Document doc, int sample = 20)
    {
        var mine = new List<long>();
        var ids = new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds()
            .Concat(new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds());
        var clock = Stopwatch.StartNew();
        int scanned = 0;
        bool complete = true;
        foreach (var id in ids)
        {
            if ((scanned & 255) == 0 && clock.Elapsed.TotalSeconds > HeldScanSeconds) { complete = false; break; }
            scanned++;
            try { if (WorksharingUtils.GetCheckoutStatus(doc, id) == CheckoutStatus.OwnedByCurrentUser) mine.Add(id.Value); } catch { /* not checkable */ }
        }
        var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets()
            .Where(w => w.Owner == doc.Application.Username).Select(w => w.Name).ToList();
        var held = new Obj { ["elementsHeld"] = mine.Count, ["sample"] = mine.Take(sample).ToList(), ["worksetsHeld"] = worksets,
                             ["elementsScanned"] = scanned, ["scanComplete"] = complete };
        if (!complete)
            held["scanNote"] = $"The scan stopped after {HeldScanSeconds:0} s: elementsHeld counts only the first {scanned} elements. "
                             + "Pass ids to worksharing_status for the elements that matter.";
        return held;
    }

    private static Obj Row(Document doc, ElementId id)
    {
        string owner;
        var status = WorksharingUtils.GetCheckoutStatus(doc, id, out owner);
        string updates;
        try { updates = WorksharingUtils.GetModelUpdatesStatus(doc, id).ToString(); } catch { updates = null; }
        var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, id);
        return new Obj
        {
            ["id"] = id.Value, ["checkout"] = status.ToString(), ["owner"] = owner, ["updates"] = updates,
            ["creator"] = info?.Creator, ["lastChangedBy"] = info?.LastChangedBy,
            ["workset"] = doc.GetWorksetTable().GetWorkset(doc.GetElement(id)?.WorksetId ?? WorksetId.InvalidWorksetId)?.Name,
        };
    }

    // ---------------------------------------------------------------- read

    public static object Status(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        if (!doc.IsWorkshared) return new { model = doc.Title, workshared = false, note = "Not workshared: no central model." };
        var o = new Obj
        {
            ["model"] = doc.Title, ["workshared"] = true, ["cloud"] = doc.IsModelInCloud, ["central"] = Central(doc),
            ["user"] = doc.Application.Username, ["hasUnsavedChanges"] = doc.IsModified,
        };
        foreach (var kv in Held(doc)) o[kv.Key] = kv.Value;
        if (Has(p, "ids")) o["elements"] = Ids(p, "ids", 500).Select(i => { Find.Elem(doc, i); return Row(doc, i); }).ToList();
        o["note"] = "checkout: OwnedByCurrentUser / OwnedByOtherUser / NotOwned. updates compares this copy with central: "
                  + "UpdatedInCentral (reload_latest brings it), NotYetInCentral (a new element sync sends), DeletedInCentral, "
                  + "CurrentWithCentral (also for an existing element edited here but not synced: check its checkout status).";
        return o;
    }

    // ---------------------------------------------------------------- actions

    public static object Sync(Rc rc, Obj p)
    {
        var doc = Workshared(rc);
        Confirmed(p, "Synchronize with central");
        string mode = Choice(p, "relinquish", "all", "all", "borrowed", "none");
        if (WriteGate.EnsureCentral(doc, "synchronize with central") is string denied) return Denied(denied);
        var before = Held(doc);
        var options = new SynchronizeWithCentralOptions { Comment = Str(p, "comment") ?? "", Compact = Flag(p, "compact", false) };
        // Cloud models (Autodesk Docs) must save the local cache before and after a sync; Revit refuses otherwise.
        bool saveLocal = doc.IsModelInCloud || Flag(p, "save_local", true);
        options.SaveLocalBefore = saveLocal;
        options.SaveLocalAfter = saveLocal;
        var rel = new RelinquishOptions(mode == "all");
        if (mode == "borrowed") rel.CheckedOutElements = true;
        options.SetRelinquishOptions(rel);
        var sw = Stopwatch.StartNew();
        rc.Started?.Invoke();
        doc.SynchronizeWithCentral(new TransactWithCentralOptions(), options);
        Audit.Write("sync_with_central", new { model = doc.Title, comment = options.Comment, relinquish = mode, seconds = sw.Elapsed.TotalSeconds });
        var after = Held(doc);
        return new
        {
            applied = true, model = doc.Title, central = Central(doc), comment = options.Comment, relinquish = mode, compact = options.Compact,
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), heldBefore = before, heldAfter = after, hasUnsavedChanges = doc.IsModified,
        };
    }

    public static object Reload(Rc rc, Obj p)
    {
        var doc = Workshared(rc);
        if (WriteGate.Ensure(doc, "reload latest") is string denied) return Denied(denied);
        var sw = Stopwatch.StartNew();
        rc.Started?.Invoke();
        doc.ReloadLatest(new ReloadLatestOptions());
        Audit.Write("reload_latest", new { model = doc.Title, seconds = sw.Elapsed.TotalSeconds });
        return new { applied = true, model = doc.Title, central = Central(doc), seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) };
    }

    public static object Relinquish(Rc rc, Obj p)
    {
        var doc = Workshared(rc);
        Confirmed(p, "Relinquish");
        string what = Choice(p, "what", "all", "all", "borrowed", "worksets");
        if (WriteGate.EnsureCentral(doc, "relinquish " + what) is string denied) return Denied(denied);
        var options = new RelinquishOptions(what == "all");
        if (what == "borrowed") options.CheckedOutElements = true;
        if (what == "worksets") { options.UserWorksets = true; options.StandardWorksets = true; options.ViewWorksets = true; options.FamilyWorksets = true; }
        var before = Held(doc);
        rc.Started?.Invoke();
        var items = WorksharingUtils.RelinquishOwnership(doc, options, new TransactWithCentralOptions());
        var table = doc.GetWorksetTable();
        var result = new
        {
            applied = true, model = doc.Title, what,
            relinquishedElements = items.GetRelinquishedElements().Count,
            relinquishedWorksets = items.GetRelinquishedWorksets().Select(w => table.GetWorkset(w)?.Name).ToList(),
            heldBefore = before, heldAfter = Held(doc),
        };
        Audit.Write("relinquish", new { model = doc.Title, what, result.relinquishedElements });
        return result;
    }

    public static object Checkout(Rc rc, Obj p)
    {
        var doc = Workshared(rc);
        if (!Has(p, "ids") && !Has(p, "worksets")) throw new ArgumentException("Give 'ids' (elements) and / or 'worksets' (names) to check out.");
        if (WriteGate.Ensure(doc, "check out") is string denied) return Denied(denied);
        var o = new Obj { ["applied"] = true, ["model"] = doc.Title };
        HashSet<ElementId> wantIds = null;
        if (Has(p, "ids"))
        {
            wantIds = new HashSet<ElementId>(Ids(p, "ids", 5000));
            foreach (var id in wantIds) Find.Elem(doc, id);
        }
        HashSet<WorksetId> want = null;
        var all = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets();
        if (Has(p, "worksets"))
        {
            want = new HashSet<WorksetId>();
            foreach (var name in StrList(p, "worksets", 200))
                want.Add((all.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No workset '{name}'. Worksets: {string.Join(", ", all.Select(w => w.Name))}.")).Id);
        }
        rc.Started?.Invoke();
        if (wantIds != null)
        {
            var ids = wantIds;
            var got = WorksharingUtils.CheckoutElements(doc, ids, new TransactWithCentralOptions());
            o["elementsCheckedOut"] = got.Count;
            o["notCheckedOut"] = ids.Where(i => !got.Contains(i)).Select(i => Row(doc, i)).ToList();
        }
        if (want != null)
        {
            var got = WorksharingUtils.CheckoutWorksets(doc, want, new TransactWithCentralOptions());
            var table = doc.GetWorksetTable();
            o["worksetsCheckedOut"] = got.Select(w => table.GetWorkset(w).Name).ToList();
            o["worksetsNotCheckedOut"] = want.Where(w => !got.Contains(w)).Select(w => new { name = table.GetWorkset(w).Name, owner = table.GetWorkset(w).Owner }).ToList();
        }
        Audit.Write("checkout", new { model = doc.Title });
        return o;
    }
}
