r"""Live test of 0.26.3 (phase 18, second part) on the detached HVAC model B-M-HV (Mohamed allowed real, never-saved
test edits in the detached copies; never synced, never saved).

Opens the HV copy active, links the detached architecture model X-A-GN into it, then: spaces_from_rooms, space_airflow_set,
hvac_zone_create, air_terminal_place (with flows, connected to a supply main), mep_system_add / mep_system_remove,
air_terminals read back, and refusals. Ends by opening Task 04 active and closing the HV copy WITHOUT saving (the link
and every edit are discarded with it). Started by run_live_hvac2.bat; output in tests\results\live_hvac2.txt.
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



ARCH = FOLDER + r"\KTR15-G26-Z2-X-A-GN-MDL-ZZ-90901-00.rvt"
LIB = r"C:\ProgramData\Autodesk\RVT 2024\Libraries"


def result_of(res):
    return (res.get("results") or [{}])[0].get("result") if res and res.get("applied") else None


def first_result(title, ops):
    return edit(title, ops)


# --- open the HVAC copy active
o = call("model_open", {"path": HV, "activate": True}, timeout=900)
out("model_open HV active", o)
hv_id = o.get("documentId")
if not hv_id or o.get("alreadyOpen") or not o.get("active") or call("ping").get("documentId") != hv_id:
    raise SystemExit("The test requires a newly opened, active HVAC copy. No test edits were sent.")
ensure_permission()

# --- link the architecture model (shared coordinates first, else origin to origin)
import os  # noqa: E402
arch = ARCH if os.path.exists(ARCH) else next((os.path.join(FOLDER, f) for f in os.listdir(FOLDER) if "-A-GN" in f and f.endswith(".rvt")), None)
print("architecture model:", arch, flush=True)
lk = None
for placement in ("shared", "origin"):
    try:
        r = call("edit", {"document_id": hv_id, "dry_run": False,
                          "operations": [{"op": "revit_link", "path": arch, "placement": placement, "pin": True}]}, timeout=900)
    except Exception as exc:
        print("revit_link", placement, "->", exc, flush=True)
        continue
    out("revit_link " + placement, r)
    lk = result_of(r)
    if lk:
        break
check("architecture model linked", lk and lk.get("id"), json.dumps(lk))

spaces = []
if lk:
    sf = edit("spaces_from_rooms", [{"op": "spaces_from_rooms", "link": lk["id"]}])
    items = (sf or {}).get("items", [])
    check("spaces created from the linked rooms", sf and sf.get("created", 0) > 0,
          json.dumps({k: (sf or {}).get(k) for k in ("rooms", "created", "notEnclosed", "skippedCount")}))
    spaces = [i for i in items if i.get("status") == "ok"]
    check("most new spaces are enclosed by the linked walls", sf and len(spaces) >= max(1, sf.get("created", 0) // 2), f"{len(spaces)} enclosed")
    again = edit("spaces_from_rooms again (all skipped, nothing new)", [{"op": "spaces_from_rooms", "link": lk["id"]}])
    check("second run skips rooms that have a space", again is not None and again.get("created") == 0, json.dumps({k: (again or {}).get(k) for k in ("created", "skippedCount")}))

if len(spaces) >= 2:
    a, b = spaces[0], spaces[1]
    af = edit("space_airflow_set supply / return", [{"op": "space_airflow_set", "spaces": [a["id"], b["id"]], "supply": [200, 150], "return": 120}])
    check("design airflow set and read back", af and [i.get("supplyLps") for i in af.get("items", [])] == [200, 150]
          and all(i.get("returnLps") == 120 for i in af.get("items", [])), json.dumps(af))
    edit("space_airflow_set with a wrong count (refused)", [{"op": "space_airflow_set", "spaces": [a["id"], b["id"]], "supply": [1, 2, 3]}],
         expect_error=True, contains="one value per space")
    same = [s_ for s_ in spaces if s_.get("level") == a.get("level")]
    if len(same) >= 2:
        zn = edit("hvac_zone_create", [{"op": "hvac_zone_create", "spaces": [same[0]["id"], same[1]["id"]], "name": "MCP Test Zone"}])
        check("zone with two spaces", zn and zn.get("spaces") == 2 and zn.get("name") == "MCP Test Zone", json.dumps(zn))
    other = next((s_ for s_ in spaces if s_.get("level") != a.get("level")), None)
    if other:
        edit("hvac_zone_create across two levels (refused)", [{"op": "hvac_zone_create", "spaces": [a["id"], other["id"]]}],
             expect_error=True, contains="one level")
else:
    SKIPPED.append("fewer than two enclosed spaces: airflow and zone steps skipped")

# --- air terminals: a supply type (load one from the Revit library if the model has none)
types = call("list_types", {"category": "air terminals"}).get("types", [])
out("air terminal types", types[:15])
supply = [t_ for t_ in types if "supply" in (t_.get("family", "") + t_.get("type", "")).lower()]
if not supply:
    found = []
    for root, _dirs, files in os.walk(LIB):
        if "air terminal" in root.lower() or "air-side" in root.lower():
            found += [os.path.join(root, f) for f in files if f.lower().endswith(".rfa") and "supply" in f.lower() and "diffuser" in f.lower()]
    found.sort(key=lambda f: (0 if "\\UK\\" in f or "\\Egypt\\" in f else 1, len(f)))
    print("library diffusers:", found[:8], flush=True)
    for f in found[:4]:
        fl_ = edit("family_load " + os.path.basename(f), [{"op": "family_load", "path": f}])
        if fl_:
            break
    types = call("list_types", {"category": "air terminals"}).get("types", [])
    supply = [t_ for t_ in types if "supply" in (t_.get("family", "") + t_.get("type", "")).lower()] or types
out("supply terminal types", supply[:5])

ducts = call("get_elements", {"category": "ducts", "limit": 3000, "params": ["Width", "Height", "System Classification", "System Name"]}).get("elements", [])


def supply_main(e):
    loc = e.get("location") or {}
    s_, t_ = loc.get("start"), loc.get("end")
    pr = e.get("params") or {}
    if not s_ or not t_ or abs(s_[2] - t_[2]) > 1 or "supply" not in str(pr.get("System Classification", "")).lower():
        return None
    w = pr.get("Width")
    return loc.get("length", 0) if isinstance(w, (int, float)) and w >= 300 and loc.get("length", 0) > 3000 else None


mains = sorted([e for e in ducts if supply_main(e)], key=lambda e: -supply_main(e))
main = mains[0] if mains else None
out("supply main chosen", main)
levels = {lv["name"]: lv for lv in call("list_levels").get("levels", [])}
if not supply or not main:
    SKIPPED.append("no supply air terminal type or no straight supply main")
else:
    s_, t_ = main["location"]["start"], main["location"]["end"]
    lvl = main.get("level")
    elev = (levels.get(lvl) or {}).get("elevation", 0)
    h = (main.get("params") or {}).get("Height") or 300
    bottom = s_[2] - h / 2 - elev
    height = min(2700, bottom - 500)
    p1 = [s_[0] + (t_[0] - s_[0]) / 3, s_[1] + (t_[1] - s_[1]) / 3]
    p2 = [s_[0] + 2 * (t_[0] - s_[0]) / 3, s_[1] + 2 * (t_[1] - s_[1]) / 3]
    ty = str(supply[0]["id"])
    edit("air_terminal_place with a wrong flows count (refused)", [{"op": "air_terminal_place", "type": ty, "level": lvl, "points": [p1, p2], "height": height, "flows": [1, 2, 3]}],
         expect_error=True, contains="one value per point")
    tp = edit("air_terminal_place two terminals with flows, connected to the main", [{"op": "air_terminal_place", "type": ty, "level": lvl, "points": [p1, p2],
              "height": height, "flows": [100, 120], "main": main["id"]}])
    its = (tp or {}).get("items", [])
    check("two terminals placed and connected", tp and tp.get("created") == 2 and tp.get("connected") == 2, json.dumps({k: (tp or {}).get(k) for k in ("placement", "created", "connected")}))
    check("flows as given", [i.get("flowLps") for i in its] == [100, 120], json.dumps([(i.get("flowLps"), i.get("connectorFacing"), i.get("classification")) for i in its]))
    # a third terminal, not connected, then added to and removed from the main's system
    far = [p1[0] + 30000, p1[1] + 30000]
    t3 = edit("air_terminal_place one free terminal", [{"op": "air_terminal_place", "type": ty, "level": lvl, "points": [far], "height": height, "flows": 80}])
    t3id = ((t3 or {}).get("items") or [{}])[0].get("id")
    check("single flow value applied", ((t3 or {}).get("items") or [{}])[0].get("flowLps") == 80, json.dumps((t3 or {}).get("items")))
    # the systems the connected terminals are in after the commit (never a new default "Mechanical Supply Air n")
    at0 = call("air_terminals", {"limit": 50})
    ids_ = [i.get("id") for i in its]
    after = {r["id"]: r.get("system") for r in at0.get("rows", []) if r.get("id") in ids_}
    out("systems of the connected terminals after commit", after)
    check("connected terminals joined the main's network systems, no new default system", len(after) == 2 and all(v and not v.startswith("Mechanical Supply Air") for v in after.values()),
          json.dumps(after))
    sysname = next(iter(after.values()), None) or (main.get("params") or {}).get("System Name")
    sysrow = next((r for r in call("mep_systems", {"kind": "duct", "limit": 3000}).get("systems", []) if r.get("name") == sysname), None)
    out("system used for add / remove", sysrow)
    if t3id and sysrow:
        ad = edit("mep_system_add the free terminal", [{"op": "mep_system_add", "system": sysrow["id"], "elements": [t3id]}])
        check("terminal added to the system", ad and ad.get("added") == [t3id] and ad.get("elementsNow", 0) >= 2, json.dumps(ad))
        rm = edit("mep_system_remove it again", [{"op": "mep_system_remove", "system": sysrow["id"], "elements": [t3id]}])
        check("terminal removed from the system", rm and rm.get("removed") == [t3id], json.dumps(rm))
        edit("mep_system_remove of an element not in the system (refused)", [{"op": "mep_system_remove", "system": sysrow["id"], "elements": [t3id]}],
             expect_error=True, contains="Not in system")
    else:
        SKIPPED.append("system add / remove: no free terminal or the system was not found")
    at = call("air_terminals", {"limit": 50})
    out("air_terminals after", at, 6000)
    check("air_terminals lists the new terminals", at.get("terminals", 0) >= 3, f"{at.get('terminals')} terminals, {len(at.get('spaces', []))} spaces")

# --- leave: Task 04 active, the HVAC copy closed without saving
call("model_open", {"path": TASK04, "activate": True}, timeout=900)
cl = call("model_close", {"document_id": hv_id, "discard_changes": True})
out("model_close HV (changes discarded)", cl)
check("HV copy closed without saving", cl.get("discardedChanges") is True)
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
sys.exit(1 if FAILED else 0)
