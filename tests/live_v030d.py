r"""Live test for closing phases 5 and 13 on 0.30.1: shared parameters added and removed for real (phase 5 had only dry
runs), and Mohamed's wall opening family placed where a duct crosses a wall (sleeve_place with a wall-hosted family).

Real edits only in a scratch project made by model_new (closed with its changes discarded). Started by the
live_close_chain runner (or run_live_v030d.bat); output tests\results\live_v030d_<revit>.txt.
"""
import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

OPENING = r"C:\Users\mohamed.hamed\Desktop\New folder\Wall Opening.rfa"
FAILED, SKIPPED = [], []
conn = bridge_client.pin()
REVIT = str(bridge_client.call("ping", connection=conn).get("revitVersion"))
print("Revit", REVIT, flush=True)
doc = None
LAST = {}


def call(m, p=None, timeout=900):
    return bridge_client.call(m, dict(p or {}), connection=conn, timeout=timeout)


def read(m, p=None):
    return call(m, {**(p or {}), "document_id": doc})


def out(title, v, limit=2500):
    t = json.dumps(v, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (t if len(t) < limit else t[:limit] + "\n... (truncated)"), flush=True)


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


def edit(title, ops, expect_error=None):
    try:
        res = call("edit", {"document_id": doc, "dry_run": False, "operations": ops})
    except Exception as exc:
        res = {"applied": False, "error": str(exc)}
    LAST.clear()
    LAST.update(res)
    out(title, res)
    err = res.get("error") if not res.get("applied") else (json.dumps(res.get("postCommit")) if res.get("postCommitFailed") else None)
    if expect_error:
        check(f"{title} refused", bool(err) and expect_error.lower() in str(err).lower(), str(err)[:200])
        return None
    if err:
        FAILED.append(title)
        return None
    return [r.get("result") for r in res.get("results") or []]


def first(r):
    return (r or [{}])[0] or {}


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
wt = next(t for t in read("list_types", {"category": "walls"})["types"] if "generic" in t["type"].lower())["type"]

# ------------------------------------------------------------------ phase 5: shared parameters, real add and remove
names = ["MCP Test Text", "MCP Test Length"]
edit("parameter_add: a text and a length parameter on walls", [
    {"op": "parameter_add", "name": names[0], "categories": "walls", "data_type": "text"},
    {"op": "parameter_add", "name": names[1], "categories": "walls", "data_type": "length", "binding": "type"}])
lp = read("list_parameters")
found = [x for x in json.dumps(lp) .split('"') if x in names]
check("both parameters bound in the model", set(found) == set(names), json.dumps([x for x in (lp.get("parameters") or []) if x.get("name") in names])[:400])
edit("parameter_remove both", [{"op": "parameter_remove", "names": names}])
lp2 = read("list_parameters")
check("both parameters gone", not any(n in json.dumps(lp2) for n in names), "")

# ------------------------------------------------------------------ phase 13 / 18: the wall opening family at a duct crossing
w = edit("a wall", [{"op": "wall_create", "type": wt, "level": lv, "height": 3000, "structural": False, "walls": [{"start": [0, 0], "end": [6000, 0]}]}])
wall = (first(w).get("ids") or [None])[0]
d = edit("a duct through it (rectangular 400 x 300 at 1500)", [{"op": "mep_run_create", "kind": "duct", "level": lv,
                                                                 "runs": [{"start": [3000, -2000, 1500], "end": [3000, 2000, 1500]}], "width": 400, "height": 300}])
if d is None:
    d = edit("a duct through it (round 300 at 1500)", [{"op": "mep_run_create", "kind": "duct", "level": lv,
                                                        "runs": [{"start": [3000, -2000, 1500], "end": [3000, 2000, 1500]}], "diameter": 300}])
duct = ((first(d).get("items") or [{}])[0]).get("id")
cr = read("mep_crossings")
check("mep_crossings finds the duct through the wall", wall and duct and str(wall) in json.dumps(cr) and str(duct) in json.dumps(cr), "")
fl = edit("load Wall Opening.rfa", [{"op": "family_load", "path": OPENING}])
fam = first(fl)
out("family loaded", fam)
types = fam.get("types") or []
if not types:
    SKIPPED.append("wall opening: the family did not load")
else:
    fi = read("family_inspect", {"family": fam["family"]})
    pars = fi.get("parameters") or []
    out("family placement and hosting", {"placement": fi.get("placement"), "hosting": fi.get("hosting")})
    out("family parameters", [(x["name"], x["instance"], x["kind"]) for x in pars if not x.get("builtIn")], 3000)
    inst_len = [x["name"] for x in pars if x.get("instance") and not x.get("builtIn") and "length" in str(x.get("kind")).lower() and not x.get("formula")]
    values = {}
    for n in inst_len:
        ln = n.lower()
        if "width" in ln:
            values[n] = "run_width + 100"
        elif "height" in ln:
            values[n] = "run_height + 100"
        elif "diameter" in ln:
            values[n] = "run_outside_diameter + 100"
        elif "depth" in ln or "thickness" in ln:
            values[n] = "crossing_length"
    print("values set on the opening:", values, flush=True)
    vol0 = (read("get_elements", {"ids": [wall], "params": ["Volume"]})["elements"][0].get("params") or {}).get("Volume")
    sp = edit("sleeve_place: the wall opening at the crossing", [{"op": "sleeve_place", "type": str(types[0]["id"]),
                                                                   "items": [{"run": duct, "crossing": wall}], "set": values}])
    item = (first(sp).get("items") or [{}])[0]
    check("opening in / on the crossed wall", wall in (item.get("hostedOn"), item.get("placedOnFaceOf")), json.dumps(item)[:500])
    check("its void cuts the wall", item.get("cut") == "cuts the wall", str(item.get("cut")))
    vol1 = (read("get_elements", {"ids": [wall], "params": ["Volume"]})["elements"][0].get("params") or {}).get("Volume")
    print("wall volume before / after:", vol0, vol1, flush=True)
    check("the wall lost volume where the opening cuts it", vol0 is not None and vol1 is not None and str(vol0) != str(vol1), f"{vol0} -> {vol1}")
    edit("the opening on a link's wall (refused)", [{"op": "sleeve_place", "type": str(types[0]["id"]), "crossing_link": "nope",
                                                     "items": [{"run": duct, "crossing": wall}]}], expect_error="link")

second = call("model_new", {"activate": True})
cl = call("model_close", {"document_id": doc, "discard_changes": True})
check("scratch closed, nothing saved", cl.get("discardedChanges") is True, json.dumps(cl))
print("\nSKIPPED:", "; ".join(SKIPPED) if SKIPPED else "none")
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED))
sys.exit(1 if FAILED else 0)
