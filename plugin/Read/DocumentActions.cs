using System.Diagnostics;
using System.IO;

namespace RevitMcpBridge;

/// <summary>
/// Roadmap phase 16 (first part): models as documents. File facts without opening; open (workshared files always
/// detached, never attached to their central), close, new scratch projects; reads over several models. Nothing here
/// ever saves a model the user works on: the only file written is a new scratch project under %TEMP%\RevitMcpScratch
/// (Revit can only make a document active once it has a path).
/// </summary>
internal static class DocumentActions
{
    // Ownership ends with this open document. Reopening the same file manually must not inherit it.
    private static readonly List<(Document doc, string source)> Owned = new List<(Document, string)>();

    private static string OwnedSource(Document doc)
    {
        ForgetClosed();
        return Owned.FirstOrDefault(entry => entry.doc.Equals(doc)).source;
    }

    private static void ForgetClosed() => Owned.RemoveAll(entry => !entry.doc.IsValidObject);

    public static string ScratchFolder => Path.Combine(Path.GetTempPath(), "RevitMcpScratch");

    private static string Id(Document doc) => DocumentTarget.SessionId(doc);

    private static readonly TimeSpan ScratchAge = TimeSpan.FromDays(7);

    /// <summary>Scratch projects are never needed again once closed: remove the ones nobody has touched for a week
    /// (with Revit's numbered backups). Files still open in a Revit cannot be deleted and are skipped.</summary>
    private static void SweepScratch()
    {
        try
        {
            if (!Directory.Exists(ScratchFolder)) return;
            foreach (string file in Directory.GetFiles(ScratchFolder, "scratch_*.rvt"))
            {
                try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > ScratchAge) File.Delete(file); }
                catch { /* open in a Revit, or already gone */ }
            }
        }
        catch { /* housekeeping only */ }
    }

    /// <summary>Delete the file of a scratch project this bridge created, after its document was closed.</summary>
    private static void DeleteScratch(string source)
    {
        try
        {
            if (source != null && source.StartsWith(ScratchFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(source))
                File.Delete(source);
        }
        catch { /* still open elsewhere */ }
    }

    private static IEnumerable<Document> OpenDocs(Rc rc) =>
        rc.App.Application.Documents.Cast<Document>().Where(d => !d.IsLinked);

    private static string FullPath(Obj p, string name = "path")
    {
        string path = NeedStr(p, name);
        if (path.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Cloud models are not opened by path here; open them in Revit (Home > Autodesk Docs).");
        return EditContext.FullPath(Environment.ExpandEnvironmentVariables(path), name);
    }

    private static Obj Info(string path)
    {
        var fi = new FileInfo(path);
        var o = new Obj { ["path"] = path, ["file"] = fi.Name, ["sizeMB"] = Math.Round(fi.Length / 1048576.0, 1), ["modified"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") };
        try
        {
            using (var b = BasicFileInfo.Extract(path))
            {
                o["revitVersion"] = b.Format;
                o["savedInLaterVersion"] = b.IsSavedInLaterVersion;
                o["workshared"] = b.IsWorkshared;
                o["central"] = b.IsWorkshared && b.IsCentral;
                o["local"] = b.IsWorkshared && b.IsLocal;
                o["centralPath"] = b.IsWorkshared ? b.CentralPath : null;
                o["savedBy"] = b.Username;
            }
        }
        catch (Exception ex) { o["error"] = "Revit could not read the file header: " + ex.Message; }
        return o;
    }

    private static bool IsBackup(string file) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"\.\d{4}\.rvt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static List<string> Files(Obj p, int max)
    {
        var list = new List<string>();
        if (Has(p, "paths")) foreach (var s in StrList(p, "paths", 500)) { var q = new Obj { ["path"] = s }; list.Add(FullPath(q)); }
        if (Has(p, "folder"))
        {
            string folder = FullPath(p, "folder");
            if (!Directory.Exists(folder)) throw new ArgumentException($"No folder '{folder}'.");
            string pattern = Str(p, "pattern") ?? "*.rvt";
            var opt = Flag(p, "recursive", false) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            list.AddRange(Directory.EnumerateFiles(folder, pattern, opt)
                .Where(f => f.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) && !IsBackup(f))
                .Take(max + 1).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        if (list.Count == 0) throw new ArgumentException("Give 'paths' (full paths of .rvt files) or 'folder'.");
        list = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > max) throw new ArgumentException($"At most {max} files per call. Narrow the folder or pattern.");
        return list;
    }

    // ---------------------------------------------------------------- reads (no model opened)

    public static object ListDocuments(Rc rc, Obj p)
    {
        var active = rc.App.ActiveUIDocument?.Document;
        var items = OpenDocs(rc).Select(d =>
        {
            var src = OwnedSource(d);
            return new Obj {
            ["title"] = d.Title, ["documentId"] = Id(d), ["active"] = d.Equals(active), ["path"] = string.IsNullOrEmpty(d.PathName) ? null : d.PathName,
            ["family"] = d.IsFamilyDocument, ["workshared"] = d.IsWorkshared, ["cloud"] = d.IsModelInCloud, ["unsaved"] = d.IsModified,
            ["openedByClaude"] = src != null, ["source"] = src,
            };
        }).ToList();
        return new { count = items.Count, documents = items, note = "Reads take document_id to read a model that is open but not active. Edits work on the active model only." };
    }

    public static object ModelFiles(Rc rc, Obj p)
    {
        int limit = Int(p, "limit", 200, 1, 2000);
        var files = Files(p, 5000);
        return new { count = files.Count, files = files.Take(limit).Select(Info).ToList(), truncated = files.Count > limit };
    }

    // ---------------------------------------------------------------- session actions (never save)

    private static Document Open(Rc rc, string path, bool activate, string detach, string worksets, bool audit, out bool reused)
    {
        reused = false;
        var already = OpenDocs(rc).FirstOrDefault(d => string.Equals(OwnedSource(d), path, StringComparison.OrdinalIgnoreCase))
                      ?? OpenDocs(rc).FirstOrDefault(d => string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
        if (already != null)
        {
            reused = true;
            if (activate && !already.Equals(rc.App.ActiveUIDocument?.Document))
            {
                if (string.IsNullOrEmpty(already.PathName) || already.IsDetached)
                    throw new InvalidOperationException("This open model must be activated in Revit.");
                var activated = rc.App.OpenAndActivateDocument(already.PathName).Document;
                if (!activated.Equals(already)) throw new InvalidOperationException("Revit activated a different document.");
            }
            return already;
        }
        if (!File.Exists(path)) throw new ArgumentException($"No file '{path}'.");
        var info = Info(path);
        if (info.ContainsKey("error")) throw new InvalidOperationException((string)info["error"]);
        if ((bool)info["savedInLaterVersion"]) throw new InvalidOperationException($"'{info["file"]}' was saved in Revit {info["revitVersion"]}, later than this Revit.");
        var options = new OpenOptions { Audit = audit };
        if ((bool)info["workshared"])
        {
            // Never attached to a central: a detached copy can not reach the central or borrow from it.
            options.DetachFromCentralOption = detach == "discard" ? DetachFromCentralOption.DetachAndDiscardWorksets : DetachFromCentralOption.DetachAndPreserveWorksets;
            if (detach != "discard")
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(worksets == "none" ? WorksetConfigurationOption.CloseAllWorksets : WorksetConfigurationOption.OpenAllWorksets));
        }
        var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
        var doc = activate ? rc.App.OpenAndActivateDocument(mp, options, false).Document : rc.App.Application.OpenDocumentFile(mp, options);
        Owned.Add((doc, path));
        return doc;
    }

    public static object ModelOpen(Rc rc, Obj p)
    {
        string path = FullPath(p);
        bool activate = Flag(p, "activate", false);
        string detach = Choice(p, "detach", "preserve", "preserve", "discard");
        string worksets = Choice(p, "worksets", "all", "all", "none");
        var sw = Stopwatch.StartNew();
        var doc = Open(rc, path, activate, detach, worksets, Flag(p, "audit", false), out bool reused);
        Audit.Write("model_open", new { path, activate, reused });
        return new
        {
            title = doc.Title, documentId = Id(doc), alreadyOpen = reused, active = doc.Equals(rc.App.ActiveUIDocument?.Document),
            workshared = doc.IsWorkshared, detached = doc.IsDetached, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
            note = reused ? "Reused the existing document in its current worksharing state. Check detached before editing."
                          : "Opened by the bridge. Read it with document_id. Workshared files are opened detached.",
        };
    }

    public static object ModelClose(Rc rc, Obj p)
    {
        var doc = rc.Target ?? throw new ArgumentException("Give document_id (from list_documents) of the model to close.");
        if (OwnedSource(doc) == null)
            throw new InvalidOperationException($"'{doc.Title}' was not opened by Claude: close it in Revit yourself (it may hold unsaved work).");
        if (doc.Equals(rc.App.ActiveUIDocument?.Document))
            throw new InvalidOperationException($"'{doc.Title}' is the active model and Revit cannot close the active model from an add-in. Activate another model first, or close it in Revit (without saving).");
        string title = doc.Title;
        string source = OwnedSource(doc);
        bool modified = doc.IsModified;
        if (modified)
        {
            // Throwing away changes is an edit, not a read: it needs explicit intent and the edit permission.
            if (!Flag(p, "discard_changes", false))
                throw new InvalidOperationException($"'{title}' has unsaved changes. Nothing was closed. To close it and lose them, send model_close again with discard_changes: true (needs edit permission).");
            if (!WriteGate.Granted)
                throw new InvalidOperationException($"'{title}' has unsaved changes; discarding them needs edit permission (Revit's 'allow editing' dialog or the Allow Editing button). Nothing was closed.");
        }
        if (!doc.Close(false)) throw new InvalidOperationException($"Revit did not close '{title}'.");
        ForgetClosed();
        DeleteScratch(source);
        Audit.Write("model_close", new { title, modified });
        return new { closed = title, discardedChanges = modified };
    }

    public static object ModelNew(Rc rc, Obj p)
    {
        var app = rc.App.Application;
        string template = Has(p, "template") ? FullPath(p, "template") : app.DefaultProjectTemplate;
        if (string.IsNullOrEmpty(template) || !File.Exists(template))
            throw new ArgumentException($"No project template '{template}'. Give 'template' (a .rte path).");
        bool activate = Flag(p, "activate", true);
        if (activate)
        {
            Directory.CreateDirectory(ScratchFolder);
            SweepScratch();
        }
        var doc = app.NewProjectDocument(template);
        if (!activate)
        {
            Owned.Add((doc, "new:" + template));
            return new { title = doc.Title, documentId = Id(doc), active = false, template, note = "In memory only; read it with document_id. Edits need it active (activate: true)." };
        }
        string path = Path.Combine(ScratchFolder, "scratch_" + Guid.NewGuid().ToString("N") + ".rvt");
        Owned.Add((doc, path));
        doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false });
        if (!doc.Close(false)) throw new InvalidOperationException("Revit did not close the new scratch document.");
        ForgetClosed();
        var ui = rc.App.OpenAndActivateDocument(path);
        Owned.Add((ui.Document, path));
        Audit.Write("model_new", new { template, path });
        return new
        {
            title = ui.Document.Title, documentId = Id(ui.Document), active = true, template, scratchFile = path,
            note = "A scratch project under %TEMP%\\RevitMcpScratch: edit freely, never saved again by Claude. Close it without saving when done.",
        };
    }

    // ---------------------------------------------------------------- reads over several models

    private static readonly HashSet<string> NotBatchable = new HashSet<string>(StringComparer.Ordinal)
    { "ping", "list_documents", "model_files", "model_open", "model_new", "model_close", "batch_read", "get_selection", "show_elements", "list_actions", "help", "get_edit_permission", "get_operation_status", "describe_api", "pick", "watch_start", "watch_changes", "watch_stop", "preview_show", "preview_clear", "revit_addins" };

    public static object BatchRead(Rc rc, Obj p)
    {
        // one action, or several run on each file while it is open (each file opened once)
        var steps = new List<(string action, Obj args)>();
        if (Has(p, "actions"))
        {
            if (Has(p, "action")) throw new ArgumentException("Give 'action' (with 'params') or 'actions', not both.");
            foreach (var item in ObjList(p, "actions", 20))
                steps.Add((NeedStr(item, "action"), Has(item, "params") ? (item["params"] as Obj ?? throw new ArgumentException("'params' must be an object.")) : new Obj()));
        }
        else steps.Add((NeedStr(p, "action"), Has(p, "params") ? (p["params"] as Obj ?? throw new ArgumentException("'params' must be an object.")) : new Obj()));
        foreach (var st in steps)
            if (NotBatchable.Contains(st.action) || !Registry.IsRead(st.action))
                throw new ArgumentException($"'{st.action}' cannot run in batch_read: give a read action about a model (e.g. count_by_category, takeoff, model_health, list_warnings).");
        string action = string.Join(", ", steps.Select(st => st.action));
        int max = Int(p, "max_files", 20, 1, 200);
        double maxSeconds = Dbl(p, "max_seconds") ?? 600;
        if (maxSeconds <= 0) throw new ArgumentException("max_seconds must be positive.");
        var files = Files(p, 5000);
        int start = 0;
        if (Str(p, "resume_after") is string after)
        {
            var matches = files.Where(f => string.Equals(f, after, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(f), after, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1) throw new ArgumentException("Ambiguous resume_after filename. Supply the full path returned in resumeAfter.");
            int i = matches.Count == 1 ? files.IndexOf(matches[0]) : -1;
            if (i < 0) throw new ArgumentException($"resume_after '{after}' is not in the list.");
            start = i + 1;
        }
        var results = new List<object>();
        var closeErrors = new List<object>();
        int failed = 0;
        var sw = Stopwatch.StartNew();
        string last = null;
        foreach (var path in files.Skip(start).Take(max))
        {
            if (last != null && sw.Elapsed.TotalSeconds > maxSeconds) break;
            var one = Stopwatch.StartNew();
            Document doc = null; bool reused = false;
            try
            {
                doc = Open(rc, path, false, "preserve", "all", false, out reused);
                var sub = new Rc(rc.App) { Target = doc };
                if (steps.Count == 1)
                {
                    var res = Registry.RunRead(steps[0].action, sub, new Obj(steps[0].args));
                    results.Add(new { file = Path.GetFileName(path), path, title = doc.Title, ok = true, seconds = Math.Round(one.Elapsed.TotalSeconds, 1), result = res });
                }
                else
                {
                    var each = new Obj();
                    bool allOk = true;
                    foreach (var st in steps)
                    {
                        string key = each.ContainsKey(st.action) ? $"{st.action} ({each.Count + 1})" : st.action;
                        try { each[key] = Registry.RunRead(st.action, sub, new Obj(st.args)); }
                        catch (Exception ex) { each[key] = new { error = ex.Message }; allOk = false; }
                    }
                    if (!allOk) failed++;
                    results.Add(new { file = Path.GetFileName(path), path, title = doc.Title, ok = allOk, seconds = Math.Round(one.Elapsed.TotalSeconds, 1), results = each });
                }
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new { file = Path.GetFileName(path), path, ok = false, seconds = Math.Round(one.Elapsed.TotalSeconds, 1), error = ex.Message });
            }
            finally
            {
                if (doc != null && !reused && doc.IsValidObject && !doc.Equals(rc.App.ActiveUIDocument?.Document))
                {
                    try
                    {
                        if (!doc.Close(false)) throw new InvalidOperationException("Revit did not close this document.");
                        ForgetClosed();
                    }
                    catch (Exception ex) { closeErrors.Add(new { path, error = ErrorText(ex) }); }
                }
            }
            last = path;
            if (closeErrors.Count > 0) break;
        }
        int done = start + results.Count;
        bool complete = done >= files.Count;
        Audit.Write("batch_read", new { action, files = results.Count, seconds = sw.Elapsed.TotalSeconds });
        return new
        {
            action, files = files.Count, read = results.Count, failed, closeErrors, complete,
            resumeAfter = complete ? null : last, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), results,
            note = "Each file is opened in the background (workshared files detached), read, and closed without saving; models already open are read as they are.",
        };
    }
}
