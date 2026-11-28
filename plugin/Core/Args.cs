namespace RevitMcpBridge;

/// <summary>Reading request parameters. Every accessor validates; nothing here touches Revit except ElementId.</summary>
internal static class Args
{
    // Which keys of a request object a handler asked for. Unread keys that the catalogue does not list either are
    // reported to the caller as ignored (a misspelled option), see BridgeServer.Handle. Entries go with their object.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Obj, HashSet<string>> Seen =
        new System.Runtime.CompilerServices.ConditionalWeakTable<Obj, HashSet<string>>();

    private static bool TryGet(Obj p, string name, out object value)
    {
        value = null;
        if (p == null) return false;
        var seen = Seen.GetOrCreateValue(p);
        lock (seen) seen.Add(name);
        return p.TryGetValue(name, out value) && value != null;
    }

    /// <summary>The keys of a request object that no accessor of this class was asked for.</summary>
    public static List<string> Unread(Obj p)
    {
        var unread = new List<string>();
        if (p == null) return unread;
        var seen = Seen.GetOrCreateValue(p);
        lock (seen)
            foreach (string key in p.Keys)
                if (!seen.Contains(key)) unread.Add(key);
        return unread;
    }

    public static bool Has(Obj p, string name) => TryGet(p, name, out _);

    /// <summary>A JSON value as text: 150 -> "150", true -> "true".</summary>
    public static string Text(object v)
    {
        if (v == null) return "";
        if (v is string s) return s;
        if (v is bool b) return b ? "true" : "false";
        if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- text

    public static string Str(Obj p, string name) => TryGet(p, name, out var v) && v is string s ? s : null;

    public static string NeedStr(Obj p, string name)
    {
        string s = Str(p, name);
        if (string.IsNullOrEmpty(s)) throw new ArgumentException($"Missing text parameter '{name}'.");
        return s;
    }

    /// <summary>A name or an id: text as it is, a number as its whole-number text; null when absent.</summary>
    public static string Ref(Obj p, string name) => TryGet(p, name, out var v) ? Text(v) : null;

    public static string NeedRef(Obj p, string name)
    {
        string s = Ref(p, name);
        if (string.IsNullOrEmpty(s)) throw new ArgumentException($"Missing parameter '{name}'.");
        return s;
    }

    /// <summary>One of a fixed set of words (case-insensitive); returns it in lower case.</summary>
    public static string Choice(Obj p, string name, string dflt, params string[] allowed)
    {
        string raw = Has(p, name) ? NeedStr(p, name) : dflt;
        if (raw == null) throw new ArgumentException($"'{name}' is required: one of {string.Join(", ", allowed)}.");
        string value = raw.ToLowerInvariant();
        if (!allowed.Contains(value)) throw new ArgumentException($"'{name}' must be one of: {string.Join(", ", allowed)}.");
        return value;
    }

    // ---------------------------------------------------------------- numbers and flags

    public static double Number(object v, string what)
    {
        if (!(v is double d)) throw new ArgumentException($"{what} must be a number.");
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException($"{what} is not a finite number.");
        return d;
    }

    public static double? Dbl(Obj p, string name) => TryGet(p, name, out var v) ? Number(v, $"Parameter '{name}'") : (double?)null;

    public static int IntegerValue(double value, string what)
    {
        if (!(value >= int.MinValue && value <= int.MaxValue && value == Math.Truncate(value)))
            throw new ArgumentException($"{what} must be a 32-bit integer.");
        return (int)value;
    }

    public static double NeedDbl(Obj p, string name) =>
        Dbl(p, name) ?? throw new ArgumentException($"Missing numeric parameter '{name}'.");

    public static int Int(Obj p, string name, int dflt, int min, int max)
    {
        double? d = Dbl(p, name);
        if (d == null) return dflt;
        // A fraction is a mistake, not something to round away. A value outside min..max is still brought to the
        // nearest bound: the catalogue states these limits ("at most 200") and results report what was used.
        if (d.Value != Math.Truncate(d.Value)) throw new ArgumentException($"Parameter '{name}' must be a whole number.");
        return (int)Math.Max(min, Math.Min(max, d.Value));
    }

    public static bool? Bool(Obj p, string name)
    {
        if (!TryGet(p, name, out var v)) return null;
        if (v is bool b) return b;
        throw new ArgumentException($"Parameter '{name}' must be true or false.");
    }

    public static bool Flag(Obj p, string name, bool dflt) => Bool(p, name) ?? dflt;

    // ---------------------------------------------------------------- lists and objects

    public static List<object> Arr(Obj p, string name, int max)
    {
        if (!TryGet(p, name, out var v)) return new List<object>();
        if (!(v is List<object> list)) throw new ArgumentException($"Parameter '{name}' must be a list.");
        if (list.Count > max) throw new ArgumentException($"'{name}' has {list.Count} items; the limit is {max} per call.");
        return list;
    }

    public static List<object> NeedArr(Obj p, string name, int max)
    {
        var a = Arr(p, name, max);
        if (a.Count == 0) throw new ArgumentException($"'{name}' must be a non-empty list.");
        return a;
    }

    /// <summary>A list whose items are all objects, e.g. the operations of an edit or the beams of beam_create.</summary>
    public static List<Obj> ObjList(Obj p, string name, int max, bool required = true)
    {
        var a = required ? NeedArr(p, name, max) : Arr(p, name, max);
        var list = new List<Obj>();
        for (int i = 0; i < a.Count; i++)
            list.Add(a[i] as Obj ?? throw new ArgumentException($"'{name}' item {i + 1} must be an object {{...}}."));
        return list;
    }

    public static Obj Sub(Obj p, string name)
    {
        if (!TryGet(p, name, out var v)) return null;
        return v as Obj ?? throw new ArgumentException($"Parameter '{name}' must be an object {{...}}.");
    }

    public static List<string> StrList(Obj p, string name, int max, bool required = true)
    {
        var list = new List<string>();
        foreach (object v in required ? NeedArr(p, name, max) : Arr(p, name, max))
        {
            // names and ids: text, or a number / flag written as text; an object or a list here is a malformed request
            if (v is Obj || v is List<object> || v == null)
                throw new ArgumentException($"'{name}' must be a list of names or numbers.");
            list.Add(Text(v));
        }
        return list;
    }

    /// <summary>Element ids given as numbers (or numeric text).</summary>
    public static List<ElementId> Ids(Obj p, string name, int max = 5000, bool required = true)
    {
        var list = new List<ElementId>();
        foreach (object v in required ? NeedArr(p, name, max) : Arr(p, name, max)) list.Add(ToId(v, name));
        return list;
    }

    public static ElementId ToId(object v, string what)
    {
        if (v is double d && Math.Abs(d) <= 9007199254740991d && d == Math.Truncate(d)) return new ElementId((long)d);
        if (v is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return new ElementId(n);
        throw new ArgumentException($"{what}: '{Text(v)}' is not an element id (a whole number).");
    }

    public static ElementId NeedId(Obj p, string name) =>
        TryGet(p, name, out var v) ? ToId(v, name) : throw new ArgumentException($"Missing parameter '{name}' (an element id).");

    public static string ErrorText(Exception ex)
    {
        while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
        bool plain = ex is ArgumentException || ex is InvalidOperationException || ex is TimeoutException || ex is FormatException;
        return plain ? ex.Message : ex.GetType().Name + ": " + ex.Message;
    }
}
