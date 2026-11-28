r"""Live test of 0.28.0 (phase 18, fourth part) on the detached HVAC model B-M-HV (real, never-saved test edits in the
detached copy; never synced, never saved).

mep_accessory_place: a damper at points on a test duct (two on one run, following the split pieces), at the crossing of a
test wall (centre plane), at a crossing with a wall of the linked architecture model found by mep_crossings, a valve on a
test pipe; refusals. mep_insulation_add / replace / remove and the mep_insulation read, on a duct system and single
elements. Ends by opening Task 04 active and closing the HV copy WITHOUT saving. Started by run_live_hvac4.bat; output in
tests\results\live_hvac4.txt.
"""
import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

FOLDER = r"C:\Users\mohamed.hamed\Desktop\New folder\detached\detached"
HV = FOLDER + r"\KTR15-G26-Z2-B-M-HV-MDL-ZZ-90901-00.rvt"
ARCH = FOLDER + r"\KTR15-G26-Z2-X-A-GN-MDL-ZZ-90901-00.rvt"
TASK04 = r"C:\Users\mohamed.hamed\Desktop\New folder\Revit Task 04_Mohamed A.Hamed_66088960.rvt"
X0, Y0 = 150000, 170000   # an empty corner of the HV model for the test runs
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


def edit(title, ops, expect_error=False, contains=None, quiet_fail=False):
    try:
        res = call("edit", {"document_id": hv_id, "dry_run": False, "operations": ops}, timeout=900)
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if (not expect_error and not quiet_fail) or (expect_error and contains and contains not in str(exc)):
            FAILED.append(title)
        return None
    out(title, res)
    if not res.get("applied"):
        print(f"{'EXPECTED ERROR' if expect_error else 'FAILED'}: {res.get('error')}", flush=True)
        if (not expect_error and not quiet_fail) or (expect_error and contains and contains not in str(res.get("error"))):
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


def types_of(category):
    try:
        return call("list_types", {"category": category, "limit": 200}).get("types", [])
    except Exception as exc:
        print(f"list_types {category}: {exc}", flush=True)
        return []


def label(t):
    return f"{t.get('family')} : {t.get('type')}"


# --- open the HVAC copy active
o = call("model_open", {"path": HV, "activate": True}, timeout=900)
out("model_open HV active", o)
hv_id = o.get("documentId")
if not hv_id or o.get("alreadyOpen") or not o.get("active") or call("ping").get("documentId") != hv_id:
    raise SystemExit("The test requires a newly opened, active HVAC copy. No test edits were sent.")
ensure_permission()

levels = call("list_levels").get("levels", [])
ground = next((lv["name"] for lv in levels if "GROUND" in lv["name"].upper()), levels[0]["name"])
dtypes, ptypes = types_of("ducts"), types_of("pipes")
dacc, pacc = types_of("duct accessories"), types_of("pipe accessories")
dins, dlin, pins = types_of("duct insulations"), types_of("duct linings"), types_of("pipe insulations")
out("types found", {"duct accessories": [label(t) for t in dacc][:30], "pipe accessories": [label(t) for t in pacc][:30],
                    "duct insulation": [t.get("type") for t in dins], "duct lining": [t.get("type") for t in dlin],
                    "pipe insulation": [t.get("type") for t in pins]})
rect = next((t for t in dtypes if "rect" in (t.get("family") or "").lower()), None)
rnd = next((t for t in dtypes if "round" in (t.get("family") or "").lower()), None)


def test_duct(name, dtype, y, shape_round, length=8000, z=3000):
    size = {"diameter": 400} if shape_round else {"width": 600, "height": 300}
    r = edit(f"mep_run_create {name}", [{"op": "mep_run_create", "kind": "duct", "level": ground, "type": str(dtype["id"]),
             "runs": [{"start": [X0, y], "end": [X0 + length, y]}], **size}])
    item = ((r or {}).get("items") or [{}])[0]
    return item.get("id"), item.get("start")


# --- accessories at points: find a damper type that Revit inserts into a test duct of matching shape
damper = None
duct_id = None
cands = sorted(dacc, key=lambda t: 0 if "damper" in label(t).lower() else 1)
for t in cands[:8]:
    shape_round = "round" in label(t).lower()
    dtype = rnd if shape_round else rect
    if not dtype:
        continue
    did, start = test_duct(f"test duct for {label(t)}", dtype, Y0, shape_round)
    if not did:
        break
    r = edit(f"mep_accessory_place '{label(t)}' at two points on one duct", [{"op": "mep_accessory_place", "type": str(t["id"]),
             "items": [{"run": did, "point": [X0 + 2500, Y0]}, {"run": did, "point": [X0 + 5500, Y0]}]}], quiet_fail=True)
    if r:
        damper, duct_id, damper_round = t, did, shape_round
        check("two dampers placed", r.get("placed") == 2, json.dumps(r.get("items"), default=str)[:800])
        items = r.get("items", [])
        check("each damper connected on both sides", all(i.get("openConnectors") == 0 and len(i.get("connectedTo", [])) == 2 for i in items),
              json.dumps([(i.get("connectedTo"), i.get("openConnectors"), i.get("method")) for i in items]))
        check("both dampers on the original run's pieces", len(items) == 2 and all(i.get("run") == did for i in items)
              and len({c for i in items for c in i.get("connectedTo", [])}) == 3, json.dumps([(i.get("piece"), i.get("connectedTo")) for i in items]))
        check("damper takes the duct's size", all(i.get("accessorySize") == i.get("runSize") for i in items),
              json.dumps([(i.get("accessorySize"), i.get("runSize"), i.get("method")) for i in items]))
        cc = call("mep_connectivity", {"category": "duct accessories", "limit": 50})
        open_ids = {e.get("id") for e in cc.get("openEndItems", [])}
        check("no open ends at the new dampers (mep_connectivity)", not ({i["id"] for i in items} & open_ids), json.dumps(sorted(open_ids))[:300])
        break
    edit(f"delete the test duct for {label(t)}", [{"op": "delete", "ids": [did]}])
if damper is None:
    SKIPPED.append("dampers at points: no duct accessory type in the model that inserts into a test duct" if dacc else "no duct accessory types loaded")

if damper:
    # refusals
    edit("mep_accessory_place point off the run (refused)", [{"op": "mep_accessory_place", "type": str(damper["id"]),
         "items": [{"run": duct_id, "point": [X0 + 4000, Y0 + 2000]}]}], expect_error=True, contains="off the centreline")
    edit("mep_accessory_place point beyond the run (refused)", [{"op": "mep_accessory_place", "type": str(damper["id"]),
         "items": [{"run": duct_id, "point": [X0 + 20000, Y0]}]}], expect_error=True, contains="beyond the ends")
    edit("mep_accessory_place with neither point nor crossing (refused)", [{"op": "mep_accessory_place", "type": str(damper["id"]),
         "items": [{"run": duct_id}]}], expect_error=True, contains="either 'point' or 'crossing'")
    edit("mep_accessory_place without a type (refused)", [{"op": "mep_accessory_place", "items": [{"run": duct_id, "point": [X0 + 4000, Y0]}]}],
         expect_error=True, contains="type")
    if pacc:
        edit("mep_accessory_place a pipe accessory type on a duct (refused)", [{"op": "mep_accessory_place", "type": label(pacc[0]),
             "items": [{"run": duct_id, "point": [X0 + 4000, Y0]}]}], expect_error=True, contains="No type")

    # crossings use a fire damper type when the model has one (the everyday case), else the damper above
    fire = next((t for t in dacc if "fire" in label(t).lower() and ("round" in label(t).lower()) == damper_round), damper)
    out("crossing accessory type", label(fire))

    # --- at a crossing with a test wall (centre plane)
    wtypes = types_of("walls")
    wtype = next((t for t in wtypes if "basic" in (t.get("family") or "").lower()), wtypes[0] if wtypes else None)
    if wtype:
        wy = Y0 + 3000
        did2, _ = test_duct("test duct through a wall", rnd if damper_round else rect, wy, damper_round)
        w = edit("wall_create across the second test duct", [{"op": "wall_create", "type": wtype["type"], "level": ground,
                 "walls": [{"start": [X0 + 4000, wy - 2000], "end": [X0 + 4000, wy + 2000]}], "height": 5000, "structural": False}])
        wid = ((w or {}).get("ids") or [None])[0]
        if did2 and wid:
            r = edit("mep_accessory_place at the wall crossing", [{"op": "mep_accessory_place", "type": str(fire["id"]),
                     "items": [{"run": did2, "crossing": wid}]}])
            it = ((r or {}).get("items") or [{}])[0]
            check("damper at the wall's centre plane", it.get("position") == "wall centre plane" and abs((it.get("at") or [0])[0] - (X0 + 4000)) < 1,
                  json.dumps(it, default=str)[:600])
            check("crossing damper sized and connected", it.get("accessorySize") == it.get("runSize") and it.get("openConnectors") == 0,
                  json.dumps([it.get("accessorySize"), it.get("runSize"), it.get("connectedTo")]))
            edit("mep_accessory_place at a wall the duct does not cross (refused)", [{"op": "mep_accessory_place", "type": str(damper["id"]),
                 "items": [{"run": duct_id, "crossing": wid}]}], expect_error=True, contains="does not cross")
        else:
            SKIPPED.append("wall crossing: test duct or wall not created")
    else:
        SKIPPED.append("wall crossing: no wall type in the HV model")

    # --- at a crossing with a wall of the linked architecture model (the fire damper case)
    arch = ARCH if os.path.exists(ARCH) else next((os.path.join(FOLDER, f) for f in os.listdir(FOLDER) if "-A-GN" in f and f.endswith(".rvt")), None)
    lk = None
    if arch:
        for placement in ("shared", "origin"):
            lk = edit(f"revit_link the architecture model ({placement})", [{"op": "revit_link", "path": arch, "placement": placement, "pin": True}], quiet_fail=True)
            if lk:
                break
    if lk and lk.get("id"):
        # a test duct of the damper's shape drawn square through the middle of a straight linked wall
        lw = call("get_elements", {"category": "walls", "link": lk["id"], "location": True, "box": True, "limit": 300}).get("elements", [])
        def wall_ok(w):
            loc, box = w.get("location") or {}, w.get("box") or {}
            return "start" in loc and not loc.get("curved") and (loc.get("length") or 0) > 3000 and box \
                and box["max"][2] - box["min"][2] > 2000
        wall = next((w for w in lw if wall_ok(w)), None)
        if wall:
            s0, e0 = wall["location"]["start"], wall["location"]["end"]
            mx, my = (s0[0] + e0[0]) / 2, (s0[1] + e0[1]) / 2
            mz = (wall["box"]["min"][2] + wall["box"]["max"][2]) / 2
            L = ((e0[0] - s0[0]) ** 2 + (e0[1] - s0[1]) ** 2) ** 0.5
            nx, ny = -(e0[1] - s0[1]) / L, (e0[0] - s0[0]) / L
            size = {"diameter": 400} if damper_round else {"width": 600, "height": 300}
            lr = edit("mep_run_create a test duct through a linked wall", [{"op": "mep_run_create", "kind": "duct", "level": ground,
                      "type": str((rnd if damper_round else rect)["id"]),
                      "runs": [{"start": [mx - nx * 2500, my - ny * 2500, mz], "end": [mx + nx * 2500, my + ny * 2500, mz]}], **size}])
            lrid = ((lr or {}).get("items") or [{}])[0].get("id")
            xs = call("mep_crossings", {"structure_link": lk["id"], "structure_category": "walls", "mep_category": "ducts", "limit": 50,
                                         "resume_after": (lrid or 1) - 1, "max_seconds": 120}, timeout=600)
            out("mep_crossings of the test duct with linked walls", xs)
            pick = next((x for x in xs.get("items", []) if x.get("mep") == lrid and x.get("structure") == wall["id"]), None)
            check("mep_crossings finds the test duct in the linked wall", pick is not None)
            if lrid:
                r = edit("mep_accessory_place at a linked wall crossing (fire damper case)", [{"op": "mep_accessory_place", "type": str(fire["id"]),
                         "crossing_link": lk["id"], "items": [{"run": lrid, "crossing": wall["id"]}]}])
                it = ((r or {}).get("items") or [{}])[0]
                check("damper at the linked wall", (it.get("crossing") or {}).get("element") == wall["id"] and (it.get("crossing") or {}).get("link")
                      and it.get("position") == "wall centre plane" and it.get("openConnectors") == 0, json.dumps(it, default=str)[:700])
                d = ((it.get("at") or [0, 0, 0])[0] - mx) ** 2 + ((it.get("at") or [0, 0, 0])[1] - my) ** 2
                check("linked crossing point on the wall's location line", d ** 0.5 < 5, f"{d ** 0.5:.1f} mm from the wall's mid point")
        else:
            SKIPPED.append("linked crossing: no straight linked wall over 3 m long and 2 m high")
    else:
        SKIPPED.append("linked crossing: the architecture model could not be linked")

# --- a valve on a test pipe
pid = None
if pacc and ptypes:
    valve = sorted(pacc, key=lambda t: 0 if "valve" in label(t).lower() else 1)
    pr = edit("mep_run_create a test pipe", [{"op": "mep_run_create", "kind": "pipe", "level": ground, "type": str(ptypes[0]["id"]),
              "runs": [{"start": [X0, Y0 + 6000], "end": [X0 + 6000, Y0 + 6000]}], "diameter": 100}])
    pid = ((pr or {}).get("items") or [{}])[0].get("id")
    ok = None
    for t in valve[:6]:
        ok = edit(f"mep_accessory_place '{label(t)}' on the test pipe", [{"op": "mep_accessory_place", "type": str(t["id"]),
                  "items": [{"run": pid, "point": [X0 + 3000, Y0 + 6000]}]}], quiet_fail=True)
        if ok:
            it = (ok.get("items") or [{}])[0]
            check("valve connected on both sides", it.get("openConnectors") == 0 and len(it.get("connectedTo", [])) == 2, json.dumps(it, default=str)[:500])
            break
    if not ok:
        SKIPPED.append("valve: no pipe accessory type in the model inserts into a 100 mm test pipe")
else:
    SKIPPED.append("valve: no pipe accessory or pipe types")

# --- insulation and lining
if dins and duct_id:
    t0 = dins[0]["type"]
    edit("mep_insulation_add without a thickness (refused)", [{"op": "mep_insulation_add", "kind": "insulation", "type": t0, "elements": [duct_id]}],
         expect_error=True, contains="thickness")
    edit("mep_insulation_add with both elements and system (refused)", [{"op": "mep_insulation_add", "kind": "insulation", "type": t0,
         "thickness": 25, "elements": [duct_id], "system": duct_id}], expect_error=True, contains="either 'elements' or 'system'")
    edit("mep_insulation_add with an unknown type (refused)", [{"op": "mep_insulation_add", "kind": "insulation", "type": "No Such Insulation",
         "thickness": 25, "elements": [duct_id]}], expect_error=True, contains="No duct insulation type")
    ins = edit("mep_insulation_add 25 on the test duct pieces and dampers", [{"op": "mep_insulation_add", "kind": "insulation", "type": t0,
               "thickness": 25, "elements": [duct_id]}])
    check("insulation created", ins and ins.get("created", 0) >= 1, json.dumps(ins, default=str)[:500])
    again = edit("mep_insulation_add again (skipped, nothing new)", [{"op": "mep_insulation_add", "kind": "insulation", "type": t0,
                 "thickness": 25, "elements": [duct_id]}], expect_error=True, contains="already has")
    rep = edit("mep_insulation_add replace: 50", [{"op": "mep_insulation_add", "kind": "insulation", "type": t0, "thickness": 50,
               "elements": [duct_id], "replace": True}])
    check("insulation changed to 50", rep and rep.get("changedCount", 0) >= 1, json.dumps(rep, default=str)[:400])
    rd = call("mep_insulation", {"ids": [duct_id]})
    out("mep_insulation on the test duct", rd)
    check("read shows the 50 thickness", "50" in json.dumps(rd.get("items")), json.dumps(rd.get("items"))[:300])
    rm = edit("mep_insulation_remove", [{"op": "mep_insulation_remove", "kind": "insulation", "elements": [duct_id]}])
    check("insulation removed", rm and rm.get("removed", 0) >= 1)
    edit("mep_insulation_remove again (refused: none left)", [{"op": "mep_insulation_remove", "kind": "insulation", "elements": [duct_id]}],
         expect_error=True, contains="has insulation")
    # a whole system's network
    dsys = (call("get_elements", {"ids": [duct_id], "params": ["System Name"]}).get("elements") or [{}])[0]
    sname = (dsys.get("params") or dsys.get("parameters") or {}).get("System Name")
    systems = call("mep_systems", {"kind": "duct", "limit": 2000}).get("systems", [])
    sysrow = next((x for x in systems if sname and x.get("name") == sname), None)
    out("test duct's system", {"name": sname, "row": sysrow})
    if sysrow:
        sr = edit(f"mep_insulation_add on system {sysrow.get('id')} {sysrow.get('name')}", [{"op": "mep_insulation_add", "kind": "insulation",
                  "type": t0, "thickness": 25, "system": sysrow["id"], "replace": True}])
        check("system network insulated", sr and (sr.get("created", 0) + sr.get("changedCount", 0)) > 0, json.dumps({k: v for k, v in (sr or {}).items() if k in ("created", "changedCount", "skippedCount")}))
        check("system network: equipment and terminals left out, not refused", sr is not None)
        q = call("mep_insulation", {"system": sysrow.get("name"), "missing_only": True, "limit": 20})
        out("mep_insulation missing_only on that system", {k: v for k, v in q.items() if k != "items"} | {"first": q.get("items", [])[:5]})
        check("mep_insulation totals by type", bool(q.get("byTypeAndThickness")))
    else:
        SKIPPED.append(f"system insulation: the test duct's system '{sname}' not found by mep_systems")
else:
    SKIPPED.append("insulation: no duct insulation type or no test duct")
if dlin and duct_id:
    ln = edit("mep_insulation_add lining 25", [{"op": "mep_insulation_add", "kind": "lining", "type": dlin[0]["type"], "thickness": 25, "elements": [duct_id]}])
    check("lining created", ln and ln.get("created", 0) >= 1)
else:
    SKIPPED.append("lining: no duct lining type")
if pins and pid:
    pi = edit("mep_insulation_add on the test pipe", [{"op": "mep_insulation_add", "kind": "insulation", "type": pins[0]["type"], "thickness": 25, "elements": [pid]}])
    check("pipe insulation created", pi and pi.get("created", 0) >= 1)
    edit("mep_insulation_add lining on a pipe (refused)", [{"op": "mep_insulation_add", "kind": "lining", "type": (dlin or [{"type": "x"}])[0]["type"],
         "thickness": 25, "elements": [pid]}], expect_error=True, contains="lining is for ducts only")

# --- leave: Task 04 active, the HVAC copy closed without saving
call("model_open", {"path": TASK04, "activate": True}, timeout=900)
cl = call("model_close", {"document_id": hv_id, "discard_changes": True})
out("model_close HV (changes discarded)", cl)
check("HV copy closed without saving", cl.get("discardedChanges") is True)
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
sys.exit(1 if FAILED else 0)
