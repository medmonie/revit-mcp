using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace RevitMcpBridge;

/// <summary>
/// The Revit API may only be called from Revit's own thread, inside a "valid API context". Socket threads therefore
/// never touch it: they queue the work and raise an ExternalEvent; Revit calls Execute on its main thread when it is
/// idle (no command running, no dialog open), and the work runs there.
/// The queue is bounded, a callback runs a few items and gives Revit back its UI before taking more, and on shutdown
/// every waiting request is answered (cancelled) instead of being left hanging. Work that already started finishes.
/// </summary>
internal sealed class MainThread : IExternalEventHandler
{
    private sealed class Item
    {
        public Action<UIApplication> Work;
        public Action Cancel;   // answers the waiting caller when the item will never run
    }

    public const int MaxPending = 32;     // requests waiting for Revit at once
    private const int PerCallback = 4;    // items run per idle callback before Revit gets its UI back

    private static readonly ConcurrentQueue<Item> Queue = new ConcurrentQueue<Item>();
    private static ExternalEvent _event;
    private static int _pending;          // queued and not yet started or abandoned
    private static volatile bool _closing;

    public static bool Ready => _event != null && !_closing;
    public static int Pending => Volatile.Read(ref _pending);

    /// <summary>Must be called on Revit's main thread (from IExternalApplication.OnStartup).</summary>
    public static void Init()
    {
        _closing = false;
        _event = ExternalEvent.Create(new MainThread());
    }

    public static void Shutdown()
    {
        _closing = true;
        while (Queue.TryDequeue(out var item))
        {
            try { item.Cancel?.Invoke(); } catch { /* shutting down */ }
        }
        try { _event?.Dispose(); } catch { /* shutting down */ }
        _event = null;
    }

    public void Execute(UIApplication app)
    {
        int ran = 0;
        while (ran < PerCallback && Queue.TryDequeue(out var item))
        {
            ran++;
            try { item.Work(app); } catch { /* each item reports its own error to its caller */ }
        }
        if (!Queue.IsEmpty && _event != null && !_closing)
        {
            try { _event.Raise(); } catch { /* the next request raises it again */ }
        }
    }

    public string GetName() => "Revit MCP bridge";

    private static void Admit()
    {
        if (_closing || _event == null) throw new InvalidOperationException("The bridge is not running (Revit is closing or the add-in did not start).");
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            throw new InvalidOperationException($"Revit already has {MaxPending} bridge requests waiting (a command or dialog is probably open). Finish it in Revit, then retry.");
        }
    }

    /// <summary>Queue work on Revit's main thread without waiting for it (callable from the main thread too).</summary>
    public static void Post(Action<UIApplication> work)
    {
        Admit();
        var ev = _event;
        int state = 0;
        Queue.Enqueue(new Item
        {
            Work = app =>
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                Interlocked.Decrement(ref _pending);
                work(app);
            },
            Cancel = () => { if (Interlocked.CompareExchange(ref state, 2, 0) == 0) Interlocked.Decrement(ref _pending); },
        });
        try { Raise(ev); }
        catch when (Interlocked.CompareExchange(ref state, 2, 0) == 0) { Interlocked.Decrement(ref _pending); throw; }
        catch { /* the work already started */ }
    }

    private static void Raise(ExternalEvent ev)
    {
        if (ev == null) throw new InvalidOperationException("The bridge is not initialised.");
        var status = ev.Raise();
        if (status != ExternalEventRequest.Accepted && status != ExternalEventRequest.Pending)
            throw new InvalidOperationException($"Revit rejected the bridge event ({status}). Finish the active command in Revit and retry.");
    }

    /// <summary>Run work on Revit's main thread and wait for it. Called from socket threads only.</summary>
    public static object Run(Func<UIApplication, object> work, TimeSpan timeout)
    {
        Admit();
        var ev = _event;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        object result = null;
        Exception error = null;
        int state = 0;   // 0 = queued, 1 = running, 2 = abandoned (timed out, refused or Revit closing before it started)

        Queue.Enqueue(new Item
        {
            Work = app =>
            {
                // If the caller already gave up, do nothing. This matters most for edits:
                // a request that timed out must never modify the model later.
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                Interlocked.Decrement(ref _pending);
                try { result = work(app); }
                catch (Exception ex) { error = ex; }
                finally { done.TrySetResult(true); }
            },
            Cancel = () =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) != 0) return;
                Interlocked.Decrement(ref _pending);
                error = new InvalidOperationException("Revit is closing; the request was cancelled before it started. Nothing was changed.");
                done.TrySetResult(true);
            },
        });
        try { Raise(ev); }
        catch when (Interlocked.CompareExchange(ref state, 2, 0) == 0) { Interlocked.Decrement(ref _pending); throw; }
        catch { /* already running or cancelled: wait for it below */ }

        if (!done.Task.Wait(timeout))
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                Interlocked.Decrement(ref _pending);
                throw new TimeoutException(
                    $"Revit did not become available within {timeout.TotalSeconds:0} s (a command is running, a dialog is " +
                    "open, or an element is being edited). Finish or cancel it in Revit (Esc) and try again. Request cancelled.");
            }
            done.Task.Wait();   // already running: retain the result even if the client disconnects
        }
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}
