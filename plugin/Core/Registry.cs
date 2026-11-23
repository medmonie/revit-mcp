namespace RevitMcpBridge;

/// <summary>What a handler gets: Revit's application object, and the active model on demand.</summary>
internal sealed class Rc
{
    public Rc(UIApplication app) { App = app; }

    public UIApplication App { get; }
    public UIDocument UiDoc => App.ActiveUIDocument ?? throw new InvalidOperationException("No model is open in Revit.");
    public Document Doc => UiDoc.Document;
}

internal sealed class ActionDef
{
    public ActionDef(string name, string kind, Func<Rc, Obj, object> run, bool mainThread = true)
    { Name = name; Kind = kind; Run = run; MainThread = mainThread; }

    public string Name { get; }
    public string Kind { get; }
    public Func<Rc, Obj, object> Run { get; }
    public bool MainThread { get; }
}

/// <summary>
/// The one table of everything the bridge can do. "read" actions never change the model; the single "edit" action
/// runs a batch of edit operations behind the one-time permission (see WriteGate and EditEngine).
/// </summary>
internal static class Registry
{
    private static readonly Dictionary<string, ActionDef> Actions = new Dictionary<string, ActionDef>(StringComparer.Ordinal);

    private static void Read(string name, Func<Rc, Obj, object> run, bool mainThread = true) =>
        Actions.Add(name, new ActionDef(name, "read", mainThread ? (rc, p) => { U.Set(p); return run(rc, p); } : run, mainThread));

    static Registry()
    {
        // model
        Read("ping", ModelRead.Ping);
        Read("get_project_info", ModelRead.GetProjectInfo);
        Read("list_levels", ModelRead.ListLevels);
        Read("list_grids", ModelRead.ListGrids);
        Read("list_views", ModelRead.ListViews);
        Read("list_sheets", ModelRead.ListSheets);
        Read("count_by_category", ModelRead.CountByCategory);
        Read("list_types", ModelRead.ListTypes);
        Read("list_materials", ModelRead.ListMaterials);
        Read("list_warnings", ModelRead.ListWarnings);
        // elements
        Read("get_elements", ElementRead.GetElements);
        Read("get_element", ElementRead.GetElement);
        Read("get_selection", ElementRead.GetSelection);
        Read("show_elements", ElementRead.ShowElements);
        // links and CAD
        Read("list_links", LinkRead.ListLinks);
        Read("get_cad_geometry", LinkRead.GetCadGeometry);
        Read("cad_columns", LinkRead.CadColumns);
        // quantities
        Read("takeoff", QuantityRead.Takeoff);
        Read("material_takeoff", QuantityRead.MaterialTakeoff);
        Read("rebar_takeoff", QuantityRead.RebarTakeoff);
        Read("rebar_list", RebarRead.ListRebar);
        Read("steel_takeoff", QuantityRead.SteelTakeoff);
        Read("formwork_takeoff", QuantityRead.FormworkTakeoff);
        Read("list_schedules", QuantityRead.ListSchedules);
        Read("get_schedule_data", QuantityRead.GetScheduleData);
        // model QA
        Read("check_materials", QaRead.CheckMaterials);
        Read("check_names", QaRead.CheckNames);
        Read("find_overlaps", OverlapRead.FindOverlaps);
        // model management
        Read("model_health", ManageRead.ModelHealth);
        Read("list_parameters", ManageRead.ListParameters);
        Read("list_worksets", ManageRead.ListWorksets);
        Read("list_phases", ManageRead.ListPhases);
        // meta
        Read("describe_api", ApiRead.DescribeApi);
        Read("family_inspect", FamilyRead.FamilyInspect);
        Read("list_detailing", DetailRead.ListDetailing);
        Read("list_documentation", DetailRead.ListDocumentation);
        Read("get_edit_permission", (rc, p) => WriteGate.Status());
        Read("get_operation_status", EditReceipts.Status, mainThread: false);
        Read("list_actions", ListActions);
        // the only action that can change a model or write files
        Actions["edit"] = new ActionDef("edit", "write", EditEngine.Run);
    }

    public static bool TryGet(string name, out ActionDef action) => Actions.TryGetValue(name, out action);

    private static object ListActions(Rc rc, Obj p) => new
    {
        bridgeVersion = BridgeInfo.Version,
        revitVersion = rc.App.Application.VersionNumber,
        actions = Actions.Values.Select(a => new { name = a.Name, kind = a.Kind }).ToList(),
        editOperations = EditEngine.OperationNames,
        editPermission = WriteGate.Status(),
    };
}
