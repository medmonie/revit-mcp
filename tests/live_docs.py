r"""Live test of 0.24.0 documents (phase 16): list_documents, model_files, model_open / model_close (background and
active), reads by document_id, batch_read, model_new. Never edits and never saves a user's model; the only file written
is the scratch project under %TEMP%\RevitMcpScratch. Workshared files are opened detached.
Started by run_live_docs.bat; output in tests\results\live_docs.txt.
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
ensure_permission = lambda: None
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





def call(method, params=None, **kwargs):
    """Here document_id is only sent when given: the test moves between models."""
    return bridge_client.call(method, dict(params or {}), connection=connection, **kwargs)


FOLDER = r"C:\Users\mohamed.hamed\Desktop\New folder"
TASK04 = FOLDER + r"\Revit Task 04_Mohamed A.Hamed_66088960.rvt"
TASK02 = FOLDER + r"\Revit Task 02_Mohamed A.Hamed_66088960.rvt"


def docs():
    return {d["title"]: d for d in (call("list_documents", {}) or {}).get("documents", [])}


def must_fail(title, method, params, contains):
    try:
        res = call(method, params, timeout=300)
        print(f"===== {title}\nUNEXPECTED SUCCESS: {json.dumps(res)[:400]}", flush=True)
        FAILED.append(title)
    except Exception as exc:
        ok = contains in str(exc)
        print(f"===== {title}\nEXPECTED ERROR: {exc}", flush=True)
        if not ok:
            FAILED.append(title + " (message)")


start = docs()
out("list_documents (start)", start)
active0 = next((t for t, d in start.items() if d["active"]), None)
user_docs = [t for t, d in start.items() if not d["openedByClaude"]]
mf = read("model_files in the test folder", "model_files", {"folder": FOLDER})
files = {f["file"]: f for f in (mf or {}).get("files", [])}
check("model_files lists Task 02 and Task 04 with their Revit version", files.get("Revit Task 02_Mohamed A.Hamed_66088960.rvt", {}).get("revitVersion") == "2024"
      and "Revit Task 04_Mohamed A.Hamed_66088960.rvt" in files, json.dumps(list(files)))
check("backups skipped", not any(".000" in f for f in files))

# --- background open of Task 04, reads by document_id
o = call("model_open", {"path": TASK04}, timeout=600)
out("model_open Task 04 (background)", o)
t4 = o.get("documentId")
check("Task 04 open in the background, not active", t4 and o.get("active") is False)
gi = call("get_project_info", {"document_id": t4})
check("get_project_info by document_id reads Task 04", gi.get("title", "").startswith("Revit Task 04"), gi.get("title"))
cc = call("count_by_category", {"document_id": t4})
out("count_by_category Task 04 (background)", cc)
check("active model unchanged", call("ping", {}).get("activeModel") == active0)
again = call("model_open", {"path": TASK04}, timeout=600)
check("second open reuses it", again.get("alreadyOpen") is True and again.get("documentId") == t4)
must_fail("a view-based read on a background model (refused)", "get_selection", {"document_id": t4}, "active model")
must_fail("edit on a background model (refused)", "edit", {"document_id": t4, "dry_run": True, "operations": [{"op": "view_3d_create", "name": "x"}]}, "does not match")
user_id = start[user_docs[0]]["documentId"] if user_docs else None
if user_id:
    must_fail("model_close of a model the user opened (refused)", "model_close", {"document_id": user_id}, "not opened by Claude")
cl = call("model_close", {"document_id": t4})
out("model_close Task 04", cl)
check("Task 04 closed", "Revit Task 04_Mohamed A.Hamed_66088960" not in docs())

# --- a workshared file: detached
ws = next((f for f in files.values() if f.get("workshared") and not f.get("savedInLaterVersion")), None)
if ws:
    w = call("model_open", {"path": ws["path"], "worksets": "none"}, timeout=900)
    out(f"model_open workshared {ws['file']} (background)", w)
    check("workshared file opened detached", w.get("detached") is True and w.get("workshared") is True, json.dumps(w))
    lw = call("list_worksets", {"document_id": w["documentId"]})
    check("worksets kept and readable", lw.get("workshared") is True and len(lw.get("worksets", [])) > 0, json.dumps(lw)[:300])
    call("model_close", {"document_id": w["documentId"]})
    check("workshared copy closed", ws["file"][:-4] not in " ".join(docs()))
else:
    skip("workshared open", "no workshared file in the folder")

# --- batch_read
br = call("batch_read", {"action": "count_by_category", "paths": [TASK04, TASK02]}, timeout=900)
out("batch_read count_by_category over Task 04 and Task 02", {k: v for k, v in br.items() if k != "results"})
rs = br.get("results", [])
check("both files read", br.get("read") == 2 and br.get("failed") == 0 and all(r.get("ok") for r in rs), json.dumps([{k: r.get(k) for k in ("file", "ok", "seconds", "error")} for r in rs]))
now = docs()
check("Task 04 closed again after batch_read; Task 02 (user's) still open", "Revit Task 04_Mohamed A.Hamed_66088960" not in now
      and ("Revit Task 02_Mohamed A.Hamed_66088960" in now) == ("Revit Task 02_Mohamed A.Hamed_66088960" in start), json.dumps(list(now)))
must_fail("batch_read of an edit (refused)", "batch_read", {"action": "edit", "paths": [TASK04]}, "cannot run in batch_read")
pg = call("batch_read", {"action": "model_health", "paths": [TASK04, TASK02], "max_files": 1}, timeout=900)
check("max_files stops early with resumeAfter", pg.get("read") == 1 and pg.get("complete") is False and pg.get("resumeAfter"), json.dumps({k: pg.get(k) for k in ("read", "complete", "resumeAfter")}))
pg2 = call("batch_read", {"action": "model_health", "paths": [TASK04, TASK02], "resume_after": pg.get("resumeAfter")}, timeout=900)
check("resume_after continues with the next file", pg2.get("read") == 1 and pg2.get("complete") is True, json.dumps({k: pg2.get(k) for k in ("read", "complete")}))

# --- new projects
nb = call("model_new", {"activate": False}, timeout=600)
out("model_new in memory", nb)
gi = call("get_project_info", {"document_id": nb["documentId"]})
check("new in-memory project readable", gi.get("path") in ("", None) and gi.get("title"), json.dumps({k: gi.get(k) for k in ("title", "path")}))
call("model_close", {"document_id": nb["documentId"]})
na = call("model_new", {}, timeout=600)
out("model_new active (scratch)", na)
check("scratch project is active and lives in RevitMcpScratch", call("ping", {}).get("activeModel") == na.get("title") and "RevitMcpScratch" in (na.get("scratchFile") or ""))
must_fail("model_close of the active scratch (refused)", "model_close", {"document_id": na["documentId"]}, "active model")
ba = call("model_open", {"path": TASK04, "activate": True}, timeout=600)
out("model_open Task 04 active", ba)
check("Task 04 now active", call("ping", {}).get("activeModel", "").startswith("Revit Task 04"))
call("model_close", {"document_id": na["documentId"]})
check("scratch closed", na.get("title") not in docs())
end = docs()
out("list_documents (end)", end)
check("user models untouched (still open, no new unsaved changes)", all(t in end and end[t]["unsaved"] == start[t]["unsaved"] for t in user_docs), json.dumps(list(end)))
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
