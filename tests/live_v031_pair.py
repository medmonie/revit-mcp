r"""Both 0.31.0 live tests in one Revit session (one 'allow editing' answer): live_v031b (phase 17 P2 / P3), then
live_v031 (P1). Output tests\results\live_v031_pair.txt, each part its own results file.
"""
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RESULTS = HERE / "results"
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

Y = str(bridge_client.call("ping", connection=bridge_client.pin()).get("revitVersion"))
results = {}
for script in ("live_v031b", "live_v031"):
    out = RESULTS / f"{script}_{Y}.txt"
    with open(out, "w", encoding="utf-8") as f:
        code = subprocess.call([sys.executable, str(HERE / f"{script}.py")], stdout=f, stderr=subprocess.STDOUT)
    tail = out.read_text(encoding="utf-8", errors="replace").strip().splitlines()[-1:] or [""]
    print(f"{script} on {Y} -> exit {code}: {tail[0][:300]}", flush=True)
    results[script] = code
print("ALL PARTS PASSED" if all(v == 0 for v in results.values()) else "SOME PARTS NEED A LOOK")
