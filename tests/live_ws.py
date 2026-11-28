r"""Live test of 0.23.0 worksharing (phase 12) on Mohamed's cloud TEST model KTR15-G26-Z2-X-S-RC-RFT-ZZ-20100-00 (he
confirmed it is a throwaway model only he uses, and that syncs there are fine). Refuses on any other model.

  worksharing_status; checkout of a workset and of elements; relinquish borrowed; an edit (Comments on one column) then
  sync_with_central (confirm) and check the column is current with central and no longer held; reload_latest; the
  Comments value put back and synced again; refusals (sync without confirm, unknown workset, not workshared).

Started by run_live_ws.bat; output in tests\results\live_ws.txt.
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





TEST_MODEL = "KTR15-G26-Z2-X-S-RC-RFT-ZZ-20100-00"


CENTRAL = ("sync_with_central", "relinquish")   # since 0.26.5 Revit asks for these every time


def ws(title, method, params=None, expect_error=False, contains=None):
    try:
        res = call(method, params or {}, timeout=900)
        if method in CENTRAL and not expect_error and isinstance(res, dict) and res.get("permission") == "not granted":
            print(f"{method}: click Yes in Revit's dialog 'Claude - change the central model?' "
                  "(waiting up to 3 minutes) ...", flush=True)
            for _ in range(60):
                time.sleep(3)
                res = call(method, params or {}, timeout=900)   # a new request each time; the approval is used once
                if not (isinstance(res, dict) and res.get("permission") == "not granted"):
                    break
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error or (contains and contains not in str(exc)):
            FAILED.append(title)
        return None
    out(title, res)
    if expect_error or res.get("permission") == "not granted":
        FAILED.append(title + (" (expected an error)" if expect_error else " (permission)"))
    return res


if info.get("title") != TEST_MODEL:
    raise SystemExit(f"The active model is '{info.get('title')}', not the test model {TEST_MODEL}; nothing done.")
ensure_permission()
st = read("worksharing_status", "worksharing_status", {})
check("cloud workshared model", st and st.get("workshared") and st.get("cloud"), json.dumps({k: st.get(k) for k in ("central", "user", "cloud")}))
cols = (read("columns", "get_elements", {"category": "columns", "limit": 3, "params": ["Comments"]}, show=False) or {}).get("elements", [])
col = cols[0]["id"] if cols else None
old_comment = (cols[0].get("params") or {}).get("Comments") if cols else None
print("test column", col, "Comments was", repr(old_comment), flush=True)

ws("sync without confirm (refused)", "sync_with_central", {"comment": "x"}, expect_error=True, contains="confirm: true")
ws("checkout unknown workset (refused)", "checkout", {"worksets": ["No Such Workset"]}, expect_error=True, contains="No workset")
co = ws("checkout workset Workset1 and the column", "checkout", {"worksets": ["Workset1"], "ids": [col]})
check("Workset1 checked out", "Workset1" in ((co or {}).get("worksetsCheckedOut") or []), json.dumps(co))
st = read("status after checkout", "worksharing_status", {"ids": [col]})
check("column held by me", (st.get("elements") or [{}])[0].get("checkout") == "OwnedByCurrentUser", json.dumps(st.get("elements")))
rl = ws("relinquish all (confirm)", "relinquish", {"what": "all", "confirm": True})
st = read("status after relinquish", "worksharing_status", {"ids": [col]})
check("nothing held after relinquish", st.get("elementsHeld") == 0 and not st.get("worksetsHeld"), json.dumps({k: st.get(k) for k in ("elementsHeld", "worksetsHeld")}))

ed = call("edit", {"operations": [{"op": "set_parameters", "ids": [col], "values": {"Comments": "MCP sync test"}}]}, timeout=300)
out("edit Comments (real)", ed)
st = read("status after edit", "worksharing_status", {"ids": [col]})
row = (st.get("elements") or [{}])[0]
check("edited column is held by me", row.get("checkout") == "OwnedByCurrentUser", json.dumps(row))
sy = ws("sync_with_central, relinquish all (confirm)", "sync_with_central", {"confirm": True, "comment": "MCP 0.23.0 sync test", "relinquish": "all"})
st = read("status after sync", "worksharing_status", {"ids": [col]})
row = (st.get("elements") or [{}])[0]
check("after sync: column current with central, not held, nothing unsaved", row.get("updates") == "CurrentWithCentral" and row.get("checkout") == "NotOwned"
      and st.get("elementsHeld") == 0 and st.get("hasUnsavedChanges") is False, json.dumps({**row, "held": st.get("elementsHeld"), "unsaved": st.get("hasUnsavedChanges")}))
ge = read("column after sync", "get_elements", {"ids": [col], "params": ["Comments"]}, show=False)
check("Comments kept after sync", ((ge.get("elements") or [{}])[0].get("params") or {}).get("Comments") == "MCP sync test")
ws("reload_latest", "reload_latest", {})
# put the value back and sync again, borrowed elements only
call("edit", {"operations": [{"op": "set_parameters", "ids": [col], "values": {"Comments": old_comment or ""}}]}, timeout=300)
sy2 = ws("sync again, value restored, relinquish borrowed (confirm)", "sync_with_central", {"confirm": True, "comment": "MCP test: Comments restored", "relinquish": "borrowed"})
st = read("final status", "worksharing_status", {"ids": [col]})
row = (st.get("elements") or [{}])[0]
check("final: current with central, not held", row.get("updates") == "CurrentWithCentral" and row.get("checkout") == "NotOwned", json.dumps(row))
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
