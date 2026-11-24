"""Talks to the RevitMcpBridge add-in inside Revit over 127.0.0.1.

Connection details (port + per-session token) are read on every call, so restarting Revit never requires
reconfiguring anything. Every running Revit writes %LOCALAPPDATA%\\RevitMcpBridge\\sessions\\<pid>.json; bridge.json names
the default one (the first Revit started, or the one where MCP Status was pressed).

Which Revit a call goes to:
- the one chosen with select_revit, if any;
- otherwise, when exactly one Revit is listening, that one (whatever bridge.json says);
- otherwise the default one. Reads go there; edits are refused until select_revit picks one, so a second Revit
  being open can never silently receive an edit meant for the first.
"""
from __future__ import annotations

import json
import os
import socket
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any

READ_TIMEOUT = 120.0    # the add-in gives up waiting for a busy Revit after 30 s; a big take-off may then run for a while
EDIT_TIMEOUT = 630.0    # expensive edits may outlive this response deadline


class BridgeError(RuntimeError):
    def __init__(self, message: str, operation_id: str | None = None, pid: int | None = None):
        super().__init__(message)
        self.operation_id = operation_id
        self.pid = pid


@dataclass(frozen=True)
class Connection:
    port: int
    token: str
    pid: int
    protocol: int
    revit: str = ""
    # Other Revit sessions that were listening when this one was picked automatically (empty when chosen explicitly).
    others: tuple = ()


def bridge_file() -> Path:
    override = os.environ.get("REVIT_BRIDGE_FILE")  # used by the tests
    if override:
        return Path(override)
    base = os.environ.get("LOCALAPPDATA")
    if not base:
        raise BridgeError("LOCALAPPDATA is not set; this server is meant to run on the Windows machine with Revit.")
    return Path(base) / "RevitMcpBridge" / "bridge.json"


# The Revit session chosen with select_revit (its connection file); None = the most recently started / activated one.
_selected: Path | None = None


def sessions_dir() -> Path:
    return bridge_file().parent / "sessions"


def select(path: Path | None) -> None:
    global _selected
    _selected = path


def selected() -> Path | None:
    return _selected


NOT_REACHABLE = ("Revit is not reachable. Start Revit (2024 or 2027) with the RevitMcpBridge add-in and open a model "
                 "(Add-Ins tab > Claude MCP > MCP Status confirms it is loaded), then try again.")


def _read(path: Path) -> Connection:
    info = json.loads(path.read_text(encoding="utf-8"))
    return Connection(int(info["port"]), str(info["token"]), int(info["pid"]), int(info.get("protocol", 1)),
                      str(info.get("revit", "")))


def _listening(conn: Connection) -> bool:
    """A TCP connect, not a ping: the add-in accepts connections even while Revit is busy with a long command."""
    try:
        with socket.create_connection(("127.0.0.1", conn.port), timeout=0.5):
            return True
    except OSError:
        return False


def live_sessions() -> list[Connection]:
    """Every Revit whose session file names a port that is listening (files of crashed Revits are skipped)."""
    folder = sessions_dir()
    out = []
    for f in sorted(folder.glob("*.json")) if folder.exists() else []:
        try:
            conn = _read(f)
        except (OSError, ValueError, KeyError, TypeError):
            continue
        if _listening(conn):
            out.append(conn)
    return out


def _auto() -> Connection:
    default = None
    try:
        default = _read(bridge_file())
    except FileNotFoundError:
        pass
    except (OSError, ValueError, KeyError, TypeError) as exc:
        raise BridgeError(f"Cannot read the bridge file {bridge_file()}: {exc}") from None
    live = live_sessions()
    if not live:
        if default is not None:
            return default          # an add-in without session files, or a busy one: let the call report what happens
        raise BridgeError(NOT_REACHABLE)
    chosen = next((c for c in live if default is not None and c.pid == default.pid and c.port == default.port), None)
    if chosen is None:
        chosen = live[0] if len(live) == 1 else None
    if chosen is None:              # several open and the default is gone: the one with the newest file is as good a guess as any
        chosen = max(live, key=lambda c: _mtime(sessions_dir() / f"{c.pid}.json"))
    others = tuple({"pid": c.pid, "revit": c.revit} for c in live if c is not chosen)
    return Connection(chosen.port, chosen.token, chosen.pid, chosen.protocol, chosen.revit, others)


def _mtime(path: Path) -> float:
    try:
        return path.stat().st_mtime
    except OSError:
        return 0.0


def pin(path: Path | None = None) -> Connection:
    path = path or _selected
    if path is None:
        return _auto()
    try:
        return _read(path)
    except FileNotFoundError:
        if path == _selected:
            raise BridgeError("The Revit session chosen with select_revit has closed. Call list_revit_sessions and "
                              "select_revit again (pid 'auto' goes back to automatic choice).") from None
        raise BridgeError(NOT_REACHABLE) from None
    except (OSError, ValueError, KeyError, TypeError) as exc:
        raise BridgeError(f"Cannot read the bridge file {path}: {exc}") from None


def several_open(target: Connection) -> str:
    sessions = ", ".join(f"pid {o['pid']} (Revit {o['revit']})" for o in target.others)
    return (f"{len(target.others) + 1} Revit sessions are open: pid {target.pid} (Revit {target.revit}), {sessions}. "
            "Edits are refused until one is chosen: call list_revit_sessions to see their models, then select_revit "
            "with the pid of the model to edit.")


def call(method: str, params: dict[str, Any] | None = None, timeout: float = READ_TIMEOUT,
         connection: Path | Connection | None = None) -> Any:
    target = connection if isinstance(connection, Connection) else pin(connection)
    args = dict(params or {})
    edit = method == "edit"
    if edit:
        if target.others:
            raise BridgeError(several_open(target))
        if target.protocol != 4:
            raise BridgeError("This Revit is running an incompatible add-in. Install the matching bridge and restart Revit before editing.")
        if "document_id" not in args:
            document_id = call("ping", connection=target).get("documentId")
            if not document_id:
                raise BridgeError("Open the intended Revit model before editing.")
            args["document_id"] = document_id
        args.setdefault("operation_id", uuid.uuid4().hex)
    request = {"id": uuid.uuid4().hex, "token": target.token, "method": method, "params": args}
    data = (json.dumps(request, sort_keys=True, allow_nan=False) + "\n").encode("utf-8")
    sent = False

    def transport_error(detail: str) -> BridgeError:
        if edit and sent:
            return BridgeError(f"{detail} The edit outcome is unknown. Call get_operation_status with "
                               f"operation_id={args['operation_id']!r} and pid={target.pid}. "
                               f"Retry on this process with the same operation_id, document_id={args['document_id']!r} and request.",
                               args["operation_id"], target.pid)
        return BridgeError(detail)

    try:
        with socket.create_connection(("127.0.0.1", target.port), timeout=5) as sock:
            sock.settimeout(timeout)
            sent = True
            sock.sendall(data)
            with sock.makefile("rb") as reader:
                line = reader.readline()
    except socket.timeout:
        raise transport_error(f"Revit did not answer within {timeout:.0f} s. If this was an edit, check Revit for an open "
                              "permission dialog or a running command.") from None
    except OSError as exc:
        raise transport_error(f"Could not reach Revit on port {target.port} ({exc}). Revit may have been closed; if another "
                              "Revit is open, press Add-Ins > Claude MCP > MCP Status in it, or use select_revit.") from None

    if not line:
        raise transport_error("Revit closed the connection without a reply.")
    try:
        reply = json.loads(line.decode("utf-8"))
    except (UnicodeError, ValueError) as exc:
        raise transport_error(f"Revit sent an unreadable reply: {exc}") from None
    if not isinstance(reply, dict) or reply.get("id") != request["id"]:
        raise transport_error("Revit returned a reply for a different request.")
    if not reply.get("ok"):
        raise BridgeError(f"Revit reported: {reply.get('error', 'unknown error')}")
    result = reply.get("result")
    if method == "ping" and target.others and isinstance(result, dict):
        result = {**result, "otherRevitSessions": list(target.others), "note": several_open(target).replace(
            "Edits are refused", "This answer comes from the default one; edits are refused")}
    if edit and isinstance(result, dict):
        return {**result, "operationId": args["operation_id"], "documentId": args["document_id"], "pid": target.pid}
    return result
