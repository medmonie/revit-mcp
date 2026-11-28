r"""Live test: views_copy_from and standards_copy from Revit Task 02 (drafting views and a legend) into a NEW, never saved
scratch project (both open in the same Revit). Task 02 is only read. Close the scratch project without saving.
Started by run_live_copy.bat; output in tests\results\live_copy.txt.
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





SOURCE = "Revit Task 02_Mohamed A.Hamed_66088960"
if info.get("path") or info.get("title", "").startswith(("Revit Task", "KTR")):
    raise SystemExit("The active model must be a new, never saved project; nothing done.")
if SOURCE not in [m.get("title") for m in info.get("openModels", [])]:
    raise SystemExit(f"{SOURCE} is not open in this Revit; nothing done.")
ensure_permission()
src = read("list_detailing in Task 02", "list_detailing", {"model": SOURCE, "kind": "drafting_views"})
leg = read("legends in Task 02", "list_detailing", {"model": SOURCE, "kind": "legends"})
names_d = [v.get("name") for v in (src or {}).get("drafting_views", [])]
names_l = [v.get("name") for v in (leg or {}).get("legends", [])]
print("drafting views:", names_d, "legends:", names_l, flush=True)
edit("views_copy_from without names (refused)", [{"op": "views_copy_from", "model": SOURCE}], False, expect_error=True, contains="Give 'views'")
r = edit("views_copy_from Task 02 drafting views and legend (real)", [{"op": "views_copy_from", "model": SOURCE, "views": names_d + names_l}], False)
res = result(r) if r else {}
check("every drafting view and the legend copied", res.get("copied") == len(names_d) + len(names_l), json.dumps(res)[:800])
mine = read("list_detailing here (after)", "list_detailing", {"kind": "drafting_views"}) or {}
mine_l = read("legends here (after)", "list_detailing", {"kind": "legends"}) or {}
print("here now:", json.dumps(mine)[:400], json.dumps(mine_l)[:300], flush=True)
here = [v.get("name") for v in mine.get("drafting_views", [])] + [v.get("name") for v in mine_l.get("legends", [])]
check("copied views read back here", all(n in here for n in names_d + names_l), json.dumps(here))
again = edit("views_copy_from again (skip_existing)", [{"op": "views_copy_from", "model": SOURCE, "views": names_d + names_l}], False)
res2 = result(again) if again else {}
check("second copy skips what exists", res2.get("copied") == 0 and len(res2.get("skippedExisting") or []) == len(names_d) + len(names_l), json.dumps(res2)[:400])
sc = edit("standards_copy line styles and fill patterns (real)", [{"op": "standards_copy", "model": SOURCE, "kinds": ["line_styles", "fill_patterns", "text_types"]}], False)
check("standards copied", sc is not None and sc.get("applied"), json.dumps(result(sc) if sc else {})[:600])
edit("views_copy_from unknown model (refused)", [{"op": "views_copy_from", "model": "No Such Model"}], False, expect_error=True)
after = bridge_client.call("get_project_info", connection=connection)
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
