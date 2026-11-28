namespace RevitMcpBridge;

/// <summary>Roadmap phase 8 reads: model / detail groups, assemblies, raw element geometry.</summary>
internal static class DepthRead
{
    public static object ListGroups(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        string kind = Choice(p, "kind", "all", "all", "model", "detail");
        var instances = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().ToLookup(g => g.GroupType.Id.Value);
        var types = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>()
            .Select(t =>
            {
                bool detail = t.Category?.Id.Value == (long)BuiltInCategory.OST_IOSDetailGroups;
                var mine = instances[t.Id.Value].ToList();
                return new { t, detail, mine };
            })
            .Where(x => kind == "all" || (kind == "detail") == x.detail)
            .OrderBy(x => x.t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Obj
            {
                ["id"] = x.t.Id.Value, ["name"] = x.t.Name, ["kind"] = x.detail ? "detail" : "model",
                ["instances"] = x.mine.Count,
                ["members"] = x.mine.Count > 0 ? x.mine[0].GetMemberIds().Count : (object)null,
                ["memberCategories"] = x.mine.Count == 0 ? null : x.mine[0].GetMemberIds().Select(i => doc.GetElement(i)?.Category?.Name)
                    .Where(n => n != null).GroupBy(n => n).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["instanceIds"] = x.mine.Take(20).Select(g => g.Id.Value).ToList(),
            });
        return Describe.Page(p, types, "groupTypes", 200, 5000);
    }

    public static object ListAssemblies(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.AssociatedAssemblyInstanceId != null && v.AssociatedAssemblyInstanceId != ElementId.InvalidElementId)
            .ToLookup(v => v.AssociatedAssemblyInstanceId.Value);
        var rows = new FilteredElementCollector(doc).OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>()
            .OrderBy(a => a.AssemblyTypeName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new Obj
            {
                ["id"] = a.Id.Value, ["name"] = a.AssemblyTypeName, ["typeId"] = a.GetTypeId().Value,
                ["namingCategory"] = Category.GetCategory(doc, a.NamingCategoryId)?.Name,
                ["members"] = a.GetMemberIds().Count,
                ["memberCategories"] = a.GetMemberIds().Select(i => doc.GetElement(i)?.Category?.Name).Where(n => n != null)
                    .GroupBy(n => n).ToDictionary(g => g.Key, g => (object)g.Count()),
                ["center"] = U.Xyz(a.GetCenter()),
                ["views"] = views[a.Id.Value].Select(v => new { id = v.Id.Value, name = v.Name, type = v.ViewType.ToString() }).ToList(),
            });
        return Describe.Page(p, rows, "assemblies", 200, 5000);
    }

    // ---------------------------------------------------------------- raw geometry

    private sealed class Walk
    {
        public double VolumeFeet3;
        public bool Faces, Edges;
        public int FaceLimit;
        public int FacesListed;
        public bool Truncated;
        public readonly List<object> Solids = new List<object>();
        public readonly List<object> Curves = new List<object>();
        public int Meshes;
        private int _visited;

        private static double[] V(XYZ v) => new[] { Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6) };

        private object Face(Face f, Transform tf)
        {
            var o = new Obj { ["kind"] = f.GetType().Name.Replace("Face", "").ToLowerInvariant(), ["area_m2"] = U.Area(f.Area) };
            if (f is PlanarFace pf) { o["normal"] = V(tf.OfVector(pf.FaceNormal)); o["origin"] = U.Xyz(tf.OfPoint(pf.Origin)); }
            else if (f is CylindricalFace cf)
            {
                o["axis"] = V(tf.OfVector(cf.Axis)); o["origin"] = U.Xyz(tf.OfPoint(cf.Origin));
                o["radius"] = U.Out(cf.get_Radius(0).GetLength());
            }
            try { if (f.MaterialElementId != null && f.MaterialElementId != ElementId.InvalidElementId) o["materialId"] = f.MaterialElementId.Value; } catch { /* optional */ }
            return o;
        }

        public void Run(GeometryElement ge, Transform tf, int depth = 0)
        {
            if (ge == null) return;
            if (depth > 64) throw new InvalidOperationException("Geometry nesting exceeds 64 levels.");
            foreach (GeometryObject g in ge)
            {
                if (++_visited > 100000) throw new InvalidOperationException("Element geometry exceeds the 100,000-object traversal limit.");
                switch (g)
                {
                    case GeometryInstance gi:
                        Run(gi.GetInstanceGeometry(), tf, depth + 1);
                        break;
                    case Solid s when s.Volume > 1e-9 || s.Faces.Size > 0:
                    {
                        var o = new Obj { ["volume_m3"] = U.Volume(s.Volume), ["surfaceArea_m2"] = U.Area(s.SurfaceArea), ["faces"] = s.Faces.Size, ["edges"] = s.Edges.Size };
                        VolumeFeet3 += s.Volume;
                        try { if (s.Volume > 1e-9) o["centroid"] = U.Xyz(tf.OfPoint(s.ComputeCentroid())); } catch { /* not closed */ }
                        var bb = s.GetBoundingBox();
                        if (bb != null)
                            o["box"] = Describe.Box(bb, tf);
                        if (Edges) o["edgeLength"] = U.Out(s.Edges.Cast<Edge>().Sum(e => e.ApproximateLength));
                        if (Faces)
                        {
                            var list = new List<object>();
                            foreach (Face f in s.Faces)
                            {
                                if (FacesListed >= FaceLimit) { Truncated = true; break; }
                                list.Add(Face(f, tf)); FacesListed++;
                            }
                            o["faceList"] = list;
                        }
                        Solids.Add(o);
                        break;
                    }
                    case Curve c when c.IsBound:
                        if (Curves.Count < 500) Curves.Add(U.CurveOut(c.CreateTransformed(tf)));
                        else Truncated = true;
                        break;
                    case Mesh _:
                        Meshes++;
                        break;
                }
            }
        }
    }

    public static object GetGeometry(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        var id = NeedId(p, "id");
        var e = doc.GetElement(id) ?? throw new ArgumentException($"No element with id {id.Value} in {(link == null ? "this model" : "link '" + doc.Title + "'")}.");
        string detail = Choice(p, "detail", "fine", "coarse", "medium", "fine");
        var opts = new Options
        {
            DetailLevel = detail == "coarse" ? ViewDetailLevel.Coarse : detail == "medium" ? ViewDetailLevel.Medium : ViewDetailLevel.Fine,
            IncludeNonVisibleObjects = Flag(p, "include_hidden", false),
        };
        bool original = Flag(p, "original", false);
        GeometryElement ge;
        if (original)
        {
            if (!(e is FamilyInstance fi)) throw new ArgumentException("'original' (geometry before joins and cuts) applies to family instances only.");
            ge = fi.GetOriginalGeometry(opts)?.GetTransformed(fi.GetTransform());
        }
        else ge = e.get_Geometry(opts);
        var walk = new Walk { Faces = Flag(p, "faces", true), Edges = Flag(p, "edges", false),
                              FaceLimit = Int(p, "limit", 200, 1, 5000) };
        walk.Run(ge, link?.Tf ?? Transform.Identity);
        var o = new Obj
        {
            ["units"] = U.Name, ["id"] = e.Id.Value, ["category"] = e.Category?.Name, ["detail"] = detail,
            ["geometry"] = original ? "original (before joins and cuts)" : "as modelled (after joins and cuts)",
            ["solids"] = walk.Solids, ["curves"] = walk.Curves, ["meshes"] = walk.Meshes, ["truncated"] = walk.Truncated,
            ["totalVolume_m3"] = U.Volume(walk.VolumeFeet3),
        };
        if (link != null) o["link"] = link.Info();
        return o;
    }
}
