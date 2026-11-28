r"""Both 0.30.0 live tests in one Revit session (one 'allow editing' answer): live_v030b, then live_v030.
Each writes tests\results\<name>_<revit>.txt; this runner writes tests\results\live_v030_pair.txt. Started by run_live_v030_pair.bat.
"""
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

revit = str(bridge_client.call("ping", connection=bridge_client.pin()).get("revitVersion"))
print("Revit", revit, flush=True)
codes = {}
for script in ("live_v030b.py", "live_v030.py"):
    out = HERE / "results" / f"{script[:-3]}_{revit}.txt"
    with open(out, "w", encoding="utf-8") as f:
        codes[script] = subprocess.call([sys.executable, str(HERE / script)], stdout=f, stderr=subprocess.STDOUT)
    last = (out.read_text(encoding="utf-8", errors="replace").strip().splitlines() or [""])[-1]
    print(f"{script} -> exit {codes[script]}: {last[:400]}", flush=True)
print("ALL PARTS PASSED" if all(c == 0 for c in codes.values()) else "SOME PARTS NEED A LOOK")
