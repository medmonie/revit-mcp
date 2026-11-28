namespace RevitMcpBridge;

/// <summary>
/// The only code path that changes a model. One call = one batch of operations = ONE transaction: either every
/// operation succeeds and the batch is committed (one undo step), or nothing is changed.
/// Purge previews inspect candidates. Other simulated changes are rolled back.
/// </summary>
internal static class EditEngine
{
    private const int MaxOperations = 500;

    private static readonly Dictionary<string, Func<EditContext, Obj, object>> Ops =
        new Dictionary<string, Func<EditContext, Obj, object>>(StringComparer.OrdinalIgnoreCase);

    static EditEngine()
    {
        DatumOps.Register(Ops);
        StructureOps.Register(Ops);
        ModifyOps.Register(Ops);
        ViewOps.Register(Ops);
        DocOps.Register(Ops);
        RebarOps.Register(Ops);
        ReinforcementOps.Register(Ops);
        ModelOps.Register(Ops);
        DetailOps.Register(Ops);
        LinkOps.Register(Ops);
        ExportOps.Register(Ops);
        OpeningOps.Register(Ops);
        AnnotationOps.Register(Ops);
        CadOps.Register(Ops);
        CadMemberOps.Register(Ops);
        SheetOps.Register(Ops);
        MaterialOps.Register(Ops);
        FamilyOps.Register(Ops);
        FixOps.Register(Ops);
        ManageOps.Register(Ops);
        DepthOps.Register(Ops);
        DataOps.Register(Ops);
        SpaceOps.Register(Ops);
        AnalysisOps.Register(Ops);
        MepOps.Register(Ops);
        AccessoryOps.Register(Ops);
        ModelingOps.Register(Ops);
        DocExtraOps.Register(Ops);
        FixtureOps.Register(Ops);
        ElectricalOps.Register(Ops);
    }

    public static List<string> OperationNames => Ops.Keys.OrderBy(k => k).ToList();

    public static object Run(Rc rc, Obj p)
    {
        foreach (var key in p.Keys)
            if (key != "operations" && key != "document_id" && key != "operation_id" && key != "dry_run" && key != "units")
                throw new ArgumentException($"Unknown edit parameter '{key}'.");
        U.Set(p);
        var ops = ObjList(p, "operations", MaxOperations);
        bool dryRun = p.ContainsKey("dry_run")
            ? Bool(p, "dry_run") ?? throw new ArgumentException("dry_run must be true or false.")
            : false;

        // 1. Check the shape of the request before the user is asked anything.
        var names = new List<string>();
        for (int i = 0; i < ops.Count; i++)
        {
            string name = NeedStr(ops[i], "op");
            if (ops[i].ContainsKey("dry_run") || ops[i].ContainsKey("dryRun"))
                throw new ArgumentException("dry_run belongs on the edit batch, outside operations.");
            if (!Ops.ContainsKey(name))
                throw new ArgumentException($"operation {i + 1}: unknown op '{name}'. Available: {string.Join(", ", OperationNames)}");
            names.Add(name);
        }
        string summary = string.Join(", ", names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Count()} x {g.Key}" : g.Key));

        var uidoc = rc.UiDoc;
        var doc = uidoc.Document;
        string model = doc.Title;
        string operationId = NeedStr(p, "operation_id");
        if (doc.IsFamilyDocument) throw new InvalidOperationException("The active document is a family (Family Editor). Editing works on project models only.");
        if (doc.IsReadOnly) throw new InvalidOperationException("The active model is read-only.");
        if (doc.IsModifiable) throw new InvalidOperationException("Revit has a transaction open (a command is in progress). Finish it and try again.");

        var results = new List<object>();
        if (dryRun && names.Count > 0 && names.All(n => string.Equals(n, "purge", StringComparison.OrdinalIgnoreCase)))
        {
            for (int i = 0; i < ops.Count; i++)
            {
                try { results.Add(new { index = i + 1, op = names[i], result = ManageOps.PreviewPurge(doc, ops[i]) }); }
                catch (Exception ex)
                {
                    string error = ErrorText(ex);
                    Audit.Write("edit_failed", new { model, operationId, dryRun, summary, failedAt = i + 1, error });
                    return new { applied = false, dryRun, previewSucceeded = false, failedAt = i + 1, op = names[i], error,
                        transactionStatus = "NotStarted", completedBeforeFailure = results };
                }
            }
            Audit.Write("edit_dry_run", new { model, operationId, summary, operations = ops.Count, transactionStatus = "NotStarted" });
            return new { applied = false, dryRun, model, units = U.Name, operations = ops.Count, results,
                previewSucceeded = true, transactionStatus = "NotStarted", warnings = Array.Empty<string>(), regenerationError = (string)null };
        }

        // 2. The one-time permission (a dialog on the first edit of the session).
        if (WriteGate.Ensure(doc, summary + (dryRun ? "  (dry run)" : "")) is string denied)
            return new Obj { ["applied"] = false, ["dryRun"] = dryRun, ["permission"] = "not granted", ["reason"] = denied };

        // 3. Run every operation in one transaction; the first failure rolls everything back.
        //    From here an unexpected exception leaves the outcome unknown (EditReceipts keeps it from being re-run).
        rc.Started?.Invoke();
        var failures = new FailureCollector();
        var ctx = new EditContext { App = rc.App, UiDoc = uidoc, Doc = doc, DryRun = dryRun, Failures = failures };
        string title = "Claude: " + (summary.Length > 60 ? summary.Substring(0, 57) + "..." : summary);
        bool tracePreview = dryRun && names.Any(n => n == "parameter_add" || n == "phase_set" || n == "standards_copy" || n == "purge");

        // A dry run on a model that is not workshared commits inside a transaction group that is then rolled back:
        // Revit checks failures (overlaps, joins, rebar outside its host ...) only when a transaction commits, never on
        // Regenerate, so this is the only way a preview sees them. Workshared models keep the plain roll-back (a commit
        // there could borrow elements from the central model).
        bool trialCommit = dryRun && !doc.IsWorkshared;
        int joinRetries = 0;
        TransactionGroup group = null;
        try
        {
        if (trialCommit)
        {
            group = new TransactionGroup(doc, title + " (dry run)");
            if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Revit did not start the dry-run transaction group.");
        }
        retry:
        using (var t = new Transaction(doc, title))
        {
            failures.Start(t);

            for (int i = 0; i < ops.Count; i++)
            {
                ctx.OpIndex = i + 1;
                try
                {
                    if (tracePreview) Audit.Write("preview_step", new { operationId, index = i + 1, op = names[i] });
                    results.Add(new { index = i + 1, op = names[i], result = Ops[names[i]](ctx, ops[i]) });
                }
                catch (Exception ex)
                {
                    var rollback = t.RollBack();
                    string error = ErrorText(ex);
                    Audit.Write("edit_failed", new { model, operationId, dryRun, summary, failedAt = i + 1, op = names[i], error });
                    return new
                    {
                        applied = false, dryRun, failedAt = i + 1, op = names[i], error,
                        transactionStatus = rollback.ToString(),
                        completedBeforeFailure = results, warnings = ctx.Warnings,
                    };
                }
            }

            if (dryRun)
            {
                string regenError = null;
                if (tracePreview) Audit.Write("preview_regenerate", new { operationId });
                try { doc.Regenerate(); } catch (Exception ex) { regenError = ErrorText(ex); }
                if (trialCommit && regenError == null)
                {
                    if (tracePreview) Audit.Write("preview_trial_commit", new { operationId });
                    var trial = t.Commit();   // the failures preprocessor sees what a real run would meet
                    var undone = group.RollBack();
                    bool clean = trial == TransactionStatus.Committed && undone == TransactionStatus.RolledBack;
                    // The trial commit is undone by rolling the group back. If Revit did not report that roll-back, the
                    // changes may still be in the model: say so instead of a fixed "applied: false".
                    bool leftInModel = trial == TransactionStatus.Committed && undone != TransactionStatus.RolledBack;
                    Audit.Write("edit_dry_run", new { model, operationId, summary, operations = ops.Count, trialCommit = trial.ToString(),
                        groupRollback = undone.ToString(), revitErrors = failures.Errors.Count });
                    return new
                    {
                        applied = leftInModel, dryRun = true, model, units = U.Name, operations = ops.Count, results,
                        previewSucceeded = clean, transactionStatus = undone.ToString(), trialCommit = trial.ToString(),
                        revitErrors = failures.Errors, revitWarnings = failures.Warnings,
                        resolvedByRevit = failures.Resolved.Count > 0 ? failures.Resolved : null,
                        warnings = ctx.Warnings, regenerationError = (string)null,
                        note = leftInModel
                            ? "The dry run committed on trial but Revit did not confirm the roll-back (" + undone + "): the changes may be in the model. Check Undo in Revit before anything else."
                            : trial == TransactionStatus.Committed
                            ? "Committed on trial and rolled back: nothing changed. revitWarnings are the warnings a real run would show. Newly created IDs are temporary. Purge only lists candidates."
                            : "A real run would be rolled back by Revit: see revitErrors. Nothing changed.",
                    };
                }
                if (tracePreview) Audit.Write("preview_rollback", new { operationId, regenError });
                var rollback = t.RollBack();
                Audit.Write("edit_dry_run", new { model, operationId, summary, operations = ops.Count, regenerationError = regenError });
                return new
                {
                    applied = false, dryRun = true, model, units = U.Name, operations = ops.Count, results,
                    previewSucceeded = regenError == null && rollback == TransactionStatus.RolledBack,
                    transactionStatus = rollback.ToString(),
                    warnings = ctx.Warnings, regenerationError = regenError,
                    note = "Simulated changes were rolled back. Newly created IDs are temporary. Purge only lists candidates. Revit commit warnings appear on a real run.",
                };
            }

            var status = t.Commit();
            if (status == TransactionStatus.RolledBack && joinRetries < 3 && failures.CutConflicts.Count > 0
                && names.All(n => string.Equals(n, "join_overlaps", StringComparison.OrdinalIgnoreCase))
                && failures.CutConflicts.Any(id => !ctx.SkipJoin.Contains(id)))
            {
                // Revit could not cut an element joined in this batch: run the batch again without joining those elements.
                joinRetries++;
                foreach (var id in failures.CutConflicts) ctx.SkipJoin.Add(id);
                Audit.Write("edit_retry", new { model, operationId, summary, joinRetries, skipJoin = ctx.SkipJoin.ToList() });
                failures = new FailureCollector();
                ctx.Failures = failures;
                results.Clear(); ctx.Warnings.Clear(); ctx.AfterCommit.Clear();
                goto retry;
            }
            if (status != TransactionStatus.Committed)
            {
                Audit.Write("edit_failed", new { model, operationId, summary, failedAt = "commit", errors = failures.Errors });
                return new
                {
                    applied = false, dryRun = false, failedAt = "commit", status = status.ToString(),
                    errors = failures.Errors, revitWarnings = failures.Warnings,
                    note = status == TransactionStatus.RolledBack ? "Revit rolled back the batch." : "Check the Revit transaction outcome before submitting another edit.",
                    resultsBeforeRollback = results, warnings = ctx.Warnings,
                };
            }
        }
        }
        finally
        {
            // Every early return of a dry run (failed operation, regeneration error) leaves the group to roll back here.
            if (group != null)
            {
                try { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); } catch { /* already closed */ }
                group.Dispose();
            }
        }

        // 4. Saving, exporting and view switching cannot run inside the transaction; they follow the commit.
        var postCommit = new List<Obj>();
        foreach (var (index, work) in ctx.AfterCommit)
        {
            var entry = new Obj { ["index"] = index, ["op"] = names[index - 1], ["status"] = "completed" };
            try { work(); }
            catch (Exception ex)
            {
                entry["status"] = "failed";
                entry["error"] = ErrorText(ex);
                ctx.Warnings.Add($"operation {index} after commit: {entry["error"]}");
            }
            postCommit.Add(entry);
        }
        try { uidoc.RefreshActiveView(); } catch { /* cosmetic */ }

        int postCommitFailed = postCommit.Count(x => (string)x["status"] == "failed");
        if (joinRetries > 0)
            ctx.Warnings.Add($"join_overlaps: Revit could not cut joined elements {string.Join(", ", ctx.SkipJoin)}; the batch was run again "
                           + $"({joinRetries} time(s)) without joining them. They are listed under skippedCutConflict for review.");
        bool logged = Audit.Write("edit_applied", new { model, operationId, summary, operations = ops.Count, postCommitFailed,
            warnings = ctx.Warnings.Count + failures.Warnings.Count });
        return new
        {
            applied = true, dryRun = false, model, units = U.Name, operations = ops.Count, results,
            postCommit, postCommitFailed, succeeded = postCommitFailed == 0,
            auditLogged = logged, audit = logged ? null : Audit.Health,
            warnings = ctx.Warnings, revitWarnings = failures.Warnings,
            resolvedByRevit = failures.Resolved.Count > 0 ? failures.Resolved : null,
            note = "Committed as one transaction (one Undo step in Revit: '" + title + "').",
        };
    }
}
