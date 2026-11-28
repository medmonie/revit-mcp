r"""Live test of 0.22.0: MEP runs, openings at MEP crossings, rooms and spaces, analytical members and panels, load cases,
combinations and loads (roadmap phases 10, 11, 13 edits).

  python live_p10.py task04   DRY RUNS only; refuses unless the active model is 'Revit Task 04...' (Revit 2024)
  python live_p10.py scratch  REAL runs; refuses unless the active model is a new, never saved project. Close it without saving.

Values are test values only (no design). What the model lacks is reported as SKIPPED, never as passed.
Started by run_live_p10_task04.bat / run_live_p10_scratch.bat; output in tests\results\live_p10_<mode>.txt.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

MODE = sys.argv[1] if len(sys.argv) > 1 else ""
FAILED, SKIPPED = [], []

connection = bridge_client.pin()
info = bridge_client.call("get_project_info", connection=connection)
document_id = info.get("documentId")
ping = bridge_client.call("ping", connection=connection)
REVIT = str(ping.get("revitVersion"))
print("bridge", ping.get("bridgeVersion"), "Revit", REVIT, flush=True)
if not document_id:
    raise SystemExit("The bridge did not identify the active test document.")


def call(method, params=None, **kwargs):
    return bridge_client.call(method, {**(params or {}), "document_id": document_id}, connection=connection, **kwargs)


def out(title, value):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < 4000 else text[:4000] + "\n... (truncated)"), flush=True)


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


def edit(title, operations, dry_run, expect_error=False, contains=None):
    try:
        res = call("edit", {"operations": operations, "dry_run": dry_run}, timeout=600)
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error or (contains and contains not in str(exc)):
            FAILED.append(title)
        return None
    out(title, res)
    ok = (res.get("previewSucceeded") if dry_run else res.get("applied") and res.get("succeeded")) and not res.get("error")
    if bool(ok) == expect_error:
        FAILED.append(title + (" (expected an error)" if expect_error else ""))
    if expect_error and contains and contains not in json.dumps(res):
        FAILED.append(title + f" (error should mention '{contains}')")
    return res


def result(res, i=0):
    try:
        return res["results"][i]["result"]
    except Exception:
        return {}


def check(title, condition, detail=""):
    print(f"----- check {title}: {'OK' if condition else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not condition:
        FAILED.append(title)


def skip(title, why):
    print(f"----- SKIPPED {title}: {why}", flush=True)
    SKIPPED.append(f"{title} ({why})")


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


def rebar_schema(numbering):
    return next((s for s in (numbering or {}).get("schemas", []) if s.get("kind") == "rebar"), None)




def near(a, b, tol):
    return a is not None and b is not None and abs(a - b) <= tol


def first_type(category, words=()):
    types = (read(f"{category} types", "list_types", {"category": category}, show=False) or {}).get("types", [])
    for w in words:
        t = next((x for x in types if w in (x.get("family", "") + " " + x.get("type", "")).lower()), None)
        if t:
            return t
    return types[0] if types else None


before = {k: info.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")}
out("get_project_info (before)", before)
ensure_permission()
levels = sorted((read("levels", "list_levels", {}, show=False) or {}).get("levels", []), key=lambda lv: lv.get("elevation", 0))
level = levels[0]["name"] if levels else None
lz = levels[0].get("elevation", 0) if levels else 0

# ============================================================================== Task 04: dry runs
if MODE == "task04":
    if not info.get("title", "").startswith("Revit Task 04"):
        raise SystemExit("task04 mode: the active model is not Task 04; nothing edited.")
    edit("room_create all_regions (dry run)", [{"op": "room_create", "level": level, "all_regions": True}], True)
    edit("load_case_create (dry run)", [{"op": "load_case_create", "name": "MCP dry", "category": "dead", "create_nature": True}], True)
    edit("load_case_create without category (refused)", [{"op": "load_case_create", "name": "MCP dry"}], True, expect_error=True,
         contains="'category' is required")
    edit("analytical_member_create (dry run)", [{"op": "analytical_member_create", "start": [0, 0, lz], "end": [5000, 0, lz]}], True)
    edit("analytical_panel_create (dry run)", [{"op": "analytical_panel_create", "points": [[0, 0, lz], [4000, 0, lz], [4000, 3000, lz], [0, 3000, lz]]}], True)
    mr = None
    try:
        mr = call("edit", {"operations": [{"op": "mep_run_create", "kind": "pipe", "level": level, "runs": [{"start": [0, 0, lz + 2500], "end": [3000, 0, lz + 2500]}]}],
                  "dry_run": True}, timeout=300)
        out("mep_run_create pipe (dry run)", mr)
    except Exception as exc:
        print("mep_run_create pipe (dry run):", exc, flush=True)
        if "no pipe type" in str(exc).lower() or "no piping system" in str(exc).lower():
            skip("mep_run_create dry run", "Task 04 has no pipe types")
        else:
            FAILED.append("mep_run_create pipe (dry run)")
    after = bridge_client.call("get_project_info", connection=connection)
    after = {k: after.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")}
    out("get_project_info (after)", after)
    check("Task 04 unchanged (no unsaved changes, same counts)", after == before, json.dumps(after))

# ============================================================================== scratch: real runs
elif MODE == "scratch":
    path = info.get("path") or ""
    claude_scratch = "\\RevitMcpScratch\\" in path and next((d for d in call("list_documents").get("documents", [])
                                                              if d.get("active") and d.get("openedByClaude")), None) is not None
    if (path and not claude_scratch) or info.get("title", "").startswith(("Revit Task 04", "KTR")):
        raise SystemExit("scratch mode needs a new, never saved project (no path) or a scratch made by model_new; nothing edited.")
    wall_t = first_type("walls", ("generic",))
    floor_t = first_type("floors", ("generic",))
    beam_t = first_type("beams", ("concrete",))
    if not beam_t or "concrete" not in (beam_t.get("family", "") + beam_t.get("type", "")).lower():
        lib = rf"C:\ProgramData\Autodesk\RVT {REVIT}\Libraries\English-Imperial\US\Structural Framing\Concrete\Concrete-Rectangular Beam.rfa"
        try:
            call("edit", {"operations": [{"op": "family_load", "path": lib}], "dry_run": False}, timeout=300)
        except Exception as exc:
            print("family_load:", exc, flush=True)
        beam_t = first_type("beams", ("concrete",))

    # --- the box: 4 walls 3 m high, a slab at the level, a beam 3 m up
    W = result(edit("walls 10 x 8 m (real)", [{"op": "wall_create", "type": str(wall_t["id"]), "level": level, "height": 3000, "walls": [
        {"start": [0, 0], "end": [10000, 0]}, {"start": [10000, 0], "end": [10000, 8000]},
        {"start": [10000, 8000], "end": [0, 8000]}, {"start": [0, 8000], "end": [0, 0]}]}], False)) if wall_t else {}
    wall_ids = (W or {}).get("ids", [])
    F = result(edit("slab (real)", [{"op": "floor_create", "type": str(floor_t["id"]), "level": level,
                                     "boundary": [[0, 0], [10000, 0], [10000, 8000], [0, 8000]]}], False)) if floor_t else {}
    floor_id = (F or {}).get("id") or ((F or {}).get("ids") or [None])[0]
    B = result(edit("beam 3 m up (real)", [{"op": "beam_create", "type": str(beam_t["id"]), "level": level, "z_offset": 3000,
                                            "beams": [{"start": [1000, 6000], "end": [9000, 6000]}]}], False)) if beam_t else {}
    beam_id = ((B or {}).get("ids") or [None])[0]
    print("walls", wall_ids, "floor", floor_id, "beam", beam_id, flush=True)
    check("box modelled (4 walls, slab, beam)", len(wall_ids) == 4 and floor_id and beam_id)

    # --- rooms and spaces
    r1 = result(edit("room_create at the centre (real)", [{"op": "room_create", "level": level, "points": [[5000, 4000]], "names": ["MCP Hall"], "numbers": ["101"]}], False))
    a1 = ((r1 or {}).get("items") or [{}])[0].get("areaM2")
    check("room fills the box (70-80 m2)", a1 is not None and 70 < a1 < 80, f"area {a1}")
    sep = edit("room_separation at x = 5 m (real)", [{"op": "room_separation", "level": level, "lines": [{"start": [5000, 0], "end": [5000, 8000]}]}], False)
    check("one separation line", (result(sep) or {}).get("created") == 1)
    r2 = result(edit("room_create in the other half (real)", [{"op": "room_create", "level": level, "points": [[7500, 4000]], "names": ["MCP East"], "numbers": ["102"]}], False))
    a2 = ((r2 or {}).get("items") or [{}])[0].get("areaM2")
    check("east room is about half (35-40 m2)", a2 is not None and 35 < a2 < 40, f"area {a2}")
    sp = read("list_spaces rooms", "list_spaces", {"kind": "rooms", "level": level})
    names = {i.get("name"): i.get("areaM2") for i in (sp or {}).get("items", [])}
    check("both rooms read back, the hall now about half", "MCP Hall" in names and "MCP East" in names and 35 < (names.get("MCP Hall") or 0) < 40, json.dumps(names))
    ra = result(edit("room_create all_regions (real)", [{"op": "room_create", "level": level, "all_regions": True}], False))
    check("no region left without a room", (ra or {}).get("created") == 0, json.dumps(ra))
    edit("room_create open point, outside the box (real, warns)", [{"op": "room_create", "level": level, "points": [[20000, 20000]]}], False)
    edit("room_create names count mismatch (refused)", [{"op": "room_create", "level": level, "points": [[1000, 1000], [2000, 1000]], "names": ["A"]}],
         False, expect_error=True, contains="one name per point")
    s1 = result(edit("space at a point (real)", [{"op": "room_create", "kind": "spaces", "level": level, "points": [[2500, 4000]], "names": ["MCP Space"]}], False))
    check("space created", (s1 or {}).get("created") == 1, json.dumps(s1))

    # --- MEP runs through the slab, the wall and the beam
    runs = {}
    pv = edit("pipe down through the slab (real)", [{"op": "mep_run_create", "kind": "pipe", "level": level, "diameter": 50,
              "runs": [{"start": [2000, 2000, lz - 1000], "end": [2000, 2000, lz + 1000]}]}], False)
    runs["slab"] = ((result(pv) or {}).get("items") or [{}])[0].get("id") if pv else None
    pb = edit("pipe through the beam web (real)", [{"op": "mep_run_create", "kind": "pipe", "level": level, "diameter": 50,
              "runs": [{"start": [4000, 5000, lz + 2750], "end": [4000, 7000, lz + 2750]}]}], False)
    runs["beam"] = ((result(pb) or {}).get("items") or [{}])[0].get("id") if pb else None
    dw = None
    try:
        dw = call("edit", {"operations": [{"op": "mep_run_create", "kind": "duct", "level": level, "width": 400, "height": 300,
                  "runs": [{"start": [5000, -1000, lz + 1500], "end": [5000, 1000, lz + 1500]}]}], "dry_run": False}, timeout=300)
    except Exception as exc:
        if "is round" in str(exc):
            dw = call("edit", {"operations": [{"op": "mep_run_create", "kind": "duct", "level": level, "diameter": 300,
                      "runs": [{"start": [5000, -1000, lz + 1500], "end": [5000, 1000, lz + 1500]}]}], "dry_run": False}, timeout=300)
        else:
            print("duct:", exc, flush=True)
    out("duct through the wall (real)", dw)
    runs["wall"] = ((result(dw) or {}).get("items") or [{}])[0].get("id") if dw and dw.get("applied") else None
    print("runs", runs, flush=True)
    for k, v in runs.items():
        if not v:
            skip(f"run through the {k}", "this template has no type for it")
    if runs.get("slab"):
        edit("mep_run_create unknown system (refused)", [{"op": "mep_run_create", "kind": "pipe", "level": level, "system": "No Such System",
             "runs": [{"start": [0, 0], "end": [1000, 0]}]}], False, expect_error=True, contains="No piping system type")

    cx = read("mep_crossings (host, before)", "mep_crossings", {})
    def crossings_of(res):
        return {(i["mep"], i["structure"]) for i in (res or {}).get("items", [])}
    before_x = crossings_of(cx)
    want = {"slab": floor_id, "beam": beam_id, "wall": wall_ids[0] if wall_ids else None}
    for k, rid in runs.items():
        if rid:
            check(f"mep_crossings finds the run through the {k}", (rid, want[k]) in before_x, json.dumps(sorted(before_x)))
    # openings, clearance 50 each side
    expect = {"slab": ("diameter", 150), "beam": ("diameter", 150)}
    for k, rid in runs.items():
        if not rid or not want[k]:
            continue
        o = result(edit(f"crossing_opening in the {k} (real)", [{"op": "crossing_opening", "structure": want[k], "mep": rid, "clearance": 50}], False))
        print(k, json.dumps(o), flush=True)
        if k in expect:
            key, val = expect[k]
            check(f"{k} opening is the run plus 50 each side", near((o or {}).get(key), val, 1), json.dumps(o))
        else:
            check("wall opening is the duct plus 50 each side", near((o or {}).get("width"), 500, 1) and near((o or {}).get("height"), 400, 1)
                  or near((o or {}).get("width"), 400, 1), json.dumps(o))
    after_x = crossings_of(read("mep_crossings (host, after)", "mep_crossings", {}, show=False))
    left = {(r, want[k]) for k, r in runs.items() if r} & after_x
    check("no crossing left after the openings", not left, json.dumps(sorted(left)))
    if runs.get("slab") and wall_ids:
        edit("crossing_opening where the run does not pass (refused)", [{"op": "crossing_opening", "structure": wall_ids[1], "mep": runs["slab"], "clearance": 50}],
             False, expect_error=True, contains="does not pass")
    if runs.get("slab") and floor_id:
        edit("crossing_opening again in the slab (refused: already open)", [{"op": "crossing_opening", "structure": floor_id, "mep": runs["slab"], "clearance": 50}],
             False, expect_error=True, contains="does not pass")
    if runs.get("beam") and wall_ids:
        edit("crossing_opening without clearance (refused)", [{"op": "crossing_opening", "structure": beam_id, "mep": runs["beam"]}],
             False, expect_error=True, contains="clearance")

    # --- analytical model
    am0 = read("analytical_model (before)", "analytical_model", {}, show=False) or {}
    assoc = {m.get("physical") for m in am0.get("members", {}).get("items", [])} | {m.get("physical") for m in am0.get("panels", {}).get("items", [])}
    mem_op = {"op": "analytical_member_create", "start": [1000, 6000, lz + 3000], "end": [9000, 6000, lz + 3000], "role": "beam"}
    if beam_id and beam_id not in assoc:
        mem_op["physical"] = beam_id
    pan_op = {"op": "analytical_panel_create", "points": [[0, 0, lz], [10000, 0, lz], [10000, 8000, lz], [0, 8000, lz]], "thickness": 200}
    if floor_id and floor_id not in assoc:
        pan_op["physical"] = floor_id
    am = result(edit("analytical member on the beam (real)", [mem_op], False))
    ap = result(edit("analytical panel on the slab (real)", [pan_op], False))
    mid, pid = (am or {}).get("id"), (ap or {}).get("id")
    check("analytical member and panel created", bool(mid and pid), json.dumps([am, ap]))
    am1 = read("analytical_model (after)", "analytical_model", {}) or {}
    mrow = next((m for m in am1.get("members", {}).get("items", []) if m.get("id") == mid), {})
    prow = next((m for m in am1.get("panels", {}).get("items", []) if m.get("id") == pid), {})
    check("member reads back: 8000 long, beam role", near(mrow.get("length"), 8000, 1) and "Beam" in str(mrow.get("role")), json.dumps(mrow))
    if "physical" in mem_op:
        check("member associated with the beam", mrow.get("physical") == beam_id, json.dumps(mrow))
    check("panel reads back: 200 thick", near(prow.get("thickness"), 200, 1), json.dumps(prow))
    if "physical" in pan_op:
        check("panel associated with the slab", prow.get("physical") == floor_id, json.dumps(prow))
    if beam_id and "physical" in mem_op:
        edit("second association to the same beam (refused)", [{**mem_op}], False, expect_error=True, contains="already has")

    # --- loads
    edit("load case MCP Dead (real)", [{"op": "load_case_create", "name": "MCP Dead", "category": "dead", "create_nature": True, "if_exists": "skip"}], False)
    edit("load case MCP Live (real)", [{"op": "load_case_create", "name": "MCP Live", "category": "live", "create_nature": True, "if_exists": "skip"}], False)
    edit("load case again (refused)", [{"op": "load_case_create", "name": "MCP Dead", "category": "dead"}], False, expect_error=True, contains="already exists")
    co = result(edit("combination 1.4 D + 1.6 L (real)", [{"op": "load_combination_create", "name": "MCP ULS", "components": [
        {"case": "MCP Dead", "factor": 1.4}, {"case": "MCP Live", "factor": 1.6}]}], False))
    check("combination has the two factors", [c.get("factor") for c in (co or {}).get("components", [])] == [1.4, 1.6], json.dumps(co))
    edit("combination with an unknown case (refused)", [{"op": "load_combination_create", "name": "MCP bad", "components": [{"case": "Nope", "factor": 1}]}],
         False, expect_error=True, contains="No load case")
    if mid and pid:
        loads = [
            ("point at the member end", {"kind": "point", "load_case": "MCP Dead", "host": mid, "at_end": "end", "force": [0, 0, -10]}),
            ("point at 5 m", {"kind": "point", "load_case": "MCP Live", "host": mid, "point": [5000, 6000, lz + 3000], "force": [0, 0, -20]}),
            ("line on the whole member", {"kind": "line", "load_case": "MCP Dead", "host": mid, "force": [0, 0, -15]}),
            ("line on 2-4 m", {"kind": "line", "load_case": "MCP Live", "host": mid, "start": [2000, 6000, lz + 3000], "end": [4000, 6000, lz + 3000], "force": [0, 0, -5]}),
            ("area on the whole panel", {"kind": "area", "load_case": "MCP Dead", "host": pid, "force": [0, 0, -3]}),
            ("area on a region", {"kind": "area", "load_case": "MCP Live", "host": pid, "points": [[1000, 1000, lz], [3000, 1000, lz], [3000, 3000, lz], [1000, 3000, lz]], "force": [0, 0, -2]}),
        ]
        made = []
        for title, op in loads:
            r = result(edit(f"load_place {title} (real)", [{"op": "load_place", **op}], False))
            if r and r.get("id"):
                made.append(r["id"])
        check("six loads placed", len(made) == 6, json.dumps(made))
        if beam_id and "physical" in mem_op:
            r = result(edit("load_place on the physical beam (uses its analytical member)", [{"op": "load_place", "kind": "line", "load_case": "MCP Dead", "host": beam_id, "force": [0, 0, -1]}], False))
            check("load on the physical beam lands on the analytical member", (r or {}).get("host") == mid, json.dumps(r))
        edit("point load on a panel (refused)", [{"op": "load_place", "kind": "point", "load_case": "MCP Dead", "host": pid, "force": [0, 0, -1]}],
             False, expect_error=True, contains="analytical member")
        edit("load with an unknown case (refused)", [{"op": "load_place", "kind": "line", "load_case": "Nope", "host": mid, "force": [0, 0, -1]}],
             False, expect_error=True, contains="No load case")
        ll = read("list_loads", "list_loads", {}) or {}
        items = {x["id"]: x for x in ll.get("loads", {}).get("items", [])}
        p0 = items.get(made[0]) if made else None
        check("point load reads back -10 kN in MCP Dead", p0 is not None and near((p0.get("forceKN") or [0, 0, 0])[2], -10, 0.01) and p0.get("case") == "MCP Dead", json.dumps(p0))
        a0 = items.get(made[4]) if len(made) > 4 else None
        check("area load reads back -3 kN/m2 over about 80 m2", a0 is not None and near((a0.get("force1KNm2") or [0, 0, 0])[2], -3, 0.01) and near(a0.get("areaM2"), 80, 1), json.dumps(a0))
        combo = next((c for c in ll.get("combinations", []) if c.get("name") == "MCP ULS"), None)
        check("combination reads back", combo is not None and len(combo.get("components", [])) == 2, json.dumps(combo))
    else:
        skip("loads", "no analytical member / panel")
else:
    raise SystemExit("mode: task04 | scratch")

print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
sys.exit(1 if FAILED else 0)
