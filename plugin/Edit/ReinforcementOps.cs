using Autodesk.Revit.DB.Structure;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 7 (reinforcement completion): path reinforcement, fabric areas and sheets, hooks on existing bars,
/// couplers and multi-reference rebar annotations. No design: bar types, spacings, lengths, laps, hooks and cover come
/// from the engineer (or are the model's own type values); nothing is chosen by rule or code.
/// </summary>
internal static class ReinforcementOps
{
    public static void Register(Dictionary<string, Func<EditContext, Obj, object>> ops)
    {
        ops["path_reinforcement"] = PathRein;
        ops["fabric_area"] = FabricAreaCreate;
        ops["fabric_sheet_place"] = FabricSheetPlace;
        ops["rebar_hooks"] = RebarHooks;
        ops["coupler_place"] = CouplerPlace;
        ops["rebar_annotate"] = RebarAnnotate;
        ops["rebar_numbering"] = RebarNumbering;
        ops["rebar_splice"] = RebarSplice;
        ops["splice_type_create"] = SpliceTypeCreate;
    }

    // ---------------------------------------------------------------- shared

    /// <summary>A rebar hook type by name or id; "none" (or empty) means no hook.</summary>
    public static ElementId Hook(Document doc, string spec, string what)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return ElementId.InvalidElementId;
        try { return Find.TypeOf(doc, spec, null, typeof(RebarHookType)).Id; }
        catch (ArgumentException ex) { throw new ArgumentException($"{what}: {ex.Message} Use list_reinforcement_types for the hook types, or 'none'."); }
    }

    private static string HookName(Document doc, ElementId id) => id == ElementId.InvalidElementId ? "none" : doc.GetElement(id)?.Name;

    /// <summary>A concrete host for reinforcement: checked by Revit's own rule (material, 'Can Host Rebar').</summary>
    private static Element Host(EditContext c, Obj p, string name = "host")
    {
        var host = c.Editable(Find.Elem(c.Doc, NeedId(p, name)));
        if (!RebarHostData.IsValidHost(host))
            throw new ArgumentException($"Element {host.Id.Value} ({host.Category?.Name}) cannot host reinforcement: Revit needs a concrete structural element with 'Can Host Rebar'.");
        return host;
    }

    /// <summary>
    /// Set an integer choice parameter (slab face, wall face ...) by the text Revit shows for it, trying the values
    /// Revit accepts; the API gives these choices as plain integers without names.
    /// </summary>
    private static string SetChoice(Element e, string bip, string wanted)
    {
        var prm = Find.Builtin(e, bip) ?? throw new InvalidOperationException($"Element {e.Id.Value} has no parameter {bip}.");
        if (prm.IsReadOnly) throw new InvalidOperationException($"Parameter {bip} of element {e.Id.Value} is read-only.");
        int before = prm.AsInteger();
        for (int v = 0; v < 4; v++)
        {
            try { if (!prm.Set(v)) continue; } catch { continue; }
            string shown = prm.AsValueString() ?? "";
            if (shown.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return shown;
        }
        prm.Set(before);
        throw new ArgumentException($"'{wanted}' is not one of Revit's choices for {bip}.");
    }

    private static double? OptLen(Obj p, string name)
    {
        if (!(Dbl(p, name) is double d)) return null;
        if (d < 0) throw new ArgumentException($"'{name}' must not be negative.");
        return U.In(d);
    }

    private static double NeedLen(Obj p, string name, string op)
    {
        if (!(Dbl(p, name) is double d)) throw new ArgumentException($"{op}: '{name}' is required (the engineer's value; no default).");
        if (d <= 0) throw new ArgumentException($"'{name}' must be positive.");
        return U.In(d);
    }

    private static (double lo, double hi) ZRange(Element e)
    {
        var bb = e.get_BoundingBox(null) ?? throw new InvalidOperationException($"Element {e.Id.Value} has no extents.");
        return (bb.Min.Z, bb.Max.Z);
    }

    // ---------------------------------------------------------------- path reinforcement

    /// <summary>
    /// Path reinforcement along a sketched path in a wall or slab: bars of the given type at the given spacing,
    /// perpendicular to the path, of the given length; optional alternating bars and hooks. A slab path lies at the
    /// slab top, a wall path on the wall's location plane (z defaults to the wall base).
    /// </summary>
    private static object PathRein(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var host = Host(c, p);
        bool isWall = host is Wall;
        if (!isWall && !(host is Floor)) throw new ArgumentException($"Element {host.Id.Value} is not a wall or a slab: path reinforcement works in walls and slabs (foundation slabs included).");
        var bar = RebarOps.BarType(doc, NeedRef(p, "bar"), "bar");
        double spacing = NeedLen(p, "spacing", "path_reinforcement"), length = NeedLen(p, "length", "path_reinforcement");
        var startHook = Hook(doc, Ref(p, "start_hook"), "start_hook");
        var endHook = Hook(doc, Ref(p, "end_hook"), "end_hook");
        foreach (var (h, what) in new[] { (startHook, "start_hook"), (endHook, "end_hook") })
            if (h != ElementId.InvalidElementId && !bar.GetHookPermission(h))
                throw new ArgumentException($"{what}: bar type '{bar.Name}' does not allow hook '{HookName(doc, h)}' (Rebar Bar Type > Hook Lengths).");

        var typeId = Has(p, "type")
            ? Find.TypeOf(doc, NeedRef(p, "type"), null, typeof(PathReinforcementType)).Id
            : new FilteredElementCollector(doc).OfClass(typeof(PathReinforcementType)).FirstElementId();
        if (typeId == ElementId.InvalidElementId) typeId = PathReinforcementType.CreateDefaultPathReinforcementType(doc);

        // The path: an open polyline of [x, y] or [x, y, z] points.
        var (zLo, zHi) = ZRange(host);
        double zDefault = isWall ? zLo : zHi;
        var pts = U.PtList(p, "path", 2, 200, zDefault);
        if (isWall)
        {
            // project onto the wall's location plane (vertical, through its location line)
            if (!(host.Location is LocationCurve lc) || !(lc.Curve is Line wl))
                throw new ArgumentException($"Wall {host.Id.Value} is not straight: path reinforcement here needs a straight wall.");
            var a = wl.GetEndPoint(0); var dir = (wl.GetEndPoint(1) - a).Normalize();
            pts = pts.Select(q => { double s = (q - a).DotProduct(dir); var on = a + dir * s; return new XYZ(on.X, on.Y, q.Z); }).ToList();
        }
        var curves = new List<Curve>();
        for (int i = 0; i + 1 < pts.Count; i++) curves.Add(U.Segment(pts[i], pts[i + 1], $"path segment {i + 1}"));

        var pr = PathReinforcement.Create(doc, host, curves, Flag(p, "flip", false), typeId, bar.Id, startHook, endHook)
                 ?? throw new InvalidOperationException("Revit did not create the path reinforcement.");

        c.SetBuiltin(pr, "PATH_REIN_SPACING", spacing);
        c.SetBuiltin(pr, "PATH_REIN_LENGTH_1", length);
        string face = null;
        if (Str(p, "face") is string f)
        {
            string want = f.ToLowerInvariant();
            if (isWall && want != "exterior" && want != "interior") throw new ArgumentException("'face' for a wall is exterior or interior.");
            if (!isWall && want != "top" && want != "bottom") throw new ArgumentException("'face' for a slab is top or bottom.");
            face = SetChoice(pr, isWall ? "PATH_REIN_FACE_WALL" : "PATH_REIN_FACE_SLAB", want);
        }
        if (OptLen(p, "additional_offset") is double off) pr.AdditionalOffset = off;

        object alternating = null;
        if (Sub(p, "alternating") is Obj alt)
        {
            var altBar = Has(alt, "bar") ? RebarOps.BarType(doc, NeedRef(alt, "bar"), "alternating.bar") : bar;
            double altLength = NeedLen(alt, "length", "path_reinforcement alternating");
            c.SetBuiltinInt(pr, "PATH_REIN_ALTERNATING", 1);
            c.SetBuiltin(pr, "PATH_REIN_TYPE_2", altBar.Id);
            c.SetBuiltin(pr, "PATH_REIN_LENGTH_2", altLength);
            if (OptLen(alt, "offset") is double ao) c.SetBuiltin(pr, "PATH_REIN_ALT_OFFSET", ao);
            alternating = new { bar = altBar.Name, length = U.Out(altLength), offset = OptLen(alt, "offset") is double o2 ? U.Out(o2) : (double?)null };
        }

        doc.Regenerate();
        var bars = pr.GetRebarInSystemIds();
        // QA flag, not design: bars that leave the host's extents (usually the wrong side of the path: use flip)
        var hb = host.get_BoundingBox(null);
        double tol = U.Mm(5);
        var outside = bars.Select(id => doc.GetElement(id)?.get_BoundingBox(null)).Where(b => b != null && hb != null)
            .Any(b => b.Min.X < hb.Min.X - tol || b.Min.Y < hb.Min.Y - tol || b.Max.X > hb.Max.X + tol || b.Max.Y > hb.Max.Y + tol
                      || b.Min.Z < hb.Min.Z - tol || b.Max.Z > hb.Max.Z + tol);
        if (outside)
            c.Warn($"path reinforcement {pr.Id.Value}: bars extend beyond host {host.Id.Value}'s extents; if they should lie on the other side of the path, use flip: true.");
        int count = bars.Sum(id => (doc.GetElement(id) as RebarInSystem)?.Quantity ?? 0);
        return new
        {
            id = pr.Id.Value, host = host.Id.Value, hostKind = isWall ? "wall" : "slab", bar = bar.Name,
            spacing = U.Out(spacing), length = U.Out(length), face = face ?? Find.Builtin(pr, isWall ? "PATH_REIN_FACE_WALL" : "PATH_REIN_FACE_SLAB")?.AsValueString(),
            startHook = HookName(doc, startHook), endHook = HookName(doc, endHook), flipped = Flag(p, "flip", false), alternating,
            barSets = bars.Count, bars = count, pathLength = U.Out(curves.Sum(x => x.Length)), barsBeyondHost = outside,
        };
    }

    // ---------------------------------------------------------------- fabric

    /// <summary>
    /// A fabric sheet type with real wires. A sheet type without major / minor wire types makes sheets of 0 kg that Revit
    /// cannot place, so it is refused; so is 'default' in a model with no fabric wire types (Revit's default type has none).
    /// </summary>
    private static FabricSheetType SheetType(Document doc, string spec, string what)
    {
        FabricSheetType t;
        if (spec.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            if (!new FilteredElementCollector(doc).OfClass(typeof(FabricWireType)).Any())
                throw new ArgumentException($"{what}: the model has no fabric wire types, so Revit's default sheet type would have no wires (0 kg). "
                    + "Load fabric sheet and wire types first (a structural template or another model, standards_copy), then name the sheet type.");
            t = (FabricSheetType)doc.GetElement(FabricSheetType.CreateDefaultFabricSheetType(doc));
        }
        else
        {
            try { t = (FabricSheetType)Find.TypeOf(doc, spec, null, typeof(FabricSheetType)); }
            catch (ArgumentException ex) { throw new ArgumentException($"{what}: {ex.Message} ('default' creates Revit's default sheet type.)"); }
        }
        if (!(doc.GetElement(t.MajorDirectionWireType) is FabricWireType) || !(doc.GetElement(t.MinorDirectionWireType) is FabricWireType))
            throw new ArgumentException($"{what}: sheet type '{t.Name}' has no major / minor wire type, so its sheets would have no wires (0 kg). "
                + "Set the wire types on the sheet type (Edit Type) or use another type.");
        return t;
    }

    private static FabricLocation Location(Obj p, bool isWall)
    {
        string loc = (Str(p, "location") ?? (isWall ? "exterior" : "top")).ToLowerInvariant();
        switch (loc)
        {
            case "top": case "exterior": return FabricLocation.TopOrExternal;
            case "bottom": case "interior": return FabricLocation.BottomOrInternal;
            default: throw new ArgumentException("'location' is top or bottom (slabs), exterior or interior (walls).");
        }
    }

    /// <summary>A fabric area over a slab or wall (the whole host, or a boundary in a slab) with the given sheet type.</summary>
    private static object FabricAreaCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var host = Host(c, p);
        bool isWall = host is Wall;
        var sheet = SheetType(doc, NeedRef(p, "sheet_type"), "sheet_type");
        var areaType = Has(p, "type")
            ? Find.TypeOf(doc, NeedRef(p, "type"), null, typeof(FabricAreaType)).Id
            : new FilteredElementCollector(doc).OfClass(typeof(FabricAreaType)).FirstElementId();
        if (areaType == ElementId.InvalidElementId) areaType = FabricAreaType.CreateDefaultFabricAreaType(doc);

        var (zLo, zHi) = ZRange(host);
        XYZ major = Has(p, "direction") ? U.NeedPt(p, "direction").Normalize() : null;
        FabricArea fa;
        if (Has(p, "boundary"))
        {
            if (isWall) throw new ArgumentException("'boundary' is for slabs; a wall's fabric area covers the wall.");
            var loop = EditContext.Loop(p["boundary"], "boundary", zHi);
            major = major ?? (loop.First().GetEndPoint(1) - loop.First().GetEndPoint(0)).Normalize();
            major = new XYZ(major.X, major.Y, 0).Normalize();
            var origin = Has(p, "origin") ? U.NeedPt(p, "origin", zHi) : loop.First().GetEndPoint(0);
            fa = FabricArea.Create(doc, host, new List<CurveLoop> { loop }, major, new XYZ(origin.X, origin.Y, zHi), areaType, sheet.Id);
        }
        else
        {
            if (major == null)
                major = isWall && host.Location is LocationCurve lc ? (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize() : XYZ.BasisX;
            fa = FabricArea.Create(doc, host, major, areaType, sheet.Id);
        }
        if (fa == null) throw new InvalidOperationException("Revit did not create the fabric area.");

        fa.FabricLocation = Location(p, isWall);
        // Revit lays the sheets out on the first regeneration and then takes the laps from the sheet type, so the
        // engineer's laps and cover are set after it, and read back: a value Revit does not keep is an error, never ignored.
        doc.Regenerate();
        var given = new List<(string name, double feet, Func<double> get, Action<double> set, string bip)>();
        if (OptLen(p, "cover_offset") is double co) given.Add(("cover_offset", co, () => fa.CoverOffset, v => fa.CoverOffset = v, "FABRIC_PARAM_COVER_OFFSET"));
        if (OptLen(p, "lap_major") is double lm) given.Add(("lap_major", lm, () => fa.MajorLapSpliceLength, v => fa.MajorLapSpliceLength = v, "FABRIC_PARAM_MAJOR_LAPSPLICE_LENGTH"));
        if (OptLen(p, "lap_minor") is double ln) given.Add(("lap_minor", ln, () => fa.MinorLapSpliceLength, v => fa.MinorLapSpliceLength = v, "FABRIC_PARAM_MINOR_LAPSPLICE_LENGTH"));
        foreach (var g in given) g.set(g.feet);
        if (given.Count > 0) doc.Regenerate();
        foreach (var g in given)
        {
            if (Math.Abs(g.get() - g.feet) < 1e-6) continue;
            var prm = Find.Builtin(fa, g.bip);   // second route: the instance parameter
            if (prm != null && !prm.IsReadOnly) { prm.Set(g.feet); doc.Regenerate(); }
            if (Math.Abs(g.get() - g.feet) > 1e-6)
                throw new InvalidOperationException($"Revit kept {g.name} at {U.Out(g.get())} {U.Name} instead of {U.Out(g.feet)}. "
                    + "Laps may be fixed by the fabric sheet type (Edit Type > lap splice lengths); change them there or use another type.");
        }

        var sheets = fa.GetFabricSheetElementIds();
        double kg = sheets.Select(id => doc.GetElement(id) as FabricSheet).Where(s => s != null)
                          .Sum(s => UnitUtils.ConvertFromInternalUnits(s.CutSheetMass, UnitTypeId.Kilograms));
        return new
        {
            id = fa.Id.Value, host = host.Id.Value, sheetType = sheet.Name, location = fa.FabricLocation.ToString(),
            coverOffset = U.Out(fa.CoverOffset), lapMajor = U.Out(fa.MajorLapSpliceLength), lapMinor = U.Out(fa.MinorLapSpliceLength),
            sheets = sheets.Count, sheetIds = EditContext.Values(sheets).Take(200).ToList(), massKg = Math.Round(kg, 1),
        };
    }

    /// <summary>One fabric sheet placed in a slab or wall at a point, rotated in the host plane.</summary>
    private static object FabricSheetPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var host = Host(c, p);
        bool isWall = host is Wall;
        var sheetType = SheetType(doc, NeedRef(p, "sheet_type"), "sheet_type");
        if (!FabricSheet.IsValidHost(host)) throw new ArgumentException($"Element {host.Id.Value} cannot host a fabric sheet.");
        var (zLo, zHi) = ZRange(host);
        var at = U.NeedPt(p, "point", isWall ? zLo : zHi);
        double angle = U.Rad(Dbl(p, "rotation") ?? 0);

        var tf = Transform.Identity;
        if (isWall)
        {
            if (!(host.Location is LocationCurve lc) || !(lc.Curve is Line wl)) throw new ArgumentException($"Wall {host.Id.Value} is not straight.");
            var along = (wl.GetEndPoint(1) - wl.GetEndPoint(0)).Normalize();
            var a = wl.GetEndPoint(0); double s = (at - a).DotProduct(along);
            var on = a + along * s;
            tf.Origin = new XYZ(on.X, on.Y, at.Z);
            var x = along * Math.Cos(angle) + XYZ.BasisZ * Math.Sin(angle);
            var y = XYZ.BasisZ * Math.Cos(angle) - along * Math.Sin(angle);
            tf.BasisX = x; tf.BasisY = y; tf.BasisZ = x.CrossProduct(y);
        }
        else
        {
            tf = Transform.CreateRotation(XYZ.BasisZ, angle);
            tf.Origin = new XYZ(at.X, at.Y, zHi);
        }

        var sheet = FabricSheet.Create(doc, host, sheetType.Id) ?? throw new InvalidOperationException("Revit did not create the fabric sheet.");
        try { sheet.PlaceInHost(host, tf); }
        catch (Exception ex) when (ex.Message.IndexOf("out of the host", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new ArgumentException($"The sheet ({U.Out(sheetType.OverallLength)} x {U.Out(sheetType.OverallWidth)} {U.Name}, origin at its corner "
                + $"{string.Join(", ", U.Xyz(tf.Origin))}) does not lie inside host {host.Id.Value}. Move 'point' or rotate so the whole sheet fits.");
        }
        sheet.FabricLocation = Location(p, isWall);
        if (OptLen(p, "cover_offset") is double co) sheet.CoverOffset = co;
        doc.Regenerate();
        return new
        {
            id = sheet.Id.Value, host = host.Id.Value, sheetType = sheetType.Name, location = sheet.FabricLocation.ToString(),
            point = U.Xyz(tf.Origin), rotation = Dbl(p, "rotation") ?? 0,
            cutLength = U.Out(sheet.CutOverallLength), cutWidth = U.Out(sheet.CutOverallWidth),
            massKg = Math.Round(UnitUtils.ConvertFromInternalUnits(sheet.CutSheetMass, UnitTypeId.Kilograms), 2),
        };
    }

    // ---------------------------------------------------------------- hooks on existing bars

    /// <summary>Hooks of the model's hook types at the start / end of shape-driven bar sets, with orientation and rotation.</summary>
    private static object RebarHooks(EditContext c, Obj p)
    {
        var doc = c.Doc;
        bool any = Has(p, "start") || Has(p, "end") || Has(p, "start_orientation") || Has(p, "end_orientation")
                   || Has(p, "start_rotation") || Has(p, "end_rotation");
        if (!any) throw new ArgumentException("rebar_hooks: give start and / or end (hook type or 'none'), or an orientation / rotation.");
        var done = new List<object>();
        foreach (var e in c.Elements(p, max: 2000))
        {
            if (!(e is Rebar bar)) throw new ArgumentException($"Element {e.Id.Value} is not a rebar set (bars of area / path systems take hooks from their system).");
            var barType = doc.GetElement(bar.GetTypeId()) as RebarBarType;
            var item = new Obj { ["id"] = bar.Id.Value };
            for (int end = 0; end < 2; end++)
            {
                string key = end == 0 ? "start" : "end";
                if (Has(p, key))
                {
                    var hook = Hook(doc, Ref(p, key), key);
                    if (hook != ElementId.InvalidElementId)
                    {
                        if (barType != null && !barType.GetHookPermission(hook))
                            throw new ArgumentException($"Bar {bar.Id.Value}: bar type '{barType.Name}' does not allow hook '{HookName(doc, hook)}' (Rebar Bar Type > Hook Lengths).");
                        if (!bar.CanUseHookType(hook))
                            throw new ArgumentException($"Bar {bar.Id.Value}: Revit does not allow hook '{HookName(doc, hook)}' on this bar's style / shape.");
                        if (!bar.HookAngleMatchesRebarShapeDefinition(end, hook))
                            c.Warn($"bar {bar.Id.Value}: the {key} hook angle differs from the shape's definition, so Revit will use another shape.");
                    }
                    bar.SetHookTypeId(end, hook);
                    item[key] = HookName(doc, hook);
                }
                if (Str(p, key + "_orientation") is string o)
                {
                    bool left = o.Equals("left", StringComparison.OrdinalIgnoreCase);
                    if (!left && !o.Equals("right", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"'{key}_orientation' is left or right.");
#if REVIT2024 || REVIT2025
                    bar.SetHookOrientation(end, left ? RebarHookOrientation.Left : RebarHookOrientation.Right);
#else
                    bar.SetTerminationOrientation(end, left ? RebarTerminationOrientation.Left : RebarTerminationOrientation.Right);
#endif
                    item[key + "Orientation"] = left ? "left" : "right";
                }
                if (Dbl(p, key + "_rotation") is double deg)
                {
#if REVIT2024 || REVIT2025
                    bar.SetHookRotationAngle(U.Rad(deg), end);
#else
                    bar.SetTerminationRotationAngle(end, U.Rad(deg));
#endif
                    item[key + "Rotation"] = deg;
                }
            }
            done.Add(item);
        }
        return new { bars = done.Count, items = done };
    }

    // ---------------------------------------------------------------- couplers

    private static int End(Obj b, string what)
    {
        string e = (Str(b, "end") ?? "").ToLowerInvariant();
        if (e == "start" || e == "0") return 0;
        if (e == "end" || e == "1") return 1;
        throw new ArgumentException($"{what}: 'end' is start or end.");
    }

    /// <summary>A coupler of a given type joining two bar ends (or one bar end, as an end treatment).</summary>
    private static object CouplerPlace(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var type = Find.Symbol(doc, NeedRef(p, "type"), BuiltInCategory.OST_Coupler);
        var first = Sub(p, "first") ?? throw new ArgumentException("coupler_place: 'first' {rebar, end} is required.");
        var second = Sub(p, "second");
        ReinforcementData Data(Obj b, string what)
        {
            var e = c.Editable(Find.Elem(doc, NeedId(b, "rebar")));
            if (!(e is Rebar)) throw new ArgumentException($"{what}: element {e.Id.Value} is not a rebar set.");
            return RebarReinforcementData.Create(e.Id, End(b, what));
        }
        var d1 = Data(first, "first");
        var d2 = second != null ? Data(second, "second") : null;
        var coupler = RebarCoupler.Create(doc, type.Id, d1, d2, out RebarCouplerError error);
        if (coupler == null || error != RebarCouplerError.ValidationSuccessfuly)
            throw new InvalidOperationException($"Revit refused the coupler: {error}. {CouplerHelp(error)}");
        return new
        {
            id = coupler.Id.Value, type = Find.TypeLabel(type), linksTwoBars = coupler.CouplerLinkTwoBars(),
            quantity = Find.Builtin(coupler, "COUPLER_QUANTITY")?.AsInteger(),
        };
    }

    private static string CouplerHelp(RebarCouplerError e)
    {
        switch (e.ToString())
        {
            case "InvalidDiameter": return "The coupler type is made for another bar diameter: pick the coupler type of this bar size (list_reinforcement_types).";
            case "BarsNotTouching": return "The two bar ends do not meet: the bars must touch end to end.";
            case "DifferentLayout": return "The two bar sets have different layouts (number, spacing or array length).";
            case "BarSegmentsAreNotOnSameLine": case "BarSegementsAreNotParallel": return "The coupled end segments must be straight and in line.";
            case "IncorrectEndTreatmentCoupler": return "The coupler type's end treatment does not suit this bar end (for example a coupler made for another bar size, or the end already has another coupler).";
            case "IncorrectEndTreatmentHook": return "A hooked end cannot take a coupler: remove the hook (rebar_hooks ... 'none').";
            case "CurvesOtherThanLine": return "The coupled end segments must be straight lines.";
            case "BarSegmentSmallerThanEngagement": return "The end segment is shorter than the coupler's engagement length.";
            default: return "See Revit's rules for couplers (bars in line, same layout, matching diameter).";
        }
    }

    // ---------------------------------------------------------------- multi-reference annotation

    /// <summary>
    /// A multi-reference annotation (a dimension across the bars plus one tag) for bar sets visible in a view, e.g. a
    /// section through a beam. Points in model coordinates; [x, y] takes z from the first bar.
    /// </summary>
    private static object RebarAnnotate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var view = Find.ViewOrActive(doc, p);
        if (view.IsTemplate || view is ViewSheet || view is View3D || view is ViewSchedule)
            throw new ArgumentException($"View '{view.Name}' cannot take a rebar annotation: use a plan, section or elevation.");
        var ids = Ids(p, "ids", 500);
        foreach (var id in ids)
        {
            var e = Find.Elem(doc, id);
            if (!(e is Rebar) && !(e is RebarInSystem)) throw new ArgumentException($"Element {id.Value} is not a rebar set.");
        }
        MultiReferenceAnnotationType mraType;
        if (Has(p, "type")) mraType = (MultiReferenceAnnotationType)Find.TypeOf(doc, NeedRef(p, "type"), null, typeof(MultiReferenceAnnotationType));
        else
        {
            mraType = new FilteredElementCollector(doc).OfClass(typeof(MultiReferenceAnnotationType)).Cast<MultiReferenceAnnotationType>()
                          .FirstOrDefault(t => t.ReferenceCategoryId.Value == (long)BuiltInCategory.OST_Rebar)
                      ?? MultiReferenceAnnotationType.CreateDefault(doc);
        }
        var bb = doc.GetElement(ids[0]).get_BoundingBox(null);
        double z = bb != null ? (bb.Min.Z + bb.Max.Z) / 2 : 0;
        var origin = U.NeedPt(p, "origin", z);
        XYZ dir;
        if (Has(p, "direction")) dir = U.NeedPt(p, "direction").Normalize();
        else
        {
            // a bar set is dimensioned across its bars: along its distribution direction (the bar plane's normal),
            // which must lie in the view; bars laid out along the view direction all overlap there.
            var first = doc.GetElement(ids[0]);
            XYZ n = first is Rebar rb && rb.IsRebarShapeDriven() ? rb.GetShapeDrivenAccessor().Normal : first is RebarInSystem ris ? ris.Normal : null;
            var vd = view.ViewDirection;
            var inPlane = n == null ? null : n - vd * n.DotProduct(vd);
            if (inPlane == null || inPlane.GetLength() < 0.2)
                throw new ArgumentException($"In view '{view.Name}' the bars of set {ids[0].Value} are laid out along the viewing direction and overlap; "
                    + "annotate them in a view that looks across the set (bars seen as dots, or side by side), or give 'direction'.");
            dir = inPlane.Normalize();
        }
        var head = Has(p, "tag_head") ? U.NeedPt(p, "tag_head", z) : origin;

        using (var options = new MultiReferenceAnnotationOptions(mraType))
        {
            if (!options.ElementsMatchReferenceCategory(ids))
                throw new ArgumentException($"Annotation type '{mraType.Name}' does not dimension these elements (its reference category differs).");
            options.SetElementsToDimension(ids);
            options.DimensionLineOrigin = origin;
            options.DimensionLineDirection = dir;
            options.DimensionPlaneNormal = view.ViewDirection;
            options.TagHeadPosition = head;
            options.TagHasLeader = Flag(p, "leader", true);
            var mra = MultiReferenceAnnotation.Create(doc, view.Id, options)
                      ?? throw new InvalidOperationException("Revit did not create the annotation.");
            bool tagged = mra.TagId != ElementId.InvalidElementId;
            if (!tagged)
                c.Warn($"annotation type '{mraType.Name}' has no tag type, so only the dimension was made; give the type a rebar tag (Edit Type) or pass another 'type'.");
            return new { id = mra.Id.Value, view = view.Name, type = mraType.Name, dimension = mra.DimensionId.Value, tag = tagged ? mra.TagId.Value : (long?)null, bars = ids.Count };
        }
    }

    // ---------------------------------------------------------------- numbering

    /// <summary>Renumber a partition of a numbering schema: remove gaps, shift its numbers to start at a number, or change one number.</summary>
    private static object RebarNumbering(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var schema = Numbering.Pick(doc, Str(p, "schema"));
        var parts = schema.GetNumberingSequences().ToList();
        string partition = Has(p, "partition") ? Text(p["partition"]) ?? "" : (parts.Count == 1 ? parts[0] : null);
        if (partition == null)
            throw new ArgumentException($"The {Numbering.Kind(schema)} numbering has {parts.Count} partitions; give 'partition': " + string.Join(", ", parts.Select(x => $"'{x}'")) + ".");
        if (!parts.Contains(partition))
            throw new ArgumentException($"The {Numbering.Kind(schema)} numbering has no partition '{partition}'. Partitions: " + string.Join(", ", parts.Select(x => $"'{x}'")) + ".");
        var before = Numbering.Partition(schema, partition);
        if (!Has(p, "action")) throw new ArgumentException("rebar_numbering: 'action' is required: remove_gaps, shift or change.");
        string action = Choice(p, "action", "remove_gaps", "remove_gaps", "shift", "change");
        switch (action)
        {
            case "remove_gaps": schema.RemoveGaps(partition); break;
            case "shift":
            {
                int first = IntegerValue(NeedDbl(p, "first"), "'first'");
                if (first < 1 || first > NumberingSchema.MaximumStartingNumber) throw new ArgumentException($"'first' must be 1 to {NumberingSchema.MaximumStartingNumber}.");
                schema.ShiftNumbers(partition, first);
                break;
            }
            default:
            {
                int from = IntegerValue(NeedDbl(p, "from"), "'from'"), to = IntegerValue(NeedDbl(p, "to"), "'to'");
                schema.ChangeNumber(partition, from, to);
                break;
            }
        }
        doc.Regenerate();
        return new { schema = Numbering.Kind(schema), action, before, after = Numbering.Partition(schema, partition) };
    }

    // ---------------------------------------------------------------- splices (Revit 2027)

#if REVIT2024 || REVIT2025 || REVIT2026
    private static object RebarSplice(EditContext c, Obj p) =>
        throw new InvalidOperationException("Rebar splices need Revit 2027 (the splice API does not exist before). Use select_revit for a Revit 2027 session.");

    private static object SpliceTypeCreate(EditContext c, Obj p) =>
        throw new InvalidOperationException("Rebar splice types need Revit 2027 (the splice API does not exist before).");
#else
    private static ElementId SpliceType(Document doc, string spec)
    {
        var all = RebarSpliceTypeUtils.GetAllRebarSpliceTypes(doc).Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
        var hit = all.FirstOrDefault(e => e.Id.Value.ToString() == spec.Trim() || string.Equals(e.Name, spec.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit?.Id ?? throw new ArgumentException($"No splice type '{spec}'. In this model: {string.Join(", ", all.Select(e => e.Name))}. splice_type_create makes one with your lap.");
    }

    /// <summary>A splice type with the engineer's lap (and stagger) as multiples of the bar diameter.</summary>
    private static object SpliceTypeCreate(EditContext c, Obj p)
    {
        var doc = c.Doc;
        string name = NeedStr(p, "name");
        if (RebarSpliceTypeUtils.GetAllRebarSpliceTypes(doc).Any(id => string.Equals(doc.GetElement(id)?.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A splice type '{name}' already exists.");
        double lap = Dbl(p, "lap_d") ?? throw new ArgumentException("splice_type_create: 'lap_d' (lap length in bar diameters) is required; no default.");
        if (lap <= 0) throw new ArgumentException("'lap_d' must be positive.");
        var id = RebarSpliceTypeUtils.CreateRebarSpliceType(doc, name).Id;
        RebarSpliceTypeUtils.SetLapLengthMultiplier(doc, id, lap);
        if (Dbl(p, "stagger_d") is double st) RebarSpliceTypeUtils.SetStaggerLengthMultiplier(doc, id, st);
        if (Str(p, "shift") is string sh)
        {
            if (sh != "bar_plane" && sh != "none") throw new ArgumentException("'shift' is bar_plane or none.");
            RebarSpliceTypeUtils.SetShiftOption(doc, id, sh == "bar_plane" ? RebarSpliceShiftOption.BarPlane : RebarSpliceShiftOption.None);
        }
        return new
        {
            id = id.Value, name, lapD = RebarSpliceTypeUtils.GetLapLengthMultiplier(doc, id),
            staggerD = RebarSpliceTypeUtils.GetStaggerLengthMultiplier(doc, id), shift = RebarSpliceTypeUtils.GetShiftOption(doc, id).ToString(),
        };
    }

    /// <summary>
    /// Splice bar sets with a splice type: by rules (bars no longer than max_length) or at a line seen in a view.
    /// The lengths are the engineer's; Revit places the laps.
    /// </summary>
    private static object RebarSplice(EditContext c, Obj p)
    {
        var doc = c.Doc;
        var typeId = SpliceType(doc, NeedRef(p, "type"));
        var pos = (Str(p, "position") ?? "middle").ToLowerInvariant() switch
        {
            "end1" => RebarSplicePosition.End1, "middle" => RebarSplicePosition.Middle, "end2" => RebarSplicePosition.End2,
            _ => throw new ArgumentException("'position' is end1, middle or end2."),
        };
        var rules = Sub(p, "rules");
        Obj at = Sub(p, "at");
        if ((rules == null) == (at == null)) throw new ArgumentException("rebar_splice: give either 'rules' {max_length, min_length?, run_out?} or 'at' {start, end, view?}.");
        var done = new List<object>();
        foreach (var e in c.Elements(p, max: 500))
        {
            if (!(e is Rebar bar)) throw new ArgumentException($"Element {e.Id.Value} is not a rebar set.");
            long barId = bar.Id.Value;   // Revit replaces the spliced set: the original element is no longer valid afterwards
            using (var options = new RebarSpliceOptions(doc, typeId, pos))
            {
                IList<ElementId> made;
                if (rules != null)
                {
                    double max = U.In(NeedDbl(rules, "max_length")), min = OptLen(rules, "min_length") ?? 0;
                    using (var r = RebarSpliceRules.Create(doc))
                    {
                        r.SetMaximumAndMinimumBarLength(max, min);
                        if (Str(rules, "run_out") is string ro)
                            r.RunOutPosition = ro == "end" ? RebarSpliceByRulesRunOutPosition.End
                                : ro == "start" ? RebarSpliceByRulesRunOutPosition.Start : throw new ArgumentException("'run_out' is start or end.");
                        using (var result = RebarSpliceUtils.GetSpliceGeometries(doc, bar.Id, options, r))
                        {
                            if (result.Error != RebarSpliceByRulesError.Success)
                                throw new InvalidOperationException($"Bar {barId}: Revit cannot splice by these rules: {result.Error}.");
                            made = RebarSpliceUtils.SpliceRebar(doc, bar.Id, options, result.GetSpliceGeometries()).ToList();
                        }
                    }
                }
                else
                {
                    var view = Find.ViewOrActive(doc, at);
                    var line = U.Segment(U.NeedPt(at, "start"), U.NeedPt(at, "end"), "'at'");
                    var can = RebarSpliceUtils.CanRebarBeSpliced(bar, options, line, view.Id);
                    if (can != RebarSpliceError.Success)
                        throw new InvalidOperationException($"Bar {barId}: Revit cannot splice it at this line: {can} (the line must cross every bar of the set on a straight segment, not on a hook, bend or crank).");
                    made = RebarSpliceUtils.SpliceRebar(doc, bar.Id, options, line, view.Id).ToList();
                }
                doc.Regenerate();
                var lengths = made.Select(id => doc.GetElement(id) as Rebar).Where(r => r != null)
                                  .Select(r => Math.Round(r.TotalLength / Math.Max(1, r.Quantity) * U.MmPerFoot)).ToList();
                done.Add(new { bar = barId, bars = EditContext.Values(made), barLengthsMm = lengths });
            }
        }
        return new { type = doc.GetElement(typeId)?.Name, position = pos.ToString(), items = done };
    }
#endif
}
