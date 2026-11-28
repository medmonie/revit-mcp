using System.Diagnostics;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 13 (reads): MEP systems, MEP quantities by size, and where MEP runs cross structure (for sleeves and
/// openings). Works in the host or inside loaded links (MEP and structure are usually separate linked models). Reads only.
/// </summary>
internal static class MepRead
{
    public static readonly BuiltInCategory[] Runs =
    {
        BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_FlexPipeCurves,
    };

    private static readonly BuiltInCategory[] Counted =
    {
        BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting,
        BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingFixtures,
    };

    private static string P(Element e, string bip) { var p = Find.Builtin(e, bip); return p == null || !p.HasValue ? null : p.AsString() ?? p.AsValueString(); }

    /// <summary>The size Revit shows ("300x200", "100 mmø"), and the largest cross dimension in feet.</summary>
    public static (string text, double feet) Size(Element e)
    {
        string text = P(e, "RBS_CALCULATED_SIZE");
        double f = 0;
        if (e is MEPCurve m)
        {
            try { f = Math.Max(f, m.Diameter); } catch { /* not round */ }
            try { f = Math.Max(f, Math.Max(m.Width, m.Height)); } catch { /* not rectangular */ }
        }
        return (text, f);
    }

    // ---------------------------------------------------------------- systems

    public static object MepSystems(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        int limit = Int(p, "limit", 300, 1, 5000);
        string kind = Choice(p, "kind", "all", "all", "duct", "pipe", "electrical");
        var rows = new List<Obj>();
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(MEPSystem)).Cast<MEPSystem>())
        {
            string k = s is MechanicalSystem ? "duct" : s is PipingSystem ? "pipe" : s.GetType().Name.Contains("Electrical") ? "electrical" : "other";
            if (kind != "all" && k != kind) continue;
            int network = 0;
            try { network = s is MechanicalSystem ms ? ms.DuctNetwork.Size : s is PipingSystem ps ? ps.PipingNetwork.Size : 0; } catch { /* not computed */ }
            int members = 0;
            try { members = s.Elements.Size; } catch { /* empty */ }
            Element baseEq = null;
            try { baseEq = s.BaseEquipment; } catch { /* none */ }
            bool? connected = null;
            try { connected = s is MechanicalSystem ms2 ? ms2.IsWellConnected : s is PipingSystem ps2 ? ps2.IsWellConnected : (bool?)null; } catch { /* n/a */ }
            rows.Add(new Obj
            {
                ["id"] = s.Id.Value, ["name"] = s.Name, ["kind"] = k, ["systemType"] = doc.GetElement(s.GetTypeId())?.Name,
                ["classification"] = P(s, "RBS_SYSTEM_CLASSIFICATION_PARAM"), ["terminalsAndEquipment"] = members, ["networkElements"] = network,
                ["baseEquipment"] = baseEq != null ? Describe.Label(doc, baseEq) : null, ["wellConnected"] = connected,
            });
        }
        return new
        {
            model = doc.Title, count = rows.Count, truncated = rows.Count > limit,
            byKind = rows.GroupBy(r => (string)r["kind"]).Select(g => new { kind = g.Key, systems = g.Count() }).ToList(),
            notWellConnected = rows.Count(r => r["wellConnected"] is bool b && !b),
            systems = rows.OrderBy(r => (string)r["kind"]).ThenBy(r => (string)r["name"]).Take(limit).ToList(),
        };
    }

    // ---------------------------------------------------------------- quantities

    public static object MepQuantities(Rc rc, Obj p)
    {
        var doc = LinkScope.DocOf(rc.Doc, p, out var link);
        var cats = Has(p, "category") ? Find.Cats(doc, p, "category") : Runs.ToList();
        string by = Choice(p, "group_by", "size", "size", "type", "system", "level", "category");
        var groups = new Dictionary<string, (string cat, string key, int n, double len, double area)>();
        int runs = 0;
        foreach (var e in Find.Scope(doc, p, cats))
        {
            double len = Find.Builtin(e, "CURVE_ELEM_LENGTH") is Parameter lp && lp.HasValue ? lp.AsDouble() : (e.Location as LocationCurve)?.Curve.Length ?? 0;
            if (len <= 0) continue;
            runs++;
            string cat = e.Category?.Name ?? "?";
            string key = by == "size" ? Size(e).text ?? "?" : by == "type" ? Describe.TypeLabel(doc, e) : by == "system" ? P(e, "RBS_SYSTEM_NAME_PARAM") ?? "(no system)"
                       : by == "level" ? (e is MEPCurve mc ? mc.ReferenceLevel?.Name : Describe.LevelName(doc, e)) ?? "?" : cat;
            var k = cat + "|" + key;
            groups.TryGetValue(k, out var g);
            double area = Find.Builtin(e, "RBS_CURVE_SURFACE_AREA") is Parameter ap && ap.HasValue ? ap.AsDouble() : 0;
            groups[k] = (cat, key, g.n + 1, g.len + len, g.area + area);
        }
        var counted = new List<object>();
        if (Flag(p, "fittings", true))
            foreach (var cat in Counted)
            {
                var items = Find.Scope(doc, p, new List<BuiltInCategory> { cat }).ToList();
                if (items.Count == 0) continue;
                counted.Add(new
                {
                    category = items[0].Category?.Name, count = items.Count,
                    byType = items.GroupBy(i => Describe.TypeLabel(doc, i)).OrderByDescending(x => x.Count()).Take(30).Select(x => new { type = x.Key, count = x.Count() }).ToList(),
                });
            }
        return new
        {
            model = doc.Title, groupBy = by, runs,
            lengths = groups.Values.OrderBy(g => g.cat).ThenByDescending(g => g.len)
                .Select(g => new { category = g.cat, key = g.key, pieces = g.n, lengthM = U.M(g.len, 2), surfaceAreaM2 = g.area > 0 ? Math.Round(g.area * 0.09290304, 2) : (double?)null }).ToList(),
            totalsByCategory = groups.Values.GroupBy(g => g.cat).Select(x => new { category = x.Key, lengthM = U.M(x.Sum(g => g.len), 2), pieces = x.Sum(g => g.n) }).ToList(),
            counts = counted,
            note = "Lengths are Revit's centreline lengths (fittings not included); surfaceAreaM2 is Revit's duct surface area. Sizes as Revit shows them.",
        };
    }

    // ---------------------------------------------------------------- crossings

    private static Outline BoxOf(Element e, Transform tf)
    {
        var bb = e.get_BoundingBox(null);
        if (bb == null) return null;
        var pts = new List<XYZ>();
        foreach (double x in new[] { bb.Min.X, bb.Max.X })
            foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                    pts.Add(tf.OfPoint(bb.Transform.OfPoint(new XYZ(x, y, z))));
        return new Outline(new XYZ(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z)), new XYZ(pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z)));
    }

    private static (Document doc, Transform tf, string name) Side(Document host, Obj p, string param)
    {
        if (!Has(p, param)) return (host, Transform.Identity, host.Title);
        var inst = LinkScope.Resolve(host, NeedRef(p, param));
        var d = inst.GetLinkDocument() ?? throw new ArgumentException($"Link '{inst.Name}' is not loaded.");
        return (d, inst.GetTotalTransform(), d.Title);
    }

    /// <summary>
    /// Where MEP runs (ducts, pipes, trays, conduits) pass through structure (beams, walls, slabs, columns), each model in the
    /// host or a link: the crossing point, length inside, the run's size and the structural element. Feeds opening_create.
    /// </summary>
    public static object MepCrossings(Rc rc, Obj p)
    {
        var host = rc.Doc;
        var (mdoc, mtf, mname) = Side(host, p, "mep_link");
        var (sdoc, stf, sname) = Side(host, p, "structure_link");
        var mcats = Has(p, "mep_category") ? Find.Cats(mdoc, p, "mep_category") : Runs.Take(4).ToList();
        var scats = Has(p, "structure_category") ? Find.Cats(sdoc, p, "structure_category")
            : new List<BuiltInCategory> { BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFoundation };
        double minSize = U.Len(p, "min_size", 0);
        double seconds = Dbl(p, "max_seconds") ?? 40;
        int limit = Int(p, "limit", 300, 1, 5000);
        long after = Has(p, "resume_after") ? long.Parse(NeedRef(p, "resume_after"), CultureInfo.InvariantCulture) : long.MinValue;
        var sInv = stf.Inverse;

        var structure = new FilteredElementCollector(sdoc).WherePasses(new ElementMulticategoryFilter(scats)).WhereElementIsNotElementType()
            .Select(e => (e, box: BoxOf(e, stf))).Where(x => x.box != null).ToList();
        var runs = new FilteredElementCollector(mdoc).WherePasses(new ElementMulticategoryFilter(mcats)).WhereElementIsNotElementType()
            .Where(e => e.Id.Value > after && e.Location is LocationCurve lc && lc.Curve is Line).OrderBy(e => e.Id.Value).ToList();
        var solids = new Dictionary<long, List<Solid>>();
        var options = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
        var rows = new List<Obj>();
        var sw = Stopwatch.StartNew();
        string resume = null; int scanned = 0, failed = 0;
        foreach (var m in runs)
        {
            if (sw.Elapsed.TotalSeconds > seconds) { resume = (m.Id.Value - 1).ToString(CultureInfo.InvariantCulture); break; }
            scanned++;
            var (sizeText, sizeFeet) = Size(m);
            if (sizeFeet < minSize) continue;
            var line = (Line)((LocationCurve)m.Location).Curve;
            XYZ a = mtf.OfPoint(line.GetEndPoint(0)), b = mtf.OfPoint(line.GetEndPoint(1));
            double r = sizeFeet / 2;
            var lo = new XYZ(Math.Min(a.X, b.X) - r, Math.Min(a.Y, b.Y) - r, Math.Min(a.Z, b.Z) - r);
            var hi = new XYZ(Math.Max(a.X, b.X) + r, Math.Max(a.Y, b.Y) + r, Math.Max(a.Z, b.Z) + r);
            if (a.DistanceTo(b) < 0.01) continue;
            var inS = Line.CreateBound(sInv.OfPoint(a), sInv.OfPoint(b));
            foreach (var (s, box) in structure)
            {
                if (box.MaximumPoint.X < lo.X || box.MinimumPoint.X > hi.X || box.MaximumPoint.Y < lo.Y || box.MinimumPoint.Y > hi.Y
                    || box.MaximumPoint.Z < lo.Z || box.MinimumPoint.Z > hi.Z) continue;
                if (!solids.TryGetValue(s.Id.Value, out var ss)) solids[s.Id.Value] = ss = Geo.Solids(s);
                foreach (var solid in ss)
                {
                    SolidCurveIntersection hit;
                    try { hit = solid.IntersectWithCurve(inS, options); }
                    catch { failed++; continue; }
                    for (int i = 0; i < hit.SegmentCount; i++)
                    {
                        var seg = hit.GetCurveSegment(i);
                        if (seg.Length < 0.003) continue;
                        XYZ p0 = stf.OfPoint(seg.GetEndPoint(0)), p1 = stf.OfPoint(seg.GetEndPoint(1));
                        rows.Add(new Obj
                        {
                            ["mep"] = m.Id.Value, ["mepCategory"] = m.Category?.Name, ["size"] = sizeText, ["system"] = P(m, "RBS_SYSTEM_NAME_PARAM"),
                            ["structure"] = s.Id.Value, ["structureCategory"] = s.Category?.Name, ["structureType"] = Describe.TypeLabel(sdoc, s),
                            ["level"] = Describe.LevelName(sdoc, s), ["at"] = U.Xyz((p0 + p1) / 2), ["lengthInside"] = U.Out(p0.DistanceTo(p1)),
                            ["entry"] = U.Xyz(p0), ["exit"] = U.Xyz(p1),
                        });
                    }
                }
            }
        }
        return new
        {
            units = U.Name, mepModel = mname, structureModel = sname, runsInScope = runs.Count, scanned, complete = resume == null, resumeAfter = resume,
            crossings = rows.Count, truncated = rows.Count > limit, intersectionErrors = failed,
            byPair = rows.GroupBy(x => (string)x["mepCategory"] + " / " + (string)x["structureCategory"]).Select(g => new { pair = g.Key, count = g.Count() }).ToList(),
            items = rows.Take(limit).ToList(),
            note = "A crossing is a run's centreline passing through a structural solid (sizes are not added). at = midpoint inside, "
                 + "host coordinates. Existing openings are not checked yet. Only straight runs.",
        };
    }
}
