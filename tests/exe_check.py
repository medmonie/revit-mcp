r"""Start the INSTALLED server exe over stdio (as Claude Desktop does), list its tools and ping Revit through it.

  python exe_check.py [path-to-exe]     default %LOCALAPPDATA%\RevitMcpBridge\revit-mcp-server.exe

Checks the frozen build (bundled MCP SDK, jsonschema data, request validation), which the smoke test does not.
Reads only. Started by run_exe_check.bat; output tests\results\exe_check.txt.
"""
import asyncio
import os
import sys
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

EXE = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(os.environ["LOCALAPPDATA"]) / "RevitMcpBridge" / "revit-mcp-server.exe"


async def main() -> int:
    print("exe:", EXE, EXE.stat().st_size, "bytes")
    env = {"REVIT_BRIDGE_FILE": os.environ["REVIT_BRIDGE_FILE"]} if "REVIT_BRIDGE_FILE" in os.environ else None
    async with stdio_client(StdioServerParameters(command=str(EXE), args=[], env=env)) as (read, write):
        async with ClientSession(read, write) as session:
            init = await session.initialize()
            print("server:", init.serverInfo.name, init.serverInfo.version, "protocol", init.protocolVersion)
            tools = (await session.list_tools()).tools
            print("tools:", [t.name for t in tools])
            ping = await session.call_tool("revit", {"action": "ping"})
            print("ping:", "ERROR" if ping.isError else "ok", ping.content[0].text[:400])
            bad = await session.call_tool("revit", {"action": "ping", "params": {}, "extra": 1})
            print("unknown argument refused:", bad.isError, bad.content[0].text[:160])
            envelope_ok = bad.isError and "additionalProperties" in bad.content[0].text
            bad = await session.call_tool("revit", {"action": "edit", "params": {"dryRun": True, "operations": []}})
            print("misspelled dryRun refused:", bad.isError, bad.content[0].text[:160])
            ok = (not ping.isError) and envelope_ok and bad.isError and "Unknown edit parameters" in bad.content[0].text
    print("RESULT=OK" if ok else "RESULT=FAILED")
    return 0 if ok else 1

sys.exit(asyncio.run(main()))
