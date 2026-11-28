r"""Read-only survey of a folder of models with the 0.24 document tools: model_files, then batch_read of
get_project_info, count_by_category, list_links, list_levels, mep_systems, list_spaces and model_health. Each file is
opened in the background (workshared files detached), read and closed without saving. Never edits.

  python survey_folder.py "<folder>"     output: tests\results\survey_folder.txt (via run_survey_folder.bat)
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

FOLDER = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\mohamed.hamed\Desktop\New folder\detached\detached"
connection = bridge_client.pin()
ping = bridge_client.call("ping", connection=connection)
print("bridge", ping.get("bridgeVersion"), "Revit", ping.get("revitVersion"), "active", ping.get("activeModel"), flush=True)


def show(title, value, limit=12000):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < limit else text[:limit] + "\n... (truncated)"), flush=True)


files = bridge_client.call("model_files", {"folder": FOLDER, "recursive": True}, connection=connection, timeout=300)
show("model_files", files)
for action, params in [("get_project_info", {}), ("list_links", {}), ("count_by_category", {}), ("list_levels", {}),
                       ("mep_systems", {}), ("list_spaces", {"kind": "rooms", "limit": 5}), ("list_spaces", {"kind": "spaces", "limit": 5}),
                       ("model_health", {})]:
    try:
        res = bridge_client.call("batch_read", {"action": action, "params": params, "folder": FOLDER, "recursive": True, "max_files": 50},
                                 connection=connection, timeout=1800)
        show(f"batch_read {action} {json.dumps(params)}", res)
    except Exception as exc:
        print(f"===== batch_read {action}\nFAILED: {exc}", flush=True)
after = bridge_client.call("list_documents", {}, connection=connection)
show("list_documents (after)", after)
print("\nDONE (reads only)", flush=True)
