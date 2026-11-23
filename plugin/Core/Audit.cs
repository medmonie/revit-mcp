namespace RevitMcpBridge;

/// <summary>Append-only record of every permission decision and every edit batch. Best effort: never blocks work.</summary>
internal static class Audit
{
    private const long MaxBytes = 5 * 1024 * 1024;

    public static string FilePath => IOPath.Combine(BridgeInfo.DataFolder, "audit.log");

    public static void Write(string action, object data)
    {
        try
        {
            Directory.CreateDirectory(BridgeInfo.DataFolder);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                string old = IOPath.ChangeExtension(FilePath, ".1.log");   // keep one previous file
                if (File.Exists(old)) File.Delete(old);
                File.Move(FilePath, old);
            }
            File.AppendAllText(FilePath, Json.Write(new { utc = DateTime.UtcNow, action, data }) + "\n");
        }
        catch { /* audit must never break an operation */ }
    }
}
