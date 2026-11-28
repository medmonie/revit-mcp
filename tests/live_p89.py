r"""Live test of roadmap phases 8 and 9 (0.17.0), straight to the add-in (no MCP server, so a running Claude Desktop with
an older server exe does not matter).

  python live_p89.py task04   reads and DRY RUNS only; refuses unless the active model is 'Revit Task 04...'
  python live_p89.py scratch  REAL runs; refuses unless the active model is a new project that was never saved

Started by run_live_p89_task04.bat / run_live_p89_scratch.bat; output in tests\results\live_p89_<mode>.txt.
The first edit of a Revit session opens Revit's 'allow editing' dialog: the script waits up to 3 minutes for Yes.
"""
import json
import sys
import time
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

MODE = sys.argv[1] if len(sys.argv) > 1 else ""
EXPORT_DIR = tempfile.mkdtemp(prefix="RevitMcp-P89-")
FAILED = []

# Bind the initial model check and every subsequent read/edit to one document.
connection = bridge_client.pin()
info = bridge_client.call("get_project_info", connection=connection)
document_id = info.get("documentId")
if not document_id:
    raise SystemExit("The bridge did not identify the active test document.")


def call(method, params=None, **kwargs):
    return bridge_client.call(method, {**(params or {}), "document_id": document_id}, connection=connection, **kwargs)


def out(title, value):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < 7000 else text[:7000] + "\n... (truncated)"), flush=True)


def read(title, method, params=None, expect_error=False):
    try:
        res = call(method, params or {})
        out(title, res)
        if expect_error:
            FAILED.append(title + " (expected an error)")
        return res
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error:
            FAILED.append(title)
        return None


def edit(title, operations, dry_run, expect_error=False):
    try:
        res = call("edit", {"operations": operations, "dry_run": dry_run}, timeout=600)
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error:
            FAILED.append(title)
        return None
    out(title, res)
    ok = (res.get("previewSucceeded") if dry_run else res.get("applied") and res.get("succeeded")) and not res.get("error")
    if bool(ok) == expect_error:
        FAILED.append(title + (" (expected an error)" if expect_error else ""))
    return res


def allowed():
    try:
        return call("get_edit_permission", {}).get("editingAllowed")
    except Exception:   # Revit is busy while its dialog is open
        return False


def asset_material(materials):
    """A material carrying both a structural and a thermal asset, concrete first (read-only probe)."""
    ordered = sorted(materials, key=lambda m: 0 if "concrete" in (str(m.get("name", "")) + str(m.get("materialClass", ""))).lower() else 1)
    for m in ordered[:150]:
        try:
            info = call("get_material", {"material": str(m["id"])})
        except Exception:
            continue
        if info.get("structural") and info.get("thermal"):
            return info
    return None


def ensure_permission(dry_run):
    if allowed():
        return
    print("Asking Revit for edit permission: click Yes in Revit's dialog (waiting up to 3 minutes) ...", flush=True)
    try:
        call("edit", {"operations": [{"op": "view_3d_create", "name": "MCP permission probe"}], "dry_run": True}, timeout=60)
    except Exception as exc:
        print("probe:", exc, flush=True)
    for _ in range(90):
        time.sleep(2)
        if allowed():
            print("permission granted", flush=True)
            return
    raise SystemExit("Edit permission was not granted; stopping.")


def first(items, key="id"):
    return items[0][key] if items else None


out("get_project_info (before)", {k: info.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")})

# ------------------------------------------------------------------ reads (both modes)
read("list_groups", "list_groups", {"limit": 5})
read("list_assemblies", "list_assemblies", {})
read("list_global_parameters", "list_global_parameters", {})
read("list_keynotes", "list_keynotes", {"limit": 5, "entries_limit": 3})
mats = read("list_materials", "list_materials", {"limit": 300}) or {}

if MODE == "task04":
    if not info.get("title", "").startswith("Revit Task 04"):
        raise SystemExit("task04 mode: the active model is not Task 04; nothing edited.")
    DRY = True
    read("get_geometry column 2187231", "get_geometry", {"id": 2187231, "limit": 8})
    read("get_geometry column 2187231 original", "get_geometry", {"id": 2187231, "original": True, "faces": False, "edges": True})
    read("get_geometry slab 1739650", "get_geometry", {"id": 1739650, "limit": 3})
    read("get_geometry missing id", "get_geometry", {"id": 99999999}, expect_error=True)
    read("get_material _CONCRETE-RC", "get_material", {"material": "_CONCRETE-RC"})
    read("get_material unknown", "get_material", {"material": "no such material"}, expect_error=True)

    walls = (read("walls", "get_elements", {"category": "walls", "limit": 2, "location": False}) or {}).get("elements", [])
    grids = (read("grids", "list_grids", {}) or {}).get("grids", [])
    plans = (read("plans", "list_views", {"type": "EngineeringPlan", "limit": 50}) or {}).get("views", [])
    plan = next((v["name"] for v in plans if "FIRST" in v["name"].upper()), plans[0]["name"] if plans else None)
    views3d = (read("3d views", "list_views", {"type": "ThreeD", "limit": 5}) or {}).get("views", [])
    sheets = (read("sheets", "list_sheets", {"limit": 3}) or {}).get("sheets", [])
    generic = (read("generic model types", "list_types", {"category": "generic_models", "limit": 20}) or {}).get("types", [])

    ensure_permission(DRY)
    wall = first(walls)
    edit("flip wall", [{"op": "flip", "ids": [wall], "what": "wall"}], DRY)
    edit("flip column facing (family may refuse)", [{"op": "flip", "ids": [2187231], "what": "facing"}], DRY, expect_error=True)
    edit("flip without what", [{"op": "flip", "ids": [wall]}], DRY, expect_error=True)
    edit("end_join beam disallow both + start extension 150", [{"op": "end_join", "ids": [1723276], "end": "both", "allow": False},
                                                                {"op": "end_join", "ids": [1723276], "end": "start", "extension": 150}], DRY)
    edit("end_join wall start disallow", [{"op": "end_join", "ids": [wall], "end": "start", "allow": False}], DRY)
    edit("end_join extension on a wall only (refused)", [{"op": "end_join", "ids": [wall], "end": "end", "extension": 100}], DRY, expect_error=True)
    if generic:
        g = generic[0]
        edit("family_place_hosted level-based generic (refused with its kind)", [{"op": "family_place_hosted", "category": "generic_models",
             "type": f"{g['family']} : {g['type']}", "work_plane": "GROUND FLOOR", "points": [[1000, 1000, 0]]}], DRY, expect_error=True)
    edit("family_place_hosted host and work_plane (refused)", [{"op": "family_place_hosted", "category": "columns",
         "type": "COL-RC-RECT 300X600", "host": 1723276, "work_plane": "GROUND FLOOR", "points": [[0, 0, 0]]}], DRY, expect_error=True)
    edit("group_create + group_place", [{"op": "group_create", "ids": [2158901], "name": "MCP test group"},
                                        {"op": "group_place", "group": "MCP test group", "points": [[30000, 0, 0], [35000, 0, 0]]}], DRY)
    edit("ungroup a non-group (refused)", [{"op": "ungroup", "ids": [2158901]}], DRY, expect_error=True)
    edit("assembly_create", [{"op": "assembly_create", "ids": [1723276], "name": "MCP test assembly"}], DRY)
    edit("assembly_views of a missing assembly (refused)", [{"op": "assembly_views", "assembly": "MCP test assembly", "views": ["3d"]}], DRY, expect_error=True)
    edit("schedule_create + schedule_set", [
        {"op": "schedule_create", "category": "columns", "fields": ["Type", "Base Level", "Volume", "Count"], "name": "MCP test schedule"},
        {"op": "schedule_set", "schedule": "MCP test schedule",
         "filters": [{"field": "Volume", "op": "greater", "value": 0.5}, {"field": "Type", "op": "contains", "value": "300"}],
         "sort": [{"field": "Type", "header": True, "footer": True, "footer_count": True}],
         "itemize": False, "grand_total": True,
         "fields": {"Volume": {"heading": "Volume (m3)", "total": True, "unit": "m3", "decimals": 2, "align": "right"}}}], DRY)
    edit("schedule_set filter on unknown field (refused)", [
        {"op": "schedule_create", "category": "columns", "fields": ["Type"], "name": "MCP test schedule 2"},
        {"op": "schedule_set", "schedule": "MCP test schedule 2", "filters": [{"field": "Nope", "value": 1}]}], DRY, expect_error=True)
    if plan:
        edit("dimension_rotated 0 deg on two columns", [{"op": "dimension_rotated", "view": plan, "ids": [2187231, 1713154], "angle": 0}], DRY)
        edit("dimension_rotated 90 deg on two columns", [{"op": "dimension_rotated", "view": plan, "ids": [2187231, 1713154], "angle": 90}], DRY)
        edit("dimension_rotated 30 deg (no square faces: refused)", [{"op": "dimension_rotated", "view": plan, "ids": [2187231], "angle": 30, "include_grids": False}], DRY, expect_error=True)
        straight = [g for g in grids if "radius" not in g]
        horiz = next((g["name"] for g in straight if abs(g["start"][1] - g["end"][1]) < 1), None)
        vert = next((g["name"] for g in straight if abs(g["start"][0] - g["end"][0]) < 1), None)
        if horiz and vert:
            edit(f"dimension_angular {horiz} / {vert}", [{"op": "dimension_angular", "view": plan, "between": [horiz, vert]}], DRY)
            edit("dimension_angular parallel (refused)", [{"op": "dimension_angular", "view": plan, "between": [vert, vert]}], DRY, expect_error=True)
        edit("dimension_radial on 2024 (refused)", [{"op": "dimension_radial", "view": plan, "ids": [2225279]}], DRY, expect_error=True)
        edit("spot_coordinate slab", [{"op": "spot_coordinate", "view": plan, "ids": [1739650]}], DRY)
        edit("spot_elevation slab (regression, shared face point)", [{"op": "spot_elevation", "view": plan, "ids": [1739650]}], DRY)
    edit("fix_warnings all kinds (dry run)", [{"op": "fix_warnings", "kinds": ["joined_disjoint", "duplicate_instances", "overlapping_lines"]}], DRY)
    edit("fix_warnings unknown kind (refused)", [{"op": "fix_warnings", "kinds": ["everything"]}], DRY, expect_error=True)
    edit("global_parameter_create + set + associate", [
        {"op": "global_parameter_create", "name": "MCP Test Offset", "spec": "length", "value": 25},
        {"op": "global_parameter_set", "name": "MCP Test Offset", "value": 40},
        {"op": "global_parameter_associate", "name": "MCP Test Offset", "ids": [2187231], "parameter": "Top Offset"}], DRY)
    edit("global_parameter_associate on a parameter that cannot take it (refused)", [
        {"op": "global_parameter_create", "name": "MCP Test Offset 2", "spec": "length", "value": 25},
        {"op": "global_parameter_associate", "name": "MCP Test Offset 2", "ids": [1739650], "parameter": "Height Offset From Level"}], DRY, expect_error=True)
    edit("global_parameter_create bad spec (refused)", [{"op": "global_parameter_create", "name": "X", "spec": "pressure"}], DRY, expect_error=True)
    edit("material_asset_set _CONCRETE-RC structural (no asset: refused)", [{"op": "material_asset_set", "material": "_CONCRETE-RC",
         "structural": {"density": 2500}}], DRY, expect_error=True)
    am = asset_material(mats.get("materials", []))
    if am:
        out("material with assets (before)", am)
        edit(f"material_asset_set {am['name']} structural + thermal + appearance", [{"op": "material_asset_set", "material": am["name"],
             "structural": {"density": 2500, "young_modulus": 27000, "poisson_ratio": 0.2},
             "thermal": {"conductivity": 1.6}, "appearance": {"shading_color": "#808080", "transparency": 0}}], DRY)
        edit(f"material_asset_set {am['name']} structural in place (change_shared, dry run)", [{"op": "material_asset_set", "material": am["name"],
             "change_shared": True, "structural": {"density": 2500}}], DRY)
        edit(f"material_asset_set {am['name']} structural only, copied", [{"op": "material_asset_set", "material": am["name"],
             "structural": {"density": 2500}}], DRY)
    else:
        print("===== no material with structural and thermal assets found", flush=True)
    edit("material_asset_set nothing given (refused)", [{"op": "material_asset_set", "material": "_CONCRETE-RC"}], DRY, expect_error=True)
    exp = [{"op": "export_dwf", "folder": EXPORT_DIR, "views": [plan]}, {"op": "export_dgn", "folder": EXPORT_DIR, "views": [plan]},
           {"op": "export_gbxml", "folder": EXPORT_DIR}]
    if views3d:
        exp.append({"op": "export_fbx", "folder": EXPORT_DIR, "view": views3d[0]["name"]})
    edit("exports (dry run: checked, not written)", exp, DRY)
    edit("export_fbx on a plan (refused)", [{"op": "export_fbx", "folder": EXPORT_DIR, "view": plan}], DRY, expect_error=True)

elif MODE == "scratch":
    if info.get("path") or info.get("title", "").startswith("Revit Task 04") or "KTR15" in info.get("title", ""):
        raise SystemExit("scratch mode: the active model has been saved (or is Task 04 / KTR15); nothing edited.")
    DRY = False
    levels = (read("levels", "list_levels", {}) or {}).get("levels", [])
    lv = levels[0]["name"]
    wall_type = first((read("wall types", "list_types", {"category": "walls"}) or {}).get("types", []), "type")
    ensure_permission(DRY)
    made = edit("walls + 3D view + plan + global parameter", [
        {"op": "wall_create", "type": wall_type, "level": lv, "height": 3000,
         "walls": [{"start": [0, 0], "end": [6000, 0]}, {"start": [6000, 0], "end": [6000, 4000]}, {"start": [0, 4000], "end": [6000, 4000]}]},
        {"op": "view_3d_create", "name": "MCP Test 3D"},
        {"op": "global_parameter_create", "name": "MCP Test Length", "spec": "length", "value": 1250},
        {"op": "global_parameter_create", "name": "MCP Test Flag", "spec": "yes_no", "value": True},
        {"op": "global_parameter_create", "name": "MCP Test Text", "spec": "text", "value": "abc"}], DRY) or {}
    wall_ids = made.get("results", [{}])[0].get("result", {}).get("ids", []) if made else []
    read("list_global_parameters (after create)", "list_global_parameters", {})
    edit("global_parameter_set formula", [{"op": "global_parameter_set", "name": "MCP Test Length", "formula": "1000 mm + 500 mm"}], DRY)
    read("list_global_parameters (after formula)", "list_global_parameters", {})
    if wall_ids:
        edit("global_parameter_associate wall height, plain value (real)", [
            {"op": "global_parameter_create", "name": "MCP Test Height", "spec": "length", "value": 2800},
            {"op": "global_parameter_associate", "name": "MCP Test Height", "ids": [wall_ids[0]], "parameter": "Unconnected Height"}], DRY, expect_error=True)
        edit("global_parameter_associate wall base offset (real)", [
            {"op": "global_parameter_create", "name": "MCP Test Base", "spec": "length", "value": 200},
            {"op": "global_parameter_associate", "name": "MCP Test Base", "ids": [wall_ids[0]], "parameter": "Base Offset"}], DRY)
        read("wall base offset driven by the global parameter (expect 200)", "get_elements", {"ids": [wall_ids[0]], "params": ["Base Offset"], "location": False})
        edit("global_parameter_set 350 (real)", [{"op": "global_parameter_set", "name": "MCP Test Base", "value": 350}], DRY)
        read("wall base offset after the global parameter changed (expect 350)", "get_elements", {"ids": [wall_ids[0]], "params": ["Base Offset"], "location": False})
        edit("global_parameter_associate formula-driven parameter (real; Revit may refuse)", [{"op": "global_parameter_associate", "name": "MCP Test Length",
             "ids": [wall_ids[1]], "parameter": "Unconnected Height"}], DRY, expect_error=True)
        read("list_global_parameters (after associate)", "list_global_parameters", {})
        edit("flip wall (real)", [{"op": "flip", "ids": [wall_ids[0]], "what": "wall"}], DRY)
        edit("end_join wall (real)", [{"op": "end_join", "ids": [wall_ids[1]], "end": "both", "allow": False}], DRY)
        g = edit("group_create (real)", [{"op": "group_create", "ids": wall_ids[:2], "name": "MCP Wall Pair"}], DRY) or {}
        edit("group_place (real)", [{"op": "group_place", "group": "MCP Wall Pair", "points": [[0, 10000, 0]]}], DRY)
        read("list_groups (after)", "list_groups", {"kind": "model"})
        gid = g.get("results", [{}])[0].get("result", {}).get("id") if g else None
        if gid:
            edit("ungroup (real)", [{"op": "ungroup", "ids": [gid]}], DRY)
        edit("assembly_create (real)", [{"op": "assembly_create", "ids": [wall_ids[2]], "name": "MCP Wall Assembly"}], DRY)
        edit("assembly_views (real, after commit)", [{"op": "assembly_views", "assembly": "MCP Wall Assembly",
                                                     "views": ["3d", "front", "parts_list"]}], DRY)
        read("list_assemblies (after)", "list_assemblies", {})
        read("get_geometry wall (real)", "get_geometry", {"id": wall_ids[0], "limit": 6})
        edit("schedule (real)", [
            {"op": "schedule_create", "category": "walls", "fields": ["Type", "Length", "Area"], "name": "MCP Wall Schedule"},
            {"op": "schedule_set", "schedule": "MCP Wall Schedule", "filters": [{"field": "Length", "op": "greater", "value": 4500}],
             "sort": [{"field": "Type", "header": True}], "grand_total": True,
             "fields": {"Length": {"heading": "Length (m)", "unit": "m", "decimals": 2, "total": True}}}], DRY)
        read("get_schedule_data MCP Wall Schedule", "get_schedule_data", {"schedule": "MCP Wall Schedule"})
    am = asset_material(mats.get("materials", []))
    mat = am["name"] if am else None
    if mat:
        out(f"get_material {mat} (before)", am)
        edit(f"material_asset_set {mat} (real)", [{"op": "material_asset_set", "material": mat,
             "structural": {"density": 2500, "young_modulus": 27000, "poisson_ratio": 0.2}, "thermal": {"conductivity": 1.6},
             "appearance": {"shading_color": "#808080"}}], DRY)
        read(f"get_material {mat} (after)", "get_material", {"material": mat})
        edit(f"material_asset_set {mat} render colour on a non-Generic asset (refused)", [{"op": "material_asset_set", "material": mat,
             "appearance": {"color": "#B4B4B4"}}], DRY, expect_error=True)
    generic = None
    for m in mats.get("materials", [])[:200]:
        try:
            gm = call("get_material", {"material": str(m["id"])})
        except Exception:
            continue
        if (gm.get("appearance") or {}).get("schema") == "GenericSchema" and gm["appearance"].get("color"):
            generic = gm
            break
    if generic:
        out("material with a Generic appearance (before)", {"name": generic["name"], "appearance": generic["appearance"]})
        edit(f"material_asset_set {generic['name']} render colour (real)", [{"op": "material_asset_set", "material": generic["name"],
             "appearance": {"color": "#3366CC"}}], DRY)
        read(f"get_material {generic['name']} (after colour, expect #3366CC)", "get_material", {"material": generic["name"]})
    plans = (read("plans", "list_views", {"type": "FloorPlan", "limit": 5}) or {}).get("views", [])
    plan = plans[0]["name"] if plans else None
    exp = [{"op": "export_dwf", "folder": EXPORT_DIR, "views": [plan], "overwrite": True},
           {"op": "export_dgn", "folder": EXPORT_DIR, "views": [plan], "overwrite": True},
           {"op": "export_fbx", "folder": EXPORT_DIR, "view": "MCP Test 3D", "name": "scratch_3d", "overwrite": True},
           {"op": "export_gbxml", "folder": EXPORT_DIR, "name": "scratch_energy", "overwrite": True}]
    edit("exports (real, scratch project)", exp, DRY)
    try:
        print("===== files in export folder\n" + "\n".join(f"{p.name}  {p.stat().st_size} bytes" for p in sorted(Path(EXPORT_DIR).iterdir())), flush=True)
    except Exception as exc:
        print("export folder:", exc)
else:
    raise SystemExit("usage: live_p89.py task04 | scratch")

after = call("get_project_info", {})
out("get_project_info (after)", {k: after.get(k) for k in ("title", "hasUnsavedChanges", "counts")})
print("\nALL CHECKS AS EXPECTED" if not FAILED else "\nNOT AS EXPECTED: " + "; ".join(FAILED))
sys.exit(1 if FAILED else 0)
