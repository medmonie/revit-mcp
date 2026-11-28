using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RevitMcpBridge;

/// <summary>
/// Small JSON reader and writer with no dependencies, so the same DLL source works in Revit 2024 (.NET Framework,
/// no System.Text.Json) and Revit 2027 (.NET 10) and can never clash with another add-in's JSON library.
/// Reading gives Dictionary&lt;string, object&gt; (Obj), List&lt;object&gt;, string, double, bool or null.
/// Writing accepts those plus numbers, enums, dates, ElementId and plain objects (public properties).
/// </summary>
internal static class Json
{
    private const int MaxDepth = 64;
    private static readonly Regex NumberToken = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?",
        RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ read

    public static object Parse(string text)
    {
        int i = 0;
        object value = ReadValue(text, ref i, 0);
        SkipWs(text, ref i);
        if (i != text.Length) throw new FormatException("unexpected text after the JSON value");
        return value;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    private static object ReadValue(string s, ref int i, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("JSON is nested too deeply");
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("unexpected end of JSON");
        char c = s[i];
        if (c == '{')
        {
            var obj = new Obj(StringComparer.Ordinal);
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected a property name");
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                i++;
                if (obj.ContainsKey(key)) throw new FormatException("duplicate property name '" + key + "'");
                obj.Add(key, ReadValue(s, ref i, depth + 1));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                throw new FormatException("expected ',' or '}'");
            }
        }
        if (c == '[')
        {
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(ReadValue(s, ref i, depth + 1));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list; }
                throw new FormatException("expected ',' or ']'");
            }
        }
        if (c == '"') return ReadString(s, ref i);
        if (Word(s, ref i, "true")) return true;
        if (Word(s, ref i, "false")) return false;
        if (Word(s, ref i, "null")) return null;

        var token = NumberToken.Match(s, i);
        if (!token.Success || !double.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            || double.IsInfinity(d))
            throw new FormatException("invalid or unsupported JSON number at position " + i);
        i += token.Length;
        return d;
    }

    private static bool Word(string s, ref int i, string word)
    {
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
        i += word.Length;
        return true;
    }

    private static string ReadString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;   // opening quote
        while (true)
        {
            if (i >= s.Length) throw new FormatException("unterminated string");
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c < ' ') throw new FormatException("unescaped control character");
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) throw new FormatException("unterminated string");
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length || !ushort.TryParse(s.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
                        throw new FormatException("invalid \\u escape");
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: throw new FormatException("invalid escape \\" + e);
            }
        }
    }

    // ------------------------------------------------------------------ write

    private static readonly Dictionary<Type, PropertyInfo[]> Props = new Dictionary<Type, PropertyInfo[]>();

    public static string Write(object value)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value, 0);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object v, int depth)
    {
        if (depth > MaxDepth) throw new InvalidOperationException("result is nested too deeply");
        switch (v)
        {
            case null: sb.Append("null"); return;
            case string s: WriteString(sb, s); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case double d: WriteDouble(sb, d); return;
            case float f: WriteDouble(sb, f); return;
            case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            case char ch: WriteString(sb, ch.ToString()); return;
            case DateTime t: WriteString(sb, t.ToString("o", CultureInfo.InvariantCulture)); return;
            case Enum en: WriteString(sb, en.ToString()); return;
            case ElementId id: sb.Append(id.Value.ToString(CultureInfo.InvariantCulture)); return;
            case IDictionary dict:
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, kv.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }
            case IEnumerable seq:
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }
        }

        // A plain object (usually an anonymous type): its public properties.
        PropertyInfo[] props;
        Type type = v.GetType();
        lock (Props)
        {
            if (!Props.TryGetValue(type, out props))
            {
                props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0).ToArray();
                Props[type] = props;
            }
        }
        sb.Append('{');
        for (int k = 0; k < props.Length; k++)
        {
            if (k > 0) sb.Append(',');
            WriteString(sb, props[k].Name);
            sb.Append(':');
            WriteValue(sb, props[k].GetValue(v, null), depth + 1);
        }
        sb.Append('}');
    }

    // Revit returns NaN/Infinity now and then; JSON has no such numbers, so they are written as text.
    private static void WriteDouble(StringBuilder sb, double d)
    {
        if (double.IsNaN(d)) sb.Append("\"NaN\"");
        else if (double.IsInfinity(d)) sb.Append(d > 0 ? "\"Infinity\"" : "\"-Infinity\"");
        else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
