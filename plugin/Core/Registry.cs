namespace RevitMcpBridge;

/// <summary>What a handler gets: Revit's application object, and the active model on demand.</summary>
internal sealed class Rc
{
    public Rc(UIApplication app) { App = app; }

    public UIApplication App { get; }
    /// <summary>Edits: marks the receipt "running". EditEngine calls it just before the transaction, so a request refused
    /// by a check (busy Revit, family document, bad units) stays a plain failure that may be retried.</summary>
    public Action Started { get; set; }
    /// <summary>Reads: an open model that is not the active one (document_id of a background model, or batch_read).</summary>
    public Document Target { get; set; }
    public UIDocument UiDoc => Target != null && !Target.Equals(App.ActiveUIDocument?.Document)
        ? throw new InvalidOperationException($"This needs '{Target.Title}' to be the active model (it uses the view or the selection).")
        : App.ActiveUIDocument ?? throw new InvalidOperationException("No model is open in Revit.");
    public Document Doc => Target ?? UiDoc.Document;
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

    /// <summary>A write action outside the edit batch (worksharing): permission, a document_id and an operation_id receipt like edit.</summary>
    private static void Write(string name, Func<Rc, Obj, object> run) =>
        Actions.Add(name, new ActionDef(name, "write", (rc, p) => { U.Set(p); return run(rc, p); }));

    public static bool IsRead(string name) => Actions.TryGetValue(name, out var a) && a.Kind == "read" && a.MainThread;
    public static object RunRead(string name, Rc rc, Obj p) => Actions[name].Run(rc, p);

    static Registry()
    {
        // model
        Read("ping", ModelRead.Ping);
        Read("revit_addins", ModelRead.RevitAddins);
        Read("performance_adviser", SessionRead.PerformanceAdviser);
        Read("pick", SessionRead.Pick);
        Read("watch_start", SessionRead.WatchStart);
        Read("watch_changes", SessionRead.WatchChanges);
        Read("watch_stop", SessionRead.WatchStop);
        Read("preview_show", SessionRead.PreviewShow);
        Read("preview_clear", SessionRead.PreviewClear);
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
        // coordination (phase 6)
        Read("get_coordinates", CoordRead.GetCoordinates);
        Read("link_clashes", ClashRead.LinkClashes);
        Read("list_annotations", AnnotationRead.ListAnnotations);
        // quantities
        Read("takeoff", QuantityRead.Takeoff);
        Read("material_takeoff", QuantityRead.MaterialTakeoff);
        Read("rebar_takeoff", QuantityRead.RebarTakeoff);
        Read("rebar_list", RebarRead.ListRebar);
        Read("list_reinforcement_types", ReinforcementRead.ListReinforcementTypes);
        Read("list_numbering", Numbering.ListNumbering);
        Read("ray_cast", QueryRead.RayCast);
        Read("clearances", QueryRead.Clearances);
        Read("mep_systems", MepRead.MepSystems);
        Read("mep_quantities", MepRead.MepQuantities);
        Read("mep_crossings", MepRead.MepCrossings);
        Read("list_spaces", AnalysisRead.ListSpaces);
        Read("analytical_model", AnalysisRead.AnalyticalModel);
        Read("list_loads", AnalysisRead.ListLoads);
        Read("steel_connections", AnalysisRead.SteelConnections);
        Read("steel_takeoff", QuantityRead.SteelTakeoff);
        Read("formwork_takeoff", QuantityRead.FormworkTakeoff);
        Read("list_schedules", QuantityRead.ListSchedules);
        Read("get_schedule_data", QuantityRead.GetScheduleData);
        // model QA
        Read("check_materials", QaRead.CheckMaterials);
        Read("check_names", QaRead.CheckNames);
        Read("type_layers", ArchRead.TypeLayers);
        Read("room_finishes", ArchRead.RoomFinishes);
        Read("arch_qa", ArchRead.ArchQa);
        Read("find_overlaps", OverlapRead.FindOverlaps);
        // model management
        Read("model_health", ManageRead.ModelHealth);
        Read("list_parameters", ManageRead.ListParameters);
        Read("list_worksets", ManageRead.ListWorksets);
        Read("list_phases", ManageRead.ListPhases);
        // modify and documentation depth (phase 8)
        Read("list_groups", DepthRead.ListGroups);
        Read("list_assemblies", DepthRead.ListAssemblies);
        Read("get_geometry", DepthRead.GetGeometry);
        // standards and data (phase 9)
        Read("list_global_parameters", DataRead.ListGlobalParameters);
        Read("get_material", DataRead.GetMaterial);
        Read("list_keynotes", DataRead.ListKeynotes);
        // meta
        Read("describe_api", ApiRead.DescribeApi);
        Read("family_inspect", FamilyRead.FamilyInspect);
        Read("list_detailing", DetailRead.ListDetailing);
        Read("list_documentation", DetailRead.ListDocumentation);
        Read("get_edit_permission", (rc, p) => WriteGate.Status());
        Read("get_operation_status", EditReceipts.Status, mainThread: false);
        Read("list_actions", ListActions);
        // MEP / HVAC (phase 18)
        Read("mep_flow", HvacRead.MepFlow);
        Read("mep_connectivity", HvacRead.MepConnectivity);
        Read("air_terminals", HvacRead.AirTerminals);
        Read("mep_connectors", HvacRead.MepConnectors);
        Read("mep_insulation", HvacRead.MepInsulation);
        Read("electrical_circuits", ElectricalRead.ElectricalCircuits);
        // documents (phase 16): file facts, open / close / new (never saving a user's model), reads over several models
        Read("list_documents", DocumentActions.ListDocuments);
        Read("model_files", DocumentActions.ModelFiles);
        Read("model_open", DocumentActions.ModelOpen);
        Read("model_close", DocumentActions.ModelClose);
        Read("model_new", DocumentActions.ModelNew);
        Read("batch_read", DocumentActions.BatchRead);
        // worksharing (phase 12): each a separate, confirmed step, never inside an edit batch
        Read("worksharing_status", WorkshareActions.Status);
        Write("sync_with_central", WorkshareActions.Sync);
        Write("reload_latest", WorkshareActions.Reload);
        Write("relinquish", WorkshareActions.Relinquish);
        Write("checkout", WorkshareActions.Checkout);
        // the action that changes a model or writes files
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
