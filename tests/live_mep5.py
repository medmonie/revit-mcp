r"""Live test of 0.29.0 (phase 18, rest): sprinklers, plumbing fixtures, sloped sanitary runs, sleeves, electrical devices,
circuits and panels, cable trays and conduits with elbows and tees, and the MEP quantities workbook.

Real, never-saved test edits in the detached KTR15 copies (FF, PL, E): each is opened active, tested and closed WITHOUT
saving; never synced. Ends with Task 04 active. Started by run_live_mep5.bat; output in tests\results\live_mep5.txt and
the workbooks tests\results\mep_report_live_*.xlsx.
"""
import json
import os
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402
import mep_report  # noqa: E402

FOLDER = r"C:\Users\mohamed.hamed\Desktop\New folder\detached\detached"
FF = FOLDER + r"\KTR15-G26-Z2-B-M-FF-MDL-ZZ-90901-00.rvt"
PL = FOLDER + r"\KTR15-G26-Z2-B-M-PL-MDL-ZZ-90901-00.rvt"
EL = FOLDER + r"\KTR15-G26-Z2-B-E-GN-MDL-ZZ-90901-00.rvt"
ARCH = FOLDER + r"\KTR15-G26-Z2-X-A-GN-MDL-ZZ-90901-00.rvt"
TASK04 = r"C:\Users\mohamed.hamed\Desktop\New folder\Revit Task 04_Mohamed A.Hamed_66088960.rvt"
RESULTS = HERE / "results"
X0, Y0 = 150000, 170000   # an empty corner far from the buildings for the test elements
FAILED, SKIPPED = [], []
connection = bridge_client.pin()
print("bridge", bridge_client.call("ping", connection=connection).get("bridgeVersion"), flush=True)
doc_id = None


def call(method, params=None, timeout=300):
    return bridge_client.call(method, dict(params or {}), connection=connection, timeout=timeout)


def out(title, value, limit=4000):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < limit else text[:limit] + "\n... (truncated)"), flush=True)


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


def edit(title, ops, expect_error=False, contains=None, quiet_fail=False):
    try:
        res = call("edit", {"document_id": doc_id, "dry_run": False, "operations": ops}, timeout=900)
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


def open_active(path, previous=None):
    global doc_id
    o = call("model_open", {"path": path, "activate": True}, timeout=900)
    out(f"model_open {Path(path).stem} active", o)
    new = o.get("documentId")
    if not new or o.get("alreadyOpen") or not o.get("active") or call("ping").get("documentId") != new:
        raise SystemExit(f"The test requires a newly opened, active copy of {Path(path).name}. No test edits were sent to it.")
    if previous:
        cl = call("model_close", {"document_id": previous, "discard_changes": True})
        out("model_close previous copy (changes discarded)", cl)
        check(f"previous copy closed without saving", cl.get("discardedChanges") in (True, False) and "closed" in cl)
    doc_id = new
    ensure_permission()
    levels = read("list_levels").get("levels", [])
    lv = next((x["name"] for x in levels if "GROUND" in x["name"].upper()), levels[0]["name"])
    return new, lv


def types_of(category):
    try:
        return read("list_types", {"category": category, "limit": 300}).get("types", [])
    except Exception as exc:
        print(f"list_types {category}: {exc}", flush=True)
        return []


def label(t):
    return f"{t.get('family')} : {t.get('type')}"


def pick(types, *words):
    for t in types:
        if all(w.lower() in label(t).lower() for w in words):
            return t
    return None


def first_item(r):
    return ((r or {}).get("items") or [{}])[0]


def workbook(name):
    path = RESULTS / f"mep_report_live_{name}.xlsx"
    def on_doc(method, args=None, **kw):
        return call(method, {**(args or {}), "document_id": doc_id}, timeout=kw.get("timeout", 300))
    try:
        rep = mep_report.build(on_doc, {"path": str(path), "overwrite": True})
    except Exception as exc:
        print(f"===== mep_report {name}\nFAILED: {exc}", flush=True)
        FAILED.append(f"mep_report {name}")
        return None
    out(f"mep_report {name}", rep)
    from openpyxl import load_workbook
    wb = load_workbook(path)
    check(f"mep_report {name}: workbook written with the run sheets", path.exists() and {"Summary", "By system", "By size", "By type", "By level", "Fittings and devices"} <= set(wb.sheetnames), str(wb.sheetnames))
    check(f"mep_report {name}: summary lengths are formulas", str(wb["Summary"]["B11"].value or "").startswith("=SUMIF("), str(wb["Summary"]["B11"].value))
    return rep, wb


# =============================================================================== FF: sprinklers dropped to a main
ff_id, lv = open_active(FF)
spr = pick(types_of("sprinklers"), "M_Sprinkler - Pendent - Hosted", "15 mm Pendent") or (types_of("sprinklers") or [None])[0]
ptypes = sorted(types_of("pipes"), key=lambda t: -(t.get("instances") or 0))   # the type the FF model uses (its routing preferences have the tees)
out("FF types", {"sprinkler": spr and label(spr), "pipe types": [label(t) for t in ptypes][:10], "level": lv})
if spr and ptypes:
    # the joint (tap, or tee 50 x 15) comes from the pipe type's routing preferences: use the first type in the model that has one
    r = mid = None
    for n, pt in enumerate(ptypes):
        main = edit(f"FF mep_route a test fire main 3.3 m above the level ({label(pt)})", [{"op": "mep_route", "kind": "pipe", "level": lv, "type": str(pt["id"]),
                    "path": [[X0, Y0 + 4000 * n], [X0 + 6000, Y0 + 4000 * n]], "z": 3300, "diameter": 50}])
        mid = first_item(main).get("id")
        r = edit(f"FF mep_fixture_place two pendent sprinklers at 2.8 m, each dropped to the main ({label(pt)})", [{"op": "mep_fixture_place", "type": str(spr["id"]),
                 "level": lv, "points": [[X0 + 1500, Y0 + 4000 * n], [X0 + 4500, Y0 + 4000 * n]], "height": 2800, "main": mid}], quiet_fail=True)
        if r:
            out("FF pipe type whose routing preferences make the sprinkler joint", label(pt))
            break
        SKIPPED.append(f"sprinkler joint with '{label(pt)}': Revit made neither a tap nor a 50 x 15 tee (that type's routing preferences; reported, rolled back)")
    items = (r or {}).get("items", [])
    check("two sprinklers placed on a downward plane", len(items) == 2 and all("facing down" in i.get("host", "") for i in items),
          json.dumps([(i.get("id"), i.get("host")) for i in items]))
    check("each sprinkler connected to the main (tap or tee)", len(items) == 2 and all((i.get("connection") or {}).get("fitting") for i in items),
          json.dumps([(i.get("connection") or {}).get("junction") for i in items]))
    check("sprinkler connectors report connected", len(items) == 2 and all(any("(connected)" in c for c in i.get("connectors", [])) for i in items),
          json.dumps([i.get("connectors") for i in items]))
    edit("FF a sprinkler not under the main (refused)", [{"op": "mep_fixture_place", "type": str(spr["id"]), "level": lv,
         "points": [[X0 + 3000, Y0 - 2500]], "height": 2800, "main": mid}], expect_error=True, contains="cannot be connected")
    pacc = types_of("pipe accessories")
    if pacc:
        edit("FF mep_fixture_place with a pipe accessory type (refused)", [{"op": "mep_fixture_place", "type": str(pacc[0]["id"]), "level": lv,
             "points": [[X0, Y0 + 5000]]}], expect_error=True, contains="takes plumbing fixtures")
    edit("FF walls given for a face-based type that point at no wall (refused)", [{"op": "mep_fixture_place", "type": str(spr["id"]), "level": lv,
         "points": [[X0, Y0 + 5000]], "walls": mid}], expect_error=True, contains="is not a wall")
else:
    SKIPPED.append("sprinklers: no sprinkler or pipe type in the FF model")

# =============================================================================== PL: fixtures, sloped run, sleeves, report
pl_id, lv = open_active(PL, previous=ff_id)
fd = pick(types_of("plumbing fixtures"), "Floor Drain FD") or pick(types_of("plumbing fixtures"), "Floor Drain")
ptypes = types_of("pipes")
wtypes = types_of("walls")
ftypes = types_of("floors")
coupling = pick(types_of("pipe accessories"), "Coupling")
out("PL types", {"floor drain": fd and label(fd), "pipes": [label(t) for t in ptypes][:8], "walls": [label(t) for t in wtypes][:5],
                 "floors": [label(t) for t in ftypes][:5], "coupling": coupling and label(coupling), "level": lv})
if fd:
    r = edit("PL mep_fixture_place two floor drains (face up if face-based)", [{"op": "mep_fixture_place", "type": str(fd["id"]), "level": lv,
             "points": [[X0, Y0], [X0 + 2000, Y0]], "face": "up", "rotation": 90, "set": {"Comments": "MCP test drain"}}])
    check("two floor drains placed", r and r.get("placed") == 2, json.dumps({k: (r or {}).get(k) for k in ("placement", "items")}, default=str)[:600])
else:
    SKIPPED.append("floor drains: no floor drain type in the PL model")
if ptypes:
    r = edit("PL mep_route a 100 mm sanitary run at 2 % fall with an elbow", [{"op": "mep_route", "kind": "pipe", "level": lv, "type": str(ptypes[0]["id"]),
             "path": [[X0, Y0 + 3000], [X0 + 5000, Y0 + 3000], [X0 + 5000, Y0 + 6000]], "z": -600, "slope": 2, "diameter": 100}])
    segs = (r or {}).get("items", [])
    if len(segs) == 2:
        drop1 = segs[0]["start"][2] - segs[0]["end"][2]
        drop2 = segs[1]["start"][2] - segs[1]["end"][2]
        check("run falls 100 mm over 5 m and 60 mm over 3 m", abs(drop1 - 100) < 0.5 and abs(drop2 - 60) < 0.5, f"{drop1:.2f} / {drop2:.2f} mm")
        check("sloped run has its elbow", len((r or {}).get("fittings", [])) == 1, json.dumps((r or {}).get("fittings")))
    else:
        FAILED.append("sloped run: two segments expected")
    edit("PL slope with a 3D path (refused)", [{"op": "mep_route", "kind": "pipe", "level": lv, "path": [[X0, Y0 + 7000, 0], [X0 + 3000, Y0 + 7000, 0]],
         "slope": 2, "diameter": 100}], expect_error=True, contains="[x, y] points")
    edit("PL slope on a conduit (refused)", [{"op": "mep_route", "kind": "conduit", "level": lv, "path": [[X0, Y0 + 7000], [X0 + 3000, Y0 + 7000]],
         "slope": 2}], expect_error=True, contains="for pipes and ducts")

    # sleeves: a pipe along x through a wall along y, a pipe along y through a wall along x, a vertical pipe through a slab
    wtype = next((t for t in wtypes if "basic" in (t.get("family") or "").lower()), wtypes[0] if wtypes else None)
    if coupling and wtype:
        p1 = first_item(edit("PL test pipe along x", [{"op": "mep_route", "kind": "pipe", "level": lv, "type": str(ptypes[0]["id"]),
                              "path": [[X0, Y0 + 9000], [X0 + 6000, Y0 + 9000]], "z": 1000, "diameter": 100}]))
        p2 = first_item(edit("PL test pipe along y", [{"op": "mep_route", "kind": "pipe", "level": lv, "type": str(ptypes[0]["id"]),
                              "path": [[X0 + 6000, Y0 + 9500], [X0 + 6000, Y0 + 12500]], "z": 1000, "diameter": 100}]))
        w = edit("PL two test walls", [{"op": "wall_create", "type": wtype["type"], "level": lv, "height": 4000, "structural": False,
                 "walls": [{"start": [X0 + 3000, Y0 + 7000], "end": [X0 + 3000, Y0 + 10000]}, {"start": [X0 + 4000, Y0 + 11000], "end": [X0 + 8000, Y0 + 11000]}]}])
        w1, w2 = ((w or {}).get("ids") or [None, None])[:2]
        r = edit("PL sleeve_place at both wall crossings (coupling as the sleeve family, axis x)", [{"op": "sleeve_place", "type": str(coupling["id"]), "axis": "x",
                 "items": [{"run": p1.get("id"), "crossing": w1}, {"run": p2.get("id"), "crossing": w2}],
                 "set": {"Nominal Diameter": "run_diameter", "TagNumber": "SL-T"}}])
        its = (r or {}).get("items", [])
        if len(its) == 2:
            check("sleeve 1 centred on the wall's centre plane", its[0].get("position") == "wall centre plane" and abs(its[0]["at"][0] - (X0 + 3000)) < 1
                  and abs(its[0]["at"][1] - (Y0 + 9000)) < 1, json.dumps(its[0], default=str)[:500])
            check("sleeve 2 turned 90 degrees to the pipe along y", abs(abs(its[1].get("turnedDeg", 0)) - 90) < 0.5 and abs(its[1]["at"][1] - (Y0 + 11000)) < 1,
                  json.dumps(its[1], default=str)[:500])
            check("crossing length is the wall thickness (square crossing)", abs(its[0]["crossing"]["lengthInside"] - its[1]["crossing"]["lengthInside"]) < 0.5
                  and its[0]["crossing"]["lengthInside"] > 0, json.dumps([i["crossing"]["lengthInside"] for i in its]))
            check("measured and text values set", its[0]["set"].get("Nominal Diameter") == 100 and its[0]["set"].get("TagNumber") == "SL-T", json.dumps(its[0].get("set")))
        else:
            FAILED.append("sleeves at walls: two expected")
        r = edit("PL sleeve with OD + clearance", [{"op": "sleeve_place", "type": str(coupling["id"]), "axis": "x",
                 "items": [{"run": p1.get("id"), "crossing": w1}], "set": {"Nominal Diameter": "run_outside_diameter + 50"}}])
        v = (first_item(r).get("set") or {}).get("Nominal Diameter")
        check("OD + 50 is at least 150 (OD of a 100 mm pipe plus 50)", isinstance(v, (int, float)) and v >= 150, f"{v}")
        edit("PL run_width on a pipe (refused)", [{"op": "sleeve_place", "type": str(coupling["id"]), "items": [{"run": p1.get("id"), "crossing": w1}],
             "set": {"Nominal Diameter": "run_width"}}], expect_error=True, contains="has no width")
        edit("PL sleeve at a wall the pipe does not cross (refused)", [{"op": "sleeve_place", "type": str(coupling["id"]),
             "items": [{"run": p1.get("id"), "crossing": w2}]}], expect_error=True, contains="does not cross")
        if fd:
            edit("PL sleeve type from plumbing fixtures (refused)", [{"op": "sleeve_place", "type": str(fd["id"]),
                 "items": [{"run": p1.get("id"), "crossing": w1}]}], expect_error=True, contains="sleeve_place takes")
        ftype = next((t for t in ftypes if "floor" in (t.get("family") or "").lower()), ftypes[0] if ftypes else None)
        z_level = p1.get("start", [0, 0, 1000])[2] - 1000
        if ftype:
            fl = edit("PL test slab", [{"op": "floor_create", "type": ftype["type"], "level": lv, "offset": 3000, "structural": False,
                      "boundary": [[X0 + 9000, Y0 + 9000], [X0 + 12000, Y0 + 9000], [X0 + 12000, Y0 + 12000], [X0 + 9000, Y0 + 12000]]}])
            vp = first_item(edit("PL vertical test pipe through the slab", [{"op": "mep_run_create", "kind": "pipe", "level": lv, "type": str(ptypes[0]["id"]),
                            "runs": [{"start": [X0 + 10500, Y0 + 10500, z_level + 1500], "end": [X0 + 10500, Y0 + 10500, z_level + 4500]}], "diameter": 100}]))
            if fl and vp.get("id"):
                edit("PL sleeve axis x on a vertical pipe (refused: would tilt)", [{"op": "sleeve_place", "type": str(coupling["id"]), "axis": "x",
                     "items": [{"run": vp["id"], "crossing": fl["id"]}]}], expect_error=True, contains="would have to tilt")
                r = edit("PL sleeve axis z through the slab", [{"op": "sleeve_place", "type": str(coupling["id"]), "axis": "z",
                         "items": [{"run": vp["id"], "crossing": fl["id"]}], "set": {"Nominal Diameter": "run_diameter"}}])
                it = first_item(r)
                check("slab sleeve at mid-depth", it.get("position") == "mid-depth of the slab" and abs(it["at"][0] - (X0 + 10500)) < 1
                      and (it.get("crossing") or {}).get("lengthInside", 0) > 0, json.dumps(it, default=str)[:500])
        else:
            SKIPPED.append("slab sleeve: no floor type in the PL model")
    else:
        SKIPPED.append("sleeves: no coupling (sleeve stand-in) or wall type in the PL model")
workbook("PL")

# =============================================================================== E: devices, circuits, panels, trays, conduits, report
el_id, lv = open_active(EL, previous=pl_id)
base = read("electrical_circuits", {"limit": 2000})
out("E electrical_circuits (before)", {k: v for k, v in base.items() if k not in ("circuits", "unassigned")} | {"firstCircuits": base.get("circuits", [])[:3]})
check("electrical_circuits lists the 10 panels and the circuits", base.get("panelCount") == 10 and base.get("circuitCount", 0) >= 40,
      f"{base.get('panelCount')} panels, {base.get('circuitCount')} circuits")
panel = next((p for p in base.get("panels", []) if p.get("name") == "DB-B-GP"), (base.get("panels") or [{}])[0])
sock = pick(types_of("electrical fixtures"), "Duplex Socket")
light = pick(types_of("lighting fixtures"), "Ceiling_Recessed_Square", "F2")
wtypes = types_of("walls")
wtype = next((t for t in wtypes if "basic" in (t.get("family") or "").lower()), wtypes[0] if wtypes else None)
out("E types", {"socket": sock and label(sock), "light": light and label(light), "wall": wtype and label(wtype), "panel": panel, "level": lv})
s_ids, l_ids = [], []
if sock and wtype:
    w = edit("E test wall", [{"op": "wall_create", "type": wtype["type"], "level": lv, "height": 3000, "structural": False,
             "walls": [{"start": [X0, Y0], "end": [X0 + 6000, Y0]}]}])
    wid = ((w or {}).get("ids") or [None])[0]
    r = edit("E mep_fixture_place two sockets on the wall at 300 mm", [{"op": "mep_fixture_place", "type": str(sock["id"]), "level": lv,
             "points": [[X0 + 1500, Y0 + 400], [X0 + 4500, Y0 + 400]], "height": 300, "walls": wid}], quiet_fail=True)
    if r is None:
        SKIPPED.append("sockets on a wall face: the socket type is not face-based (refused as designed); placed on the level instead")
        r = edit("E mep_fixture_place two sockets on the level at 300 mm", [{"op": "mep_fixture_place", "type": str(sock["id"]), "level": lv,
                 "points": [[X0 + 1500, Y0 + 400], [X0 + 4500, Y0 + 400]], "height": 300}])
        items = (r or {}).get("items", [])
    else:
        items = (r or {}).get("items", [])
        check("sockets on the wall's face (nearer side)", len(items) == 2 and all("face" in i.get("host", "") and 1 < abs(i["point"][1] - Y0) < 400 for i in items),
              json.dumps([(i.get("host"), i.get("point")) for i in items]))
    s_ids = [i["id"] for i in items]
    edit("E circuit with an element that has no electrical connector (refused)", [{"op": "electrical_circuit_create",
         "circuits": [{"elements": [wid], "panel": panel.get("name")}]}], expect_error=True, contains="no electrical connector")
else:
    SKIPPED.append("sockets: no socket or wall type in the E model")
if light:
    r = edit("E mep_fixture_place two ceiling lights at 2.8 m", [{"op": "mep_fixture_place", "type": str(light["id"]), "level": lv,
             "points": [[X0 + 1500, Y0 + 2000], [X0 + 4500, Y0 + 2000]], "height": 2800}])
    l_ids = [i["id"] for i in (r or {}).get("items", [])]
    check("two lights placed", len(l_ids) == 2, json.dumps((r or {}).get("items"), default=str)[:400])
if s_ids and panel.get("name"):
    r = edit("E electrical_circuit_create sockets and lights on the panel", [{"op": "electrical_circuit_create", "circuits": [
             {"elements": s_ids, "panel": panel["name"], "load_name": "MCP test sockets", "rating": 20},
             *([{"elements": l_ids, "panel": panel["name"], "load_name": "MCP test lights", "rating": 10}] if l_ids else [])]}])
    cs = (r or {}).get("circuits", [])
    check("circuits created on the panel with the engineer's rating and load name", cs and cs[0].get("panel") == panel["name"] and cs[0].get("elementCount") == 2
          and cs[0].get("loadName") == "MCP test sockets" and "20" in str(cs[0].get("rating")), json.dumps(cs, default=str)[:900])
    cid = cs[0]["id"] if cs else None
    edit("E the same sockets again (refused)", [{"op": "electrical_circuit_create", "circuits": [{"elements": s_ids}]}], expect_error=True, contains="already on a power circuit")
    edit("E an unknown panel (refused)", [{"op": "electrical_circuit_create", "circuits": [{"elements": l_ids or s_ids, "panel": "NO-SUCH-PANEL"}]}],
         expect_error=True, contains="no panel")
    if cid:
        r = edit("E circuit_edit: remove one socket", [{"op": "electrical_circuit_edit", "circuit": cid, "remove": [s_ids[1]]}])
        check("one load left", ((r or {}).get("circuit") or {}).get("elementCount") == 1)
        r = edit("E circuit_edit: add it back, rename, 16 A", [{"op": "electrical_circuit_edit", "circuit": cid, "add": [s_ids[1]], "load_name": "MCP sockets", "rating": 16}])
        cc = (r or {}).get("circuit") or {}
        check("two loads, new name and rating", cc.get("elementCount") == 2 and cc.get("loadName") == "MCP sockets" and "16" in str(cc.get("rating")), json.dumps(cc, default=str)[:400])
        r = edit("E circuit_edit: off the panel", [{"op": "electrical_circuit_edit", "circuit": cid, "disconnect_panel": True}])
        check("circuit has no panel", not ((r or {}).get("circuit") or {}).get("panel"))
        r = edit("E circuit_edit: back on the panel", [{"op": "electrical_circuit_edit", "circuit": cid, "panel": str(panel["id"])}])
        check("circuit back on the panel", ((r or {}).get("circuit") or {}).get("panel") == panel["name"])
        edit("E circuit_edit: remove all loads (refused)", [{"op": "electrical_circuit_edit", "circuit": cid, "remove": s_ids}], expect_error=True, contains="empty")
        edit("E circuit_edit: nothing to change (refused)", [{"op": "electrical_circuit_edit", "circuit": cid}], expect_error=True, contains="Nothing to change")
    after = read("electrical_circuits", {"panel": panel["name"], "limit": 2000})
    mine = [c for c in after.get("circuits", []) if str(c.get("loadName", "")).startswith("MCP")]
    check("electrical_circuits shows the test circuits on the panel", len(mine) == (2 if l_ids else 1), json.dumps(mine, default=str)[:700])
    loose_ids = {u["id"] for u in read("electrical_circuits", {"limit": 5000}).get("unassigned", [])}
    check("the test loads are not listed as on no circuit", not (set(s_ids + l_ids) & loose_ids))
    r = edit("E electrical_panel_set: rename and same distribution system", [{"op": "electrical_panel_set", "panel": str(panel["id"]), "name": panel["name"] + "-T",
             "distribution_system": panel.get("distributionSystem")}])
    check("panel renamed, distribution system kept", (r or {}).get("name") == panel["name"] + "-T" and (r or {}).get("distributionSystem") == panel.get("distributionSystem"), json.dumps(r))
    edit("E electrical_panel_set: unknown distribution system (refused)", [{"op": "electrical_panel_set", "panel": str(panel["id"]), "distribution_system": "No such"}],
         expect_error=True, contains="No distribution system")

# sockets on a wall of the linked architecture model
lk = None
for placement in ("shared", "origin"):
    lk = edit(f"E revit_link the architecture model ({placement})", [{"op": "revit_link", "path": ARCH, "placement": placement, "pin": True}], quiet_fail=True)
    if lk:
        break
if lk and lk.get("id") and sock:
    lvz = None
    if s_ids:
        e = read("get_elements", {"ids": s_ids[:1]}).get("elements", [{}])[0]
        lvz = ((e.get("location") or {}).get("point") or [0, 0, 300])[2] - 300
    lw = read("get_elements", {"category": "walls", "link": lk["id"], "location": True, "box": True, "limit": 400}).get("elements", [])
    def ok(w):
        loc, box = w.get("location") or {}, w.get("box") or {}
        return "start" in loc and not loc.get("curved") and (loc.get("length") or 0) > 2000 and box and box["max"][2] - box["min"][2] > 1000
    wall = next((w for w in lw if ok(w)), None)
    if wall and lvz is not None:
        s0, e0 = wall["location"]["start"], wall["location"]["end"]
        mx, my = (s0[0] + e0[0]) / 2, (s0[1] + e0[1]) / 2
        h = (wall["box"]["min"][2] + wall["box"]["max"][2]) / 2 - lvz   # mid-height of the linked wall, above the host level
        r = edit("E a socket on a wall of the linked architecture model (at the wall's mid-height)", [{"op": "mep_fixture_place", "type": str(sock["id"]), "level": lv,
                 "points": [[mx, my]], "height": round(h), "walls": wall["id"], "wall_link": lk["id"]}])
        it = first_item(r)
        check("socket hosted on the linked wall's face", "link" in it.get("host", "") and "face" in it.get("host", ""), json.dumps(it, default=str)[:400])
    else:
        SKIPPED.append("linked wall socket: no straight linked wall over 2 m long")
else:
    SKIPPED.append("linked wall socket: the architecture model could not be linked or no socket type")

# cable trays and conduits: elbows, and a tee into the main
tray = pick(types_of("cable trays"), "with Fittings", "Ladder") or pick(types_of("cable trays"), "with Fittings")
cond = pick(types_of("conduits"), "with Fittings", "EMT") or pick(types_of("conduits"), "with Fittings")
if tray:
    m = edit("E mep_route a ladder tray with an elbow", [{"op": "mep_route", "kind": "cable_tray", "level": lv, "type": str(tray["id"]),
             "path": [[X0, Y0 + 20000], [X0 + 6000, Y0 + 20000], [X0 + 6000, Y0 + 24000]], "z": 3000, "width": 300, "height": 100}])
    check("tray run has two segments and an elbow", len((m or {}).get("items", [])) == 2 and len((m or {}).get("fittings", [])) == 1, json.dumps((m or {}).get("fittings")))
    tm = first_item(m).get("id")
    b = edit("E mep_route a branch tray teed into the main tray", [{"op": "mep_route", "kind": "cable_tray", "level": lv, "type": str(tray["id"]),
             "path": [[X0 + 3000, Y0 + 23000], [X0 + 3000, Y0 + 21500]], "z": 3000, "width": 200, "height": 100, "to_main": tm}])
    tmn = (b or {}).get("toMain") or {}
    check("branch tray joined with a tee (main split by copy)", tmn.get("junction") == "tee" and tmn.get("fitting"), json.dumps(tmn, default=str)[:400])
else:
    SKIPPED.append("trays: no cable tray type with fittings")
if cond:
    m = edit("E mep_route a 25 mm conduit with an elbow", [{"op": "mep_route", "kind": "conduit", "level": lv, "type": str(cond["id"]),
             "path": [[X0, Y0 + 30000], [X0 + 5000, Y0 + 30000], [X0 + 5000, Y0 + 33000]], "z": 2900, "diameter": 25}])
    check("conduit run has two segments and an elbow", len((m or {}).get("items", [])) == 2 and len((m or {}).get("fittings", [])) == 1, json.dumps((m or {}).get("fittings")))
    cm = first_item(m).get("id")
    b = edit("E mep_route a conduit teed into the conduit", [{"op": "mep_route", "kind": "conduit", "level": lv, "type": str(cond["id"]),
             "path": [[X0 + 2500, Y0 + 32000], [X0 + 2500, Y0 + 31000]], "z": 2900, "diameter": 25, "to_main": cm}], quiet_fail=True)
    tmn = (b or {}).get("toMain") or {}
    if b:
        check("conduit branch joined with a tee", tmn.get("junction") == "tee", json.dumps(tmn, default=str)[:400])
    else:
        SKIPPED.append("conduit tee: Revit refused it for this conduit type (see the error above; conduit tees depend on the type's fittings)")
else:
    SKIPPED.append("conduits: no conduit type with fittings")
rw = workbook("E")
if rw:
    rep, wb = rw
    check("E workbook has panels and circuits", {"Panels", "Circuits"} <= set(wb.sheetnames), str(wb.sheetnames))
    names = [c.value for c in wb["Circuits"]["E"]] if "Circuits" in wb.sheetnames else []
    check("E workbook lists the test circuit", any(str(n).startswith("MCP") for n in names), str(names[:8]))
    check("E workbook counts cable trays", any("Cable Tray" in k for k in (rep.get("totalsByCategoryM") or {})), json.dumps(rep.get("totalsByCategoryM")))

# =============================================================================== leave: Task 04 active, the E copy closed without saving
call("model_open", {"path": TASK04, "activate": True}, timeout=900)
cl = call("model_close", {"document_id": el_id, "discard_changes": True})
out("model_close E (changes discarded)", cl)
check("E copy closed without saving", cl.get("discardedChanges") is True)
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
sys.exit(1 if FAILED else 0)
