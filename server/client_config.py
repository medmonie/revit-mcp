"""Add or remove the "revit" server in the MCP clients on this PC (run by install.bat / uninstall.bat).

    revit-mcp-server.exe --configure-clients <path of the installed exe>
    revit-mcp-server.exe --remove-clients

Done in Python (inside the exe) rather than PowerShell: company Group Policy often blocks PowerShell scripts, which
used to leave Claude Desktop without the entry and no message. Every file is backed up, merged (other servers and
settings kept), written, then read back to confirm. Exit code 0 when every client found was handled.

Claude Desktop: %APPDATA%\\Claude\\claude_desktop_config.json, for the Microsoft Store (MSIX) app also
%LOCALAPPDATA%\\Packages\\Claude_*\\LocalCache\\Roaming\\Claude\\claude_desktop_config.json when that folder exists, and
for Claude Desktop 6.x %LOCALAPPDATA%\\Claude-Data\\claude_*\\claude_desktop_config.json (the file 6.x reads; it holds other
settings too, which are kept). The file is created when Claude Desktop is installed but has never been started.
Codex: %CODEX_HOME%\\config.toml, else ~/.codex/config.toml; the result is checked as TOML before it is kept. The ChatGPT
desktop app reads the same file (OpenAI: "The ChatGPT desktop app, Codex CLI, and IDE extension share this
configuration"), so this one entry serves both. It sets tool_timeout_sec: Codex / ChatGPT stop a tool call after 60 s by
default, shorter than a long edit or batch read.
"""
from __future__ import annotations

import json
import os
import time
import uuid
from pathlib import Path

NAME = "revit"


def _env(name: str) -> Path | None:
    value = os.environ.get(name)
    return Path(value) if value else None


def claude_files() -> list[Path]:
    appdata, local = _env("APPDATA"), _env("LOCALAPPDATA")
    files: list[Path] = []
    packages = sorted((local / "Packages").glob("Claude_*")) if local and (local / "Packages").is_dir() else []
    installed = bool(packages) or bool(local and (local / "AnthropicClaude").is_dir())
    if appdata and ((appdata / "Claude").is_dir() or installed):
        files.append(appdata / "Claude" / "claude_desktop_config.json")
    for package in packages:
        store = package / "LocalCache" / "Roaming" / "Claude"
        if store.is_dir():
            files.append(store / "claude_desktop_config.json")
    # Claude Desktop 6.x reads its MCP servers from Claude-Data\claude_<profile>\claude_desktop_config.json.
    data = local / "Claude-Data" if local else None
    if data and data.is_dir():
        for profile in sorted(data.glob("claude_*")):
            if profile.is_dir():
                files.append(profile / "claude_desktop_config.json")
    return files


def _backup(path: Path) -> None:
    if path.exists():
        # unique: two installs in the same second must not overwrite each other's backup
        backup = path.with_name(path.name + ".bak-" + time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6])
        backup.write_bytes(path.read_bytes())


def _claude(path: Path, exe: str | None) -> str:
    text = path.read_text(encoding="utf-8-sig") if path.exists() else ""
    try:
        config = json.loads(text) if text.strip() else {}
    except ValueError as exc:
        raise RuntimeError(f"not valid JSON ({exc}); fix it or add the entry by hand") from None
    if not isinstance(config, dict):
        raise RuntimeError("the file is not a JSON object; add the entry by hand")
    servers = config.get("mcpServers")
    if servers is None:
        servers = {}
    if not isinstance(servers, dict):
        raise RuntimeError('"mcpServers" is not an object; add the entry by hand')
    if exe is None:
        if NAME not in servers:
            return "no revit entry"
        _backup(path)
        del servers[NAME]
    else:
        previous = servers.get(NAME)
        if previous is not None and not isinstance(previous, dict):
            raise RuntimeError('the existing "revit" entry is not an object; fix it or add the entry by hand')
        # keep what the user added to the entry (env ...); the exe takes no arguments, so old python + server.py args go
        updated = {**(previous or {}), "command": exe}
        updated.pop("args", None)
        if previous == updated:
            return "already configured"
        _backup(path)
        servers[NAME] = updated
    config["mcpServers"] = servers
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(config, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(temporary, path)
    check = json.loads(path.read_text(encoding="utf-8")).get("mcpServers", {})
    if (exe is None) == (NAME in check) or (exe is not None and check[NAME].get("command") != exe):
        raise RuntimeError("written but not found when read back")
    return "removed" if exe is None else "configured"


CODEX_STARTUP_TIMEOUT = 30     # s; the one-file exe unpacks itself before it answers (Codex default 10)
CODEX_TOOL_TIMEOUT = 3600      # s; edits may run 10 min and batch_read up to 3000 s (Codex / ChatGPT default 60)


def codex_entry(exe: str) -> list[str]:
    """The [mcp_servers.revit] table for Codex and the ChatGPT desktop app (they share ~/.codex/config.toml)."""
    return ["[mcp_servers.revit]", "command = " + json.dumps(exe),
            f"startup_timeout_sec = {CODEX_STARTUP_TIMEOUT}", f"tool_timeout_sec = {CODEX_TOOL_TIMEOUT}",
            "", "[mcp_servers.revit.env]", 'REVIT_MCP_SPLIT = "1"']


def _codex_table(line: str) -> str | None:
    """The table a TOML header line names, without quotes or spaces; None for any other line."""
    t = line.strip()
    if not t.startswith("[") or "]" not in t:
        return None
    return t[1:t.index("]")].replace('"', "").replace("'", "").replace(" ", "")


def _codex(path: Path, exe: str | None) -> str:
    original = path.read_text(encoding="utf-8-sig")
    kept, in_revit, had = [], False, False
    for line in original.splitlines():
        table = _codex_table(line)
        if table is not None:
            # also [mcp_servers."revit"]: a second table for the same server would make the file invalid TOML
            in_revit = table == "mcp_servers.revit" or table.startswith("mcp_servers.revit.")
            had = had or in_revit
        if not in_revit:
            kept.append(line)
    if exe is None and not had:
        return "no revit entry"
    while kept and not kept[-1].strip():
        kept.pop()
    if exe is not None:
        kept += [""] + codex_entry(exe)
    text = "\n".join(kept) + "\n"
    if exe is not None and text.strip() == original.replace("\r\n", "\n").strip():
        return "already configured"
    try:  # never leave Codex with a file it cannot read
        import tomllib
        parsed = tomllib.loads(text)
    except ModuleNotFoundError:
        parsed = None
    except ValueError as exc:
        raise RuntimeError(f"the result would not be valid TOML ({exc}); the file was left as it was") from None
    if parsed is not None and exe is not None and parsed.get("mcp_servers", {}).get(NAME, {}).get("command") != exe:
        raise RuntimeError("the revit entry was not found in the result; the file was left as it was")
    _backup(path)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(text, encoding="utf-8")
    os.replace(temporary, path)
    if path.read_text(encoding="utf-8") != text:
        raise RuntimeError("written but different when read back")
    return "removed" if exe is None else "configured"


def run(exe: str | None) -> int:
    """exe: the installed server path to add, or None to remove. Prints one line per client."""
    failed = 0
    files = claude_files()
    if not files:
        print("Claude Desktop: not installed for this user (no Claude folder or app found)")
    for path in files:
        try:
            print(f"Claude Desktop: {_claude(path, exe)}: {path}")
        except Exception as exc:  # noqa: BLE001 - report every client, then fail at the end
            failed += 1
            print(f"Claude Desktop: FAILED: {path}: {exc}")
    home = _env("USERPROFILE") or Path.home()
    codex = (_env("CODEX_HOME") or home / ".codex") / "config.toml"
    if codex.exists():
        try:
            print(f"Codex: {_codex(codex, exe)}: {codex}")
        except Exception as exc:  # noqa: BLE001
            failed += 1
            print(f"Codex: FAILED: {codex}: {exc}")
    if failed:
        print("Add the entry by hand: Claude Desktop > Settings > Developer > Edit Config, inside \"mcpServers\": "
              + json.dumps({NAME: {"command": exe or "<path of revit-mcp-server.exe>"}}))
    return 1 if failed else 0
