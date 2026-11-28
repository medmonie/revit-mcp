r"""Live test of the 0.18.1 API-alignment fixes, straight to the add-in. READS and DRY RUNS only, on Task 04.

  python live_audit.py     refuses unless the active model is 'Revit Task 04...'

Checks:
  1. Length-type specs other than SpecTypeId.Length (bar diameter, rebar spacing ...) follow the request units, and
     every number parameter names its unit (parameterDetails[].unit).
  2. rebar_list reports weightSource ('d2/162' in Revit 2024, 'model' in 2027 when the bar type has a mass).
  3. A dry run commits on trial inside a rolled-back transaction group: Revit's commit warnings (two overlapping
     walls) are reported, and the model is left with no unsaved changes and the same counts.
  4. find_overlaps still computes volumes (overlap volume now per solid pair).

Started by run_live_audit.bat; output in tests\results\live_audit.txt.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

FAILED = []
connection = bridge_client.pin()
info = bridge_client.call("get_project_info", connection=connection)
document_id = info.get("documentId")
if not document_id:
    raise SystemExit("The bridge did not identify the active test document.")
if not info.get("title", "").startswith("Revit Task 04"):
    raise SystemExit("The active model is not Task 04; nothing done.")


def call(method, params=None, **kwargs):
    return bridge_client.call(method, {**(params or {}), "document_id": document_id}, connection=connection, **kwargs)


def out(title, value):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < 5000 else text[:5000] + "\n... (truncated)"), flush=True)


def read(title, method, params=None, show=True):
    try:
        res = call(method, params or {}, timeout=300)
        if show:
            out(title, res)
        return res
    except Exception as exc:
        print(f"===== {title}\nFAILED: {exc}", flush=True)
        FAILED.append(title)
        return None


def check(title, condition, detail=""):
    print(f"----- check {title}: {'OK' if condition else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not condition:
        FAILED.append(title)


def allowed():
    try:
        return call("get_edit_permission", {}).get("editingAllowed")
    except Exception:
        return False


def ensure_permission():
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


before = {k: info.get(k) for k in ("title", "hasUnsavedChanges", "counts")}
out("get_project_info (before)", before)
print("bridge", bridge_client.call("ping", connection=connection).get("bridgeVersion"), flush=True)

# 1 + 2. Rebar: units of length-type specs, unit labels, weight source.
rl = read("rebar_list (3)", "rebar_list", {"limit": 3})
bars = (rl or {}).get("bars", [])
check("rebar_list weightSource", bool(bars) and all(b.get("weightSource") in ("d2/162", "model") for b in bars),
      json.dumps([(b.get("id"), b.get("weightKg"), b.get("weightSource")) for b in bars]))
check("rebar_list weightNote", bool((rl or {}).get("weightNote")))

if bars:
    bid = bars[0]["id"]
    mm = read("get_element rebar (mm)", "get_element", {"id": bid, "units": "mm", "include_type": True}, show=False)
    m = read("get_element rebar (m)", "get_element", {"id": bid, "units": "m", "include_type": True}, show=False)
    if mm and m:
        rows = []
        for key in ("parameterDetails", "typeParameterDetails"):
            a = {(d["name"], d.get("spec")): d for d in mm.get(key, [])}
            b = {(d["name"], d.get("spec")): d for d in m.get(key, [])}
            for k, d in a.items():
                if isinstance(d.get("value"), (int, float)) and not isinstance(d.get("value"), bool) and k in b:
                    rows.append({"param": d["name"], "spec": (d.get("spec") or "").split(":")[-1], "unit": d.get("unit"),
                                 "mm": d["value"], "m": b[k].get("value"), "unitInM": b[k].get("unit")})
        out("number parameters of the rebar (and its type) in mm and m", rows)
        lengths = [r for r in rows if r["unit"] == "mm"]
        other_len = [r for r in lengths if not r["spec"].startswith("length")]
        measured = [r for r in rows if r["spec"] and "int" not in r["spec"].split("-")[0]]   # whole-number specs have no unit
        check("every measured number parameter names its unit", all(r["unit"] for r in measured),
              json.dumps([r["param"] for r in measured if not r["unit"]]))
        check("length values scale with units (mm = 1000 x m)",
              all(abs(r["mm"] - 1000 * r["m"]) <= max(0.002, 1e-6 * abs(r["mm"])) and r["unitInM"] == "m" for r in lengths),
              json.dumps([r for r in lengths if abs(r["mm"] - 1000 * r["m"]) > max(0.002, 1e-6 * abs(r["mm"]))][:5]))
        check("length-type specs other than 'length' now follow units", len(other_len) > 0,
              json.dumps(sorted({r["spec"] for r in other_len})))

# 3. Trial-commit dry run: two identical walls overlap, a warning Revit gives only at commit.
ensure_permission()
wtypes = (read("wall types", "list_types", {"category": "walls", "limit": 50}, show=False) or {}).get("types", [])
levels = (read("levels", "list_levels", {}, show=False) or {}).get("levels", [])
if wtypes and levels:
    basic = next((w for w in wtypes if w.get("family") == "Basic Wall"), wtypes[0])
    seg = {"start": [-50000, -50000], "end": [-45000, -50000]}
    res = call("edit", {"operations": [{"op": "wall_create", "type": str(basic["id"]), "level": levels[0]["name"],
                                        "walls": [seg, seg], "height": 3000, "structural": False}], "dry_run": True}, timeout=300)
    out("dry run: two identical walls", res)
    check("dry run committed on trial", res.get("trialCommit") == "Committed", str(res.get("trialCommit")))
    check("dry run rolled the group back", res.get("transactionStatus") == "RolledBack", str(res.get("transactionStatus")))
    check("dry run reports Revit's commit warning (overlap)", len(res.get("revitWarnings") or []) > 0, json.dumps(res.get("revitWarnings")))
    check("dry run previewSucceeded", res.get("previewSucceeded") is True)
else:
    check("wall types and levels for the dry run", False)

# 4. Overlap volumes.
ov = read("find_overlaps (volumes, 20 s)", "find_overlaps", {"volumes": True, "max_seconds": 20, "limit": 10})
if ov is not None:
    print("overlap summary:", json.dumps({k: v for k, v in ov.items() if k not in ("pairs",)}, default=str)[:1500], flush=True)

after = bridge_client.call("get_project_info", connection=connection)
after = {k: after.get(k) for k in ("title", "hasUnsavedChanges", "counts")}
out("get_project_info (after)", after)
check("Task 04 unchanged (no unsaved changes, same counts)", after == before, json.dumps(after))

print("\nALL CHECKS AS EXPECTED" if not FAILED else "\nNOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
