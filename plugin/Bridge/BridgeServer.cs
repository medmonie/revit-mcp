using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace RevitMcpBridge;

/// <summary>
/// Localhost-only, newline-delimited JSON protocol (the same as the Civil 3D bridge).
///   request : {"id":"..","token":"..","method":"list_levels","params":{...}}
///   response: {"id":"..","ok":true,"result":{...}}  or  {"id":"..","ok":false,"error":".."}
/// The port is chosen by the OS and, together with a random per-session token, written to
/// %LOCALAPPDATA%\RevitMcpBridge\sessions\<pid>.json (one per running Revit). bridge.json names the default Revit:
/// the first one started, until MCP Status is pressed in another; when the default closes it hands over to another
/// open Revit. The MCP server reads these files on every call, so no configuration is needed and processes without
/// access to the files cannot call in.
/// </summary>
internal static class BridgeServer
{
    // How long a request may WAIT for Revit's main thread. Once a handler has started it is never abandoned,
    // so this is not a limit on how long an edit or the permission dialog may take.
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRequestChars = 4 * 1024 * 1024;        // 4 MB per request line (large edit batches)
    private const int IdleTimeoutMs = 10 * 60 * 1000;           // idle connections are dropped
    private const int MaxConnections = 16;
    private static int _connections;
    private static readonly int Pid = CurrentPid();

    private static int CurrentPid()
    {
        using (var p = Process.GetCurrentProcess()) return p.Id;
    }

    private static TcpListener _listener;
    private static volatile bool _running;
    private static string _token = "";
    private static string _revitVersion = "";
    private static DateTime _startedUtc;

    public static int Port => _listener?.LocalEndpoint is IPEndPoint ep ? ep.Port : 0;

    private static string BridgeFile => IOPath.Combine(BridgeInfo.DataFolder, "bridge.json");

    /// <summary>One file per running Revit, so Claude can list them and pick one (2024 and 2027 open together).</summary>
    private static string SessionFile => IOPath.Combine(BridgeInfo.DataFolder, "sessions", Pid + ".json");

    public static void Start(string revitVersion)
    {
        if (_listener != null) return;
        _revitVersion = revitVersion;
        _startedUtc = DateTime.UtcNow;

        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        _token = BitConverter.ToString(bytes).Replace("-", "");

        _listener = new TcpListener(IPAddress.Loopback, 0);   // 127.0.0.1 only, never 0.0.0.0
        _listener.Start();
        _running = true;
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(SessionFile));
            RemoveStaleSessions();
            Publish(SessionFile);
        }
        catch { /* listing sessions is a convenience */ }
        // A second Revit must not take over from one that is already open: Claude would silently switch models.
        // A lock held too long by another Revit must not stop this one's bridge: MCP Status can still make it the default.
        try { UpdateDefault(() => { if (!BridgeFileOwnerRunning()) Publish(BridgeFile); }); }
        catch (TimeoutException) { }

        new Thread(AcceptLoop) { IsBackground = true, Name = "RevitMcpBridge accept" }.Start();
    }

    /// <summary>Publishes this Revit as the one Claude talks to (also done by the MCP Status button when several are open).</summary>
    public static void WriteBridgeFile()
    {
        if (_listener == null) return;
        UpdateDefault(() => Publish(BridgeFile));
    }

    private static void UpdateDefault(Action update)
    {
        Directory.CreateDirectory(BridgeInfo.DataFolder);
        using (var user = System.Security.Principal.WindowsIdentity.GetCurrent())
        using (var mutex = new Mutex(false, @"Global\RevitMcpBridge.Default." + user.User.Value))
        {
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new TimeoutException("Another Revit process is updating the default bridge.");
            try { update(); }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static void Publish(string path)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, Connection());
            if (File.Exists(path)) File.Replace(temp, path, null);
            else
            {
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { File.Replace(temp, path, null); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Connection() => Json.Write(new Obj
    {
        ["port"] = Port, ["token"] = _token, ["pid"] = Pid,
        ["revit"] = _revitVersion, ["startedUtc"] = _startedUtc, ["protocol"] = 4,
    });

    private static int? FilePid(string path)
    {
        try
        {
            return Json.Parse(File.ReadAllText(path)) is Obj o && o.TryGetValue("pid", out var pid) && pid is double d
                ? (int)d : (int?)null;
        }
        catch { return null; }
    }

    private static bool BridgeFileIsMine() => FilePid(BridgeFile) == Pid;

    /// <summary>True when the process is a running Revit (a pid of a closed Revit may be reused by another program).</summary>
    private static bool RevitRunning(int pid)
    {
        try
        {
            using (var p = Process.GetProcessById(pid))
                return !p.HasExited && string.Equals(p.ProcessName, "Revit", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool BridgeFileOwnerRunning()
    {
        int? pid = File.Exists(BridgeFile) ? FilePid(BridgeFile) : null;
        return pid.HasValue && pid.Value != Pid && RevitRunning(pid.Value);
    }

    /// <summary>Session files left by a Revit that crashed or was killed (OnShutdown never ran).</summary>
    private static void RemoveStaleSessions()
    {
        foreach (string f in Directory.GetFiles(IOPath.GetDirectoryName(SessionFile), "*.json"))
        {
            if (!int.TryParse(IOPath.GetFileNameWithoutExtension(f), out int pid) || RevitRunning(pid)) continue;
            try { File.Delete(f); } catch { /* another Revit may be cleaning up too */ }
        }
    }

    /// <summary>When the default Revit closes, make another open one the default instead of leaving none.</summary>
    private static void HandOver()
    {
        string folder = IOPath.GetDirectoryName(SessionFile);
        int me = Pid;
        if (Directory.Exists(folder))
        {
            foreach (string f in Directory.GetFiles(folder, "*.json").OrderBy(File.GetLastWriteTimeUtc))
            {
                if (!int.TryParse(IOPath.GetFileNameWithoutExtension(f), out int pid) || pid == me || !RevitRunning(pid)) continue;
                string temp = BridgeFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(f, temp, true);
                    File.Replace(temp, BridgeFile, null);
                    return;
                }
                catch { /* try the next one */ }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }
        File.Delete(BridgeFile);
    }

    public static void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { /* shutting down */ }
        _listener = null;
        try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { /* best effort */ }
        try { UpdateDefault(() => { if (BridgeFileIsMine()) HandOver(); }); } catch { /* best effort */ }
    }

    public static string Describe() => _listener == null
        ? "Revit MCP bridge: not running."
        : $"Revit MCP bridge v{BridgeInfo.Version} (Revit {_revitVersion}) is listening on 127.0.0.1:{Port}.\n" +
          $"Editing by Claude: {(WriteGate.Granted ? "ALLOWED for this Revit session" : "not allowed yet")}.\n" +
          (BridgeFileIsMine() ? "This Revit is Claude's default. With several Revit sessions open, Claude still asks which one to edit (select_revit).\n"
                              : "Another open Revit is Claude's default.\n") +
          $"Connection details: {BridgeFile}\nAudit log: {Audit.FilePath}";

    private const int MaxAcceptFailures = 50;   // in a row

    private static void AcceptLoop()
    {
        var listener = _listener;
        int failures = 0;
        while (_running && listener != null)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
                failures = 0;
            }
            catch (SocketException ex) when (_running && failures < MaxAcceptFailures)
            {
                // A single connection that failed before it was accepted (reset by the caller, no buffers for a moment)
                // must not end the bridge for the rest of the Revit session: note it, wait briefly, keep listening.
                failures++;
                if (failures == 1) Audit.Write("bridge_accept_error", new { error = ex.SocketErrorCode.ToString() });
                Thread.Sleep(Math.Min(1000, 20 * failures));
                continue;
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    // Not a shutdown: stop cleanly, so the MCP server reports "not reachable" at once instead of
                    // waiting on a port that still accepts connections but is no longer served.
                    Audit.Write("bridge_stopped", new { error = ErrorText(ex) });
                    Stop();
                }
                break;
            }
            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                // say why instead of dropping the connection: the caller can retry when Revit has caught up.
                // The request is drained before the socket closes, so the reply is not lost to a reset.
                var busy = client;
                new Thread(() =>
                {
                    try
                    {
                        using (busy)
                        {
                            var s = busy.GetStream();
                            s.WriteTimeout = 2000;
                            s.ReadTimeout = 2000;
                            byte[] reply = new UTF8Encoding(false).GetBytes(Error(null,
                                $"Revit is busy: {MaxConnections} bridge connections are already open (a command or dialog may be holding Revit). Nothing was run; retry in a moment.") + "\n");
                            s.Write(reply, 0, reply.Length);
                            s.Flush();
                            busy.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                            var sink = new byte[8192];
                            int total = 0, n;
                            while (total < 4 * 1024 * 1024 && (n = s.Read(sink, 0, sink.Length)) > 0) total += n;
                        }
                    }
                    catch { /* the caller went away */ }
                }) { IsBackground = true, Name = "RevitMcpBridge busy" }.Start();
                continue;
            }
            try
            {
                var thread = new Thread(() =>
                {
                    try { HandleClient(client); }
                    finally { Interlocked.Decrement(ref _connections); }
                }) { IsBackground = true, Name = "RevitMcpBridge client" };
                thread.Start();
            }
            catch (Exception)
            {
                // The thread never started (out of memory / handles): give the slot and the socket back.
                Interlocked.Decrement(ref _connections);
                client.Close();
            }
        }
    }

    private static void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = IdleTimeoutMs;
                stream.WriteTimeout = IdleTimeoutMs;
                var utf8 = new UTF8Encoding(false);
                var reader = new StreamReader(stream, utf8, false, 16 * 1024);
                string line;
                try
                {
                    while (_running && (line = ReadLine(reader)) != null)
                    {
                        if (line.Length == 0) continue;
                        byte[] reply = utf8.GetBytes(Handle(line) + "\n");
                        stream.Write(reply, 0, reply.Length);
                        stream.Flush();
                    }
                }
                catch (InvalidDataException)
                {
                    // Tell the caller why instead of dropping the connection; the rest of the line is not read.
                    byte[] reply = utf8.GetBytes(Error(null, $"request too large (limit {MaxRequestChars / (1024 * 1024)} MB per request); split the batch") + "\n");
                    stream.Write(reply, 0, reply.Length);
                    stream.Flush();
                }
            }
        }
        catch { /* client went away, sent too much or was idle too long; nothing to report */ }
    }

    /// <summary>One request line, refusing anything longer than MaxRequestChars. Null when the connection closed.</summary>
    private static string ReadLine(StreamReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int c = reader.Read();
            if (c < 0) return null;
            if (c == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)c);
            if (sb.Length > MaxRequestChars) throw new InvalidDataException("request too large");
        }
    }

    private static bool SameToken(string supplied)
    {
        // Constant-time comparison.
        byte[] a = Encoding.UTF8.GetBytes(supplied ?? ""), b = Encoding.UTF8.GetBytes(_token);
        int diff = a.Length ^ b.Length;
        for (int i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static string Handle(string line)
    {
        string reqId = null;
        string method;
        Obj args;
        try
        {
            if (!(Json.Parse(line) is Obj root)) return Error(reqId, "invalid JSON request");
            if (root.TryGetValue("id", out var id)) reqId = Text(id);
            if (!SameToken(Str(root, "token"))) return Error(reqId, "unauthorized");
            method = Str(root, "method") ?? "";
            args = Sub(root, "params") ?? new Obj();
        }
        catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
        {
            return Error(reqId, "invalid JSON request");
        }

        if (!Registry.TryGet(method, out var action))
            return Error(reqId, $"unknown method '{method}'. Call list_actions for the available ones.");

        try
        {
            bool edit = action.Kind == "write";
            object RunOnMainThread(Action started) => MainThread.Run(app =>
            {
                var rc = new Rc(app) { Started = started };
                DocumentTarget.Verify(rc, args, required: edit);   // a wrong model is refused before the edit counts as running
                if (action.Kind != "write") started?.Invoke();      // write actions mark it themselves, after their own checks
                return action.Run(rc, args);
            }, QueueTimeout);

            object result = !action.MainThread ? action.Run(null, args)
                : edit ? EditReceipts.Run(args, RunOnMainThread) : RunOnMainThread(null);
            var reply = new Obj { ["id"] = reqId, ["ok"] = true, ["result"] = result };
            var unread = UnreadKeys(args);
            if (unread != null) reply["unread"] = unread;   // outside "result": no result shape changes
            return Json.Write(reply);
        }
        catch (Exception ex)
        {
            return Error(reqId, ErrorText(ex));
        }
    }

    /// <summary>
    /// Keys of the request (and of each edit operation) that the handler never asked for. The MCP server reports the
    /// ones its catalogue does not know either as ignoredParams, so a misspelled option is no longer silent.
    /// </summary>
    private static Obj UnreadKeys(Obj args)
    {
        try
        {
            var found = new Obj();
            var top = Unread(args);
            if (top.Count > 0) found["params"] = top;
            if (args.TryGetValue("operations", out var raw) && raw is List<object> ops)
            {
                var byOperation = new Obj();
                for (int i = 0; i < ops.Count; i++)
                    if (ops[i] is Obj op && Unread(op) is List<string> keys && keys.Count > 0)
                        byOperation[(i + 1).ToString(CultureInfo.InvariantCulture)] = keys;
                if (byOperation.Count > 0) found["operations"] = byOperation;
            }
            return found.Count > 0 ? found : null;
        }
        catch { return null; }   // a hint only: never fail a finished request over it
    }

    private static string Error(string id, string message) =>
        Json.Write(new Obj { ["id"] = id, ["ok"] = false, ["error"] = message });
}
