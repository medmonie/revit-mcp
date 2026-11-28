r"""Live READ-ONLY test of the 0.21 readers on Revit Task 02 (a steel model with connections): steel_connections,
analytical_model, list_loads, list_spaces. Refuses unless the active model is 'Revit Task 02...'. Never edits.
Started by run_live_reads_task02.bat; output in tests\results\live_reads_task02.txt.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

FAILED = []
connection = bridge_client.pin()
info = bridge_client.call("get_project_info", connection=connection)
if not info.get("title", "").startswith("Revit Task 02"):
    raise SystemExit("The active model is not Task 02; nothing read.")
before = {k: info.get(k) for k in ("title", "hasUnsavedChanges", "counts")}
print("bridge", bridge_client.call("ping", connection=connection).get("bridgeVersion"), flush=True)


def read(title, method, params=None):
    try:
        res = bridge_client.call(method, params or {}, connection=connection, timeout=300)
    except Exception as exc:
        print(f"===== {title}\nFAILED: {exc}", flush=True)
        FAILED.append(title)
        return {}
    text = json.dumps(res, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < 6000 else text[:6000] + "\n... (truncated)"), flush=True)
    return res


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


sc = read("steel_connections", "steel_connections", {"limit": 20})
check("connections found", sc.get("count", 0) > 0, f"count {sc.get('count')}")
items = sc.get("items", [])
check("each connection has a type and at least one member", all(i.get("type") and i.get("members") for i in items), json.dumps(items[:3]))
if items:
    ids = sorted({m for i in items[:5] for m in i["members"]})
    ge = read("members of the first connections", "get_elements", {"ids": ids, "allow_missing": True})
    cats = {e.get("category") for e in ge.get("elements", [])}
    check("connected members are structural framing / columns", cats and cats <= {"Structural Framing", "Structural Columns", "Structural Foundations", "Floors", "Walls"}, str(cats))
am = read("analytical_model", "analytical_model", {"limit": 5})
check("analytical_model answers", "members" in am and "panels" in am)
ll = read("list_loads", "list_loads", {"limit": 10})
check("list_loads answers", "loadCases" in ll)
read("list_spaces rooms", "list_spaces", {"kind": "rooms", "limit": 5})
after = bridge_client.call("get_project_info", connection=connection)
after = {k: after.get(k) for k in ("title", "hasUnsavedChanges", "counts")}
check("Task 02 unchanged", after == before, json.dumps(after))
print("\nALL CHECKS AS EXPECTED" if not FAILED else "\nNOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
