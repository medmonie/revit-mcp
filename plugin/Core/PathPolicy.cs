namespace RevitMcpBridge;

/// <summary>
/// Where Claude may read and write files on this PC. Optional: without the file every full path is accepted, as before.
/// %LOCALAPPDATA%\RevitMcpBridge\allowed_roots.txt lists one folder per line (# starts a comment); with at least one
/// folder listed, every path a request names must lie inside one of them. The MCP server applies the same file to the
/// workbooks it writes (server/xlsx_util.py). Device paths (\\?\ and \\.\) are never accepted.
/// </summary>
internal static class PathPolicy
{
    public static string FilePath => IOPath.Combine(BridgeInfo.DataFolder, "allowed_roots.txt");

    public static List<string> Roots()
    {
        var roots = new List<string>();
        try
        {
            if (!File.Exists(FilePath)) return roots;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                roots.Add(WithSeparator(IOPath.GetFullPath(System.Environment.ExpandEnvironmentVariables(line))));
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            // An unreadable or malformed list must not open everything up.
            throw new InvalidOperationException($"The allowed folders list {FilePath} cannot be read: {ex.Message}");
        }
        return roots;
    }

    private static string WithSeparator(string folder) =>
        folder.EndsWith(IOPath.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? folder : folder + IOPath.DirectorySeparatorChar;

    /// <summary>fullPath as given, or an error naming the argument when it is a device path or outside the allowed folders.</summary>
    public static string Check(string fullPath, string argument)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException($"'{argument}' must be an ordinary file path, not a device path.");
        var roots = Roots();
        if (roots.Count == 0) return fullPath;
        roots.Add(WithSeparator(DocumentActions.ScratchFolder));   // the bridge's own scratch projects
        string candidate = WithSeparator(fullPath);
        foreach (string root in roots)
            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return fullPath;
        throw new ArgumentException($"'{argument}' is outside the folders Claude may use on this PC. Allowed: " +
            string.Join("; ", roots.Take(roots.Count - 1).Select(r => r.TrimEnd(IOPath.DirectorySeparatorChar))) +
            $". To allow another folder, add it to {FilePath}.");
    }
}
