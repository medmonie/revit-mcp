namespace RevitMcpBridge;

/// <summary>Revit links, linked / imported CAD files and the geometry inside them.</summary>
internal static class LinkRead
{
    public static object ListLinks(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var revit = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Select(i =>
        {
            var o = new Obj { ["id"] = i.Id.Value, ["name"] = i.Name, ["pinned"] = i.Pinned };
            if (doc.GetElement(i.GetTypeId()) is RevitLinkType t)
            {
                o["typeId"] = t.Id.Value;
                o["file"] = t.Name;
                try { o["status"] = t.GetLinkedFileStatus().ToString(); } catch { /* optional */ }
                try { o["attachment"] = t.AttachmentType.ToString(); } catch { /* optional */ }
                try { if (t.IsNestedLink) o["nested"] = true; } catch { /* optional */ }
                try { if (t.GetConversionData() != null) o["fromIfc"] = true; } catch { /* not a converted (IFC) link */ }
                try
                {
                    var r = t.GetExternalFileReference();
                    if (r != null) o["path"] = ModelPathUtils.ConvertModelPathToUserVisiblePath(r.GetAbsolutePath());
                }
                catch { /* cloud or unresolved path */ }
            }
            var linked = i.GetLinkDocument();
            o["loaded"] = linked != null;
            if (linked != null)
            {
                o["document"] = linked.Title;
                o["workshared"] = linked.IsWorkshared;
            }
            try { o["transform"] = LinkScope.TransformOut(i.GetTotalTransform()); } catch { /* optional */ }
            return o;
        }).ToList();

        var cad = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().Select(i =>
        {
            var o = new Obj
            {
                ["id"] = i.Id.Value, ["file"] = Find.Builtin(i, "IMPORT_SYMBOL_NAME")?.AsString() ?? i.Category?.Name,
                ["linked"] = i.IsLinked, ["pinned"] = i.Pinned, ["viewSpecific"] = i.ViewSpecific,
            };
            if (i.ViewSpecific) o["view"] = doc.GetElement(i.OwnerViewId)?.Name;
            return o;
        }).ToList();

        return new
        {
            units = U.Name, revitLinks = revit, cadFiles = cad,
            note = revit.Count == 0 ? null : "Read inside a loaded link with link=<id or file> on get_elements, get_element, count_by_category, "
                + "list_types, list_levels and list_grids. transform: the link's origin and plan rotation in this model.",
        };
    }

    private sealed class CadWalk
    {
        public Document Doc;
        public string Layer;            // only this layer, or null
        public int Limit;
        public bool Truncated;
        public int Other;
        public readonly List<object> Items = new List<object>();
        public readonly SortedDictionary<string, int> Layers = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private string LayerOf(GeometryObject g)
        {
            var style = g.GraphicsStyleId != null && g.GraphicsStyleId != ElementId.InvalidElementId
                ? Doc.GetElement(g.GraphicsStyleId) as GraphicsStyle : null;
            return style?.GraphicsStyleCategory?.Name ?? "";
        }

        private bool Take(string layer)
        {
            Layers.TryGetValue(layer, out int n);
            Layers[layer] = n + 1;
            if (Layer != null && !string.Equals(layer, Layer, StringComparison.OrdinalIgnoreCase)) return false;
            if (Items.Count >= Limit) { Truncated = true; return false; }
            return true;
        }

        public void Walk(GeometryElement ge, Transform tf)
        {
            if (ge == null) return;
            foreach (GeometryObject g in ge)
            {
                if (g is GeometryInstance gi) { Walk(gi.GetSymbolGeometry(), tf.Multiply(gi.Transform)); continue; }
                string layer = LayerOf(g);
                if (g is Line line && line.IsBound)
                {
                    if (Take(layer))
                        Items.Add(new { layer, kind = "line", start = U.Xyz(tf.OfPoint(line.GetEndPoint(0))), end = U.Xyz(tf.OfPoint(line.GetEndPoint(1))) });
                }
                else if (g is Arc arc)
                {
                    if (!Take(layer)) continue;
                    double radius = U.Out(arc.Radius * tf.Scale);
                    if (arc.IsBound)
                        Items.Add(new { layer, kind = "arc", center = U.Xyz(tf.OfPoint(arc.Center)), radius,
                                        start = U.Xyz(tf.OfPoint(arc.GetEndPoint(0))), end = U.Xyz(tf.OfPoint(arc.GetEndPoint(1))) });
                    else
                        Items.Add(new { layer, kind = "circle", center = U.Xyz(tf.OfPoint(arc.Center)), radius });
                }
                else if (g is PolyLine pl)
                {
                    if (Take(layer))
                        Items.Add(new { layer, kind = "polyline", points = pl.GetCoordinates().Select(c => U.Xyz(tf.OfPoint(c))).ToList() });
                }
                else Other++;
            }
        }
    }

    public static object GetCadGeometry(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var imp = Find.Elem(doc, NeedId(p, "id")) as ImportInstance
                  ?? throw new ArgumentException("That id is not a linked or imported CAD file. Use list_links.");
        bool layersOnly = Flag(p, "layers_only", false);
        var walk = new CadWalk { Doc = doc, Layer = Str(p, "layer"), Limit = layersOnly ? 0 : Int(p, "limit", 2000, 1, 20000) };
        walk.Walk(imp.get_Geometry(new Options()), Transform.Identity);
        return new
        {
            units = U.Name,
            note = "Coordinates are in the Revit model (internal origin). Text and dimensions inside a CAD file are not exposed by Revit.",
            layers = walk.Layers.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            otherGeometry = walk.Other,
            count = walk.Items.Count,
            truncated = walk.Truncated && !layersOnly,
            entities = walk.Items,
        };
    }

    /// <summary>Column outlines on a CAD layer, matched to column types, without changing the model (the plan of columns_from_cad).</summary>
    public static object CadColumns(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var level = Has(p, "level") ? Find.NeedLevel(doc, p) : null;
        // Without a level: the drawing's own elevation (a plan linked at its level sits at that height).
        double baseZ = (level?.ProjectElevation ?? Cad.NeedImport(doc, p).GetTotalTransform().Origin.Z) + U.Len(p, "base_offset", 0);
        var plan = CadOps.PlanColumns(doc, p, baseZ);
        return CadOps.Summary(plan, Int(p, "limit", 50, 0, 2000));
    }
}
