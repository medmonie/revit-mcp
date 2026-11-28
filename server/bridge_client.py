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
import time
import uuid
from collections import deque
from dataclasses import dataclass
from pathlib import Path
from threading import Lock
from typing import Any
from request_lifetime import checkpoint, ended

BRIDGE_PROTOCOL = 4     # must match ["protocol"] in plugin/Bridge/BridgeServer.cs
MAX_REQUEST_CHARS = 4 * 1024 * 1024   # must match MaxRequestChars in plugin/Bridge/BridgeServer.cs
MAX_REPLY_BYTES = 128 * 1024 * 1024   # one reply line; larger replies are refused instead of exhausting memory
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
_selected: tuple[Path, Connection] | None = None


def sessions_dir() -> Path:
    return bridge_file().parent / "sessions"


def select(path: Path | None) -> None:
    global _selected
    checkpoint()
    _selected = (path, pin(path)) if path is not None else None


def selected() -> Path | None:
    selection = _selected
    return selection[0] if selection is not None else None


NOT_REACHABLE = ("Revit is not reachable. Start Revit (2024 or 2027) with the RevitMcpBridge add-in and open a model "
                 "(Add-Ins tab > Claude MCP > MCP Status confirms it is loaded), then try again.")


def _read(path: Path) -> Connection:
    info = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(info, dict):
        raise ValueError("expected a connection object")
    port, pid, token, protocol = info["port"], info["pid"], info["token"], info.get("protocol", 1)
    if (type(port) is not int or not 1 <= port <= 65535 or type(pid) is not int or not 1 <= pid <= 0xffffffff
            or not isinstance(token, str) or not token or type(protocol) is not int):
        raise ValueError("invalid connection fields")
    return Connection(port, token, pid, protocol, str(info.get("revit", "")))


def pid_alive(pid: int) -> bool:
    """False only when the process is known to be gone: a Revit that crashed leaves its session file behind, and
    probing its port costs every call half a second (or reaches whatever program was given that port since)."""
    try:
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
            kernel32.OpenProcess.restype = wintypes.HANDLE
            kernel32.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
            kernel32.GetExitCodeProcess.restype = wintypes.BOOL
            kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
            kernel32.CloseHandle.restype = wintypes.BOOL
            handle = kernel32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
            if not handle:
                return ctypes.get_last_error() != 87            # ERROR_INVALID_PARAMETER: no process has this id
            try:
                code = wintypes.DWORD()
                return not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)) or code.value == 259   # STILL_ACTIVE
            finally:
                kernel32.CloseHandle(handle)
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except Exception:  # noqa: BLE001 - when in doubt the session counts as alive and the port probe decides, as before
        return True


def listening(conn: Connection) -> bool:
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
        if f.stem == str(conn.pid) and pid_alive(conn.pid) and listening(conn):
            out.append(conn)
    return out


def _auto() -> Connection:
    default = None
    default_problem = None
    try:
        default = _read(bridge_file())
    except FileNotFoundError:
        pass
    except (OSError, ValueError, KeyError, TypeError) as exc:
        default_problem = str(exc)
    live = live_sessions()
    if not live:
        if default is not None:
            return default          # an add-in without session files, or a busy one: let the call report what happens
        if default_problem is not None:
            raise BridgeError(f"Cannot read the bridge file {bridge_file()}: {default_problem}")
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
    selection = _selected if path is None else None
    path = selection[0] if selection is not None else path
    if path is None:
        return _auto()
    try:
        conn = _read(path)
        if selection is not None and conn != selection[1]:
            raise BridgeError("The selected Revit session changed. Call select_revit again.")
        return conn
    except FileNotFoundError:
        if selection is not None:
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


# Methods that change the model or the central model: one Revit only, a document_id and an operation_id receipt.
WRITE_METHODS = {"edit", "sync_with_central", "reload_latest", "relinquish", "checkout"}

# Every write this server process dispatched, newest last. A client that timed out or cancelled never saw the reply,
# and with it the generated operation_id; get_operation_status without an id reads this list, so the outcome can be
# checked before anything is sent again.
_journal: deque[dict[str, Any]] = deque(maxlen=50)
_journal_lock = Lock()


def _journal_open(method: str, args: dict[str, Any], pid: int) -> dict[str, Any]:
    entry = {"operationId": args["operation_id"], "method": method, "pid": pid, "documentId": args["document_id"],
             "dryRun": args.get("dry_run") is True, "dispatchedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
             "state": "dispatched"}
    if method == "edit" and isinstance(args.get("operations"), list):
        entry["operations"] = len(args["operations"])
    with _journal_lock:
        _journal.append(entry)
    return entry


def _journal_close(entry: dict[str, Any] | None, state: str, result: Any = None) -> None:
    if entry is None:
        return
    with _journal_lock:
        entry["state"] = state
        entry["replyDelivered"] = not ended()   # false: the MCP request was cancelled or timed out before the reply
        if isinstance(result, dict):
            entry.update({k: result[k] for k in ("applied", "permission", "failedAt", "succeeded") if k in result})


def recent_operations() -> list[dict[str, Any]]:
    """Copies of the journal entries, newest first."""
    with _journal_lock:
        return [dict(entry) for entry in reversed(_journal)]


def call(method: str, params: dict[str, Any] | None = None, timeout: float = READ_TIMEOUT,
         connection: Path | Connection | None = None, report_unread: bool = False) -> Any:
    """report_unread: keep the add-in's list of request keys its handler never read, as result['unreadParams']."""
    checkpoint()
    target = connection if isinstance(connection, Connection) else pin(connection)
    args = dict(params or {})
    edit = method in WRITE_METHODS
    if edit:
        if target.others:
            raise BridgeError(several_open(target))
        if target.protocol != BRIDGE_PROTOCOL:
            raise BridgeError("This Revit is running an incompatible add-in. Install the matching bridge and restart Revit before editing.")
        if "document_id" not in args:
            document_id = call("ping", connection=target).get("documentId")
            if not document_id:
                raise BridgeError("Open the intended Revit model before editing.")
            args["document_id"] = document_id
        args.setdefault("operation_id", uuid.uuid4().hex)
    request = {"id": uuid.uuid4().hex, "token": target.token, "method": method, "params": args}
    text = json.dumps(request, sort_keys=True, allow_nan=False)
    if len(text) > MAX_REQUEST_CHARS:
        # The add-in stops reading at its limit and closes the connection, which reaches this side as a reset: refuse
        # here, before anything is sent, so an oversized edit is not reported as "outcome unknown".
        raise BridgeError(f"The request is {len(text) / (1024 * 1024):.1f} MB; the add-in accepts {MAX_REQUEST_CHARS // (1024 * 1024)} MB "
                          "per request. Nothing was sent. Split the batch.")
    data = (text + "\n").encode("utf-8")
    sent = False
    entry: dict[str, Any] | None = None

    def transport_error(detail: str) -> BridgeError:
        if edit and sent:
            _journal_close(entry, "outcome_unknown")
            return BridgeError(f"{detail} The edit outcome is unknown. Call get_operation_status with "
                               f"operation_id={args['operation_id']!r} and pid={target.pid}. "
                               f"Retry on this process with the same operation_id, document_id={args['document_id']!r} and request.",
                               args["operation_id"], target.pid)
        return BridgeError(detail)

    try:
        with socket.create_connection(("127.0.0.1", target.port), timeout=5) as sock:
            sock.settimeout(timeout)
            checkpoint()
            if edit:
                entry = _journal_open(method, args, target.pid)
            sent = True
            sock.sendall(data)
            deadline = time.monotonic() + timeout
            line = bytearray()
            while len(line) <= MAX_REPLY_BYTES:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise socket.timeout()
                sock.settimeout(remaining)
                chunk = sock.recv(min(65536, MAX_REPLY_BYTES + 1 - len(line)))
                if not chunk:
                    break
                line.extend(chunk)
                if b"\n" in chunk:
                    break
    except socket.timeout:
        raise transport_error(f"Revit did not answer within {timeout:.0f} s. If this was an edit, check Revit for an open "
                              "permission dialog or a running command.") from None
    except OSError as exc:
        raise transport_error(f"Could not reach Revit on port {target.port} ({exc}). Revit may have been closed; if another "
                              "Revit is open, press Add-Ins > Claude MCP > MCP Status in it, or use select_revit.") from None

    if not line:
        raise transport_error("Revit closed the connection without a reply.")
    if len(line) > MAX_REPLY_BYTES or not line.endswith(b"\n"):
        raise transport_error(f"Revit's reply exceeded {MAX_REPLY_BYTES // (1024 * 1024)} MB or was cut off; "
                              "narrow the request (limit, level, view or category).")
    try:
        reply = json.loads(line.decode("utf-8"))
    except (UnicodeError, ValueError, RecursionError) as exc:
        raise transport_error(f"Revit sent an unreadable reply: {exc}") from None
    if isinstance(reply, dict) and reply.get("id") is None and reply.get("ok") is False:
        # The add-in rejected the line before reading its id (unparseable or too large): nothing was run.
        _journal_close(entry, "rejected")
        raise BridgeError(f"Revit reported: {reply.get('error', 'unknown error')}")
    if not isinstance(reply, dict) or reply.get("id") != request["id"]:
        raise transport_error("Revit returned a reply for a different request.")
    if type(reply.get("ok")) is not bool or (reply["ok"] and "result" not in reply):
        raise transport_error("Revit sent an invalid response envelope.")
    if not reply["ok"]:
        _journal_close(entry, "rejected")
        raise BridgeError(f"Revit reported: {reply.get('error', 'unknown error')}")
    result = reply.get("result")
    if (edit or method == "ping") and not isinstance(result, dict):
        raise transport_error("Revit sent an invalid result object.")
    if edit and result.get("state") == "outcome_unknown":
        raise transport_error(str(result.get("error") or "Edit stopped after dispatch."))
    if method == "ping" and target.others and isinstance(result, dict):
        result = {**result, "otherRevitSessions": list(target.others), "note": several_open(target).replace(
            "Edits are refused", "This answer comes from the default one; edits are refused")}
    if report_unread and isinstance(result, dict) and isinstance(reply.get("unread"), dict):
        result = {**result, "unreadParams": reply["unread"]}
    if edit and isinstance(result, dict):
        _journal_close(entry, "answered", result)
        return {**result, "operationId": args["operation_id"], "documentId": args["document_id"], "pid": target.pid}
    return result
