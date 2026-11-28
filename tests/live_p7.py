r"""Live test of roadmap phase 7 (0.19.0), straight to the add-in (no MCP server needed).

  python live_p7.py task04   reads and DRY RUNS only; refuses unless the active model is 'Revit Task 04...'
  python live_p7.py scratch  REAL runs; refuses unless the active model is a new, never saved project.
                             Close it without saving afterwards.

Values (bars, spacings, lengths, laps) are test values only, chosen to exercise the API.
What the model lacks (no coupler family, no rebar in a section ...) is reported as SKIPPED, never as passed.
Started by run_live_p7_task04.bat / run_live_p7_scratch.bat; output in tests\results\live_p7_<mode>.txt.
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
if not document_id:
    raise SystemExit("The bridge did not identify the active test document.")


def call(method, params=None, **kwargs):
    return bridge_client.call(method, {**(params or {}), "document_id": document_id}, connection=connection, **kwargs)


def out(title, value):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < 5000 else text[:5000] + "\n... (truncated)"), flush=True)


def read(title, method, params=None, expect_error=False, show=True):
    try:
        res = call(method, params or {}, timeout=300)
        if show:
            out(title, res)
        if expect_error:
            FAILED.append(title + " (expected an error)")
        return res
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error:
            FAILED.append(title)
        return None


def edit(title, operations, dry_run, expect_error=False):
    try:
        res = call("edit", {"operations": operations, "dry_run": dry_run}, timeout=600)
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error:
            FAILED.append(title)
        return None
    out(title, res)
    ok = (res.get("previewSucceeded") if dry_run else res.get("applied") and res.get("succeeded")) and not res.get("error")
    if bool(ok) == expect_error:
        FAILED.append(title + (" (expected an error)" if expect_error else ""))
    return res


def result(res, i=0):
    """The result object of operation i of an edit reply, or {}."""
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


def pick_bar(types, mm=16):
    bars = types.get("barTypes", [])
    return min(bars, key=lambda b: abs(b["diameterMm"] - mm)) if bars else None


def hook_for(types, bar_name):
    bar = next((b for b in types.get("barTypes", []) if b["name"] == bar_name), None)
    return (bar or {}).get("hooksAllowed", [None])[0] if bar and bar.get("hooksAllowed") else None


before = {k: info.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")}
out("get_project_info (before)", before)
print("bridge", bridge_client.call("ping", connection=connection).get("bridgeVersion"), flush=True)
ensure_permission()

types = read("list_reinforcement_types", "list_reinforcement_types", {"limit": 40}) or {}
check("list_reinforcement_types has every list", all(k in types for k in (
    "barTypes", "hookTypes", "pathReinforcementTypes", "areaReinforcementTypes", "fabricAreaTypes", "fabricSheetTypes",
    "fabricWireTypes", "couplerTypes", "annotationTypes")))
bar = pick_bar(types)
sheet_type = types["fabricSheetTypes"][0]["name"] if types.get("fabricSheetTypes") else "default"
coupler = types["couplerTypes"][0] if types.get("couplerTypes") else None


def coupler_for(bar_name):
    """A coupler type whose name carries the bar type name (e.g. '#5'), or None."""
    return next((c for c in types.get("couplerTypes", []) if bar_name and bar_name.lower() in (c["family"] + " " + c["name"]).lower()), None)
if not bar:
    raise SystemExit("The model has no rebar bar types; nothing to test.")

# ============================================================================== Task 04: reads and dry runs
if MODE == "task04":
    if not info.get("title", "").startswith("Revit Task 04"):
        raise SystemExit("task04 mode: the active model is not Task 04; nothing edited.")
    rl = read("rebar_list (5)", "rebar_list", {"limit": 5}) or {}
    check("rebar_list lists fabric sheets and couplers", "fabricSheets" in rl and "couplers" in rl)
    check("rebar_list systemKind on every bar", all("systemKind" in b for b in rl.get("bars", [])))

    floors = (read("floors", "get_elements", {"category": "floors", "box": True, "limit": 50}, show=False) or {}).get("elements", [])
    # flat slabs only (a ramp's box top is not its surface), largest first
    boxed = [f for f in floors if f.get("box") and f["box"]["max"][2] - f["box"]["min"][2] <= 600]
    area = lambda f: (f["box"]["max"][0] - f["box"]["min"][0]) * (f["box"]["max"][1] - f["box"]["min"][1])
    boxed.sort(key=area, reverse=True)
    slab = boxed[0] if boxed else None
    print("flat slabs (largest first):", [(f["id"], f.get("type"), round(area(f) / 1e6, 1)) for f in boxed[:4]], flush=True)
    if slab:
        lo, hi = slab["box"]["min"], slab["box"]["max"]
        y = lo[1] + 300
        edit("path_reinforcement slab (dry run)", [{"op": "path_reinforcement", "host": slab["id"],
             "path": [[lo[0] + 300, y], [hi[0] - 300, y]], "bar": bar["name"], "spacing": 200, "length": 1000,
             "face": "bottom"}], True)
        usable = bool(types.get("fabricSheetTypes")) and bool(types.get("fabricWireTypes"))
        fa = edit("fabric_area slab (dry run)" + ("" if usable else ": no wire types, refused"), [{"op": "fabric_area", "host": slab["id"],
                  "sheet_type": sheet_type, "location": "bottom"}], True, expect_error=not usable)
        if not usable:
            check("refusal names the missing wire types", "no fabric wire types" in json.dumps(fa or {}) or "wire type" in json.dumps(fa or {}))
            edit("fabric_sheet_place: no wire types, refused", [{"op": "fabric_sheet_place", "host": slab["id"], "sheet_type": sheet_type,
                 "point": [lo[0] + 1000, lo[1] + 1000]}], True, expect_error=True)
            skip("fabric_sheet_place positive in Task 04", "the model has no fabric sheet / wire types (proven in the scratch project)")
        if usable:
          # A sheet placed wholly inside the slab: try origins on a grid inside its box (the slab need not be a rectangle);
          # an origin outside must be refused with the sheet size, and one inside must preview.
          st = next((t for t in types.get("fabricSheetTypes", []) if t["name"] == sheet_type), None)
          sl, sw = (st["overallLength"], st["overallWidth"]) if st else (3000, 2000)
          tries, placed, refused = [], None, 0
          for cand in boxed[:2]:
            clo, chi = cand["box"]["min"], cand["box"]["max"]
            for fx in (0.1, 0.3, 0.5):
              for fy in (0.1, 0.3, 0.5):
                  pt = [clo[0] + fx * (chi[0] - clo[0]), clo[1] + fy * (chi[1] - clo[1])]
                  if pt[0] + sl > chi[0] or pt[1] + sw > chi[1]:
                      continue
                  res = call("edit", {"operations": [{"op": "fabric_sheet_place", "host": cand["id"], "sheet_type": sheet_type,
                                                      "point": pt, "location": "top"}], "dry_run": True}, timeout=300)
                  tries.append({"slab": cand["id"], "point": [round(v) for v in pt], "ok": res.get("previewSucceeded"), "error": (res.get("error") or "")[:90]})
                  if res.get("previewSucceeded"):
                      placed = res
                      break
                  refused += "does not lie inside host" in (res.get("error") or "")
              if placed:
                  break
            if placed:
              break
          out("fabric_sheet_place tries (dry runs)", tries)
          check("fabric sheet previews at a point inside the slab", placed is not None, json.dumps(tries[-1:] if tries else "no point fits"))
          check("points outside are refused with the sheet size", refused == sum(1 for t in tries if not t["ok"]), f"{refused} refused")
        edit("path_reinforcement missing length (refused)", [{"op": "path_reinforcement", "host": slab["id"],
             "path": [[lo[0] + 300, y], [hi[0] - 300, y]], "bar": bar["name"], "spacing": 200}], True, expect_error=True)
    else:
        skip("slab dry runs", "no floor with a box")

    shape_bars = [b for b in (read("rebar_list (200)", "rebar_list", {"limit": 200}, show=False) or {}).get("bars", []) if not b.get("system")]
    if shape_bars:
        b0 = shape_bars[0]
        hook = hook_for(types, b0["barType"])
        if hook:
            edit("rebar_hooks start (dry run)", [{"op": "rebar_hooks", "ids": [b0["id"]], "start": hook, "start_orientation": "right"}], True)
        else:
            skip("rebar_hooks with a hook", f"bar type {b0['barType']} allows no hook")
        edit("rebar_hooks none + rotation (dry run)", [{"op": "rebar_hooks", "ids": [b0["id"]], "end": "none", "start_rotation": 0}], True)
        edit("rebar_hooks unknown hook (refused)", [{"op": "rebar_hooks", "ids": [b0["id"]], "start": "No Such Hook"}], True, expect_error=True)
        edit("rebar_hooks nothing given (refused)", [{"op": "rebar_hooks", "ids": [b0["id"]]}], True, expect_error=True)
        if coupler:
            res = call("edit", {"operations": [{"op": "coupler_place", "type": str(coupler["id"]),
                                                "first": {"rebar": b0["id"], "end": "end"}}], "dry_run": True}, timeout=300)
            out("coupler_place one bar end (dry run; Revit decides)", res)
        else:
            skip("coupler_place", "no coupler family loaded in Task 04")
        edit("coupler_place unknown type (refused)", [{"op": "coupler_place", "type": "No Such Coupler",
             "first": {"rebar": b0["id"], "end": "end"}}], True, expect_error=True)
    else:
        skip("hooks / couplers", "no shape-driven bars")

    sections = (read("sections", "list_views", {"type": "Section", "limit": 50}, show=False) or {}).get("views", [])
    # A bar set seen across its bars in a section previews; sets laid out along the view are refused with the clear message.
    attempts, done = [], None
    for v in sections[:15]:
        seen = (read(f"rebar in {v['name']}", "rebar_list", {"view": v["name"], "limit": 20}, show=False) or {}).get("bars", [])
        for b in seen[:8]:
            el = (read("bar box", "get_elements", {"ids": [b["id"]], "box": True}, show=False) or {}).get("elements", [{}])[0]
            if not el.get("box"):
                continue
            lo, hi = el["box"]["min"], el["box"]["max"]
            res = call("edit", {"operations": [{"op": "rebar_annotate", "ids": [b["id"]], "view": v["name"],
                                                "origin": [(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2] - 300]}], "dry_run": True}, timeout=300)
            attempts.append({"view": v["name"], "bar": b["id"], "ok": res.get("previewSucceeded"), "error": (res.get("error") or "")[:100]})
            if res.get("previewSucceeded"):
                done = res
                break
        if done:
            break
    out("rebar_annotate attempts (dry runs)", attempts)
    if attempts:
        check("rebar_annotate previews for a bar set seen across its bars", done is not None, json.dumps(attempts[-1:]))
        bad = [a for a in attempts if not a["ok"] and "laid out along the viewing direction" not in a["error"]]
        check("other attempts refused only with the clear along-the-view message", not bad, json.dumps(bad[:3]))
    else:
        skip("rebar_annotate", "no section shows rebar")

    after = bridge_client.call("get_project_info", connection=connection)
    after = {k: after.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")}
    out("get_project_info (after)", after)
    check("Task 04 unchanged (no unsaved changes, same counts)", after == before, json.dumps(after))

# ============================================================================== scratch: real runs
elif MODE == "scratch":
    if info.get("path") or info.get("title", "").startswith(("Revit Task 04", "KTR")):
        raise SystemExit("scratch mode needs a new, never saved project (no path); nothing edited.")
    levels = (read("levels", "list_levels", {}, show=False) or {}).get("levels", [])
    level = levels[0]["name"]
    ftypes = (read("floor types", "list_types", {"category": "floors"}, show=False) or {}).get("types", [])
    wtypes = (read("wall types", "list_types", {"category": "walls"}, show=False) or {}).get("types", [])
    fnd = (read("foundation types", "list_types", {"category": "foundations"}, show=False) or {}).get("types", [])
    concrete = lambda ts: next((t for t in ts if "concrete" in t["type"].lower()), ts[0] if ts else None)
    ft, wt = concrete(ftypes), concrete([t for t in wtypes if t.get("family") == "Basic Wall"] or wtypes)

    slab = result(edit("slab (real)", [{"op": "floor_create", "type": str(ft["id"]), "level": level, "structural": True,
                       "boundary": [[0, 0], [6000, 0], [6000, 4000], [0, 4000]]}], False))
    slab_id = (slab.get("ids") or [slab.get("id")])[0] if slab else None
    wall = result(edit("wall (real)", [{"op": "wall_create", "type": str(wt["id"]), "level": level, "height": 3000,
                       "structural": True, "walls": [{"start": [0, 8000], "end": [6000, 8000]}]}], False))
    wall_id = (wall.get("ids") or [wall.get("id")])[0] if wall else None
    print("slab", slab_id, "wall", wall_id, flush=True)

    if slab_id:
        pr = edit("path_reinforcement slab (real)", [{"op": "path_reinforcement", "host": slab_id, "path": [[300, 300], [5700, 300]],
                  "bar": bar["name"], "spacing": 200, "length": 1500, "face": "bottom"}], False)
        r = result(pr) if pr else {}
        check("bars beyond the slab flagged (path at the edge, no flip)", r.get("barsBeyondHost") is True
              and any("beyond host" in w for w in (pr or {}).get("warnings", [])), str(r.get("barsBeyondHost")))
        check("slab path reinforcement made bars", r.get("bars", 0) > 0, json.dumps({k: r.get(k) for k in ("bars", "barSets", "face", "spacing", "length")}))
        fa = edit("fabric_area slab (real)", [{"op": "fabric_area", "host": slab_id, "sheet_type": sheet_type, "location": "top",
                  "lap_major": 400, "lap_minor": 300}], False)
        r = result(fa) if fa else {}
        check("fabric area laps as given (400 / 300)", r.get("lapMajor") == 400 and r.get("lapMinor") == 300,
              json.dumps({k: r.get(k) for k in ("lapMajor", "lapMinor")}))
        check("fabric area made sheets", r.get("sheets", 0) > 0, json.dumps({k: r.get(k) for k in ("sheets", "massKg", "lapMajor", "lapMinor", "location")}))
        fs = edit("fabric_sheet_place (real)", [{"op": "fabric_sheet_place", "host": slab_id, "sheet_type": r.get("sheetType") or sheet_type,
                  "point": [3000, 2000], "rotation": 30, "location": "bottom"}], False)
        r = result(fs) if fs else {}
        check("fabric sheet placed", bool(r.get("id")), json.dumps(r))
        pf = edit("path_reinforcement slab flipped (real)", [{"op": "path_reinforcement", "host": slab_id, "path": [[300, 300], [5700, 300]],
                  "bar": bar["name"], "spacing": 200, "length": 1500, "face": "top", "flip": True}], False)
        r = result(pf) if pf else {}
        check("flipped path keeps the bars inside the slab", r.get("barsBeyondHost") is False and r.get("bars", 0) > 0,
              json.dumps({k: r.get(k) for k in ("bars", "barsBeyondHost", "flipped", "face")}))
    if wall_id:
        pw = edit("path_reinforcement wall + alternating (real)", [{"op": "path_reinforcement", "host": wall_id,
                  "path": [[200, 8000], [5800, 8000]], "bar": bar["name"], "spacing": 150, "length": 1200, "face": "exterior",
                  "alternating": {"length": 1000}}], False)
        r = result(pw) if pw else {}
        check("wall path reinforcement made bars", r.get("bars", 0) > 0, json.dumps({k: r.get(k) for k in ("bars", "face", "alternating")}))

    rl = read("rebar_list", "rebar_list", {"limit": 50}) or {}
    kinds = {b.get("systemKind") for b in rl.get("bars", [])}
    check("rebar_list shows path bars", "path" in kinds, str(kinds))
    check("rebar_list shows fabric sheets", rl.get("fabricSheetCount", 0) > 0, str(rl.get("fabricSheetCount")))

    # Shape-driven bars for hooks: a footing reinforced with rebar_footing.
    bars = []
    if fnd:
        ftg = result(edit("footing (real)", [{"op": "footing_create", "type": str(fnd[0]["id"]), "level": level, "points": [[10000, 2000]]}], False))
        ftg_id = (ftg.get("ids") or [None])[0] if ftg else None
        if ftg_id:
            rf = edit("rebar_footing (real)", [{"op": "rebar_footing", "ids": [ftg_id], "bar": bar["name"], "spacing": 200, "cover": 75}], False)
            items = (result(rf) or {}).get("items", []) if rf else []
            bars = items[0].get("rebarIds", []) if items else []
    if bars:
        hook = hook_for(types, bar["name"])
        if hook:
            hk = edit("rebar_hooks (real)", [{"op": "rebar_hooks", "ids": bars[:2], "start": hook, "start_orientation": "right",
                      "end": hook}], False)
            r = result(hk) if hk else {}
            check("hooks set on the bars", r.get("bars") == len(bars[:2]), json.dumps(r))
            got = read("hooked bar", "get_element", {"id": bars[0], "include_type": False}, show=False) or {}
            hp = {k: v for k, v in (got.get("parameters") or {}).items() if "hook" in k.lower()}
            out("hook parameters of the first bar", hp)
            check("bar reports the start hook", any(hook == (v.get("name") if isinstance(v, dict) else v) for v in hp.values()), json.dumps(hp))
            edit("rebar_hooks none (real)", [{"op": "rebar_hooks", "ids": bars[:1], "start": "none", "end": "none"}], False)
        else:
            skip("rebar_hooks", f"bar type {bar['name']} allows no hook type")
        match = coupler_for(bar["name"])
        if match:
            cp = edit("coupler_place matching type on one bar end (real)", [{"op": "coupler_place", "type": str(match["id"]),
                      "first": {"rebar": bars[0], "end": "end"}}], False)
            r = result(cp) if cp else {}
            check("coupler placed", bool(r.get("id")), json.dumps(r))
        else:
            skip("coupler_place real", f"no coupler type named for bar {bar['name']}")
        other = next((c for c in types.get("couplerTypes", []) if c is not match), None)
        if other:
            edit("coupler_place wrong diameter (refused with help)", [{"op": "coupler_place", "type": str(other["id"]),
                 "first": {"rebar": bars[0], "end": "start"}}], True, expect_error=True)
    else:
        skip("rebar_hooks / coupler_place", "no footing type to make shape-driven bars")

    # Annotation of the slab path bars in a section through the slab.
    edit("section along the slab bars (real)", [{"op": "section_create", "start": [3000, -3000], "end": [3000, 5000],
         "name": "P7 along", "depth": 1000}], False)
    along = [b for b in (read("rebar in the along section", "rebar_list", {"view": "P7 along", "limit": 50}, show=False) or {}).get("bars", [])
             if b.get("systemKind") == "path" and b.get("host") == slab_id]
    seen, sec_name = [], "P7 test section"
    if along:
        edit("rebar_annotate bars along the view (refused, clear)", [{"op": "rebar_annotate", "ids": [along[0]["id"]], "view": "P7 along",
             "origin": [3000, 1000, -400]}], True, expect_error=True)
        el = (read("path bar box", "get_elements", {"ids": [along[0]["id"]], "box": True}, show=False) or {}).get("elements", [{}])[0]
        if el.get("box"):
            ymid = (el["box"]["min"][1] + el["box"]["max"][1]) / 2
            print("path bars span y", el["box"]["min"][1], "to", el["box"]["max"][1], flush=True)
            edit("section across the slab bars (real)", [{"op": "section_create", "start": [-1000, ymid], "end": [7000, ymid],
                 "name": sec_name, "depth": 300}], False)
            seen = [b for b in (read("rebar in the section", "rebar_list", {"view": sec_name, "limit": 50}, show=False) or {}).get("bars", [])
                    if b.get("systemKind") == "path" and b.get("host") == slab_id]
    else:
        skip("rebar_annotate refusal", "no slab path bars in the along section")
    if seen:
        el = (read("bar box", "get_elements", {"ids": [seen[0]["id"]], "box": True}, show=False) or {}).get("elements", [{}])[0]
        lo, hi = el["box"]["min"], el["box"]["max"]
        an = edit("rebar_annotate (real)", [{"op": "rebar_annotate", "ids": [seen[0]["id"]], "view": sec_name,
                  "origin": [3000, ymid, lo[2] - 400], "tag_head": [3000, ymid, lo[2] - 900]}], False)
        r = result(an) if an else {}
        check("annotation has a dimension", (r.get("dimension") or 0) > 0, json.dumps(r))
        warned = any("no tag type" in w for w in (an or {}).get("warnings", []))
        check("tag made, or the missing tag type reported", (r.get("tag") or 0) > 0 or warned, json.dumps({"tag": r.get("tag"), "warned": warned}))
    else:
        skip("rebar_annotate", "no rebar visible in the test section")
    read("list_reinforcement_types (after)", "list_reinforcement_types", {"limit": 10})
else:
    raise SystemExit("mode: task04 | scratch")

print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
