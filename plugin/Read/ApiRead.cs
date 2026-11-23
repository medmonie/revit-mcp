using System.Reflection;

namespace RevitMcpBridge;

/// <summary>Developer aid: look at the Revit API that is actually loaded in this Revit (it differs between releases).</summary>
internal static class ApiRead
{
    private static IEnumerable<Assembly> RevitAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase));

    private static string Short(Type t)
    {
        if (t.IsGenericType)
            return t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Short)) + ">";
        return t.Name;
    }

    public static object DescribeApi(Rc rc, Obj p)
    {
        string name = NeedStr(p, "type_name");
        string contains = Str(p, "contains");
        int limit = Int(p, "limit", 300, 1, 2000);

        var matches = new List<Type>();
        foreach (var asm in RevitAssemblies())
        {
            var exact = asm.GetType(name, false, true);
            if (exact != null) { matches.Clear(); matches.Add(exact); break; }
            Type[] types;
            try { types = asm.GetExportedTypes(); } catch { continue; }
            matches.AddRange(types.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)));
        }
        if (matches.Count == 0) throw new ArgumentException($"No Revit API type named '{name}'. Give the full name, e.g. Autodesk.Revit.DB.Wall.");
        if (matches.Count > 1)
            return new { ambiguous = matches.Select(t => t.FullName).ToList(), note = "Several types have that short name; call again with the full name." };

        var type = matches[0];
        if (type.IsEnum)
        {
            var values = Enum.GetNames(type).Where(n => contains == null || n.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return new { type = type.FullName, kind = "enum", count = values.Count, truncated = values.Count > limit, values = values.Take(limit).ToList() };
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var members = new List<string>();
        foreach (var c in type.GetConstructors())
            members.Add("new " + type.Name + "(" + string.Join(", ", c.GetParameters().Select(a => Short(a.ParameterType) + " " + a.Name)) + ")");
        foreach (var pr in type.GetProperties(flags))
            members.Add(Short(pr.PropertyType) + " " + pr.Name + " { " + (pr.CanRead ? "get; " : "") + (pr.CanWrite ? "set; " : "") + "}");
        foreach (var m in type.GetMethods(flags).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)))
            members.Add((m.IsStatic ? "static " : "") + Short(m.ReturnType) + " " + m.Name + "(" +
                        string.Join(", ", m.GetParameters().Select(a => (a.IsOut ? "out " : "") + Short(a.ParameterType) + " " + a.Name)) + ")");
        if (contains != null) members = members.Where(s => s.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        members.Sort(StringComparer.OrdinalIgnoreCase);
        return new
        {
            type = type.FullName, baseType = type.BaseType?.FullName, revitVersion = rc.App.Application.VersionNumber,
            count = members.Count, truncated = members.Count > limit, members = members.Take(limit).ToList(),
        };
    }
}
