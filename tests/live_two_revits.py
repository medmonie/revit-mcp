r"""Live check of several Revit sessions, straight to the add-ins (no MCP server needed).

  python live_two_revits.py <label>

Prints which Revit is the default (bridge.json), every session file and whether its Revit runs and answers, what an
automatic call reaches, and whether an edit is refused while several are open. The edit is a dry run of 'purge'
and is refused by the client before anything is sent when several Revits are open; with one open it is not sent.
Appends to tests\results\two_revits.txt. Started by run_two_revits_check.bat.
"""
import json
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402
from bridge_client import BridgeError, call  # noqa: E402

label = sys.argv[1] if len(sys.argv) > 1 else "check"
print(f"########## {label}  {time.strftime('%Y-%m-%d %H:%M:%S')}")
running = subprocess.run(["tasklist", "/fi", "imagename eq Revit.exe", "/fo", "csv", "/nh"],
                         capture_output=True, text=True).stdout
revit_pids = sorted(int(line.split('","')[1]) for line in running.splitlines() if line.startswith('"Revit.exe"'))
print("Revit.exe processes:", revit_pids)
try:
    default = json.loads(bridge_client.bridge_file().read_text(encoding="utf-8"))
    print("bridge.json (default): pid", default.get("pid"), "revit", default.get("revit"),
          "running" if default.get("pid") in revit_pids else "NOT RUNNING")
except FileNotFoundError:
    print("bridge.json (default): none")
for f in sorted(bridge_client.sessions_dir().glob("*.json")):
    info = json.loads(f.read_text(encoding="utf-8"))
    try:
        ping = call("ping", connection=f, timeout=10)
        state = f"answers: model '{ping.get('activeModel')}', bridge {ping.get('bridgeVersion')}"
    except BridgeError as exc:
        state = f"does not answer ({str(exc)[:80]})"
    print(f"session {f.name}: revit {info.get('revit')}, {'running' if info.get('pid') in revit_pids else 'NOT RUNNING'}, {state}")
try:
    target = bridge_client.pin()
    print(f"automatic call goes to pid {target.pid} (Revit {target.revit}); other open sessions: {list(target.others)}")
    ping = call("ping")
    print("automatic ping: model", repr(ping.get("activeModel")), "| otherRevitSessions", ping.get("otherRevitSessions"))
    if target.others:
        try:
            call("edit", {"dry_run": True, "operations": [{"op": "purge"}]})
            print("EDIT WAS NOT REFUSED (unexpected)")
        except BridgeError as exc:
            print("edit refused as expected:", str(exc)[:200])
except BridgeError as exc:
    print("automatic call:", exc)
print()
