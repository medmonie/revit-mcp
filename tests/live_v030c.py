r"""Short check of STEP (Revit 2027) and gbXML export in a scratch project: a closed box of walls up to the next level,
floors at both levels and two rooms. Nothing is saved; files go to tests\results\v030c_export.
Started by run_live_v030c.bat; output tests\results\live_v030c.txt.
"""
import json
import shutil
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

OUT = HERE / "results" / "v030c_export"
FAILED = []
conn = bridge_client.pin()
REVIT = str(bridge_client.call("ping", connection=conn).get("revitVersion"))
print("Revit", REVIT, flush=True)


def call(m, p=None, timeout=900):
    return bridge_client.call(m, dict(p or {}), connection=conn, timeout=timeout)


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


def edit(title, ops):
    res = call("edit", {"document_id": doc, "dry_run": False, "operations": ops})
    ok = res.get("applied") and not res.get("postCommitFailed")
    print(f"===== {title}: {'ok' if ok else 'FAILED'} {'' if ok else json.dumps(res.get('error') or res.get('postCommit'))[:600]}", flush=True)
    if not ok:
        FAILED.append(title)
    return [r.get("result") for r in res.get("results") or []]


shutil.rmtree(OUT, ignore_errors=True)
OUT.mkdir(parents=True, exist_ok=True)
new = call("model_new", {"activate": True})
doc = new["documentId"]
for _ in range(150):
    if call("get_edit_permission").get("editingAllowed"):
        break
    if _ == 0:
        print("Asking for edit permission: click Yes in Revit ...", flush=True)
        try:
            call("edit", {"document_id": doc, "dry_run": True, "operations": [{"op": "view_3d_create", "name": "probe"}]}, timeout=60)
        except Exception as exc:
            print("probe:", exc, flush=True)
    time.sleep(2)
levels = call("list_levels", {"document_id": doc}).get("levels", [])
lv, top = levels[0]["name"], levels[1]["name"]
wt = next(t for t in call("list_types", {"document_id": doc, "category": "walls"}).get("types", []) if "generic" in t["type"].lower())["type"]
ft = next(t for t in call("list_types", {"document_id": doc, "category": "floors"}).get("types", []) if "generic" in t["type"].lower())["type"]
box = [[0, 0], [8000, 0], [8000, 5000], [0, 5000]]
edit("closed box", [
    {"op": "wall_create", "type": wt, "level": lv, "top_level": top, "structural": False, "walls": [
        {"start": [0, 0], "end": [8000, 0]}, {"start": [8000, 0], "end": [8000, 5000]}, {"start": [8000, 5000], "end": [0, 5000]},
        {"start": [0, 5000], "end": [0, 0]}, {"start": [4000, 0], "end": [4000, 5000]}]},
    {"op": "floor_create", "type": ft, "level": lv, "boundary": box, "structural": False},
    {"op": "floor_create", "type": ft, "level": top, "boundary": box, "structural": False},
    {"op": "room_create", "kind": "rooms", "level": lv, "points": [[2000, 2500], [6000, 2500]], "names": ["A", "B"], "numbers": ["1", "2"]},
    {"op": "view_3d_create", "name": "MCP export view"}])
edit("gbXML export", [{"op": "export_gbxml", "folder": str(OUT), "name": "box"}])
time.sleep(2)
check("gbXML file", (OUT / "box.xml").exists(), json.dumps([p.name for p in OUT.iterdir()]))
if REVIT == "2027":
    edit("STEP export", [{"op": "export_step", "folder": str(OUT), "name": "box", "view": "MCP export view"}])
    time.sleep(2)
    check("STEP file", any(p.suffix.lower() in (".stp", ".step") for p in OUT.iterdir()), json.dumps([p.name for p in OUT.iterdir()]))
second = call("model_new", {"activate": True})
cl = call("model_close", {"document_id": doc, "discard_changes": True})
check("scratch closed, nothing saved", cl.get("discardedChanges") is True, json.dumps(cl))
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED))
sys.exit(1 if FAILED else 0)
