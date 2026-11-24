namespace RevitMcpBridge;

/// <summary>Turning Revit elements and parameters into plain values for Claude, and values back into parameters.</summary>
internal static class Describe
{
    private static readonly string[] LevelParams =
    {
        "FAMILY_BASE_LEVEL_PARAM", "INSTANCE_REFERENCE_LEVEL_PARAM", "SCHEDULE_LEVEL_PARAM", "WALL_BASE_CONSTRAINT",
        "FAMILY_LEVEL_PARAM", "LEVEL_PARAM", "INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM",
    };

    public static string LevelName(Document doc, Element e)
    {
        if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId && doc.GetElement(e.LevelId) is Level direct) return direct.Name;
        foreach (string bip in LevelParams)
        {
            var p = Find.Builtin(e, bip);
            if (p != null && p.StorageType == StorageType.ElementId && doc.GetElement(p.AsElementId()) is Level l) return l.Name;
        }
        return null;
    }

    /// <summary>The element's type as "Family : Type" (or the element's own name when it has no type).</summary>
    public static string TypeLabel(Document doc, Element e) => doc.GetElement(e.GetTypeId()) is ElementType t ? Find.TypeLabel(t) : e.Name;

    /// <summary>"Category : Type name", for lists of elements.</summary>
    public static string Label(Document doc, Element e) =>
        e.Category?.Name + " : " + (doc.GetElement(e.GetTypeId()) is ElementType t ? t.Name : e.Name);

    /// <summary>Cut a list at the 'limit' parameter and say so.</summary>
    public static object Page<T>(Obj p, IEnumerable<T> all, string key, int dflt, int max = 20000)
    {
        int limit = Int(p, "limit", dflt, 1, max);
        var list = all.Take(limit + 1).ToList();
        bool truncated = list.Count > limit;
        if (truncated) list.RemoveAt(list.Count - 1);
        return new Obj { ["units"] = U.Name, ["count"] = list.Count, ["truncated"] = truncated, [key] = list };
    }

    public static Obj Brief(Document doc, Element e)
    {
        var o = new Obj { ["id"] = e.Id.Value, ["uniqueId"] = e.UniqueId, ["category"] = e.Category?.Name };
        if (doc.GetElement(e.GetTypeId()) is ElementType t)
        {
            if (!string.IsNullOrEmpty(t.FamilyName)) o["family"] = t.FamilyName;
            o["type"] = t.Name;
        }
        else if (!string.IsNullOrEmpty(e.Name)) o["name"] = e.Name;
        string level = LevelName(doc, e);
        if (level != null) o["level"] = level;
        return o;
    }

    /// <summary>Insertion point or end points, in request units; null for elements without a simple location.</summary>
    /// <param name="tf">Optional transform into the reported coordinates (an element of a linked model shown in the host).</param>
    public static object Location(Element e, Transform tf = null)
    {
        bool moved = tf != null && !tf.IsIdentity;
        if (e.Location is LocationPoint lp)
        {
            var o = new Obj { ["point"] = U.Xyz(moved ? tf.OfPoint(lp.Point) : lp.Point) };
            double rot = 0;
            try { rot = lp.Rotation; } catch { /* some families have no rotation */ }
            if (moved)
            {
                rot += Math.Atan2(tf.BasisX.Y, tf.BasisX.X);
                rot -= 2 * Math.PI * Math.Floor(rot / (2 * Math.PI));   // 0 .. 360 degrees, as Revit reports it
                if (2 * Math.PI - rot < 1e-9) rot = 0;
            }
            if (Math.Abs(rot) > 1e-9) o["rotationDeg"] = U.Deg(rot);
            return o;
        }
        if (e.Location is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
            return U.CurveOut(moved ? lc.Curve.CreateTransformed(tf) : lc.Curve);
        return null;
    }

    public static object Box(Element e, Transform tf = null)
    {
        var b = e.get_BoundingBox(null);
        if (b == null) return null;
        if (tf == null || tf.IsIdentity) return new Obj { ["min"] = U.Xyz(b.Min), ["max"] = U.Xyz(b.Max) };
        // The box's own transform (usually identity) then the link's; report the axis-aligned box of the eight corners.
        var full = b.Transform == null ? tf : tf.Multiply(b.Transform);
        var pts = new List<XYZ>();
        foreach (double x in new[] { b.Min.X, b.Max.X })
            foreach (double y in new[] { b.Min.Y, b.Max.Y })
                foreach (double z in new[] { b.Min.Z, b.Max.Z })
                    pts.Add(full.OfPoint(new XYZ(x, y, z)));
        var min = new XYZ(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Min(q => q.Z));
        var max = new XYZ(pts.Max(q => q.X), pts.Max(q => q.Y), pts.Max(q => q.Z));
        return new Obj { ["min"] = U.Xyz(min), ["max"] = U.Xyz(max) };
    }

    // ---------------------------------------------------------------- parameter values

    private static bool Is(ForgeTypeId a, ForgeTypeId b) => a != null && b != null && a.TypeId == b.TypeId;

    public static Obj ParameterInfo(Parameter p, string target) => new Obj
    {
        ["name"] = p.Definition.Name, ["key"] = Find.ParameterKey(p), ["target"] = target,
        ["kind"] = Is(p.Definition.GetDataType(), SpecTypeId.Boolean.YesNo) ? "Boolean" : p.StorageType.ToString(),
        ["spec"] = p.Definition.GetDataType()?.TypeId, ["readOnly"] = p.IsReadOnly,
    };

    /// <summary>Lengths in request units, areas m2, volumes m3, angles degrees, other measurable values in the project's display unit.</summary>
    public static object Value(Document doc, Parameter p)
    {
        if (!p.HasValue) return null;
        switch (p.StorageType)
        {
            case StorageType.String: return p.AsString();
            case StorageType.Integer:
            {
                int n = p.AsInteger();
                try { if (Is(p.Definition.GetDataType(), SpecTypeId.Boolean.YesNo)) return n != 0; } catch { /* not a spec */ }
                // Many integer parameters are choices (justification, structural usage ...): show the name Revit displays.
                string shown = null;
                try { shown = p.AsValueString(); } catch { /* no display string */ }
                if (!string.IsNullOrEmpty(shown) && shown != n.ToString(CultureInfo.InvariantCulture))
                    return new Obj { ["value"] = n, ["text"] = shown };
                return n;
            }
            case StorageType.ElementId:
            {
                var id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return null;
                string name = doc.GetElement(id)?.Name;
                return string.IsNullOrEmpty(name) ? (object)id.Value : new Obj { ["id"] = id.Value, ["name"] = name };
            }
            case StorageType.Double:
            {
                double v = p.AsDouble();
                try
                {
                    var spec = p.Definition.GetDataType();
                    if (Is(spec, SpecTypeId.Length)) return U.Out(v);
                    if (Is(spec, SpecTypeId.Area)) return U.Area(v);
                    if (Is(spec, SpecTypeId.Volume)) return U.Volume(v);
                    if (Is(spec, SpecTypeId.Angle)) return U.Deg(v);
                    if (UnitUtils.IsMeasurableSpec(spec)) return Math.Round(UnitUtils.ConvertFromInternalUnits(v, p.GetUnitTypeId()), 6);
                }
                catch { /* fall through to the raw value */ }
                return Math.Round(v, 9);
            }
            default: return null;
        }
    }

    public static object EditValue(Parameter p)
    {
        object value = Value(p.Element.Document, p);
        if (value != null && p.StorageType == StorageType.ElementId) return Text(p.AsElementId().Value);
        if (value is Obj fields && fields.TryGetValue("value", out var raw)) return raw;
        return value;
    }

    /// <summary>
    /// An element-id parameter given by name (a level, material, phase, type ...): look among elements of the class the
    /// parameter points to now, then levels, materials, phases and element types.
    /// </summary>
    private static ElementId IdByName(Parameter p, string name, string what)
    {
        var doc = p.Element.Document;
        var classes = new List<Type>();
        if (doc.GetElement(p.AsElementId()) is Element cur) classes.Add(cur.GetType());
        classes.AddRange(new[] { typeof(Level), typeof(Material), typeof(Phase), typeof(ElementType) });
        foreach (var cls in classes.Distinct())
        {
            List<Element> hits;
            try { hits = new FilteredElementCollector(doc).OfClass(cls).Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToList(); }
            catch { continue; }   // OfClass refuses some API classes
            if (hits.Count == 1) return hits[0].Id;
            if (hits.Count > 1)
                throw new ArgumentException($"{what}: '{p.Definition.Name}': more than one {cls.Name} is named '{name}' (ids "
                    + string.Join(", ", hits.Take(5).Select(h => h.Id.Value)) + "); give the id.");
        }
        throw new ArgumentException($"{what}: '{p.Definition.Name}': nothing named '{name}' (looked for a level, material, phase or type); give the name exactly or the id.");
    }

    /// <summary>A number in request units (lengths), m2, m3 or degrees, converted to Revit's internal value for this parameter.</summary>
    public static double ToInternal(Parameter p, double d)
    {
        try
        {
            var spec = p.Definition.GetDataType();
            if (Is(spec, SpecTypeId.Length)) return U.In(d);
            if (Is(spec, SpecTypeId.Area)) return d / U.M2PerFt2;
            if (Is(spec, SpecTypeId.Volume)) return d / U.M3PerFt3;
            if (Is(spec, SpecTypeId.Angle)) return U.Rad(d);
            if (UnitUtils.IsMeasurableSpec(spec)) return UnitUtils.ConvertToInternalUnits(d, p.GetUnitTypeId());
        }
        catch { /* not a measurable spec: keep the raw number */ }
        return d;
    }

    /// <summary>Set a parameter from a JSON value, converting units the same way Value reports them.</summary>
    public static void SetValue(Parameter p, object value, string what)
    {
        if (p.IsReadOnly) throw new InvalidOperationException($"{what}: parameter '{p.Definition.Name}' is read-only.");
        bool ok;
        switch (p.StorageType)
        {
            case StorageType.String:
                ok = p.Set(Text(value));
                break;
            case StorageType.Integer:
                if (value is bool b) ok = p.Set(b ? 1 : 0);
                else if (value is double n) ok = p.Set((int)Math.Round(n));
                else ok = p.SetValueString(Text(value));
                break;
            case StorageType.ElementId:
                ok = p.Set(value == null ? ElementId.InvalidElementId
                    : value is string name && !long.TryParse(name.Trim(), out _) ? IdByName(p, name.Trim(), what)
                    : ToId(value, $"{what}: '{p.Definition.Name}'"));
                break;
            case StorageType.Double:
                if (value is double d) ok = p.Set(ToInternal(p, d));
                else ok = p.SetValueString(Text(value));   // text such as "300 mm" is parsed with the project units
                break;
            default:
                throw new InvalidOperationException($"{what}: parameter '{p.Definition.Name}' cannot be set.");
        }
        if (!ok) throw new InvalidOperationException($"{what}: Revit refused the value '{Text(value)}' for parameter '{p.Definition.Name}'.");
    }

    /// <summary>The named parameters of an element as name -> value; a missing one is looked up on the type, then reported as null.</summary>
    public static Obj Values(Document doc, Element e, List<string> names, Obj details = null)
    {
        var o = new Obj();
        Element type = null;
        foreach (string name in names)
        {
            string target = e is ElementType ? "type" : "instance";
            var p = Find.ParamOrNull(e, name);
            if (p == null && target == "instance")
            {
                type = type ?? doc.GetElement(e.GetTypeId());
                if (type != null) p = Find.ParamOrNull(type, name);
                target = "type";
            }
            o[name] = p == null ? null : Value(doc, p);
            if (details != null) details[name] = p == null ? null : ParameterInfo(p, target);
        }
        return o;
    }
}
