r"""Probe a freshly built server exe before build_package.bat promotes it.

  python tools/probe_exe.py <path-to-revit-mcp-server.exe>

Starts tests/fake_bridge.py (no Revit needed), runs the exe over stdio as Claude does, and checks: the MCP handshake,
the server version equals server/catalog.py BRIDGE_VERSION, the tool list, a ping through the bridge, request
validation, and that the exe exits cleanly at end of input. Prints RESULT=OK or RESULT=FAILED (exit code 0 / 1).
"""
from __future__ import annotations

import asyncio
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "server"))
import catalog  # noqa: E402


async def probe(exe: Path, bridge_file: Path) -> list[str]:
    problems: list[str] = []
    env = {**os.environ, "REVIT_BRIDGE_FILE": str(bridge_file)}
    async with stdio_client(StdioServerParameters(command=str(exe), args=[], env=env)) as (read, write):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            print("server:", init.serverInfo.name, init.serverInfo.version, "protocol", init.protocolVersion)
            if init.serverInfo.version != catalog.BRIDGE_VERSION:
                problems.append(f"exe reports {init.serverInfo.version}, sources are {catalog.BRIDGE_VERSION}")
            tools = [t.name for t in (await session.list_tools()).tools]
            print("tools:", tools)
            if "revit" not in tools:
                problems.append("no 'revit' tool")
            ping = await session.call_tool("revit", {"action": "ping"})
            print("ping:", "ERROR" if ping.isError else "ok")
            if ping.isError:
                problems.append("ping through the fake bridge failed: " + ping.content[0].text[:200])
            bad = await session.call_tool("revit", {"action": "ping", "params": {}, "extra": 1})
            if not (bad.isError and "additionalProperties" in bad.content[0].text):
                problems.append("an unknown top-level argument was not refused")
            bad = await session.call_tool("revit", {"action": "edit", "params": {"dryRun": True, "operations": []}})
            if not (bad.isError and "Unknown edit parameters" in bad.content[0].text):
                problems.append("a misspelled edit parameter was not refused")
    return problems


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    exe = Path(sys.argv[1]).resolve()
    if not exe.is_file():
        print(f"RESULT=FAILED no exe at {exe}")
        return 1
    with tempfile.TemporaryDirectory() as tmp:
        bridge_file = Path(tmp) / "bridge.json"
        fake = subprocess.Popen([sys.executable, str(ROOT / "tests" / "fake_bridge.py"), str(bridge_file)],
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            for _ in range(100):
                if bridge_file.exists():
                    break
                time.sleep(0.1)
            problems = asyncio.run(probe(exe, bridge_file))
            # end of input: the exe must exit by itself with code 0 and no traceback
            run = subprocess.run([str(exe)], input=b"", capture_output=True, timeout=60,
                                 env={**os.environ, "REVIT_BRIDGE_FILE": str(bridge_file)})
            err = run.stderr.decode("utf-8", "replace")
            print("exit at end of input:", run.returncode)
            if run.returncode != 0 or "Traceback" in err:
                problems.append(f"exit at end of input: code {run.returncode}; stderr {err.strip()[-300:]}")
        except Exception as exc:   # noqa: BLE001 - any failure fails the probe
            problems = [f"{type(exc).__name__}: {exc}"]
        finally:
            fake.terminate()
            fake.wait(10)
    for p in problems:
        print("PROBLEM:", p)
    print("RESULT=OK" if not problems else "RESULT=FAILED")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
