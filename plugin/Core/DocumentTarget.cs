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
        if (!string.Equals(expected, SessionId(ModelKey(rc.App.ActiveUIDocument?.Document)), StringComparison.Ordinal))
            throw new InvalidOperationException("The active Revit document does not match document_id. Activate the intended model and check ping before continuing.");
    }
}
