r"""Live test of 0.25.0 MEP / HVAC (phase 18, first part) on the detached HVAC model B-M-HV (Mohamed allowed real,
never-saved test edits in the detached copies; never synced, never saved).

Opens KTR15-G26-Z2-B-M-HV-MDL-ZZ-90901-00.rvt active with model_open, reads mep_flow / mep_connectivity /
air_terminals, then edits: mep_route (L-shaped connected run with an elbow), mep_connect (a short duct under a main,
branched onto it with a tap), family_place of a mechanical equipment and mep_system_create with it as base equipment;
refusals. Ends by opening Task 04 active and closing the HVAC copy WITHOUT saving. Also batch_read with several actions.
Started by run_live_hvac.bat; output in tests\results\live_hvac.txt.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

FOLDER = r"C:\Users\mohamed.hamed\Desktop\New folder\detached\detached"
HV = FOLDER + r"\KTR15-G26-Z2-B-M-HV-MDL-ZZ-90901-00.rvt"
TASK04 = r"C:\Users\mohamed.hamed\Desktop\New folder\Revit Task 04_Mohamed A.Hamed_66088960.rvt"
FAILED, SKIPPED = [], []
connection = bridge_client.pin()
print("bridge", bridge_client.call("ping", connection=connection).get("bridgeVersion"), flush=True)


def call(method, params=None, timeout=300):
    return bridge_client.call(method, dict(params or {}), connection=connection, timeout=timeout)


def out(title, value, limit=5000):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < limit else text[:limit] + "\n... (truncated)"), flush=True)


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


def edit(title, ops, expect_error=False, contains=None):
    try:
        res = call("edit", {"document_id": hv_id, "dry_run": False, "operations": ops}, timeout=600)
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error or (contains and contains not in str(exc)):
            FAILED.append(title)
        return None
    out(title, res)
    if not res.get("applied"):
        print(f"{'EXPECTED ERROR' if expect_error else 'FAILED'}: {res.get('error')}", flush=True)
        if not expect_error or (contains and contains not in str(res.get("error"))):
            FAILED.append(title)
        return None
    if expect_error:
        FAILED.append(title + " (expected an error)")
        return None
    return (res.get("results") or [{}])[0].get("result")


def allowed():
    try:
        return bool(call("get_edit_permission", timeout=60).get("editingAllowed"))
    except Exception:
        return False   # Revit is busy while its permission dialog is open


def ensure_permission():
    if allowed():
        return
    print("Asking Revit for edit permission: click Yes in Revit's dialog ...", flush=True)
    try:
        call("edit", {"document_id": hv_id, "dry_run": True,
                      "operations": [{"op": "view_3d_create", "name": "MCP permission probe"}]}, timeout=60)
    except Exception as exc:
        print("probe:", exc, flush=True)
    for _ in range(150):
        time.sleep(2)
        if allowed():
            print("permission granted", flush=True)
            return
    raise SystemExit("Edit permission was not granted; stopping.")


# --- batch_read with several actions over the MEP models (each opened once)
br = call("batch_read", {"paths": [FOLDER + r"\KTR15-G26-Z2-B-M-HV-MDL-ZZ-90901-00.rvt", FOLDER + r"\KTR15-G26-Z2-B-M-PL-MDL-ZZ-90901-00.rvt"],
                         "actions": [{"action": "mep_connectivity", "params": {"limit": 3}}, {"action": "mep_flow", "params": {"kind": "duct", "limit": 3}},
                                     {"action": "air_terminals", "params": {"limit": 3}}]}, timeout=1800)
out("batch_read three actions over HV and PL", br, 8000)
check("batch_read with actions: both files, three results each", br.get("read") == 2 and br.get("failed") == 0
      and all(len(r.get("results", {})) == 3 for r in br.get("results", [])), json.dumps({k: br.get(k) for k in ("read", "failed", "seconds")}))

# --- open the HVAC copy active
o = call("model_open", {"path": HV, "activate": True}, timeout=900)
out("model_open HV active", o)
hv_id = o.get("documentId")
if not hv_id or o.get("alreadyOpen") or not o.get("active") or call("ping").get("documentId") != hv_id:
    raise SystemExit("The test requires a newly opened, active HVAC copy. No test edits were sent.")
fl = call("mep_flow", {"kind": "duct", "limit": 5})
out("mep_flow ducts", fl)
check("mep_flow reads every duct with systems", fl.get("elements", 0) > 500 and len(fl.get("systems", [])) > 5, f"{fl.get('elements')} ducts, {len(fl.get('systems', []))} systems")
fv = call("mep_flow", {"kind": "duct", "max_velocity": 0.5, "limit": 5})
check("max_velocity lists only runs over the limit", all(r.get("flags") for r in fv.get("rows", [])), f"over: {fv.get('overLimits')}")
fp = call("mep_flow", {"kind": "pipe", "limit": 3})
out("mep_flow pipes", {k: fp.get(k) for k in ("elements", "systems")})
cn0 = call("mep_connectivity", {"limit": 5})
out("mep_connectivity (before)", cn0)
at = call("air_terminals", {"limit": 5})
out("air_terminals", at)
check("air_terminals answers (no spaces note)", "terminals" in at)

ensure_permission()
# a straight, long, rectangular main (largest)
ducts = call("get_elements", {"category": "ducts", "limit": 2000, "params": ["Width", "Height", "Diameter"]}).get("elements", [])


def horizontal_main(e):
    loc = e.get("location") or {}
    s, t = loc.get("start"), loc.get("end")
    if not s or not t or abs(s[2] - t[2]) > 1:
        return None
    w = (e.get("params") or {}).get("Width")
    if not isinstance(w, (int, float)) or w < 400:
        return None
    return loc.get("length", 0)


mains = sorted([e for e in ducts if horizontal_main(e)], key=lambda e: -horizontal_main(e))
main = mains[0] if mains else None
out("main chosen", main)
if not main:
    SKIPPED.append("no straight rectangular main of 400 mm or more")
else:
    s, t = main["location"]["start"], main["location"]["end"]
    mid = [(s[0] + t[0]) / 2, (s[1] + t[1]) / 2, s[2]]
    level = main.get("level")
    h = (main.get("params") or {}).get("Height") or 300
    # --- mep_connect: a short duct under the main, branched onto it
    st = edit("short duct under the main (mep_run_create)", [{"op": "mep_run_create", "kind": "duct", "level": level, "width": 200, "height": 200,
               "runs": [{"start": [mid[0], mid[1], mid[2] - h / 2 - 900], "end": [mid[0], mid[1], mid[2] - h / 2 - 400]}]}])
    stub = ((st or {}).get("items") or [{}])[0].get("id")
    if stub:
        cx = edit("mep_connect the stub to the main", [{"op": "mep_connect", "element": stub, "main": main["id"]}])
        check("branch and tap / tee made", cx and cx.get("branch") and cx.get("fitting"), json.dumps(cx))
        g = call("get_element", {"id": stub})
        cn = call("mep_connectivity", {"category": "ducts", "limit": 2000})
        stub_open = [i for i in cn.get("openEndItems", []) if i["id"] == stub]
        check("the stub's top end is connected now (one open end left)", len(stub_open) == 1, json.dumps(stub_open))
    # --- refusal: connector line misses the main
    far = edit("far stub (mep_run_create)", [{"op": "mep_run_create", "kind": "duct", "level": level, "width": 200, "height": 200,
               "runs": [{"start": [mid[0] + 50000, mid[1] + 50000, mid[2] - 1500], "end": [mid[0] + 50000, mid[1] + 50000, mid[2] - 1000]}]}])
    far_id = ((far or {}).get("items") or [{}])[0].get("id")
    if far_id:
        edit("mep_connect when the line misses the main (refused)", [{"op": "mep_connect", "element": far_id, "main": main["id"]}],
             expect_error=True, contains="misses the main")
    # --- mep_route: an L-shaped connected run away from the model
    X, Y = mid[0] + 60000, mid[1] + 60000
    rt = edit("mep_route L-shaped duct run", [{"op": "mep_route", "kind": "duct", "level": level, "z": 3000, "width": 400, "height": 300,
               "path": [[X, Y], [X + 4000, Y], [X + 4000, Y + 3000]]}])
    check("two segments and one elbow", rt and len(rt.get("items", [])) == 2 and len(rt.get("fittings", [])) == 1, json.dumps(rt)[:400])
    edit("mep_route with three points in a line (refused)", [{"op": "mep_route", "kind": "duct", "level": level, "z": 3000, "width": 400, "height": 300,
          "path": [[X, Y + 9000], [X + 2000, Y + 9000], [X + 4000, Y + 9000]]}], expect_error=True, contains="straight line")
    # --- equipment and a system
    eqt = call("list_types", {"category": "mechanical equipment"}).get("types", [])
    out("mechanical equipment types", eqt[:10])
    placed = None
    for t_ in eqt[:6]:
        r = None
        try:
            r = call("edit", {"document_id": hv_id, "dry_run": False, "operations": [
                {"op": "family_place", "category": "mechanical equipment", "type": str(t_["id"]), "level": level, "points": [[X, Y + 20000]], "offset": 2500}]}, timeout=600)
        except Exception as exc:
            print("family_place", t_.get("family"), t_.get("type"), "->", exc, flush=True)
        if r and r.get("applied"):
            placed = (((r.get("results") or [{}])[0].get("result") or {}).get("ids") or [None])[0]
            print("placed equipment", t_.get("family"), t_.get("type"), placed, flush=True)
            break
    if placed:
        edit("duct system from a unit with no duct connector (refused)", [{"op": "mep_system_create", "kind": "duct", "elements": [placed]}],
             expect_error=True, contains="free connector")
        edit("system with only its base equipment (refused)", [{"op": "mep_system_create", "kind": "pipe",
             "elements": [placed], "base_equipment": placed}], expect_error=True, contains="besides its base equipment")
        r2 = call("edit", {"document_id": hv_id, "dry_run": False, "operations": [
            {"op": "family_place", "category": "mechanical equipment", "type": str(t_["id"]), "level": level, "points": [[X + 3000, Y + 20000]], "offset": 2500}]}, timeout=600)
        second = (((r2.get("results") or [{}])[0].get("result") or {}).get("ids") or [None])[0] if r2.get("applied") else None
        print("second unit", second, flush=True)
        sy = edit("mep_system_create piping system from two units (type by classification), first unit as base", [{"op": "mep_system_create", "kind": "pipe",
                   "elements": [placed, second], "base_equipment": placed, "name": "MCP Test System"}])
        check("system created with base equipment", sy and sy.get("baseEquipment") == placed, json.dumps(sy))
        ms = call("mep_systems", {"kind": "pipe", "limit": 3000})
        check("the new system reads back", any(r.get("name") == "MCP Test System" for r in ms.get("systems", [])), "")
    else:
        SKIPPED.append("no mechanical equipment type could be placed at a point")
    flo = call("mep_flow", {"kind": "duct", "system": "MCP", "limit": 5})
    out("mep_flow of the new system", flo)

# --- leave: Task 04 active, the HVAC copy closed without saving
call("model_open", {"path": TASK04, "activate": True}, timeout=900)
cl = call("model_close", {"document_id": hv_id, "discard_changes": True})
out("model_close HV (changes discarded)", cl)
check("HV copy closed without saving", cl.get("discardedChanges") is True)
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
sys.exit(1 if FAILED else 0)
