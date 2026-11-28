r"""Revit 2027 in one session (one 'allow editing' answer): live_v030b, live_v030, then the 0.22.0 checks (live_p10 scratch)
in a scratch made by model_new, then Task 04 on Revit 2027 (live_p10 task04: dry runs only), Task 04 opened by the bridge
(upgraded in memory, never saved) and closed with nothing kept.

Each part writes its own results file; this runner writes tests\results\live_2027_chain.txt with a line per part.
Started by run_live_2027_chain.bat.
"""
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RESULTS = HERE / "results"
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

TASK04 = r"C:\Users\mohamed.hamed\Desktop\New folder\Revit Task 04_Mohamed A.Hamed_66088960.rvt"
connection = bridge_client.pin()


def call(method, params=None, timeout=900):
    return bridge_client.call(method, dict(params or {}), connection=connection, timeout=timeout)


def run(script, out_name, *args):
    out = RESULTS / out_name
    with open(out, "w", encoding="utf-8") as f:
        code = subprocess.call([sys.executable, str(HERE / script), *args], stdout=f, stderr=subprocess.STDOUT)
    tail = out.read_text(encoding="utf-8", errors="replace").strip().splitlines()[-1:] or [""]
    print(f"{script} {' '.join(args)} -> exit {code}: {tail[0][:300]}", flush=True)
    return code


ping = call("ping")
print("bridge", ping.get("bridgeVersion"), "Revit", ping.get("revitVersion"), flush=True)
if str(ping.get("revitVersion")) != "2027":
    raise SystemExit("Run this with Revit 2027 as the default Revit.")

results = {}
results["live_v030b"] = run("live_v030b.py", "live_v030b_2027.txt")
results["live_v030"] = run("live_v030.py", "live_v030_2027.txt")

# 0.22.0 checks in a fresh scratch
new = call("model_new", {"activate": True})
print("scratch for live_p10:", new.get("title"), flush=True)
results["live_p10 scratch"] = run("live_p10.py", "live_p10_scratch_2027.txt", "scratch")

# Task 04 on Revit 2027: dry runs only; opened by the bridge, never saved
t = call("model_open", {"path": TASK04, "activate": True})
print("Task 04 opened:", t.get("title"), "active", t.get("active"), "alreadyOpen", t.get("alreadyOpen"), flush=True)
if t.get("active") and not t.get("alreadyOpen"):
    results["live_p10 task04"] = run("live_p10.py", "live_p10_task04_2027.txt", "task04")
    unsaved = next((d.get("unsaved") for d in call("list_documents").get("documents", []) if d.get("documentId") == t.get("documentId")), None)
    print("Task 04 unsaved after the dry runs (the 2024 to 2027 upgrade counts as a change):", unsaved, flush=True)
    call("model_new", {"activate": True})   # Task 04 must not stay active to be closed
    cl = call("model_close", {"document_id": t.get("documentId"), "discard_changes": True})
    print("Task 04 closed, nothing saved:", cl, flush=True)
else:
    print("Task 04 was already open or did not become active; its dry runs were not started.", flush=True)
    results["live_p10 task04"] = None

print("\nSUMMARY")
for k, v in results.items():
    print(f"  {k}: {'passed' if v == 0 else 'not run' if v is None else 'see its results file'}")
print("ALL PARTS PASSED" if all(v == 0 for v in results.values()) else "SOME PARTS NEED A LOOK")
