using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI.Selection;

namespace RevitMcpBridge;

/// <summary>
/// Phase 15 reads and session actions: Revit's Performance Adviser checks, the user picking elements or points while
/// Claude waits, watching what changes in a model, and temporary preview graphics (lines and boxes drawn in the views,
/// never part of the model). None of these changes the model.
/// </summary>
internal static class SessionRead
{
    // ---------------------------------------------------------------- Performance Adviser

    public static object PerformanceAdviser(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var pa = Autodesk.Revit.DB.PerformanceAdviser.GetPerformanceAdviser();
        int n = pa.GetNumberOfRules();
        string only = Str(p, "rule_contains");
        bool run = Flag(p, "run", true);
        int limit = Int(p, "limit", 50, 1, 2000);
        double budget = Dbl(p, "max_seconds") ?? 60;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<Obj>();
        bool stopped = false;
        for (int i = 0; i < n; i++)
        {
            string name = pa.GetRuleName(i);
            if (only != null && name.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var row = new Obj { ["rule"] = name, ["description"] = pa.GetRuleDescription(i), ["enabled"] = pa.IsRuleEnabled(i) };
            if (run && pa.IsRuleEnabled(i))
            {
                if (sw.Elapsed.TotalSeconds > budget) { row["skipped"] = "time budget used"; stopped = true; }
                else
                {
                    var msgs = pa.ExecuteRules(doc, new List<int> { i });
                    var ids = msgs.SelectMany(m => m.GetFailingElements()).Select(x => x.Value).Distinct().ToList();
                    row["findings"] = msgs.Count;
                    row["messages"] = msgs.Select(m => m.GetDescriptionText()).Distinct().Take(10).ToList();
                    row["elements"] = ids.Take(limit).ToList();
                    row["elementCount"] = ids.Count;
                }
            }
            rows.Add(row);
        }
        return new
        {
            model = doc.Title, rules = rows.Count, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), stoppedAtBudget = stopped,
            withFindings = rows.Where(r => r.TryGetValue("findings", out var f) && (int)f > 0).Select(r => r["rule"]).ToList(),
            items = rows,
            note = "Revit's own Performance Adviser rules (Manage > Performance Adviser); findings as Revit reports them.",
        };
    }

    // ---------------------------------------------------------------- picking

    private sealed class CategoryFilter : ISelectionFilter
    {
        private readonly HashSet<long> _cats;
        public CategoryFilter(HashSet<long> cats) { _cats = cats; }
        public bool AllowElement(Element e) => _cats == null || (e.Category != null && _cats.Contains(e.Category.Id.Value));
        public bool AllowReference(Reference r, XYZ p) => true;
    }

    /// <summary>
    /// Ask the user to pick in Revit while Claude waits: one element, several elements (finish with Finish / Enter) or a
    /// point. Esc cancels. Only the active model; the model is not changed.
    /// </summary>
    public static object Pick(Rc rc, Obj p)
    {
        var ui = rc.UiDoc;
        string kind = Choice(p, "kind", "element", "element", "elements", "point");
        string prompt = Str(p, "prompt") ?? (kind == "point" ? "Claude: pick a point" : kind == "elements" ? "Claude: pick elements, then Finish" : "Claude: pick an element");
        HashSet<long> cats = null;
        if (Has(p, "category"))
            cats = new HashSet<long>(Find.Cats(ui.Document, p, "category").Select(k => (long)(int)k));
        var filter = new CategoryFilter(cats);
        try
        {
            if (kind == "point")
            {
                if (ui.ActiveView.SketchPlane == null)
                    throw new InvalidOperationException($"The active view '{ui.ActiveView.Name}' has no work plane, so Revit cannot pick a point there. Open a plan view (or set a work plane) and ask again.");
                var pt = ui.Selection.PickPoint(prompt);
                return new { picked = true, kind, point = U.Xyz(pt), view = ui.ActiveView.Name };
            }
            if (kind == "elements")
            {
                var refs = ui.Selection.PickObjects(ObjectType.Element, filter, prompt);
                var els = refs.Select(r => ui.Document.GetElement(r)).Where(e => e != null).ToList();
                return new { picked = true, kind, count = els.Count, items = els.Select(e => Describe(e)).ToList() };
            }
            var one = ui.Selection.PickObject(ObjectType.Element, filter, prompt);
            var el = ui.Document.GetElement(one);
            return new { picked = true, kind, item = Describe(el), point = one.GlobalPoint != null ? U.Xyz(one.GlobalPoint) : null };
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return new { picked = false, kind, cancelled = true, note = "The user pressed Esc (or picking was cancelled)." };
        }
    }

    private static object Describe(Element e) => new
    {
        id = e.Id.Value, category = e.Category?.Name, name = e.Name,
        family = (e as FamilyInstance)?.Symbol?.FamilyName, level = (e.Document.GetElement(e.LevelId) as Level)?.Name,
    };

    // ---------------------------------------------------------------- change watching (dynamic model updater)

    private sealed class Watcher : IUpdater
    {
        private readonly UpdaterId _id;
        public readonly string ModelKey;
        public readonly DateTime Since = DateTime.UtcNow;
        public readonly object Gate = new object();
        public readonly Dictionary<long, string> Changes = new Dictionary<long, string>();   // id -> added | modified | deleted
        public int Events;
        public bool Overflow;

        public Watcher(AddInId addin, string modelKey)
        {
            _id = new UpdaterId(addin, Guid.NewGuid());
            ModelKey = modelKey;
        }

        public void Execute(UpdaterData data)
        {
            lock (Gate)
            {
                Events++;
                void Note(IEnumerable<ElementId> ids, string what)
                {
                    foreach (var id in ids)
                    {
                        if (Changes.Count >= 20000) { Overflow = true; return; }
                        // an element added and then changed stays 'added'; anything deleted is 'deleted'
                        if (what == "modified" && Changes.TryGetValue(id.Value, out var was) && was == "added") continue;
                        Changes[id.Value] = what;
                    }
                }
                Note(data.GetAddedElementIds(), "added");
                Note(data.GetModifiedElementIds(), "modified");
                Note(data.GetDeletedElementIds(), "deleted");
            }
        }

        public UpdaterId GetUpdaterId() => _id;
        public ChangePriority GetChangePriority() => ChangePriority.Annotations;
        public string GetUpdaterName() => "Revit MCP change watch";
        public string GetAdditionalInformation() => "Lists what changes in the model while Claude watches; changes nothing.";
    }

    private static readonly Dictionary<string, (Watcher w, Document doc)> Watches = new Dictionary<string, (Watcher, Document)>();

    public static object WatchStart(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        string key = DocumentTarget.ModelKey(doc);
        if (Watches.ContainsKey(key)) throw new InvalidOperationException($"'{doc.Title}' is already watched. Read watch_changes, or watch_stop first.");
        var w = new Watcher(rc.App.ActiveAddInId, key);
        UpdaterRegistry.RegisterUpdater(w, doc, true);
        ElementFilter filter;
        var names = new List<string>();
        if (Has(p, "category"))
        {
            var cats = Find.Cats(doc, p, "category");
            filter = new ElementMulticategoryFilter(cats);
            names = cats.Select(k => k.ToString()).ToList();
        }
        else filter = new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true));
        UpdaterRegistry.AddTrigger(w.GetUpdaterId(), doc, filter, Element.GetChangeTypeAny());
        UpdaterRegistry.AddTrigger(w.GetUpdaterId(), doc, filter, Element.GetChangeTypeElementAddition());
        UpdaterRegistry.AddTrigger(w.GetUpdaterId(), doc, filter, Element.GetChangeTypeElementDeletion());
        Watches[key] = (w, doc);
        return new { watching = doc.Title, categories = names.Count > 0 ? names : null, since = w.Since,
                     note = "Changes by anyone (the user, Claude, other add-ins) in this model are listed by watch_changes until watch_stop or Revit closes." };
    }

    private static (Watcher w, Document doc) WatchOf(Rc rc)
    {
        string key = DocumentTarget.ModelKey(rc.Doc);
        if (!Watches.TryGetValue(key, out var entry)) throw new InvalidOperationException($"'{rc.Doc.Title}' is not watched. Call watch_start first.");
        return entry;
    }

    public static object WatchChanges(Rc rc, Obj p)
    {
        var (w, doc) = WatchOf(rc);
        bool clear = Flag(p, "clear", true);
        int limit = Int(p, "limit", 500, 1, 20000);
        List<KeyValuePair<long, string>> items;
        int events; bool overflow;
        lock (w.Gate)
        {
            items = w.Changes.ToList();
            events = w.Events; overflow = w.Overflow;
            if (clear) { w.Changes.Clear(); w.Overflow = false; }
        }
        var rows = items.Take(limit).Select(kv =>
        {
            var e = kv.Value == "deleted" ? null : doc.GetElement(new ElementId(kv.Key));
            return new { id = kv.Key, change = kv.Value, category = e?.Category?.Name, name = e?.Name };
        }).ToList();
        return new
        {
            model = doc.Title, since = w.Since, events, count = items.Count, truncated = items.Count > limit, overflow, cleared = clear,
            added = items.Count(x => x.Value == "added"), modified = items.Count(x => x.Value == "modified"), deleted = items.Count(x => x.Value == "deleted"),
            byCategory = rows.Where(r => r.category != null).GroupBy(r => r.category).Select(g => new { category = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList(),
            items = rows,
        };
    }

    public static object WatchStop(Rc rc, Obj p)
    {
        var (w, doc) = WatchOf(rc);
        try { UpdaterRegistry.RemoveAllTriggers(w.GetUpdaterId()); } catch { /* document closing */ }
        try { UpdaterRegistry.UnregisterUpdater(w.GetUpdaterId(), doc); } catch { /* already gone */ }
        Watches.Remove(w.ModelKey);
        int left;
        lock (w.Gate) left = w.Changes.Count;
        return new { stopped = doc.Title, unreadChanges = left };
    }

    // ---------------------------------------------------------------- preview graphics (DirectContext3D)

    private sealed class Preview : IDirectContext3DServer
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly string ModelKey;
        public readonly List<(XYZ a, XYZ b)> Lines;
        public readonly ColorWithTransparency Colour;
        private VertexBuffer _vb; private IndexBuffer _ib; private int _vertices, _lines;

        public Preview(string modelKey, List<(XYZ, XYZ)> lines, Color colour)
        {
            ModelKey = modelKey; Lines = lines;
            Colour = new ColorWithTransparency(colour.Red, colour.Green, colour.Blue, 0);
        }

        public Guid GetServerId() => Id;
        public string GetVendorId() => "RevitMcp";
        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName() => "Revit MCP preview";
        public string GetDescription() => "Temporary lines Claude shows before an edit; not part of the model.";
        public string GetApplicationId() => "";
        public string GetSourceId() => "";
        public bool UsesHandles() => false;
        public bool CanExecute(View v) => (v is View3D || v is ViewPlan || v is ViewSection) && DocumentTarget.ModelKey(v.Document) == ModelKey;
        public bool UseInTransparentPass(View v) => false;

        public Outline GetBoundingBox(View v)
        {
            var pts = Lines.SelectMany(l => new[] { l.a, l.b }).ToList();
            return new Outline(new XYZ(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z)), new XYZ(pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z)));
        }

        public void RenderScene(View v, DisplayStyle style)
        {
            try
            {
                if (_vb == null || !_vb.IsValid())
                {
                    _vertices = Lines.Count * 2; _lines = Lines.Count;
                    int vsize = _vertices * VertexPositionColored.GetSizeInFloats();
                    _vb = new VertexBuffer(vsize);
                    _vb.Map(vsize);
                    var vs = _vb.GetVertexStreamPositionColored();
                    foreach (var (a, b) in Lines) { vs.AddVertex(new VertexPositionColored(a, Colour)); vs.AddVertex(new VertexPositionColored(b, Colour)); }
                    _vb.Unmap();
                    int isize = _lines * IndexLine.GetSizeInShortInts();
                    _ib = new IndexBuffer(isize);
                    _ib.Map(isize);
                    var istream = _ib.GetIndexStreamLine();
                    for (int i = 0; i < _lines; i++) istream.AddLine(new IndexLine(2 * i, 2 * i + 1));
                    _ib.Unmap();
                }
                DrawContext.FlushBuffer(_vb, _vertices, _ib, _lines * IndexLine.GetSizeInShortInts(),
                    new VertexFormat(VertexFormatBits.PositionColored), new EffectInstance(VertexFormatBits.PositionColored), PrimitiveType.LineList, 0, _lines);
            }
            catch { /* drawing must never disturb Revit */ }
        }
    }

    private static readonly List<Preview> Previews = new List<Preview>();

    private static MultiServerService Service() =>
        ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService
        ?? throw new InvalidOperationException("Revit's DirectContext3D service is not available.");

    /// <summary>Lines (and boxes as their 12 edges) drawn in the views of the active model until preview_clear.</summary>
    public static object PreviewShow(Rc rc, Obj p)
    {
        var ui = rc.UiDoc;
        var lines = new List<(XYZ, XYZ)>();
        if (Has(p, "lines"))
            foreach (var (item, i) in Arr(p, "lines", 5000).Select((v, i) => (v, i)))
            {
                if (!(item is List<object> seg) || seg.Count != 2) throw new ArgumentException($"lines item {i + 1} must be [[x, y, z], [x, y, z]].");
                lines.Add((U.Pt(seg[0], $"line {i + 1} start"), U.Pt(seg[1], $"line {i + 1} end")));
            }
        if (Has(p, "boxes"))
            foreach (var (item, i) in Arr(p, "boxes", 1000).Select((v, i) => (v, i)))
            {
                if (!(item is List<object> bx) || bx.Count != 2) throw new ArgumentException($"boxes item {i + 1} must be [[min x, y, z], [max x, y, z]].");
                XYZ a = U.Pt(bx[0], $"box {i + 1} min"), b = U.Pt(bx[1], $"box {i + 1} max");
                var c = new[] { new XYZ(a.X, a.Y, a.Z), new XYZ(b.X, a.Y, a.Z), new XYZ(b.X, b.Y, a.Z), new XYZ(a.X, b.Y, a.Z) };
                var t = c.Select(q => new XYZ(q.X, q.Y, b.Z)).ToArray();
                for (int k = 0; k < 4; k++) { lines.Add((c[k], c[(k + 1) % 4])); lines.Add((t[k], t[(k + 1) % 4])); lines.Add((c[k], t[k])); }
            }
        if (lines.Count == 0) throw new ArgumentException("Give 'lines' and / or 'boxes' to show.");
        string hex = (Str(p, "color") ?? "#FF6600").Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            throw new ArgumentException("'color' must be like #FF6600.");
        var preview = new Preview(DocumentTarget.ModelKey(ui.Document), lines, new Color((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF)));
        var service = Service();
        service.AddServer(preview);
        var active = service.GetActiveServerIds().ToList();
        active.Add(preview.Id);
        service.SetActiveServers(active);
        Previews.Add(preview);
        try { ui.RefreshActiveView(); } catch { /* cosmetic */ }
        return new { shown = lines.Count, preview = preview.Id.ToString("N"), activePreviews = Previews.Count,
                     note = "Drawn in plan, section and 3D views of this model only until preview_clear; nothing is added to the model." };
    }

    public static object PreviewClear(Rc rc, Obj p)
    {
        var service = Service();
        string only = Str(p, "preview");
        var gone = Previews.Where(x => only == null || x.Id.ToString("N") == only).ToList();
        if (only != null && gone.Count == 0) throw new ArgumentException($"No preview '{only}'.");
        var active = service.GetActiveServerIds().Where(id => !gone.Any(g => g.Id == id)).ToList();
        service.SetActiveServers(active);
        foreach (var g in gone) { try { service.RemoveServer(g.Id); } catch { /* already removed */ } Previews.Remove(g); }
        try { rc.App.ActiveUIDocument?.RefreshActiveView(); } catch { /* cosmetic */ }
        return new { cleared = gone.Count, activePreviews = Previews.Count };
    }
}
