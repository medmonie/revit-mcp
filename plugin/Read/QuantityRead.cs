namespace RevitMcpBridge;

/// <summary>Quantity take-off straight from the model, and reading schedules. Nothing here changes anything.</summary>
internal static class QuantityRead
{
    private const double SteelDensity = 7850;   // kg/m3

    /// <summary>Group labels of an element for group_by type / level / type_level (null when not grouped by it).</summary>
    private static (string type, string level) Groups(Document doc, Element e, string groupBy) =>
        (groupBy == "type" || groupBy == "type_level" ? Describe.TypeLabel(doc, e) : null,
         groupBy == "level" || groupBy == "type_level" ? Describe.LevelName(doc, e) ?? "(no level)" : null);

    private static double Feet(Element e, params string[] bips)
    {
        foreach (string b in bips)
        {
            var prm = Find.Builtin(e, b);
            if (prm != null && prm.HasValue && prm.StorageType == StorageType.Double) return prm.AsDouble();
        }
        return 0;
    }

    private sealed class Sum
    {
        public string Category, Type, Level, MaterialKind;
        public int Count;
        public double Length, Area, Volume;

        /// <summary>The running total for this element's group (category + the type / level the grouping keeps), counted once.</summary>
        public static Sum For(Dictionary<string, Sum> sums, Document doc, Element e, string groupBy)
        {
            var (type, level) = Groups(doc, e, groupBy);
            string key = e.Category.Name + "|" + type + "|" + level;
            if (!sums.TryGetValue(key, out var s)) sums[key] = s = new Sum { Category = e.Category.Name, Type = type, Level = level };
            s.Count++;
            return s;
        }

        /// <summary>The group's row with its keys and count; callers add the quantities.</summary>
        public Obj Row()
        {
            var o = new Obj { ["category"] = Category };
            if (Type != null) o["type"] = Type;
            if (Level != null) o["level"] = Level;
            o["count"] = Count;
            return o;
        }
    }

    /// <summary>Count, length, area and volume as Revit computes them (the same numbers a schedule shows).</summary>
    public static object Takeoff(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var cats = Find.Cats(doc, p, "category", Find.Structural);
        string groupBy = Choice(p, "group_by", "type", "type", "level", "type_level", "category");
        var sums = new Dictionary<string, Sum>();
        foreach (Element e in Find.Scope(doc, p, cats))
        {
            var s = Sum.For(sums, doc, e, groupBy);
            s.Length += Feet(e, "STRUCTURAL_FRAME_CUT_LENGTH", "CURVE_ELEM_LENGTH", "INSTANCE_LENGTH_PARAM");
            s.Area += Feet(e, "HOST_AREA_COMPUTED");
            s.Volume += Feet(e, "HOST_VOLUME_COMPUTED");
        }

        var rows = sums.Values.OrderBy(s => s.Category).ThenBy(s => s.Level).ThenBy(s => s.Type).Select(s =>
        {
            var o = s.Row();
            if (s.Length > 0) o["length_m"] = U.M(s.Length, 3);
            if (s.Area > 0) o["area_m2"] = U.Area(s.Area);
            if (s.Volume > 0) o["volume_m3"] = U.Volume(s.Volume);
            return o;
        }).ToList();
        var totals = sums.Values.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => (object)new
        {
            count = g.Sum(s => s.Count), length_m = U.M(g.Sum(s => s.Length)),
            area_m2 = U.Area(g.Sum(s => s.Area)), volume_m3 = U.Volume(g.Sum(s => s.Volume)),
        });
        return new
        {
            note = "Net quantities as Revit computes them after joins and cuts (e.g. a slab joined into a column takes the shared " +
                   "volume from one of them). Lengths: cut length for framing, height for columns, centre-line length for walls.",
            groupBy, rows, totalsByCategory = totals,
        };
    }

    private const string NoMaterial = "(no material assigned)";

    /// <summary>Volume and area per material (layers of walls and floors are split by material).</summary>
    public static object MaterialTakeoff(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var cats = Find.Cats(doc, p, "category", Find.Structural);
        string groupBy = Choice(p, "group_by", "category", "category", "level", "type");
        bool excludeSteel = Flag(p, "exclude_steel", false);
        string familyContains = Str(p, "family_contains");
        int steelSkipped = 0;
        int measurementFailures = 0;
        var sums = new Dictionary<(string Category, string Material, string Group), Sum>();
        var kinds = new Dictionary<ElementId, (string Name, string Kind)>();
        foreach (Element e in Find.Scope(doc, p, cats))
        {
            if (excludeSteel && Materials.IsSteel(doc, e, familyContains)) { steelSkipped++; continue; }
            var g = Groups(doc, e, groupBy);
            string extra = g.level ?? g.type;
            ICollection<ElementId> mats;
            try { mats = e.GetMaterialIds(false); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { measurementFailures++; continue; }
            if (mats.Count == 0)
            {
                // No material assigned (common for steel sections): keep the element in the take-off with Revit's volume.
                var key0 = (e.Category.Name, NoMaterial, extra);
                if (!sums.TryGetValue(key0, out var s0))
                    sums[key0] = s0 = new Sum { Category = e.Category.Name, Type = NoMaterial, Level = extra, MaterialKind = "unclassified" };
                s0.Count++;
                s0.Volume += Feet(e, "HOST_VOLUME_COMPUTED");
                s0.Area += Feet(e, "HOST_AREA_COMPUTED");
                continue;
            }
            foreach (var mid in mats)
            {
                if (!kinds.TryGetValue(mid, out var material))
                {
                    var revitMaterial = doc.GetElement(mid) as Material;
                    kinds[mid] = material = (revitMaterial?.Name ?? "(no material)", Materials.QuantityKind(doc, revitMaterial));
                }
                var key = (e.Category.Name, material.Name, extra);
                if (!sums.TryGetValue(key, out var s))
                    sums[key] = s = new Sum { Category = e.Category.Name, Type = material.Name, Level = extra, MaterialKind = material.Kind };
                s.Count++;
                try { s.Volume += e.GetMaterialVolume(mid); s.Area += e.GetMaterialArea(mid, false); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { measurementFailures++; }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { measurementFailures++; }
            }
        }
        var rows = sums.Values.OrderBy(s => s.Category).ThenBy(s => s.Level).ThenBy(s => s.Type).Select(s =>
        {
            var o = new Obj { ["category"] = s.Category };
            if (groupBy == "level") o["level"] = s.Level;
            if (groupBy == "type") o["type"] = s.Level;
            o["material"] = s.Type;
            o["materialKind"] = s.MaterialKind;
            o["elements"] = s.Count;
            o["volume_m3"] = U.Volume(s.Volume);
            o["area_m2"] = U.Area(s.Area);
            return o;
        }).ToList();
        var byMaterial = sums.Values.GroupBy(s => s.Type).ToDictionary(g => g.Key, g => (object)U.Volume(g.Sum(s => s.Volume)));
        int unassigned = sums.Values.Where(s => s.Type == NoMaterial).Sum(s => s.Count);
        return new
        {
            groupBy, rows, volumeByMaterial_m3 = byMaterial, elementsWithoutMaterial = unassigned, measurementFailures,
            steelMembersExcluded = excludeSteel ? steelSkipped : (int?)null,
            note = unassigned > 0
                ? $"{unassigned} element(s) have no material assigned; they are listed as '{NoMaterial}' with Revit's computed volume. Steel sections often appear here: assign a material for a weight-based take-off."
                : null,
        };
    }

    // ---------------------------------------------------------------- formwork

    /// <summary>
    /// Formwork (contact) areas from the element faces: sides for every category, soffits for beams and suspended slabs,
    /// never top faces. Steel members are left out.
    /// </summary>
    public static object FormworkTakeoff(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var cats = Find.Cats(doc, p, "category", Find.Structural);
        var soffit = Find.Cats(doc, p, "soffit_categories", BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Floors);
        string groupBy = Choice(p, "group_by", "type", "type", "level", "type_level", "category");
        string familyContains = Str(p, "family_contains");
        var sums = new Dictionary<string, (string cat, string type, string level, int n, double side, double bottom, double top)>();
        int steel = 0;
        foreach (Element e in Find.Scope(doc, p, cats))
        {
            if (Materials.IsSteel(doc, e, familyContains)) { steel++; continue; }
            var a = Geo.FaceAreas(e);
            var (type, level) = Groups(doc, e, groupBy);
            string key = e.Category.Name + "|" + type + "|" + level;
            sums.TryGetValue(key, out var s);
            sums[key] = (e.Category.Name, type, level, s.n + 1, s.side + a.Side, s.bottom + a.Bottom, s.top + a.Top);
        }
        var soffitNames = new HashSet<string>(soffit.Select(b => Category.GetCategory(doc, b)?.Name).Where(n => n != null));
        var rows = sums.Values.OrderBy(x => x.cat).ThenBy(x => x.level).ThenBy(x => x.type).Select(x =>
        {
            bool withSoffit = soffitNames.Contains(x.cat);
            var o = new Obj { ["category"] = x.cat };
            if (x.type != null) o["type"] = x.type;
            if (x.level != null) o["level"] = x.level;
            o["count"] = x.n;
            o["sides_m2"] = U.Area(x.side);
            o["soffit_m2"] = U.Area(x.bottom);
            o["top_m2"] = U.Area(x.top);
            o["formwork_m2"] = U.Area(x.side + (withSoffit ? x.bottom : 0));
            return o;
        }).ToList();
        return new
        {
            groupBy, rows,
            totalFormwork_m2 = Math.Round(rows.Sum(r => (double)r["formwork_m2"]), 2),
            soffitIncludedFor = soffitNames.OrderBy(n => n).ToList(),
            steelMembersSkipped = steel,
            note = "Formwork = side faces (+ soffit for soffit_categories); top faces are never formed. Faces come from the " +
                   "element geometry after joins, so joined overlaps are already removed, but faces that merely TOUCH another " +
                   "element (e.g. the upper part of a beam side against a joined slab edge, a column face where a beam frames in, " +
                   "slab-on-grade soffits) are still counted. Remove slabs on grade from soffit_categories or filter by level.",
        };
    }

    // ---------------------------------------------------------------- steel tonnage

    /// <summary>Steel weight per section: Revit's volume x density, with length and kg/m. Independent of the material assigned.</summary>
    public static object SteelTakeoff(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var cats = Find.Cats(doc, p, "category", BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming);
        double density = Dbl(p, "density") ?? SteelDensity;
        if (density <= 0) throw new ArgumentException("density must be positive.");
        string familyContains = Str(p, "family_contains");
        string groupBy = Choice(p, "group_by", "type", "type", "level", "type_level", "category");
        var sums = new Dictionary<string, Sum>();
        int noVolume = 0;
        foreach (Element e in Find.Scope(doc, p, cats))
        {
            if (!Materials.IsSteel(doc, e, familyContains)) continue;
            var s = Sum.For(sums, doc, e, groupBy);
            s.Length += Feet(e, "STRUCTURAL_FRAME_CUT_LENGTH", "INSTANCE_LENGTH_PARAM", "CURVE_ELEM_LENGTH");
            double v = Feet(e, "HOST_VOLUME_COMPUTED");
            if (v <= 0) noVolume++;
            s.Volume += v;
        }
        var rows = sums.Values.OrderBy(s => s.Category).ThenBy(s => s.Level).ThenBy(s => s.Type).Select(s =>
        {
            double kg = s.Volume * U.M3PerFt3 * density, m = s.Length * U.MPerFoot;
            var o = s.Row();
            o["length_m"] = Math.Round(m, 3);
            o["volume_m3"] = U.Volume(s.Volume);
            o["weight_kg"] = Math.Round(kg, 1);
            o["kg_per_m"] = m > 0 ? Math.Round(kg / m, 2) : (double?)null;
            return o;
        }).ToList();
        double totalKg = sums.Values.Sum(s => s.Volume) * U.M3PerFt3 * density;
        return new
        {
            groupBy, density_kg_m3 = density, rows,
            totalWeight_kg = Math.Round(totalKg, 1), totalWeight_t = Math.Round(totalKg / 1000, 3),
            membersWithoutVolume = noVolume,
            note = "Steel = structural material type 'Steel', a metal material, or a family name containing family_contains. " +
                   "Weight = Revit volume x density (net of cuts and joins; no connections, plates or bolts). Compare kg/m with " +
                   "the section tables to spot wrong family geometry.",
        };
    }

    private sealed class Bars
    {
        public string Key;
        public int Sets, Count;
        public double LengthFeet, WeightKg;
    }

    /// <summary>Reinforcement by bar diameter (or host category / bar type): number of bars, total length, weight at 7850 kg/m3.</summary>
    public static object RebarTakeoff(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        string groupBy = Choice(p, "group_by", "diameter", "diameter", "host_category", "type", "diameter_host_category");
        var sums = new Dictionary<string, Bars>();
        int noDiameter = 0;
        var hostCategories = Find.Cats(doc, p, "host_category");
        string level = Has(p, "level") ? Find.LevelOf(doc, Ref(p, "level")).Name : null;
        var rebarScope = new Obj(p);
        rebarScope.Remove("level");
        foreach (Element e in Find.Scope(doc, rebarScope, new List<BuiltInCategory> { BuiltInCategory.OST_Rebar }))
        {
            ElementId hostId = (e as RStruct.Rebar)?.GetHostId() ?? (e as RStruct.RebarInSystem)?.GetHostId();
            var hostElement = hostId == null ? null : doc.GetElement(hostId);
            if (hostCategories.Count > 0 && (hostElement?.Category == null
                || !hostCategories.Contains((BuiltInCategory)hostElement.Category.Id.Value))) continue;
            if (level != null && (hostElement == null ? Describe.LevelName(doc, e) : Describe.LevelName(doc, hostElement)) != level) continue;
            var type = doc.GetElement(e.GetTypeId());
            double dFeet = type != null ? Feet(type, "REBAR_BAR_DIAMETER") : 0;
            if (dFeet <= 0) dFeet = Feet(e, "REBAR_BAR_DIAMETER");
            double total = Feet(e, "REBAR_ELEM_TOTAL_LENGTH");
            int qty = Find.Builtin(e, "REBAR_ELEM_QUANTITY_OF_BARS") is Parameter q && q.HasValue ? q.AsInteger() : 1;
            if (total <= 0) total = Feet(e, "REBAR_ELEM_LENGTH") * Math.Max(qty, 1);
            if (dFeet <= 0) noDiameter++;

            string host = hostElement?.Category?.Name ?? "(no host)";
            string dia = dFeet > 0 ? "D" + Math.Round(dFeet * U.MmPerFoot, 1).ToString(CultureInfo.InvariantCulture) : "(unknown diameter)";
            string key = groupBy == "diameter" ? dia
                : groupBy == "host_category" ? host
                : groupBy == "type" ? (type?.Name ?? "(no type)")
                : dia + " | " + host;

            if (!sums.TryGetValue(key, out var s)) sums[key] = s = new Bars { Key = key };
            s.Sets++;
            s.Count += qty;
            s.LengthFeet += total;
            double dM = dFeet * U.MPerFoot, lM = total * U.MPerFoot;
            s.WeightKg += Math.PI / 4 * dM * dM * lM * SteelDensity;
        }
        var rows = sums.Values.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => new
        {
            group = s.Key, rebarSets = s.Sets, bars = s.Count,
            totalLength_m = U.M(s.LengthFeet, 2), weight_kg = Math.Round(s.WeightKg, 1),
        }).ToList();
        return new
        {
            groupBy, rows,
            totalLength_m = U.M(sums.Values.Sum(s => s.LengthFeet), 2),
            totalWeight_kg = Math.Round(sums.Values.Sum(s => s.WeightKg), 1),
            setsWithoutDiameter = noDiameter,
            note = "Weight = bar area x total bar length x 7850 kg/m3 (no laps or wastage beyond what is modelled). " +
                   "Area and path reinforcement count only when their bars are hosted as rebar elements.",
        };
    }

    public static object ListSchedules(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var list = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                string category = null;
                try { category = Category.GetCategory(doc, s.Definition.CategoryId)?.Name; } catch { /* multi-category */ }
                return new { id = s.Id.Value, name = s.Name, category, fields = s.Definition.GetFieldCount() };
            });
        return Describe.Page(p, list, "schedules", 300);
    }

    public static object GetScheduleData(Rc rc, Obj p)
    {
        var doc = rc.Doc;
        var vs = Find.ViewOf(doc, NeedRef(p, "schedule")) as ViewSchedule
                 ?? throw new ArgumentException("That is not a schedule. Use list_schedules.");
        int limit = Int(p, "limit", 500, 1, 20000);
        var body = vs.GetTableData().GetSectionData(SectionType.Body);
        int rows = body.NumberOfRows, cols = body.NumberOfColumns;
        var data = new List<List<string>>();
        for (int r = 0; r < rows && r < limit; r++)
        {
            var row = new List<string>();
            for (int c = 0; c < cols; c++) row.Add(vs.GetCellText(SectionType.Body, r, c));
            data.Add(row);
        }
        return new
        {
            schedule = vs.Name, rowCount = rows, columnCount = cols, truncated = rows > limit,
            note = "Rows are as displayed, column headers first; values are text formatted with the schedule's units.",
            rows = data,
        };
    }
}
