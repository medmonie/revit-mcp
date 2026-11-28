using System.Security.Cryptography;

namespace RevitMcpBridge;

internal static class DocumentTarget
{
    private static readonly string Session = Guid.NewGuid().ToString("N");

    // Include the path, since separately saved copies can share the same creation GUID.
    public static string ModelKey(Document doc)
    {
        if (doc == null) return null;
        string location = doc.PathName.ToUpperInvariant();
        if (doc.IsModelInCloud)
        {
            var path = doc.GetCloudModelPath();
            location = path.GetProjectGUID().ToString("N") + ":" + path.GetModelGUID().ToString("N");
        }
        using (var sha = SHA256.Create())
        {
            var identity = doc.CreationGUID.ToString("N") + "\n" + location;
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "");
        }
    }

    public static string SessionId(string modelKey) => modelKey == null ? null : Session + ":" + modelKey;

    public static void Verify(Rc rc, Obj args, bool required)
    {
        if (!required && !args.ContainsKey("document_id")) return;
        string expected = NeedStr(args, "document_id");
        var active = rc.App.ActiveUIDocument?.Document;
        if (string.Equals(expected, SessionId(ModelKey(active)), StringComparison.Ordinal))
        {
            if (!required) rc.Target = active;   // actions about a named model (model_close) see which one was meant
            return;
        }
        if (!required)
        {
            // Reads may target another open model (not linked): list_documents gives its document_id.
            foreach (Document d in rc.App.Application.Documents)
                if (!d.IsLinked && string.Equals(expected, SessionId(ModelKey(d)), StringComparison.Ordinal)) { rc.Target = d; return; }
            throw new InvalidOperationException("No open model matches document_id. See list_documents.");
        }
        throw new InvalidOperationException("The active Revit document does not match document_id. Activate the intended model and check ping before continuing.");
    }
}
