using Autodesk.Revit.Attributes;

namespace RevitMcpBridge;

/// <summary>
/// Entry point. Revit calls OnStartup on its main thread when the add-in is loaded and OnShutdown on exit.
/// </summary>
public class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            MainThread.Init();
            BridgeServer.Start(application.ControlledApplication.VersionNumber);
            var ca = application.ControlledApplication;
            ca.DocumentOpened += (s, e) => { try { DocumentTarget.Opened(e.Document); } catch { /* identity only */ } };
            ca.DocumentCreated += (s, e) => { try { DocumentTarget.Opened(e.Document); } catch { /* identity only */ } };
            ca.DocumentSavedAs += (s, e) => { try { DocumentTarget.Opened(e.Document); } catch { /* identity only */ } };
        }
        catch (Exception ex)
        {
            BridgeServer.Stop();
            MainThread.Shutdown();
            TaskDialog.Show("Revit MCP bridge", "The bridge failed to start: " + ex.Message);
            return Result.Succeeded;   // never stop Revit from starting
        }

        try
        {
            string dll = typeof(App).Assembly.Location;
            var panel = application.CreateRibbonPanel("Claude MCP");   // on the Add-Ins tab
            Button(panel, dll, "McpStatus", "MCP\nStatus", typeof(StatusCommand),
                "Shows the bridge state. With several Revit windows open, makes THIS one the one Claude talks to.");
            Button(panel, dll, "McpAllow", "Allow\nEditing", typeof(AllowCommand),
                "Allow Claude to edit for the rest of this Revit session without the one-time dialog.");
            Button(panel, dll, "McpRevoke", "Stop\nEditing", typeof(RevokeCommand),
                "Withdraw the edit permission; the next edit request asks again. Reading keeps working.");
        }
        catch { /* the buttons are a convenience; the bridge works without them */ }
        return Result.Succeeded;
    }

    private static void Button(RibbonPanel panel, string dll, string name, string text, Type command, string tip)
    {
        var data = new PushButtonData(name, text, dll, command.FullName) { ToolTip = tip };
        panel.AddItem(data);
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        BridgeServer.Stop();
        MainThread.Shutdown();
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public class StatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        BridgeServer.WriteBridgeFile();
        TaskDialog.Show("Revit MCP bridge", BridgeServer.Describe());
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public class AllowCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        WriteGate.Grant("Allow Editing button");
        TaskDialog.Show("Revit MCP bridge", "Claude may now edit models in this Revit session.\nPress Stop Editing to switch it off.");
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public class RevokeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        WriteGate.Revoke("Stop Editing button");
        TaskDialog.Show("Revit MCP bridge", "Editing by Claude is switched off. Reading still works.");
        return Result.Succeeded;
    }
}
