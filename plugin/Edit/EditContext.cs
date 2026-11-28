namespace RevitMcpBridge;

/// <summary>What an edit operation works with: the model and the single open transaction of its batch.</summary>
internal sealed class EditContext
{
    public UIApplication App;
    public UIDocument UiDoc;
    public Document Doc;
    public bool DryRun;
    public int OpIndex;
    public FailureCollector Failures;
    public readonly List<string> Warnings = new List<string>();

    /// <summary>Elements join_overlaps must leave alone: Revit refused a join with them at commit ("can't cut joined element").</summary>
    public readonly HashSet<long> SkipJoin = new HashSet<long>();

    /// <summary>Work that must run after the transaction is committed (saving, exporting, switching view). Skipped on dry runs.</summary>
    public readonly List<(int Index, Action Run)> AfterCommit = new List<(int, Action)>();
    private readonly HashSet<string> _outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public void Defer(Action work) => AfterCommit.Add((OpIndex, work));

    public void Warn(string message) => Warnings.Add($"operation {OpIndex}: {message}");

    // ---------------------------------------------------------------- helpers shared by the operations

    public static List<long> Values(IEnumerable<ElementId> ids) => ids.Select(i => i.Value).ToList();

    /// <summary>Set a built-in parameter when the element has it and it is writable; otherwise add a warning.</summary>
    public void SetBuiltin(Element e, string bip, ElementId value)
    {
        var p = Find.Builtin(e, bip);
        if (p == null || p.IsReadOnly) { Warn($"element {e.Id.Value} has no writable parameter {bip}; left as it is."); return; }
        p.Set(value);
    }

    public void SetBuiltinInt(Element e, string bip, int value)
    {
        var p = Find.Builtin(e, bip);
        if (p == null || p.IsReadOnly) { Warn($"element {e.Id.Value} has no writable parameter {bip}; left as it is."); return; }
        p.Set(value);
    }

    public void SetBuiltin(Element e, string bip, double feet)
    {
        var p = Find.Builtin(e, bip);
        if (p == null || p.IsReadOnly) { Warn($"element {e.Id.Value} has no writable parameter {bip}; left as it is."); return; }
        p.Set(feet);
    }

    /// <summary>The elements named by an id list; in a workshared model, fails early when someone else owns one.</summary>
    public List<Element> Elements(Obj p, string name = "ids", int max = 5000)
    {
        var list = new List<Element>();
        foreach (var id in Ids(p, name, max).Distinct())
            list.Add(Editable(Find.Elem(Doc, id)));
        return list;
    }

    public Element Editable(Element element)
    {
        if (Doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(Doc, element.Id) == CheckoutStatus.OwnedByOtherUser)
            throw new InvalidOperationException($"Element {element.Id.Value} is owned by another user in this workshared model.");
        return element;
    }

    /// <summary>A closed polygon from a list of [x, y] points (request units) at one elevation.</summary>
    public static CurveLoop Loop(object points, string what, double zFeet)
    {
        if (!(points is List<object> raw)) throw new ArgumentException($"{what} must be a list of [x, y] points.");
        var pts = raw.Select((v, i) => U.Pt(v, $"{what} point {i + 1}")).ToList();
        if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.003) pts.RemoveAt(pts.Count - 1);   // closing point repeated
        return Poly.Loop(pts, zFeet, what);
    }

    public static string FullPath(string path, string argument)
    {
        if (!IOPath.IsPathRooted(path) || IOPath.GetPathRoot(path).Length < 3)
            throw new ArgumentException($"'{argument}' must be a full path, e.g. C:\\Projects\\out.");
        return PathPolicy.Check(IOPath.GetFullPath(path), argument);
    }

    public static string Folder(Obj p) => FullPath(NeedStr(p, "folder"), "folder");

    public void DeferOutput(string folder, Action work) => Defer(() =>
    {
        Directory.CreateDirectory(folder);
        work();
    });

    public static string SafeName(string name)
    {
        foreach (char ch in IOPath.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name;
    }

    /// <summary>Refuse to overwrite files unless overwrite: true was given.</summary>
    public void GuardOverwrite(Obj p, string folder, string pattern)
    {
        if (!_outputs.Add(IOPath.Combine(folder, pattern)))
            throw new ArgumentException($"More than one operation targets '{IOPath.Combine(folder, pattern)}'. Choose distinct output names.");
        if (Flag(p, "overwrite", false) || !Directory.Exists(folder)) return;
        var existing = Directory.GetFiles(folder, pattern);
        if (existing.Length > 0)
            throw new InvalidOperationException($"'{IOPath.GetFileName(existing[0])}' already exists in {folder}. Pass overwrite: true to replace it.");
    }
}

/// <summary>
/// Keeps Revit from opening warning / error dialogs during a commit: warnings are collected and dismissed,
/// errors roll the whole transaction back and are reported to Claude.
/// </summary>
internal sealed class FailureCollector : IFailuresPreprocessor
{
    public readonly List<string> Warnings = new List<string>();
    public readonly List<string> Errors = new List<string>();
    public readonly List<string> Resolved = new List<string>();

    /// <summary>
    /// Only pairs created by join_overlaps can receive the specific detach resolution.
    /// </summary>
    public readonly HashSet<(long, long)> JoinPairs = new HashSet<(long, long)>();

    /// <summary>Elements of pairs joined in this batch that Revit could not cut at commit; join_overlaps retries without them.</summary>
    public readonly HashSet<long> CutConflicts = new HashSet<long>();

    public void Start(Transaction transaction)
    {
        if (transaction.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Revit did not start the transaction.");
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(this);
        options.SetClearAfterRollback(true);
        options.SetForcedModalHandling(true);
        transaction.SetFailureHandlingOptions(options);
    }

    private static IEnumerable<long> failing(FailureMessageAccessor f, ICollection<ElementId> ids) =>
        (ids ?? new List<ElementId>()).Concat(f.GetAdditionalElementIds()).Select(i => i.Value).Distinct();

    private bool IsRegisteredPair(FailureMessageAccessor failure, ICollection<ElementId> failing)
    {
        var ids = failing.Concat(failure.GetAdditionalElementIds()).Select(id => id.Value).Distinct().Take(3).ToArray();
        return ids.Length == 2 && JoinPairs.Contains((Math.Min(ids[0], ids[1]), Math.Max(ids[0], ids[1])));
    }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        bool resolvedAny = false;
        int errorsBefore = Errors.Count;
        foreach (var f in accessor.GetFailureMessages())
        {
            string text;
            try { text = f.GetDescriptionText(); } catch { text = "(no description)"; }
            var ids = f.GetFailingElementIds();
            if (ids != null && ids.Count > 0) text += " [elements " + string.Join(", ", ids.Take(10).Select(i => i.Value)) + "]";
            if (f.GetSeverity() == FailureSeverity.Warning)
            {
                Warnings.Add(text);
                accessor.DeleteWarning(f);
            }
            else if (JoinPairs.Count > 0 && f.GetFailureDefinitionId() == BuiltInFailures.JoinElementsFailures.CannotCutJoinedGeometry
                     && IsRegisteredPair(f, ids) && f.HasResolutionOfType(FailureResolutionType.DetachElements)
                     && accessor.IsFailureResolutionPermitted(f, FailureResolutionType.DetachElements)
                     && !accessor.GetAttemptedResolutionTypes(f).Contains(FailureResolutionType.DetachElements))
            {
                f.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                accessor.ResolveFailure(f);
                Resolved.Add(text + " -> detach");
                resolvedAny = true;
            }
            else
            {
                if (JoinPairs.Count > 0)
                {
                    // Any commit error in a batch that joined elements (Revit reports "can't cut joined element" under
                    // more than one failure definition): join_overlaps runs again without that element.
                    // The failing element may also be a third element already joined to one we joined; skipping it then
                    // removes the joins of this batch that touch it, and never touches the joins that were there before.
                    var hit = failing(f, ids).ToList();
                    foreach (var id in hit) CutConflicts.Add(id);
                    if (hit.Count > 0) Audit.Write("join_commit_error", new { text, definition = f.GetFailureDefinitionId().Guid.ToString() });
                }
                Errors.Add(text);
            }
        }
        if (Errors.Count > errorsBefore) return FailureProcessingResult.ProceedWithRollBack;
        return resolvedAny ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
    }
}
