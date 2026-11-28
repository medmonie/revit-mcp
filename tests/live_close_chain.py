r"""Closing phases 5, 6, 8, 9 and 13 on 0.30.1, one Revit session (one 'allow editing' answer), either year:
live_v030d (shared parameters real add / remove, the wall opening family), live_p6 and live_p89 in fresh scratches
(real edits), then Task 04 opened by the bridge for live_p6 / live_p89 task04 (reads and dry runs only), closed with
nothing kept. Output tests\results\live_close_chain_<revit>.txt plus a file per part.
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


ping = call("ping")
Y = str(ping.get("revitVersion"))
print("bridge", ping.get("bridgeVersion"), "Revit", Y, flush=True)


def run(script, out_name, *args):
    out = RESULTS / out_name
    with open(out, "w", encoding="utf-8") as f:
        code = subprocess.call([sys.executable, str(HERE / script), *args], stdout=f, stderr=subprocess.STDOUT)
    tail = out.read_text(encoding="utf-8", errors="replace").strip().splitlines()[-1:] or [""]
    print(f"{script} {' '.join(args)} -> exit {code}: {tail[0][:300]}", flush=True)
    return code


results = {}
results["live_v030d"] = run("live_v030d.py", f"live_v030d_{Y}.txt")
for s in ("live_p6", "live_p89"):
    new = call("model_new", {"activate": True})
    print(f"scratch for {s}:", new.get("title"), flush=True)
    results[f"{s} scratch"] = run(f"{s}.py", f"{s}_scratch_{Y}.txt", "scratch")
    call("model_new", {"activate": True})
    print("  scratch closed:", call("model_close", {"document_id": new["documentId"], "discard_changes": True}).get("discardedChanges"), flush=True)

t = call("model_open", {"path": TASK04, "activate": True})
print("Task 04 opened:", t.get("title"), "active", t.get("active"), "alreadyOpen", t.get("alreadyOpen"), flush=True)
if t.get("active") and not t.get("alreadyOpen"):
    for s in ("live_p6", "live_p89"):
        results[f"{s} task04"] = run(f"{s}.py", f"{s}_task04_{Y}.txt", "task04")
    call("model_new", {"activate": True})
    cl = call("model_close", {"document_id": t.get("documentId"), "discard_changes": True})
    print("Task 04 closed, nothing saved:", cl, flush=True)
else:
    print("Task 04 was already open or did not become active; its dry runs were not started.", flush=True)
    results["task04"] = None

print("\nSUMMARY")
for k, v in results.items():
    print(f"  {k}: {'passed' if v == 0 else 'not run' if v is None else 'see its results file'}")
print("ALL PARTS PASSED" if all(v == 0 for v in results.values()) else "SOME PARTS NEED A LOOK")
