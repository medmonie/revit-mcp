namespace RevitMcpBridge;

/// <summary>What the drawings say: dimension values and overrides, spot dimensions, tag texts, text notes, with QA flags.</summary>
internal static class AnnotationRead
{
    private static readonly string[] Kinds = { "dimensions", "spots", "tags", "text" };

    /// <summary>The views to look in: one view, every view placed on a sheet, or the whole model.</summary>
    private static List<View> Views(Document doc, Obj p, out string scope)
    {
        if (Has(p, "view")) { var v = Find.ViewOf(doc, NeedRef(p, "view")); scope = "view " + v.Name; return new List<View> { v }; }
        if (Has(p, "sheet"))
        {
            string given = NeedRef(p, "sheet");
            var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => s.SheetNumber == given || s.Name == given || s.Id.Value.ToString(CultureInfo.InvariantCulture) == given)
                ?? throw new ArgumentException($"No sheet '{given}' (number, name or id). Use list_sheets.");
            scope = $"sheet {sheet.SheetNumber} - {sheet.Name}";
            var list = new List<View> { sheet };
            list.AddRange(sheet.GetAllPlacedViews().Select(id => doc.GetElement(id)).OfType<View>());
            return list;
        }
        scope = "whole model";
        return null;
    }

    private static double? DimValue(Dimension d, double? v)
    {
        if (v == null) return null;
        try
        {
            if (d.DimensionShape == DimensionShape.Angular) return U.Deg(v.Value);
        }
        catch { /* spot dimensions have no shape */ }
        return U.Out(v.Value);
    }

    private static Obj Segment(Dimension d, string valueString, double? value, string over, string prefix, string suffix, string above, string below)
    {
        var o = new Obj { ["text"] = valueString };
        if (DimValue(d, value) is double x) o["value"] = x;
        if (!string.IsNullOrEmpty(over)) o["override"] = over;
        if (!string.IsNullOrEmpty(prefix)) o["prefix"] = prefix;
        if (!string.IsNullOrEmpty(suffix)) o["suffix"] = suffix;
        if (!string.IsNullOrEmpty(above)) o["above"] = above;
        if (!string.IsNullOrEmpty(below)) o["below"] = below;
        return o;
    }

    public static object ListAnnotations(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var want = Has(p, "kind")
            ? new HashSet<string>(StrList(p, "kind", 4).Select(k => k.Trim().ToLowerInvariant()))
            : new HashSet<string>(Kinds);
        foreach (var k in want)
            if (!Kinds.Contains(k)) throw new ArgumentException($"kind '{k}': use dimensions, spots, tags or text.");
        bool onlyIssues = Flag(p, "only_issues", false);
        int limit = Int(p, "limit", 500, 1, 20000);
        var views = Views(doc, p, out string scope);

        IEnumerable<Element> Of(Type t)
        {
            if (views == null) return new FilteredElementCollector(doc).OfClass(t).WhereElementIsNotElementType().ToElements();
            var ids = new HashSet<ElementId>(views.Select(v => v.Id));
            return new FilteredElementCollector(doc).OfClass(t).WhereElementIsNotElementType().ToElements()
                .Where(e => ids.Contains(e.OwnerViewId));
        }
        string ViewName(Element e) => doc.GetElement(e.OwnerViewId)?.Name;

        var items = new List<Obj>();
        var issues = new SortedDictionary<string, int>();
        var counts = new SortedDictionary<string, int>();
        int listed = 0;
        void Add(string kind, Obj o, List<string> flags)
        {
            counts.TryGetValue(kind, out int c); counts[kind] = c + 1;
            foreach (var f in flags) { issues.TryGetValue(f, out int n); issues[f] = n + 1; }
            if (onlyIssues && flags.Count == 0) return;
            if (flags.Count > 0) o["issues"] = flags;
            if (listed++ < limit) items.Add(o);
        }

        if (want.Contains("dimensions") || want.Contains("spots"))
            foreach (Dimension d in Of(typeof(Dimension)))
            {
                bool spot = d is SpotDimension;
                if (spot ? !want.Contains("spots") : !want.Contains("dimensions")) continue;
                var flags = new List<string>();
                var o = new Obj { ["id"] = d.Id.Value, ["kind"] = spot ? "spot" : "dimension", ["view"] = ViewName(d), ["type"] = d.DimensionType?.Name };
                try { if (!spot) o["shape"] = d.DimensionShape.ToString(); } catch { /* optional */ }
                try
                {
                    if (d.NumberOfSegments > 1)
                    {
                        var segs = new List<Obj>();
                        foreach (DimensionSegment s in d.Segments)
                        {
                            segs.Add(Segment(d, s.ValueString, s.Value, s.ValueOverride, s.Prefix, s.Suffix, s.Above, s.Below));
                            if (!string.IsNullOrEmpty(s.ValueOverride)) flags.Add("text_override");
                            if (s.Value is double sv && Math.Abs(sv) < 1e-6) flags.Add("zero_value");
                        }
                        o["segments"] = segs;
                        o["segmentCount"] = segs.Count;
                    }
                    else
                    {
                        foreach (var kv in Segment(d, d.ValueString, d.Value, d.ValueOverride, d.Prefix, d.Suffix, d.Above, d.Below)) o[kv.Key] = kv.Value;
                        if (!string.IsNullOrEmpty(d.ValueOverride)) flags.Add("text_override");
                        if (d.Value is double dv && Math.Abs(dv) < 1e-6 && !spot) flags.Add("zero_value");
                    }
                }
                catch (Exception ex) { o["error"] = ex.Message; }
                try
                {
                    var refs = new List<long>();
                    foreach (Reference r in d.References) if (r.ElementId != ElementId.InvalidElementId) refs.Add(r.ElementId.Value);
                    o["references"] = refs.Distinct().ToList();
                    if (refs.Count == 0 && !spot) flags.Add("no_references");
                }
                catch { /* optional */ }
                Add(spot ? "spots" : "dimensions", o, flags.Distinct().ToList());
            }

        if (want.Contains("tags"))
            foreach (IndependentTag t in Of(typeof(IndependentTag)))
            {
                var flags = new List<string>();
                var o = new Obj { ["id"] = t.Id.Value, ["kind"] = "tag", ["view"] = ViewName(t), ["category"] = t.Category?.Name };
                try { o["text"] = t.TagText; if (string.IsNullOrWhiteSpace(t.TagText)) flags.Add("empty_text"); } catch { /* text from linked or unsupported */ }
                try
                {
                    var tagged = t.GetTaggedLocalElementIds().Where(id => id != ElementId.InvalidElementId).Select(id => id.Value).ToList();
                    o["tagged"] = tagged;
                    var first = tagged.Count > 0 ? doc.GetElement(new ElementId(tagged[0])) : null;
                    if (first != null) o["taggedCategory"] = first.Category?.Name;
                }
                catch { /* optional */ }
                try { if (t.IsOrphaned) flags.Add("orphaned"); } catch { /* optional */ }
                Add("tags", o, flags);
            }

        if (want.Contains("text"))
            foreach (TextNote n in Of(typeof(TextNote)))
            {
                var flags = new List<string>();
                string text = (n.Text ?? "").Trim();
                if (text.Length == 0) flags.Add("empty_text");
                Add("text", new Obj { ["id"] = n.Id.Value, ["kind"] = "text", ["view"] = ViewName(n), ["type"] = n.TextNoteType?.Name, ["text"] = text }, flags);
            }

        return new
        {
            units = U.Name, scope, counts, issues, listed = items.Count, truncated = listed > limit, items,
            note = "text = what the drawing shows (as Revit formats it); value = the measured value (lengths in units, angles in degrees). "
                   + "Issues: text_override (the shown text replaces the measured value), zero_value, no_references (dimension lost its references), "
                   + "orphaned (tag lost its element), empty_text. Room, area and space tags are not included.",
        };
    }
}
