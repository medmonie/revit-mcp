using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace RevitMcpBridge;

/// <summary>
/// The Revit API may only be called from Revit's own thread, inside a "valid API context". Socket threads therefore
/// never touch it: they queue the work and raise an ExternalEvent; Revit calls Execute on its main thread when it is
/// idle (no command running, no dialog open), and the work runs there.
/// </summary>
internal sealed class MainThread : IExternalEventHandler
{
    private static readonly ConcurrentQueue<Action<UIApplication>> Queue = new ConcurrentQueue<Action<UIApplication>>();
    private static ExternalEvent _event;

    public static bool Ready => _event != null;

    /// <summary>Must be called on Revit's main thread (from IExternalApplication.OnStartup).</summary>
    public static void Init() => _event = ExternalEvent.Create(new MainThread());

    public static void Shutdown()
    {
        try { _event?.Dispose(); } catch { /* shutting down */ }
        _event = null;
    }

    public void Execute(UIApplication app)
    {
        while (Queue.TryDequeue(out var work))
        {
            try { work(app); } catch { /* each item reports its own error to its caller */ }
        }
    }

    public string GetName() => "Revit MCP bridge";

    /// <summary>Queue work on Revit's main thread without waiting for it (callable from the main thread too).</summary>
    public static void Post(Action<UIApplication> work)
    {
        var ev = _event ?? throw new InvalidOperationException("The bridge is not initialised.");
        int state = 0;
        Queue.Enqueue(app =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) == 0) work(app);
        });
        try { Raise(ev); }
        catch when (Interlocked.CompareExchange(ref state, 2, 0) != 0) { /* the work already started */ }
    }

    private static void Raise(ExternalEvent ev)
    {
        var status = ev.Raise();
        if (status != ExternalEventRequest.Accepted && status != ExternalEventRequest.Pending)
            throw new InvalidOperationException($"Revit rejected the bridge event ({status}). Finish the active command in Revit and retry.");
    }

    /// <summary>Run work on Revit's main thread and wait for it. Called from socket threads only.</summary>
    public static object Run(Func<UIApplication, object> work, TimeSpan timeout)
    {
        var ev = _event ?? throw new InvalidOperationException("The bridge is not initialised.");
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        object result = null;
        Exception error = null;
        int state = 0;   // 0 = queued, 1 = running, 2 = abandoned (the caller timed out before Revit picked it up)

        Queue.Enqueue(app =>
        {
            // If the caller already gave up, do nothing. This matters most for edits:
            // a request that timed out must never modify the model later.
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { result = work(app); }
            catch (Exception ex) { error = ex; }
            finally { done.SetResult(true); }
        });
        try { Raise(ev); }
        catch when (Interlocked.CompareExchange(ref state, 2, 0) != 0) { /* await the running work */ }

        if (!done.Task.Wait(timeout))
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                throw new TimeoutException(
                    $"Revit did not become available within {timeout.TotalSeconds:0} s (a command is running, a dialog is " +
                    "open, or an element is being edited). Finish or cancel it in Revit (Esc) and try again. Request cancelled.");
            done.Task.Wait();   // already running: retain the result even if the client disconnects
        }
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}
