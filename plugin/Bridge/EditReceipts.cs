using System.Security.Cryptography;

namespace RevitMcpBridge;

/// <summary>
/// Operation IDs are unique within one Revit process. Old results expire, but their IDs stay reserved so a replay can
/// never edit twice; only when 65,536 IDs are held is the oldest expired one released to make room.
/// </summary>
internal static class EditReceipts
{
    private sealed class Receipt
    {
        public string Id, Digest, State, Error;   // State: queued, running, completed, not_applied, failed, result_expired
        public object Result;
        public bool Retained;                     // in the Completed queue
    }

    private const int RetainedResults = 128;
    private const int MaxReservedIds = 65536;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, Receipt> ById = new Dictionary<string, Receipt>(StringComparer.Ordinal);
    private static readonly Queue<Receipt> Completed = new Queue<Receipt>();
    private static readonly Queue<string> Expired = new Queue<string>();   // ids whose result expired, oldest first

    private static string OperationId(Obj p)
    {
        string id = NeedStr(p, "operation_id");
        if (id.Length > 128 || id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.'))
            throw new ArgumentException("operation_id must use letters, digits, hyphens, underscores or dots and be at most 128 characters.");
        return id;
    }

    private static Obj Snapshot(Receipt receipt) => new Obj
    {
        ["operationId"] = receipt.Id, ["state"] = receipt.State,
        ["result"] = receipt.Result, ["error"] = receipt.Error,
    };

    public static object Status(Rc rc, Obj p)
    {
        string id = OperationId(p);
        lock (Sync)
            return ById.TryGetValue(id, out var receipt) ? Snapshot(receipt) : new Obj { ["operationId"] = id, ["state"] = "unknown" };
    }

    /// <summary>
    /// Retry pre-dispatch failures and permission requests. Once execution has started, an unhandled exception
    /// cannot establish whether a model or post-commit side effect occurred.
    /// </summary>
    private static bool Rerunnable(string state) => state == "failed" || state == "not_applied";

    private static bool NotApplied(object result) =>
        result is Obj o && o.TryGetValue("permission", out var v) && v is string s && s == "not granted";

    public static object Run(Obj args, Func<Action, object> execute)
    {
        string id = OperationId(args);
        string digest;
        using (var sha = SHA256.Create())
            digest = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(args))));

        Receipt receipt;
        lock (Sync)
        {
            if (ById.TryGetValue(id, out receipt))
            {
                if (receipt.Digest != digest) throw new ArgumentException("operation_id is already in use for a different request.");
                if (!Rerunnable(receipt.State))
                    return receipt.State == "completed" ? receipt.Result : Snapshot(receipt);
                receipt.State = "queued";
                receipt.Result = null;
                receipt.Error = null;
            }
            else
            {
                while (ById.Count >= MaxReservedIds && Expired.Count > 0)
                {
                    // Tens of thousands of edits ago: release the oldest expired id instead of refusing every new edit.
                    string oldest = Expired.Dequeue();
                    if (ById.TryGetValue(oldest, out var stale) && stale.State == "result_expired") ById.Remove(oldest);
                }
                if (ById.Count >= MaxReservedIds)
                    throw new InvalidOperationException("The session receipt limit was reached. Restart Revit before submitting new edit IDs.");
                receipt = new Receipt { Id = id, Digest = digest, State = "queued" };
                ById.Add(id, receipt);
            }
        }

        try
        {
            object result = execute(() => { lock (Sync) receipt.State = "running"; });
            Finish(receipt, NotApplied(result) ? "not_applied" : "completed", result, null);
            return result;
        }
        catch (Exception ex)
        {
            bool started;
            lock (Sync) started = receipt.State == "running";
            if (started)
            {
                string error = $"Edit {id} stopped after dispatch. Inspect the model and get_operation_status. {ErrorText(ex)}";
                Finish(receipt, "outcome_unknown", null, error);
                lock (Sync) return Snapshot(receipt);
            }
            Finish(receipt, "failed", null, ErrorText(ex));
            throw;
        }
    }

    private static void Finish(Receipt receipt, string state, object result, string error)
    {
        lock (Sync)
        {
            receipt.State = state;
            receipt.Result = result;
            receipt.Error = error;
            if (!receipt.Retained)
            {
                receipt.Retained = true;
                Completed.Enqueue(receipt);
            }
            while (Completed.Count > RetainedResults)
            {
                var old = Completed.Dequeue();
                old.Retained = false;
                if (old.State == "completed")
                {
                    // the id stays reserved: replaying a committed request must never edit twice
                    old.State = "result_expired";
                    old.Result = null;
                    Expired.Enqueue(old.Id);
                }
                else if (Rerunnable(old.State))
                    ById.Remove(old.Id);   // nothing was applied, so the id need not stay reserved
                // queued / running: a re-run is in progress; Finish retains it again when it ends
            }
        }
    }
}
