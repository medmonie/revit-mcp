namespace RevitMcpBridge;

/// <summary>
/// The single permission for editing. Reading never needs it. The first edit request of a Revit session shows ONE
/// dialog; after "Yes" every later edit in this session runs without asking again, in any open model, until Revit is
/// closed or the user presses "Stop Editing". "Allow Editing" on the ribbon grants it up front. Main thread only.
/// </summary>
internal static class WriteGate
{
    private static readonly TimeSpan DeclineCooldown = TimeSpan.FromSeconds(30);

    private static bool _granted;
    private static DateTime _grantedUtc;
    private static string _grantedBy = "";
    private static DateTime _declinedUtc = DateTime.MinValue;

    // One approval in Revit for one action on the central model (sync, relinquish): usable once, for a short time.
    private static readonly TimeSpan CentralApprovalLifetime = TimeSpan.FromMinutes(5);
    private static string _centralApproval;
    private static DateTime _centralApprovalUtc;

    public static bool Granted => _granted;

    public static object Status() => new
    {
        editingAllowed = _granted,
        grantedUtc = _granted ? _grantedUtc : (DateTime?)null,
        grantedBy = _granted ? _grantedBy : null,
        scope = "this Revit session, all open models; ends when Revit closes or 'Stop Editing' is pressed",
        howToGrant = "answer Yes to the one-time dialog on the first edit, or press Add-Ins > Claude MCP > Allow Editing in Revit",
        auditLog = Audit.FilePath,
        audit = Audit.Health,
    };

    public static void Grant(string by)
    {
        _granted = true;
        _grantedUtc = DateTime.UtcNow;
        _grantedBy = by;
        Audit.Write("permission_granted", new { by });
    }

    public static void Revoke(string by)
    {
        if (_granted) Audit.Write("permission_revoked", new { by });
        _granted = false;
        _centralApproval = null;
    }

    private static volatile bool _asking;

    /// <summary>
    /// Returns null when editing may proceed, otherwise the reason it may not. When permission has not been given yet,
    /// the dialog is opened in Revit WITHOUT holding this request open (Claude's connection gives up after about a
    /// minute, which is too short to answer a dialog); the request returns at once and is sent again after "Yes".
    /// </summary>
    public static string Ensure(Document doc, string firstRequestSummary)
    {
        if (_granted) return null;
        if (_asking) return "The permission dialog is open in Revit. Answer it there; after Yes, send the edit again (as a new request).";
        if (DateTime.UtcNow - _declinedUtc < DeclineCooldown)
            return "Editing was declined in Revit a moment ago. Ask the user before trying again (they can also press Add-Ins > Claude MCP > Allow Editing).";

        _asking = true;
        string title = doc.Title;
        try
        {
            MainThread.Post(app =>
            {
                try { Ask(title, firstRequestSummary); }
                finally { _asking = false; }
            });
        }
        catch { _asking = false; throw; }
        return "Revit is now asking the user to allow editing (dialog 'Claude - allow editing in Revit?'). Nothing was changed. " +
               "Ask the user to click Yes there, check with get_edit_permission, then send the edit again (as a new request: no operation_id, or a new one).";
    }

    /// <summary>
    /// Actions that change the CENTRAL model for everyone and cannot be undone need the user's Yes in Revit every
    /// time, not only the session permission and a confirm flag the caller sets itself. Works like Ensure: the
    /// request returns at once while the dialog is open; after Yes the same action on the same model passes once.
    /// </summary>
    public static string EnsureCentral(Document doc, string action)
    {
        if (Ensure(doc, action) is string denied) return denied;
        string wanted = action + "|" + doc.Title;
        if (_centralApproval == wanted && DateTime.UtcNow - _centralApprovalUtc < CentralApprovalLifetime)
        {
            _centralApproval = null;
            return null;
        }
        if (_asking) return "A permission dialog is open in Revit. Answer it there; after Yes, send the request again (as a new request).";
        if (DateTime.UtcNow - _declinedUtc < DeclineCooldown)
            return "This was declined in Revit a moment ago. Ask the user before trying again.";

        _asking = true;
        string title = doc.Title;
        try
        {
            MainThread.Post(app =>
            {
                try { AskCentral(title, action, wanted); }
                finally { _asking = false; }
            });
        }
        catch { _asking = false; throw; }
        return "Revit is now asking the user to approve this change to the central model (dialog 'Claude - change the central model?'). " +
               "Nothing was changed. Ask the user to click Yes there, then send the same request again within 5 minutes (as a new request: no operation_id, or a new one).";
    }

    private static void AskCentral(string model, string action, string wanted)
    {
        var dialog = new TaskDialog("Claude - change the central model?")
        {
            MainInstruction = "Claude is asking to " + action + " (model " + model + ").",
            MainContent =
                "This reaches the central model that other people work in and cannot be undone.\n\n" +
                "Yes = allow this one action. You are asked again next time.\n" +
                "No = nothing is changed.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        if (dialog.Show() == TaskDialogResult.Yes)
        {
            _centralApproval = wanted;
            _centralApprovalUtc = DateTime.UtcNow;
            Audit.Write("central_action_approved", new { model, action });
        }
        else
        {
            _declinedUtc = DateTime.UtcNow;
            Audit.Write("central_action_declined", new { model, action });
        }
    }

    private static void Ask(string model, string firstRequestSummary)
    {
        if (_granted) return;
        var dialog = new TaskDialog("Claude - allow editing in Revit?")
        {
            MainInstruction = "Claude is asking to EDIT models in this Revit session.",
            MainContent =
                "First request (model " + model + "):\n" + firstRequestSummary + "\n\n" +
                "Yes = allow edits until Revit is closed. You will not be asked again.\n" +
                "No = nothing is changed.\n\n" +
                "Every edit is one undo step and is written to the audit log. Press Add-Ins > Claude MCP > Stop Editing at any time.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        if (dialog.Show() == TaskDialogResult.Yes) Grant("dialog");
        else
        {
            _declinedUtc = DateTime.UtcNow;
            Audit.Write("permission_declined", new { model });
        }
    }
}
