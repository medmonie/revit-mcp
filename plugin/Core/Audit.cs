namespace RevitMcpBridge;

/// <summary>
/// Append-only record of every permission decision and every edit batch. Never blocks work, but never silent either:
/// several Revit processes share the file, so rotation and append run under a named (per-user) mutex, and a write
/// that fails is counted and reported (Health) separately from the model's own transaction result.
/// </summary>
internal static class Audit
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly Mutex Gate = CreateGate();
    private static int _failures;
    private static string _lastError;
    private static DateTime? _lastErrorUtc;

    public static string FilePath => IOPath.Combine(BridgeInfo.DataFolder, "audit.log");

    private static Mutex CreateGate()
    {
        try { return new Mutex(false, @"Local\RevitMcpBridge.audit"); }
        catch { return null; }   // falls back to the in-process lock only (reported in Health)
    }

    public static object Health => new
    {
        file = FilePath,
        healthy = _failures == 0,
        failedWrites = _failures,
        lastError = _lastError,
        lastErrorUtc = _lastErrorUtc,
        sharedLock = Gate != null,
    };

    /// <summary>True when the entry was written. A false never changes the result of the work it records.</summary>
    public static bool Write(string action, object data)
    {
        string line;
        try { line = Json.Write(new { utc = DateTime.UtcNow, pid = BridgeInfo.ProcessId, action, data }) + "\n"; }
        catch (Exception ex) { return Failed("could not serialise the entry: " + ex.Message); }
        bool owned = false;
        try
        {
            lock (FilePathLock)
            {
                if (Gate != null)
                {
                    try { owned = Gate.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { owned = true; }   // another Revit died holding it: the file is still usable
                    if (!owned) return Failed("another Revit held the audit log lock for more than 5 s");
                }
                Directory.CreateDirectory(BridgeInfo.DataFolder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = IOPath.ChangeExtension(FilePath, ".1.log");   // keep one previous file
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                return true;
            }
        }
        catch (Exception ex) { return Failed(ex.Message); }
        finally
        {
            if (owned) { try { Gate.ReleaseMutex(); } catch { /* not owned any more */ } }
        }
    }

    private static readonly object FilePathLock = new object();

    private static bool Failed(string why)
    {
        Interlocked.Increment(ref _failures);
        _lastError = why;
        _lastErrorUtc = DateTime.UtcNow;
        return false;
    }
}
