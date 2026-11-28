r"""Live test of 0.31.0 (phase 17 P1, architecture), in a scratch project made by model_new (real edits, closed with its
changes discarded): layered wall and floor types, a two-room plan, doors and windows in walls (by distance, by point, by
point without a wall), room tags and door / window tags, schedule presets, room finishes measured from the rooms, the
architectural QA, and the room_report workbook. Output tests\results\live_v031_<revit>.txt (and room_report_<revit>.xlsx).
"""
import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

FAILED = []
conn = bridge_client.pin()
REVIT = str(bridge_client.call("ping", connection=conn).get("revitVersion"))
print("Revit", REVIT, flush=True)
doc = None


def call(m, p=None, timeout=900):
    return bridge_client.call(m, dict(p or {}), connection=conn, timeout=timeout)


def read(m, p=None, timeout=900):
    return call(m, {**(p or {}), "document_id": doc}, timeout=timeout)


def out(title, v, limit=3000):
    t = json.dumps(v, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (t if len(t) < limit else t[:limit] + "\n... (truncated)"), flush=True)


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


def edit(title, ops, expect_error=None, dry=False):
    try:
        res = call("edit", {"document_id": doc, "dry_run": dry, "operations": ops})
    except Exception as exc:
        res = {"applied": False, "error": str(exc)}
    out(title, res)
    err = res.get("error") if not (res.get("applied") or (dry and res.get("succeeded"))) else None
    if expect_error:
        check(f"{title} refused", bool(err) and expect_error.lower() in str(err).lower(), str(err)[:250])
        return None
    if err:
        FAILED.append(title)
        return None
    return [r.get("result") for r in res.get("results") or []]


def first(r):
    return (r or [{}])[0] or {}


def near(a, b, tol):
    return a is not None and b is not None and abs(a - b) <= tol


new = call("model_new", {"activate": True})
doc = new["documentId"]
for i in range(150):
    try:   # Revit stays busy while its 'allow editing' dialog is open: keep waiting
        if call("get_edit_permission", timeout=60).get("editingAllowed"):
            break
    except Exception as exc:
        print("waiting for Revit:", str(exc)[:120], flush=True)
    if i == 0:
        print("Asking for edit permission: click Yes in Revit ...", flush=True)
        try:
            call("edit", {"document_id": doc, "dry_run": True, "operations": [{"op": "view_3d_create", "name": "probe"}]}, timeout=60)
        except Exception as exc:
            print("probe:", exc, flush=True)
    time.sleep(2)

lv = read("list_levels")["levels"][0]["name"]
mats = [m.get("name") for m in (read("list_materials", {"limit": 500}).get("materials") or [])]
def mat(*words):
    return next((m for w in words for m in mats if w.lower() in (m or "").lower()), None)
m_plaster, m_block, m_conc, m_ins = mat("plaster", "gypsum", "paint"), mat("masonry", "block", "brick", "concrete"), mat("concrete"), mat("insulation", "rigid")
print("materials used:", m_plaster, m_block, m_conc, m_ins, flush=True)

def layer(fn, t, m=None):
    d = {"function": fn, "thickness": t}
    if m:
        d["material"] = m
    return d

# ------------------------------------------------------------------ layered types
wt = edit("layered_type_create: wall plaster / block / insulation / plaster", [{"op": "layered_type_create", "kind": "wall", "name": "MCP Ext 15+200+50+15",
          "layers": [layer("finish1", 15, m_plaster), layer("structure", 200, m_block), layer("insulation", 50, m_ins), layer("finish2", 15, m_plaster)]}])
r = first(wt)
check("wall type total 280 with core = the block layer", near(r.get("totalThickness"), 280, 0.5) and [x["core"] for x in r.get("layers") or []] == [False, True, False, False], json.dumps(r)[:300])
edit("layered_type_create: interior wall", [{"op": "layered_type_create", "kind": "wall", "name": "MCP Int 15+100+15",
     "layers": [layer("finish1", 15, m_plaster), layer("structure", 100, m_block), layer("finish1", 15, m_plaster)]}])
ft = edit("layered_type_create: floor tiles / screed / slab", [{"op": "layered_type_create", "kind": "floor", "name": "MCP Floor 10+50+200",
          "layers": [layer("finish1", 10), layer("substrate", 50), layer("structure", 200, m_conc)]}])
check("floor type total 260", near(first(ft).get("totalThickness"), 260, 0.5), json.dumps(first(ft))[:200])
tl = read("type_layers", {"types": ["MCP Ext 15+200+50+15", "MCP Floor 10+50+200"]})
out("type_layers", tl)
check("type_layers reads the new layers back", [len(t.get("layers") or []) for t in tl.get("types", [])] == [4, 3])
edit("layered_type_create existing name (refused)", [{"op": "layered_type_create", "kind": "wall", "name": "MCP Ext 15+200+50+15", "layers": [layer("structure", 200)]}], expect_error="exists")
edit("layered_type_create update", [{"op": "layered_type_create", "kind": "wall", "name": "MCP Int 15+100+15", "if_exists": "update",
     "layers": [layer("finish1", 15, m_plaster), layer("structure", 150, m_block), layer("finish1", 15, m_plaster)]}])
edit("layered_type_create membrane with thickness (refused)", [{"op": "layered_type_create", "kind": "wall", "name": "MCP Bad",
     "layers": [layer("membrane", 5), layer("structure", 200)]}], expect_error="membrane")
edit("layered_type_create unknown function (refused)", [{"op": "layered_type_create", "kind": "wall", "name": "MCP Bad2",
     "layers": [layer("cladding", 20), layer("structure", 200)]}], expect_error="not one of")

# ------------------------------------------------------------------ a two-room plan
w = edit("exterior walls 10 x 6 m and a partition", [
    {"op": "wall_create", "type": "MCP Ext 15+200+50+15", "level": lv, "height": 3000, "structural": False,
     "walls": [{"start": [0, 0], "end": [10000, 0]}, {"start": [10000, 0], "end": [10000, 6000]},
               {"start": [10000, 6000], "end": [0, 6000]}, {"start": [0, 6000], "end": [0, 0]}]},
    {"op": "wall_create", "type": "MCP Int 15+100+15", "level": lv, "height": 3000, "structural": False, "walls": [{"start": [6000, 0], "end": [6000, 6000]}]},
    {"op": "floor_create", "type": "MCP Floor 10+50+200", "level": lv, "boundary": [[0, 0], [10000, 0], [10000, 6000], [0, 6000]], "structural": False}])
ext = (w[0] or {}).get("ids") or []
part = ((w[1] or {}).get("ids") or [None])[0]
rm = edit("rooms: office and store", [{"op": "room_create", "level": lv, "points": [[3000, 3000], [8000, 3000]], "names": ["Office", "Store"], "numbers": ["101", "102"]}])
rooms = [x["id"] for x in first(rm).get("items") or []]
check("both rooms enclosed", first(rm).get("notEnclosed") == 0 and len(rooms) == 2, json.dumps(first(rm))[:300])
edit("room finishes and data", [{"op": "set_parameters", "ids": [rooms[0]], "values": {"bip:ROOM_FINISH_FLOOR": "Porcelain tiles", "bip:ROOM_FINISH_WALL": "Emulsion paint",
     "bip:ROOM_FINISH_CEILING": "Gypsum board", "bip:ROOM_FINISH_BASE": "Tile skirting", "bip:ROOM_DEPARTMENT": "Admin"}},
     {"op": "set_parameters", "ids": [rooms[1]], "values": {"bip:ROOM_FINISH_FLOOR": "Porcelain tiles", "bip:ROOM_FINISH_WALL": "Epoxy paint"}}])

# ------------------------------------------------------------------ doors and windows
dtype = next((t for t in read("list_types", {"category": "doors"})["types"]), None)
wtype = next((t for t in read("list_types", {"category": "windows"})["types"]), None)
print("door type:", dtype, "window type:", wtype, flush=True)
if not dtype or not wtype:
    FAILED.append("the scratch template has no door or window type")
else:
    d = edit("door in the partition by distance (3000 along)", [{"op": "door_window_place", "type": str(dtype["id"]), "items": [{"wall": part, "distance": 3000, "mark": "D01"}]}])
    di = (first(d).get("items") or [{}])[0]
    check("door between Office and Store", {str(di.get("fromRoom")), str(di.get("toRoom"))} == {"101 Office", "102 Store"}, json.dumps(di))
    check("door 3000 along the wall", near(di.get("distance"), 3000, 1), "")
    wi = edit("windows: by point on the south wall (sill 900) and by wall + distance (head 2400)", [
        {"op": "door_window_place", "type": str(wtype["id"]), "items": [
            {"at": [3000, 0], "level": lv, "sill_height": 900, "mark": "W01"},
            {"wall": ext[1], "distance": 3000, "head_height": 2400, "mark": "W02"}]}])
    items = first(wi).get("items") or []
    check("window W01 found its wall by point, sill 900", len(items) == 2 and items[0].get("wall") == ext[0] and near(items[0].get("sillHeight"), 900, 1), json.dumps(items)[:400])
    check("window W02 head 2400", len(items) == 2 and near(items[1].get("headHeight"), 2400, 1), "")
    check("W01 is in the Office", len(items) == 2 and items[0].get("room") == "101 Office", "")
    edit("window by a point far from any wall (refused)", [{"op": "door_window_place", "type": str(wtype["id"]), "items": [{"at": [3000, 3000], "level": lv}]}], expect_error="no wall")
    edit("door beyond the wall's end (refused)", [{"op": "door_window_place", "type": str(dtype["id"]), "items": [{"wall": part, "distance": 9000}]}], expect_error="outside the wall")
    edit("sill and head together (refused)", [{"op": "door_window_place", "type": str(wtype["id"]), "sill_height": 900, "head_height": 2100, "items": [{"wall": ext[2], "distance": 2000}]}], expect_error="not both")
    edit("a wall type as the type (refused)", [{"op": "door_window_place", "type": "MCP Ext 15+200+50+15", "items": [{"wall": part, "distance": 1000}]}], expect_error="No type")

# ------------------------------------------------------------------ tags, schedules
plan = next((v for v in read("list_views", {"type": "FloorPlan", "limit": 50}).get("views", []) if v.get("level") == lv), None)
print("plan:", plan, flush=True)
if plan:
    t = edit("tag rooms, doors and windows in the plan", [{"op": "tag_elements", "view": plan["name"], "category": ["rooms", "doors", "windows"]}])
    check("rooms, the door and the windows tagged (2 + 1 + 2)", first(t).get("tagged") == 5, json.dumps(first(t))[:300])
    t2 = edit("tag again (all already tagged)", [{"op": "tag_elements", "view": plan["name"], "category": ["rooms", "doors", "windows"]}], dry=True)
    check("second run skips them all", first(t2).get("tagged") == 0 and first(t2).get("alreadyTagged") == 5, json.dumps(first(t2))[:200])
for preset in ("doors", "windows", "rooms", "room_finishes"):
    s = edit(f"schedule preset {preset}", [{"op": "schedule_create", "preset": preset, "name": f"MCP {preset}"}])
    print("  fields:", first(s).get("fields"), "skipped:", first(s).get("skippedFields"), flush=True)
    data = read("get_schedule_data", {"schedule": f"MCP {preset}"})
    rows = data.get("rows") or []
    want = {"doors": 1, "windows": 2, "rooms": 2, "room_finishes": 2}[preset]
    filled = [row for row in rows if any(str(c).strip() for c in row)]
    check(f"schedule {preset} lists {want} element(s)", len(filled) - 1 == want, json.dumps(rows)[:400])   # first filled row: the headers
edit("schedule preset with another category (refused)", [{"op": "schedule_create", "preset": "doors", "category": "windows"}], expect_error="leave 'category' out")

# ------------------------------------------------------------------ room finishes, QA
rf = read("room_finishes", {"level": lv})
out("room_finishes", rf, 6000)
office = next((x for x in rf.get("rooms", []) if x.get("number") == "101"), {})
m = office.get("measured") or {}
check("office floor area = room area", near(m.get("floorM2"), office.get("areaM2"), 0.05), f"{m.get('floorM2')} vs {office.get('areaM2')}")
check("office openings: the door and W01", sorted(o.get("category") for o in office.get("openings") or []) == ["Doors", "Windows"], json.dumps(office.get("openings"))[:300])
check("office net wall = gross - openings", near(m.get("wallNetM2"), (m.get("wallGrossM2") or 0) - (m.get("openingsM2") or 0), 0.01) and (m.get("openingsM2") or 0) > 0, json.dumps(m))
check("office finishes read back", (office.get("finishes") or {}).get("floor") == "Porcelain tiles", "")
check("office walls bounded by both wall types", {b.get("type") for b in office.get("bounding") or []} >= {"Basic Wall : MCP Ext 15+200+50+15", "Basic Wall : MCP Int 15+100+15"}, json.dumps(office.get("bounding"))[:300])
qa = read("arch_qa")
out("arch_qa (clean plan)", qa)
problems = {(i["what"], i["problem"]) for i in qa.get("items") or []}
check("no unmarked or untagged doors / windows, no room problems", not any(p[1] in ("no mark", "not tagged in any view", "unplaced", "not enclosed or redundant") for p in problems), json.dumps(sorted(problems)))
if wtype:
    edit("a second window marked W01", [{"op": "door_window_place", "type": str(wtype["id"]), "items": [{"wall": ext[2], "distance": 2000, "mark": "W01"}]}])
    qa2 = read("arch_qa")
    p2 = {f'{i["what"]}: {i["problem"]}': i["count"] for i in qa2.get("items") or []}
    check("arch_qa finds the duplicate window mark and the untagged window", p2.get("windows: duplicate mark") == 2 and p2.get("windows: not tagged in any view") == 1, json.dumps(p2))

# ------------------------------------------------------------------ room_report workbook (server module, through the same bridge)
try:
    import room_report
    xlsx = HERE / "results" / f"room_report_{REVIT}.xlsx"
    rep = room_report.build(lambda m_, p_=None, timeout=900: read(m_, p_, timeout), {"path": str(xlsx), "overwrite": True})
    out("room_report", rep)
    check("room_report wrote the workbook with 2 rooms", xlsx.exists() and rep.get("rooms") == 2 and "Finishes" in rep.get("sheets", []), "")
except Exception as exc:
    check("room_report", False, str(exc)[:300])

call("model_new", {"activate": True})
cl = call("model_close", {"document_id": doc, "discard_changes": True})
check("scratch closed, nothing saved", cl.get("discardedChanges") is True, json.dumps(cl))
print("\nALL CHECKS AS EXPECTED" if not FAILED else "\nNOT AS EXPECTED: " + "; ".join(FAILED))
sys.exit(1 if FAILED else 0)
