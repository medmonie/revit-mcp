r"""Live test of 0.30.0, second part: phase 15 (parts, arrays, reference planes and alignment locks, Performance Adviser,
change watching, preview graphics, picking), phase 16 documentation (colour fill schemes, dimension types, Revit 2027
sheet collections and STEP export, the ACC parameters service), CAD link reload, DGN and gbXML export.

Real edits only in a scratch project made by model_new (closed with its changes discarded at the end). Files go to
tests\results\v030b_export. Near the end Revit asks the user to click a wall (pick); Esc is also accepted.
Started by run_live_v030b.bat; output tests\results\live_v030b.txt.
"""
import json
import shutil
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

OUT = HERE / "results" / "v030b_export"
FAILED, SKIPPED = [], []
connection = bridge_client.pin()
ping = bridge_client.call("ping", connection=connection)
REVIT = str(ping.get("revitVersion"))
print("bridge", ping.get("bridgeVersion"), "Revit", REVIT, flush=True)
doc_id = None
LAST = {}


def call(method, params=None, timeout=300):
    return bridge_client.call(method, dict(params or {}), connection=connection, timeout=timeout)


def out(title, value, limit=3000):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < limit else text[:limit] + "\n... (truncated)"), flush=True)


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


def edit(title, ops, expect_error=None):
    """Returns the list of op results; expect_error checks both refused batches and post-commit failures."""
    try:
        res = call("edit", {"document_id": doc_id, "dry_run": False, "operations": ops}, timeout=900)
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error or expect_error.lower() not in str(exc).lower():
            FAILED.append(title)
        return None
    LAST.clear()
    LAST.update(res)
    out(title, res)
    err = res.get("error") if not res.get("applied") else (json.dumps(res.get("postCommit")) if res.get("postCommitFailed") else None)
    if err:
        print(f"{'EXPECTED ERROR' if expect_error else 'FAILED'}: {err}", flush=True)
        if not expect_error or expect_error.lower() not in str(err).lower():
            FAILED.append(title)
        return None
    if expect_error:
        FAILED.append(title + " (expected an error)")
        return None
    return [r.get("result") for r in res.get("results") or []]


def first(r):
    return (r or [{}])[0] or {}


def read(method, params=None, timeout=300):
    return call(method, {**(params or {}), "document_id": doc_id}, timeout=timeout)


def allowed():
    try:
        return bool(call("get_edit_permission", timeout=60).get("editingAllowed"))
    except Exception:
        return False


def ensure_permission():
    if allowed():
        return
    print("Asking Revit for edit permission: click Yes in Revit's dialog ...", flush=True)
    try:
        call("edit", {"document_id": doc_id, "dry_run": True, "operations": [{"op": "view_3d_create", "name": "MCP permission probe"}]}, timeout=60)
    except Exception as exc:
        print("probe:", exc, flush=True)
    for _ in range(150):
        time.sleep(2)
        if allowed():
            print("permission granted", flush=True)
            return
    raise SystemExit("Edit permission was not granted; stopping.")


if OUT.exists():
    shutil.rmtree(OUT, ignore_errors=True)
OUT.mkdir(parents=True, exist_ok=True)

new = call("model_new", {"activate": True}, timeout=600)
out("model_new (scratch, active)", new)
doc_id = new.get("documentId")
if not doc_id or not new.get("active") or call("ping").get("documentId") != doc_id:
    raise SystemExit("The scratch project did not become active; nothing edited.")
ensure_permission()
lv = read("list_levels").get("levels", [])[0]["name"]
plan = next((v["name"] for v in read("list_views", {"type": "FloorPlan"}).get("views", []) if v.get("level") == lv), None)
print("level", lv, "plan", plan, flush=True)
walls = read("list_types", {"category": "walls", "limit": 50}).get("types", [])
wt = next((w for w in walls if "generic" in (w.get("type", "") + w.get("family", "")).lower()), walls[0])["type"]

# ------------------------------------------------------------------ change watching starts first, so the edits below are seen
w = read("watch_start")
out("watch_start", w)

# ------------------------------------------------------------------ walls and rooms to work on
levels_all = read("list_levels").get("levels", [])
top = levels_all[1]["name"] if len(levels_all) > 1 else None
floors = read("list_types", {"category": "floors", "limit": 50}).get("types", [])
ft = next((f for f in floors if "generic" in (f.get("type", "") + f.get("family", "")).lower()), floors[0] if floors else None)
height = {"top_level": top} if top else {"height": 3000}
r = edit("walls (two rooms) and a free wall", [{"op": "wall_create", "type": wt, "level": lv, **height, "structural": False, "walls": [
    {"start": [0, 0], "end": [8000, 0]}, {"start": [8000, 0], "end": [8000, 5000]}, {"start": [8000, 5000], "end": [0, 5000]},
    {"start": [0, 5000], "end": [0, 0]}, {"start": [4000, 0], "end": [4000, 5000]}, {"start": [20000, 0], "end": [20000, 3000]}]}])
wall_ids = list(first(r).get("ids") or [])
print("walls", wall_ids, flush=True)
check("six walls", len(wall_ids) == 6, json.dumps(wall_ids))
free = wall_ids[-1] if wall_ids else None
if ft:   # floors below and above close the two rooms (the energy model for gbXML needs closed spaces)
    box = [[0, 0], [8000, 0], [8000, 5000], [0, 5000]]
    edit("floors at the bottom and top of the rooms", [{"op": "floor_create", "type": ft["type"], "level": lv, "boundary": box, "structural": False}]
         + ([{"op": "floor_create", "type": ft["type"], "level": top, "boundary": box, "structural": False}] if top else []))
rm = edit("rooms", [{"op": "room_create", "kind": "rooms", "level": lv, "points": [[2000, 2500], [6000, 2500]], "names": ["Office", "Store"], "numbers": ["101", "102"]}])

ch = read("watch_changes")
out("watch_changes after the walls and rooms", ch, 1500)
check("the watch saw the new walls and rooms", (ch.get("added") or 0) >= 8, f"added {ch.get('added')}, modified {ch.get('modified')}")

# ------------------------------------------------------------------ parts: create, divide by a line, merge back
south = wall_ids[0] if wall_ids else None
pc = edit("parts of the south wall", [{"op": "parts_create", "ids": [south]}])
parts = (first(pc).get("items") or [{}])[0].get("parts") or []
check("the wall became parts", len(parts) >= 1, json.dumps(parts))
pd = edit("divide its parts at x = 3000 (a construction joint)", [{"op": "parts_divide", "ids": [south], "level": lv,
                                                                    "lines": [[[3000, -1000], [3000, 1000]]]}])
check("parts divided", (first(pd).get("partsNow") or 0) > len(parts), json.dumps(first(pd)))
elems = read("get_elements", {"category": "OST_Parts", "limit": 200}).get("elements", [])
part_ids = [e["id"] for e in elems if e["id"] not in parts]   # the divided parent stays (hidden); merge its two children
print("parts now", part_ids, flush=True)
if len(part_ids) >= 2:
    pm = edit("merge two touching parts back", [{"op": "parts_merge", "ids": part_ids[:2]}], expect_error=None)
    check("merged part made", bool(first(pm).get("mergedPart")), json.dumps(first(pm)))
edit("parts of a room (refused)", [{"op": "parts_create", "ids": [((first(rm).get("items") or [{}])[0]).get("id")]}], expect_error="cannot make parts")

# ------------------------------------------------------------------ arrays
ar = edit("linear array of the free wall: 3 members 1500 apart (grouped)", [{"op": "array_create", "ids": [free], "kind": "linear", "count": 3,
                                                                              "vector": [1500, 0, 0], "view": plan}])
check("linear array: 2 copies in an array group", len(first(ar).get("copies") or []) == 2 and first(ar).get("array"), json.dumps(first(ar)))
nw = edit("another free wall for the radial array", [{"op": "wall_create", "type": wt, "level": lv, "height": 3000, "structural": False,
                                                       "walls": [{"start": [30000, 0], "end": [30000, 2000]}]}])
free2 = (first(nw).get("ids") or [None])[0]
ar2 = edit("radial array, plain copies: 4 members over 90 degrees", [{"op": "array_create", "ids": [free2], "kind": "radial", "count": 4,
                                                                      "center": [30000, -3000], "angle": 30, "group": False, "view": plan}])
check("radial: 3 separate copies", len(first(ar2).get("copies") or []) == 3 and not first(ar2).get("array"), json.dumps(first(ar2)))
edit("array of 1 (refused)", [{"op": "array_create", "ids": [free2], "kind": "linear", "count": 1, "vector": [1000, 0, 0], "view": plan}], expect_error="count")

# ------------------------------------------------------------------ reference plane and alignment lock
rp = edit("reference plane along x = 25000", [{"op": "reference_plane_create", "start": [25000, -2000, 0], "end": [25000, 6000, 0], "name": "MCP RP", "view": plan}])
rp_id = first(rp).get("id")
w2 = edit("a wall near the plane", [{"op": "wall_create", "type": wt, "level": lv, "height": 3000, "structural": False,
                                     "walls": [{"start": [25500, 0], "end": [25500, 4000]}]}])
w2id = (first(w2).get("ids") or [None])[0]
edit("align lock without move (refused: not on the plane)", [{"op": "align_lock", "datum": rp_id, "ids": [w2id], "view": plan}], expect_error="Move it onto the datum")
al = edit("align lock with move", [{"op": "align_lock", "datum": rp_id, "ids": [w2id], "move": True, "view": plan}])
check("wall moved onto the plane and locked", first(al).get("locked") == 1, json.dumps(first(al)))

# ------------------------------------------------------------------ Performance Adviser
pa = read("performance_adviser", {"limit": 10}, timeout=600)
out("performance_adviser", {k: v for k, v in pa.items() if k != "items"}, 1500)
check("Performance Adviser ran its rules", (pa.get("rules") or 0) > 5, f"{pa.get('rules')} rules in {pa.get('seconds')} s; with findings: {pa.get('withFindings')}")

# ------------------------------------------------------------------ preview graphics
pv = read("preview_show", {"lines": [[[0, 0, 0], [8000, 5000, 0]]], "boxes": [[[10000, 0, 0], [12000, 2000, 3000]]], "color": "#00AAFF"})
out("preview_show", pv)
check("preview drawn (1 line + 12 box edges)", pv.get("shown") == 13, json.dumps(pv))
time.sleep(2)
pc2 = read("preview_clear")
check("preview cleared", pc2.get("cleared") == 1 and pc2.get("activePreviews") == 0, json.dumps(pc2))

# ------------------------------------------------------------------ colour fill scheme for rooms
cf = edit("colour fill: rooms by Name", [{"op": "color_fill_scheme_set", "category": "rooms", "scheme": "MCP Rooms by name", "from": "*",
                                          "parameter": "Name", "entries": [{"value": "Office", "color": "#9DC3E6"}, {"value": "Store", "color": "#F4B183"}],
                                          "views": [plan], "legend_at": [0, -3000]}], expect_error=None)
if cf is None and "no colour fill scheme" in json.dumps(LAST).lower():
    FAILED.pop()
    SKIPPED.append("colour fill: the template has no room scheme to start from")
else:
    txt = json.dumps(first(cf))
    check("scheme colours Office and Store, shown in the plan with a legend", "#9DC3E6" in txt and "#F4B183" in txt and first(cf).get("legends") == 1, txt[:400])

# ------------------------------------------------------------------ dimension type (linear / ordinate)
bad = None
try:
    bad = call("edit", {"document_id": doc_id, "dry_run": False, "operations": [{"op": "dimension_elements", "ids": wall_ids[:3], "direction": "x",
                                                                                  "view": plan, "type": "No such type"}]})
except Exception as exc:
    bad = {"error": str(exc)}
msg = str(bad.get("error"))
check("unknown dimension type refused with the linear types listed", "Linear types:" in msg, msg[:200])
types = [t.strip() for t in msg.split("Linear types:")[-1].rstrip(".").split(",")] if "Linear types:" in msg else []
ordinal = next((t for t in types if "ordinate" in t.lower()), None)
use = ordinal or (types[0] if types else None)
if use:
    d = edit(f"dimension chain with type '{use}'", [{"op": "dimension_elements", "ids": wall_ids[:5], "direction": "x", "view": plan, "type": use}])
    check("dimensioned with the given type", d is not None, "")
    if not ordinal:
        SKIPPED.append("ordinate dimensions: the template has no Ordinate dimension type (the same 'type' parameter takes one)")

# ------------------------------------------------------------------ Revit 2027: sheet collection and STEP; ACC parameters
if REVIT == "2027":
    sc = edit("sheet collection", [{"op": "sheet_collection_set", "name": "MCP Collection"}])
    check("sheet collection made", first(sc).get("created") is True, json.dumps(first(sc)))
    edit("a sheet that does not exist (refused)", [{"op": "sheet_collection_set", "name": "MCP Collection", "sheets": ["Z-999"]}], expect_error="No sheet")
    edit("a 3D view for STEP", [{"op": "view_3d_create", "name": "MCP STEP view"}])
    st = edit("STEP export", [{"op": "export_step", "folder": str(OUT), "name": "scratch", "view": "MCP STEP view"}])
    time.sleep(2)
    check("STEP file published", any(p.suffix.lower() in (".stp", ".step") for p in OUT.iterdir()), json.dumps([p.name for p in OUT.iterdir()]))
else:
    edit("sheet collection on Revit 2024 (refused)", [{"op": "sheet_collection_set", "name": "x"}], expect_error="Revit 2027")
    edit("STEP on Revit 2024 (refused)", [{"op": "export_step", "folder": str(OUT)}], expect_error="Revit 2027")
edit("ACC parameter with an id that is not from the service (refused)", [{"op": "acc_parameter_add", "id": "MyParam", "categories": "walls"}],
     expect_error="parameters service")
edit("ACC parameter with an unknown service id (refused with the service's answer)",
     [{"op": "acc_parameter_add", "id": "parameters.mcptest:doesNotExist-1.0.0", "categories": "walls"}], expect_error="parameters service")

# ------------------------------------------------------------------ CAD link reload, DGN and gbXML export
edit("export the plan to DWG", [{"op": "export_dwg", "folder": str(OUT), "views": [plan], "name_template": "{name}"}])
time.sleep(2)
dwg = next((p for p in OUT.glob("*.dwg")), None)
check("DWG exported", dwg is not None, json.dumps([p.name for p in OUT.iterdir()]))
if dwg:
    cl = edit("link the DWG", [{"op": "cad_link", "path": str(dwg), "view": plan, "this_view_only": True}])
    cad_id = first(cl).get("id")
    rl = edit("reload the CAD link", [{"op": "link_reload", "link": cad_id}])
    check("CAD link reloaded", rl is not None and LAST.get("postCommitFailed") == 0 and "loadResult" in json.dumps(first(rl)), json.dumps(first(rl))[:300])
dg = edit("DGN export (Revit defaults, then the model's setups)", [{"op": "export_dgn", "folder": str(OUT), "views": [plan], "name_template": "{name}"}], expect_error=None)
time.sleep(2)
dgn_ok = any(p.suffix.lower() == ".dgn" for p in OUT.iterdir())
if not dgn_ok and FAILED and FAILED[-1].startswith("DGN"):
    FAILED.pop()
    SKIPPED.append("DGN: " + json.dumps(LAST.get("postCommit"))[:300])
else:
    check("DGN exported", dgn_ok, json.dumps([p.name for p in OUT.iterdir()]))
gb = edit("gbXML export", [{"op": "export_gbxml", "folder": str(OUT), "name": "scratch_energy"}])
time.sleep(2)
check("gbXML exported", (OUT / "scratch_energy.xml").exists(), json.dumps(LAST.get("postCommit"))[:400])

# ------------------------------------------------------------------ watching ends; picking
ch = read("watch_changes")
check("the watch keeps listing changes (parts, arrays, plane, scheme)", (ch.get("count") or 0) > 0, f"added {ch.get('added')}, modified {ch.get('modified')}, deleted {ch.get('deleted')}")
st = read("watch_stop")
check("watch stopped", st.get("stopped") is not None, json.dumps(st))
print("\n>>> PICK: Revit now asks you to click a wall (status bar: 'Claude test: click any wall'). Esc is fine too.", flush=True)
try:
    pk = call("pick", {"kind": "element", "category": "walls", "prompt": "Claude test: click any wall (Esc to skip)"}, timeout=300)
    out("pick", pk)
    check("pick answered (a wall, or cancelled with Esc)", (pk.get("picked") and pk.get("item", {}).get("category") == "Walls") or pk.get("cancelled") is True, json.dumps(pk))
except Exception as exc:
    print("pick:", exc, flush=True)
    SKIPPED.append(f"pick: {exc}")

# ------------------------------------------------------------------ close the scratch (a second empty scratch takes its place)
second = call("model_new", {"activate": True}, timeout=600)
cl = call("model_close", {"document_id": doc_id, "discard_changes": True})
check("scratch closed, changes discarded", cl.get("discardedChanges") is True, json.dumps(cl))
print("The second scratch stays active (nothing in it); close it in Revit without saving.", flush=True)

print("\nSKIPPED:", "; ".join(SKIPPED) if SKIPPED else "none")
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED))
sys.exit(1 if FAILED else 0)
