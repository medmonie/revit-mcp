"""Generate configuration snippets from this checkout's location."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    command = str(ROOT / "server" / ".venv" / "Scripts" / "python.exe")
    server = str(ROOT / "server" / "server.py")
    claude = {"mcpServers": {"revit": {"command": command, "args": [server]}}}
    (ROOT / "claude_config_snippet.json").write_text(json.dumps(claude, indent=2) + "\n", encoding="utf-8")
    codex = (
        "[mcp_servers.revit]\n"
        f"command = {json.dumps(command, ensure_ascii=False)}\n"
        f"args = [{json.dumps(server, ensure_ascii=False)}]\n"
        "\n[mcp_servers.revit.env]\n"
        'REVIT_MCP_SPLIT = "1"\n'
    )
    (ROOT / "codex_config_snippet.toml").write_text(codex, encoding="utf-8")


if __name__ == "__main__":
    main()
