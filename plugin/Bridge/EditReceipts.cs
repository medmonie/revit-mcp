using System.Security.Cryptography;

namespace RevitMcpBridge;

/// <summary>Operation IDs are unique within one Revit process. Old results can expire, but their IDs stay reserved.</summary>
internal static class EditReceipts
{
    private sealed class Receipt
    {
        public string Id, Digest, State, Error;
        public object Result;
    }

    private const int RetainedResults = 128;
    private static readonly object Sync = new object();
    private static readonly Dictionary<string, Receipt> ById = new Dictionary<string, Receipt>(StringComparer.Ordinal);
    private static readonly Queue<Receipt> Completed = new Queue<Receipt>();

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
                if (receipt.State == "failed") throw new InvalidOperationException(receipt.Error);
                return receipt.State == "completed" ? receipt.Result : Snapshot(receipt);
            }
            receipt = new Receipt { Id = id, Digest = digest, State = "queued" };
            ById.Add(id, receipt);
        }

        try
        {
            object result = execute(() => { lock (Sync) receipt.State = "running"; });
            Finish(receipt, "completed", result, null);
            return result;
        }
        catch (Exception ex)
        {
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
            Completed.Enqueue(receipt);
            if (Completed.Count > RetainedResults)
            {
                var old = Completed.Dequeue();
                old.State = "result_expired";
                old.Result = null;
                old.Error = null;
            }
        }
    }
}
