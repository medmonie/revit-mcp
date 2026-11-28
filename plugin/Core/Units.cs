namespace RevitMcpBridge;

/// <summary>
/// Revit stores every length in feet. Claude works in the unit named by the request's 'units' parameter
/// (mm by default): U.In converts a request value to feet, U.Out converts a Revit value back.
/// Areas are always reported in m2, volumes in m3, angles in degrees. Main thread only.
/// </summary>
internal static class U
{
    private const double MaxFeet = 1e5;   // about 30 km: beyond Revit's model extent, so a unit mix-up or a typo

    public static string Name { get; private set; } = "mm";
    private static double _feetPerUnit = 1 / 304.8;
    private static int _digits = 3;

    /// <summary>Called once at the start of every action with its parameters.</summary>
    public static void Set(Obj p)
    {
        Name = Choice(p, "units", "mm", "mm", "cm", "m", "ft", "in");
        switch (Name)
        {
            case "cm": _feetPerUnit = 1 / 30.48; _digits = 4; break;
            case "m": _feetPerUnit = 1 / 0.3048; _digits = 6; break;
            case "ft": _feetPerUnit = 1; _digits = 6; break;
            case "in": _feetPerUnit = 1 / 12.0; _digits = 4; break;
            default: _feetPerUnit = 1 / 304.8; _digits = 3; break;
        }
    }

    // Fixed conversions (independent of the request's 'units').
    public const double MmPerFoot = 304.8;
    public const double MPerFoot = 0.3048;
    public const double M2PerFt2 = 0.09290304;
    public const double M3PerFt3 = 0.028316846592;

    public static double In(double value) => value * _feetPerUnit;
    public static double Out(double feet) => Math.Round(feet / _feetPerUnit, _digits);
    public static double Area(double squareFeet) => Math.Round(squareFeet * M2PerFt2, 4);
    public static double Volume(double cubicFeet) => Math.Round(cubicFeet * M3PerFt3, 4);

    /// <summary>Millimetres to feet: for built-in defaults and tolerances, which are in mm whatever the request units.</summary>
    public static double Mm(double mm) => mm / MmPerFoot;
    /// <summary>Feet to whole millimetres as text ("650"), for type names and size keys.</summary>
    public static string MmText(double feet) => Math.Round(feet * MmPerFoot).ToString(CultureInfo.InvariantCulture);
    /// <summary>Feet to metres, rounded.</summary>
    public static double M(double feet, int digits = 3) => Math.Round(feet * MPerFoot, digits);
    /// <summary>Square metres to square feet.</summary>
    public static double FromM2(double m2) => m2 / M2PerFt2;
    public static double Deg(double radians) => Math.Round(radians * 180 / Math.PI, 6);
    public static double Rad(double degrees) => degrees * Math.PI / 180;

    // ---------------------------------------------------------------- value kinds by spec

    public enum Kind { None, Length, Area, Volume, Angle, Other }
    private static readonly Dictionary<string, Kind> KindCache = new Dictionary<string, Kind>();

    /// <summary>
    /// What a number of this spec measures, from the spec's own unit dimension rather than its name: bar diameter,
    /// rebar cover / spacing / length, section dimensions, displacement ... are all lengths stored in feet, just as
    /// SpecTypeId.Length. Tested with UnitUtils on the spec's valid units, so slope (a ratio) is not taken for an angle.
    /// </summary>
    public static Kind KindOf(ForgeTypeId spec)
    {
        if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return Kind.None;
        if (KindCache.TryGetValue(spec.TypeId, out var k)) return k;
        k = Kind.None;
        try
        {
            if (UnitUtils.IsMeasurableSpec(spec))
            {
                k = Kind.Other;
                var valid = new HashSet<string>(UnitUtils.GetValidUnits(spec).Select(u => u.TypeId));
                bool Same(ForgeTypeId unit, double expected) =>
                    valid.Contains(unit.TypeId) && Math.Abs(UnitUtils.ConvertFromInternalUnits(1, unit) - expected) < 1e-9 * Math.Max(1, expected);
                if (Same(UnitTypeId.Millimeters, MmPerFoot)) k = Kind.Length;
                else if (Same(UnitTypeId.SquareMeters, M2PerFt2)) k = Kind.Area;
                else if (Same(UnitTypeId.CubicMeters, M3PerFt3)) k = Kind.Volume;
                else if (Same(UnitTypeId.Degrees, 180 / Math.PI)) k = Kind.Angle;
            }
        }
        catch { k = Kind.None; }
        KindCache[spec.TypeId] = k;
        return k;
    }

    /// <summary>
    /// An internal value of this spec as Claude sees it: lengths in request units, areas m2, volumes m3, angles degrees,
    /// other measurable values in the unit given (the project's or the field's display unit), else the raw number.
    /// </summary>
    public static double OutSpec(double v, ForgeTypeId spec, ForgeTypeId displayUnit)
    {
        switch (KindOf(spec))
        {
            case Kind.Length: return Out(v);
            case Kind.Area: return Area(v);
            case Kind.Volume: return Volume(v);
            case Kind.Angle: return Deg(v);
            case Kind.Other: if (displayUnit != null) return Math.Round(UnitUtils.ConvertFromInternalUnits(v, displayUnit), 6); break;
        }
        return Math.Round(v, 9);
    }

    /// <summary>The reverse of OutSpec: a number given by Claude converted to Revit's internal value.</summary>
    public static double InSpec(double d, ForgeTypeId spec, ForgeTypeId displayUnit)
    {
        switch (KindOf(spec))
        {
            case Kind.Length: return In(d);
            case Kind.Area: return d / M2PerFt2;
            case Kind.Volume: return d / M3PerFt3;
            case Kind.Angle: return Rad(d);
            case Kind.Other: if (displayUnit != null) return UnitUtils.ConvertToInternalUnits(d, displayUnit); break;
        }
        return d;
    }

    /// <summary>The unit a number of this spec is reported in ("mm", "m2", "m3", "deg", or the display unit's label).</summary>
    public static string UnitLabel(ForgeTypeId spec, ForgeTypeId displayUnit)
    {
        switch (KindOf(spec))
        {
            case Kind.Length: return Name;
            case Kind.Area: return "m2";
            case Kind.Volume: return "m3";
            case Kind.Angle: return "deg";
            case Kind.Other:
                try { return displayUnit != null ? LabelUtils.GetLabelForUnit(displayUnit) : null; } catch { return null; }
        }
        return null;
    }

    /// <summary>The project's display unit for a measurable spec, or null.</summary>
    public static ForgeTypeId ProjectUnit(Document doc, ForgeTypeId spec)
    {
        try { return KindOf(spec) == Kind.Other ? doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId() : null; }
        catch { return null; }
    }

    /// <summary>A length parameter converted to feet, or the default (already in feet) when it is absent.</summary>
    public static double Len(Obj p, string name, double defaultFeet) => Dbl(p, name) is double d ? In(d) : defaultFeet;

    /// <summary>A length parameter in request units, or a default given in mm; returned in feet.</summary>
    public static double LenMm(Obj p, string name, double defaultMm) => Len(p, name, Mm(defaultMm));

    public static double[] Xyz(XYZ p) => new[] { Out(p.X), Out(p.Y), Out(p.Z) };
    public static double[] Xy(XYZ p) => new[] { Out(p.X), Out(p.Y) };

    /// <summary>[x, y] or [x, y, z] in request units; zFeet is used when z is not given.</summary>
    public static XYZ Pt(object v, string what, double zFeet = 0)
    {
        if (!(v is List<object> c) || c.Count < 2 || c.Count > 3) throw new ArgumentException($"{what} must be [x, y] or [x, y, z].");
        double x = In(Number(c[0], what)), y = In(Number(c[1], what));
        double z = c.Count == 3 ? In(Number(c[2], what)) : zFeet;
        if (Math.Abs(x) > MaxFeet || Math.Abs(y) > MaxFeet || Math.Abs(z) > MaxFeet)
            throw new ArgumentException($"{what} is implausibly far from the origin ({Text(c[0])}, {Text(c[1])} {Name}). Check the units.");
        return new XYZ(x, y, z);
    }

    public static XYZ NeedPt(Obj p, string name, double zFeet = 0) =>
        Has(p, name) ? Pt(p[name], $"'{name}'", zFeet) : throw new ArgumentException($"Missing point parameter '{name}'.");

    public static List<XYZ> PtList(Obj p, string name, int min, int max, double zFeet = 0)
    {
        var list = NeedArr(p, name, max).Select((v, i) => Pt(v, $"'{name}' point {i + 1}", zFeet)).ToList();
        if (list.Count < min) throw new ArgumentException($"'{name}' needs at least {min} points.");
        return list;
    }

    /// <summary>
    /// A straight segment {start, end} or, when the item has 'mid', an arc through start, mid and end.
    /// zFeet is used for points given as [x, y]; flatten puts all three points at zFeet (walls, grids).
    /// </summary>
    public static Curve Curve(Obj item, string what, double zFeet, bool flatten = false)
    {
        XYZ a = NeedPt(item, "start", zFeet), b = NeedPt(item, "end", zFeet);
        if (flatten) { a = new XYZ(a.X, a.Y, zFeet); b = new XYZ(b.X, b.Y, zFeet); }
        if (!Has(item, "mid")) return Segment(a, b, what);
        XYZ m = Pt(item["mid"], $"{what} 'mid'", zFeet);
        if (flatten) m = new XYZ(m.X, m.Y, zFeet);
        if (a.DistanceTo(b) < 0.003) throw new ArgumentException($"{what}: start and end are the same point.");
        // 'mid' must not lie on the straight line start-end, or there is no arc.
        var ab = b - a;
        double offLine = ab.CrossProduct(m - a).GetLength() / ab.GetLength();
        if (offLine < 0.003) throw new ArgumentException($"{what}: 'mid' lies on the straight line from start to end, so it defines no arc. Leave 'mid' out for a straight element.");
        return Arc.Create(a, b, m);
    }

    /// <summary>Start, end and (for arcs) a point halfway along, in request units.</summary>
    public static Obj CurveOut(Curve c)
    {
        var o = new Obj { ["start"] = Xyz(c.GetEndPoint(0)), ["end"] = Xyz(c.GetEndPoint(1)), ["length"] = Out(c.Length) };
        if (c is Arc arc)
        {
            o["mid"] = Xyz(c.Evaluate(0.5, true));
            o["radius"] = Out(arc.Radius);
            o["curved"] = true;
        }
        else if (!(c is Line)) o["curved"] = true;
        return o;
    }

    /// <summary>A straight segment; fails with a clear message when the two points coincide.</summary>
    public static Line Segment(XYZ a, XYZ b, string what)
    {
        if (a.DistanceTo(b) < 0.003) throw new ArgumentException($"{what}: start and end are the same point (or closer than 1 mm).");
        return Line.CreateBound(a, b);
    }
}
