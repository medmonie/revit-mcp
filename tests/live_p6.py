r"""Live test of roadmap phase 6 (0.18.0), straight to the add-in (no MCP server needed).

  python live_p6.py task04   reads and DRY RUNS only; refuses unless the active model is 'Revit Task 04...'
  python live_p6.py scratch  REAL runs; refuses unless the active model is a new, never saved project. It links Task 04
                             from disk (so Task 04 must not be open in this Revit), models a wall through a linked
                             column, clashes, reloads, unloads and reloads the link. Close the project without saving.

Started by run_live_p6_task04.bat / run_live_p6_scratch.bat; output in tests\results\live_p6_<mode>.txt.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

MODE = sys.argv[1] if len(sys.argv) > 1 else ""
TASK04 = r"C:\Users\mohamed.hamed\Desktop\New folder\Revit Task 04_Mohamed A.Hamed_66088960.rvt"
FAILED = []

connection = bridge_client.pin()
info = bridge_client.call("get_project_info", connection=connection)
document_id = info.get("documentId")
if not document_id:
    raise SystemExit("The bridge did not identify the active test document.")


def call(method, params=None, **kwargs):
    return bridge_client.call(method, {**(params or {}), "document_id": document_id}, connection=connection, **kwargs)


def out(title, value):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < 6000 else text[:6000] + "\n... (truncated)"), flush=True)


def read(title, method, params=None, expect_error=False):
    try:
        res = call(method, params or {}, timeout=300)
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


out("get_project_info (before)", {k: info.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")})
coords = read("get_coordinates", "get_coordinates", {})
if coords:
    check("shared transform reproduces Revit's base points", coords["sharedCoordinates"]["checkError"] < 1, str(coords["sharedCoordinates"]))

if MODE == "task04":
    if not info.get("title", "").startswith("Revit Task 04"):
        raise SystemExit("task04 mode: the active model is not Task 04; nothing edited.")
    read("list_links", "list_links", {})
    ann = read("list_annotations (all, 30)", "list_annotations", {"limit": 30})
    read("list_annotations only issues", "list_annotations", {"only_issues": True, "limit": 50})
    read("list_annotations dimensions", "list_annotations", {"kind": ["dimensions"], "limit": 20})
    sheets = (read("sheets", "list_sheets", {"limit": 5}) or {}).get("sheets", [])
    if sheets:
        read("list_annotations sheet " + str(sheets[0].get("number")), "list_annotations", {"sheet": sheets[0].get("number"), "limit": 40})
    read("list_annotations bad kind", "list_annotations", {"kind": ["arrows"]}, expect_error=True)
    read("link_clashes without link", "link_clashes", {}, expect_error=True)
    read("get_coordinates link (Task 04 has none)", "get_coordinates", {"link": "x"}, expect_error=True)
    ensure_permission()
    edit("link_reload (no links: refused)", [{"op": "link_reload", "link": "anything"}], True, expect_error=True)
    edit("link_set nothing given (refused)", [{"op": "link_set", "link": "anything"}], True, expect_error=True)

elif MODE == "scratch":
    _path = info.get("path") or ""
    _claude_scratch = "\\RevitMcpScratch\\" in _path and any(d.get("active") and d.get("openedByClaude")
                                                          for d in bridge_client.call("list_documents", connection=connection).get("documents", []))
    if (_path and not _claude_scratch) or info.get("title", "").startswith("Revit Task 04") or "KTR15" in info.get("title", ""):
        raise SystemExit("scratch mode: the active model has been saved (or is Task 04 / KTR15); nothing edited.")
    ensure_permission()
    edit("revit_link Task 04 (dry run)", [{"op": "revit_link", "path": TASK04, "placement": "origin", "pin": False}], True)
    res = edit("revit_link Task 04 (real)", [{"op": "revit_link", "path": TASK04, "placement": "origin", "pin": False}], False)
    link = str(res["results"][0]["result"]["id"]) if res else None
    if not link:
        raise SystemExit("could not link Task 04")
    links = read("list_links", "list_links", {})
    lc = read("get_coordinates link", "get_coordinates", {"link": link})
    if lc:
        check("link coordinates answered", "sharedCoordinatesAgree" in lc, str(lc.get("alignment")))

    edit("link_set pin + overlay (real)", [{"op": "link_set", "link": link, "pinned": True, "attachment": "overlay"}], False)
    edit("link_set path relative (real)", [{"op": "link_set", "link": link, "path_type": "relative"}], False)
    edit("link_set again: nothing changes", [{"op": "link_set", "link": link, "pinned": True}], False)
    edit("link_set attachment bad value", [{"op": "link_set", "link": link, "attachment": "glue"}], True, expect_error=True)

    # A wall through a linked column, at the column's height, so link_clashes has one clash to find.
    cols = (read("linked columns", "get_elements", {"link": link, "category": "columns", "limit": 3, "box": True}) or {}).get("elements", [])
    wtypes = (read("wall types", "list_types", {"category": "walls", "limit": 50}) or {}).get("types", [])
    levels = (read("levels", "list_levels", {}) or {}).get("levels", [])
    if cols and wtypes and levels:
        box = cols[0]["box"]
        lo, hi = box["min"], box["max"]
        cx, cy = (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2
        level = levels[0]
        base = lo[2] - level["elevation"]
        basic = next((w for w in wtypes if w.get("family") == "Basic Wall"), wtypes[0])
        wall = edit("wall through linked column (real)", [{"op": "wall_create", "type": str(basic["id"]), "level": level["name"],
                    "walls": [{"start": [cx - 1500, cy], "end": [cx + 1500, cy]}], "base_offset": base,
                    "height": hi[2] - lo[2], "structural": False}], False)
        clash = read("link_clashes walls vs link", "link_clashes", {"link": link, "category": "walls", "limit": 20})
        if clash:
            hit = [c for c in clash.get("clashes", []) if c["linkedId"] == cols[0]["id"]]
            check("clash found with the linked column", bool(hit), json.dumps(hit[:1]))
        read("link_clashes link_category columns", "link_clashes", {"link": link, "category": "walls", "link_category": "columns"})
        read("link_clashes default categories", "link_clashes", {"link": link, "max_seconds": 60})
    else:
        check("set-up for the clash test", False, "no linked columns, wall types or levels")

    edit("link_reload (dry run)", [{"op": "link_reload", "link": link}], True)
    r = edit("link_reload (real)", [{"op": "link_reload", "link": link}], False)
    if r:
        check("reloaded", r["results"][0]["result"].get("loadResult") == "LinkLoaded", json.dumps(r["results"][0]["result"]))
    r = edit("link_reload from path (real)", [{"op": "link_reload", "link": link, "path": TASK04}], False)
    edit("link_reload from a missing file", [{"op": "link_reload", "link": link, "path": r"C:\nowhere\missing.rvt"}], True, expect_error=True)
    edit("link_unload (dry run)", [{"op": "link_unload", "link": link}], True)
    r = edit("link_unload (real)", [{"op": "link_unload", "link": link}], False)
    if r:
        check("unloaded", r["results"][0]["result"].get("statusAfter") == "Unloaded", json.dumps(r["results"][0]["result"]))
    read("link_clashes on the unloaded link", "link_clashes", {"link": link}, expect_error=True)
    edit("link_unload again (not loaded: refused)", [{"op": "link_unload", "link": link}], True, expect_error=True)
    r = edit("link_reload after unload (real)", [{"op": "link_reload", "link": link}], False)
    if r:
        check("loaded again", r["results"][0]["result"].get("statusAfter") == "Loaded", json.dumps(r["results"][0]["result"]))
    edit("link_unload for=me (not workshared: refused)", [{"op": "link_unload", "link": link, "for": "me"}], True, expect_error=True)
    read("list_links after", "list_links", {})
else:
    raise SystemExit("usage: live_p6.py task04 | scratch")

after = call("get_project_info", {})
out("get_project_info (after)", {k: after.get(k) for k in ("title", "hasUnsavedChanges", "counts")})
print("\nALL CHECKS AS EXPECTED" if not FAILED else "\nNOT AS EXPECTED: " + "; ".join(FAILED))
sys.exit(1 if FAILED else 0)
