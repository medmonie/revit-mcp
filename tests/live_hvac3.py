r"""Live test of 0.27.0 (phase 18, third part) on the detached HVAC model B-M-HV (real, never-saved test edits in the
detached copy; never synced, never saved).

mep_connectors (equipment connectors), mep_route starting on an equipment connector (or, when no equipment has a free duct
connector, on an air terminal placed for the test) and ending on a main with a tap / tee, mep_connect with a
classification, refusals, mep_quantities with duct surface area, MEP schedules with schedule_create. Ends by opening
Task 04 active and closing the HV copy WITHOUT saving. Started by run_live_hvac3.bat; output in tests\results\live_hvac3.txt.
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




def add(a, b, k=1.0):
    return [a[0] + b[0] * k, a[1] + b[1] * k, a[2] + b[2] * k]


# --- open the HVAC copy active
o = call("model_open", {"path": HV, "activate": True}, timeout=900)
out("model_open HV active", o)
hv_id = o.get("documentId")
if not hv_id or o.get("alreadyOpen") or not o.get("active") or call("ping").get("documentId") != hv_id:
    raise SystemExit("The test requires a newly opened, active HVAC copy. No test edits were sent.")
ensure_permission()

# --- connectors of the mechanical equipment
mc = call("mep_connectors", {"category": "mechanical equipment", "limit": 200})
out("mep_connectors mechanical equipment", {k: v for k, v in mc.items() if k != "items"} | {"first": mc.get("items", [])[:3]}, 6000)
check("mep_connectors lists equipment with connectors", mc.get("withConnectors", 0) > 0, f"{mc.get('withConnectors')} of {mc.get('elements')}")
free_duct = [(e, c) for e in mc.get("items", []) for c in e["connectors"] if c["domain"] == "Hvac" and not c["connected"] and c["type"] == "End"]
pipe_only = next((e for e in mc.get("items", []) if all(c["domain"] != "Hvac" for c in e["connectors"])), None)
print("equipment with a free duct connector:", len(free_duct), "; pipe-only equipment:", pipe_only and pipe_only["id"], flush=True)

levels = call("list_levels").get("levels", [])
dtypes = call("list_types", {"category": "ducts"}).get("types", [])
out("duct types", [(t.get("family"), t.get("type")) for t in dtypes][:12])


def area(c):
    try:
        size = (c.get("size") or "0").replace("ø", "")
        a, b = size.split("x") if "x" in size else (size, size)
        return float(a) * float(b)
    except ValueError:
        return 1e12


def scenario(name, source, con, level, offset, to_main=True):
    """Route from the connector (straight out, then a clean turn), end on a test main; refusals; connector read back."""
    out(f"[{name}] route source", {"element": source["id"], "label": source.get("label"), "connector": con})
    round_conn = con["shape"] == "Round"
    shape = {"Round": "round", "Rectangular": "rectangular", "Oval": "oval"}.get(con["shape"], "rectangular")
    dtype = next((t for t in dtypes if shape in (t.get("family") or "").lower() and "elbow" in (t.get("type") or "").lower()), None) \
        or next((t for t in dtypes if shape in (t.get("family") or "").lower()), None)
    if not dtype:
        SKIPPED.append(f"{name}: no duct type of the connector's shape")
        return
    o_, f_ = con["origin"], con["facing"]
    a = add(o_, f_, offset)
    if con.get("heightAxis"):
        horizontal = con["heightAxis"] if abs(f_[2]) > 0.9 else con["widthAxis"]
        if abs(horizontal[2]) > 0.1:
            horizontal = con["widthAxis"]
    else:
        horizontal = [1, 0, 0] if abs(f_[2]) > 0.9 else [-f_[1], f_[0], 0]
    dims = [float(x) for x in (con.get("size") or "300").replace("ø", "").split("x")]
    b = add(a, horizontal, max(2500, 3 * max(dims)))
    main_mid = add(b, horizontal, max(2000, 2 * max(dims)))
    side = [-horizontal[1], horizontal[0], 0]
    big = max(dims) + 300
    size_main = {"diameter": big} if round_conn else {"width": big, "height": big}
    mr = edit(f"[{name}] mep_run_create a test main across the route's end", [{"op": "mep_run_create", "kind": "duct", "level": level,
              "type": str(dtype["id"]), "runs": [{"start": add(main_mid, side, -2 * big - 1500), "end": add(main_mid, side, 2 * big + 1500)}], **size_main}])
    main_id = ((mr or {}).get("items") or [{}])[0].get("id")
    cls = con.get("classification")
    edit(f"[{name}] mep_route from a connector of the wrong classification (refused)", [{"op": "mep_route", "kind": "duct", "level": level,
         "type": str(dtype["id"]), "path": [a, b], "from": {"element": source["id"], "classification": "NoSuchClass"}}],
         expect_error=True, contains="classification 'NoSuchClass'")
    edit(f"[{name}] mep_route from a connector with a size given (refused)", [{"op": "mep_route", "kind": "duct", "level": level, "path": [a, b],
         "from": {"element": source["id"]}, "width": 300, "height": 300}], expect_error=True, contains="connector's size")
    edit(f"[{name}] mep_connect with a classification the element has no open connector of (refused)", [{"op": "mep_connect",
         "element": source["id"], "main": main_id, "classification": "ExhaustAir" if cls != "ExhaustAir" else "ReturnAir"}],
         expect_error=True, contains="classification")
    rt = edit(f"[{name}] mep_route from the connector, ending on the main", [{"op": "mep_route", "kind": "duct", "level": level,
              "type": str(dtype["id"]), "path": [a, b], "from": {"element": source["id"], "classification": cls}, **({"to_main": main_id} if to_main else {})}])
    check(f"[{name}] route starts on the connector", rt and (rt.get("from") or {}).get("element") == source["id"], json.dumps((rt or {}).get("from")))
    check(f"[{name}] route has segments and an elbow", rt and len(rt.get("items", [])) >= 2 and len(rt.get("fittings", [])) >= 1,
          json.dumps({"segments": len((rt or {}).get("items", [])), "fittings": (rt or {}).get("fittings")}))
    if to_main:
        check(f"[{name}] route ends on the main with a tap or tee", rt and (rt.get("toMain") or {}).get("junction") in ("tap", "tee"),
              json.dumps((rt or {}).get("toMain")))
    after = call("mep_connectors", {"ids": [source["id"]]})
    c_after = next((c for c in after["items"][0]["connectors"] if c["index"] == con["index"]), {})
    check(f"[{name}] the source connector is now connected", c_after.get("connected") is True, json.dumps(c_after))


# scenario 1: an air terminal placed for the test (the everyday case: small, round or square, facing up)
ground = next((lv["name"] for lv in levels if "GROUND" in lv["name"].upper()), levels[0]["name"])
types = call("list_types", {"category": "air terminals"}).get("types", [])
supply = [t for t in types if "supply" in (t.get("family", "") + t.get("type", "")).lower()] or types
if supply:
    tp = edit("air_terminal_place one terminal as a route source", [{"op": "air_terminal_place", "type": str(supply[0]["id"]), "level": ground,
               "points": [[150000, 150000]], "height": 2700, "flows": 100}])
    tid = ((tp or {}).get("items") or [{}])[0].get("id")
    if tid:
        ts = call("mep_connectors", {"ids": [tid]})["items"][0]
        scenario("terminal", ts, next(c for c in ts["connectors"] if c["domain"] == "Hvac"), ground, 1500)
else:
    SKIPPED.append("terminal scenario: no air terminal type")

# scenario 2: the smallest free duct connector of the mechanical equipment (AHU / fan)
if free_duct:
    source, con = min(free_duct, key=lambda ec: area(ec[1]))
    lvl = (call("get_elements", {"ids": [source["id"]]}).get("elements") or [{}])[0].get("level") or levels[0]["name"]
    scenario("equipment", source, con, lvl, 3000, to_main=False)  # a 2350 mm branch is not teed into a main; the unit run continues
else:
    SKIPPED.append("equipment scenario: no equipment with a free duct connector")
if pipe_only:
    edit("mep_route from equipment with only pipe connectors (refused)", [{"op": "mep_route", "kind": "duct", "level": levels[0]["name"],
         "path": [[0, 0, 3000], [1000, 0, 3000]], "from": {"element": pipe_only["id"]}}], expect_error=True, contains="no open duct connector")

# --- quantities with duct surface area
q = call("mep_quantities", {"category": "ducts", "group_by": "size"})
out("mep_quantities ducts by size (first rows)", {"totals": q.get("totalsByCategory"), "first": q.get("lengths", [])[:5]})
check("duct surface area reported", any(r.get("surfaceAreaM2") for r in q.get("lengths", [])))

# --- MEP schedules (schedule_create on MEP categories)
s1 = edit("schedule_create air terminals", [{"op": "schedule_create", "category": "air terminals", "name": "MCP Air Terminal Schedule",
          "fields": ["Family and Type", "System Classification", "Flow"]}])
s2 = edit("schedule_create ducts", [{"op": "schedule_create", "category": "ducts", "name": "MCP Duct Schedule",
          "fields": ["System Type", "Size", "Length"]}])
check("two MEP schedules created", s1 is not None and s2 is not None)
if s2 is not None:
    sd = call("get_schedule_data", {"schedule": "MCP Duct Schedule", "limit": 5})
    out("get_schedule_data MCP Duct Schedule", sd)
    check("duct schedule has rows", len(sd.get("rows", [])) > 1)

# --- leave: Task 04 active, the HVAC copy closed without saving
call("model_open", {"path": TASK04, "activate": True}, timeout=900)
cl = call("model_close", {"document_id": hv_id, "discard_changes": True})
out("model_close HV (changes discarded)", cl)
check("HV copy closed without saving", cl.get("discardedChanges") is True)
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
sys.exit(1 if FAILED else 0)
