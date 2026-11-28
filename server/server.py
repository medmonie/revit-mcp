"""Revit MCP server (stdio). Claude launches this process; it forwards every call to the RevitMcpBridge add-in
running inside Revit (see bridge_client.py).

ONE tool by default - `revit(action, params)` - so the Claude app has a single thing to allow, once. Reading is
always available; editing is gated inside Revit by a one-time permission per Revit session (dialog or the
Allow Editing button).

Set REVIT_MCP_SPLIT=1 to expose two tools instead (`revit_read`, `revit_edit`) if you prefer to allow reading
without allowing edits in the Claude app.
"""
from __future__ import annotations

import json
import os
import time
from collections import deque
from functools import partial
from threading import Event
from typing import Any

import anyio
from jsonschema import Draft202012Validator
from mcp.server.fastmcp import Context, FastMCP
from mcp.shared.exceptions import McpError
from mcp.types import CallToolRequest, ErrorData, INVALID_PARAMS, ToolAnnotations

import bridge_client
import catalog
import request_lifetime
from bridge_client import EDIT_TIMEOUT, READ_TIMEOUT, BridgeError, call

class RevitServer(FastMCP):
    """Validate the advertised tool envelope before the SDK coerces arguments."""

    def _setup_handlers(self) -> None:
        super()._setup_handlers()
        self._calls = deque()
        # SDK 1.30's tool adapter converts all handler exceptions into execution errors.
        # Unknown tool names must be rejected at the JSON-RPC boundary, outside that adapter.
        handlers = self._mcp_server.request_handlers
        sdk_call = handlers[CallToolRequest]

        async def checked_call(request):
            if not hasattr(self, "_input_validators"):
                await self.list_tools()
            if request.params.name not in self._input_validators:
                raise McpError(ErrorData(code=INVALID_PARAMS, message="Unknown tool name. Call tools/list."))
            return await sdk_call(request)

        handlers[CallToolRequest] = checked_call

    async def list_tools(self):
        definitions = await super().list_tools()
        self._input_validators = {}
        for tool in definitions:
            tool.inputSchema = {**tool.inputSchema, "additionalProperties": False}
            self._input_validators[tool.name] = Draft202012Validator(tool.inputSchema)
        return definitions

    async def call_tool(self, name, arguments):
        now = time.monotonic()
        while self._calls and self._calls[0] <= now - 60:
            self._calls.popleft()
        if len(self._calls) >= 240:
            raise ValueError("Tool call limit reached (240 per minute per server process). Retry after the window resets.")
        self._calls.append(now)
        if not hasattr(self, "_input_validators"):
            await self.list_tools()
        validator = self._input_validators.get(name)
        if validator is not None:
            error = next(validator.iter_errors(arguments), None)
            if error is not None:
                raise ValueError(f"Invalid tool arguments at {error.json_path}: {error.validator} constraint failed.")
        return await super().call_tool(name, arguments)


mcp = RevitServer("revit")
mcp._mcp_server.version = catalog.BRIDGE_VERSION   # serverInfo.version: this server's release, not the SDK's


PROGRESS_SECONDS = 10.0


async def _heartbeat(ctx: Context) -> None:
    """Progress notifications while a bridge call runs. They are only sent when the client asked for them with a
    progressToken; clients that reset their request timeout on progress then keep a long edit or take-off alive."""
    started = time.monotonic()
    while True:
        await anyio.sleep(PROGRESS_SECONDS)
        try:
            await ctx.report_progress(round(time.monotonic() - started), None, "Waiting for Revit")
        except Exception:  # noqa: BLE001 - a client that went away must not fail the call that is still running
            return


def _help(params: dict[str, Any]) -> dict[str, Any]:
    data = catalog.help_data(params.get("topic"))
    if params.get("topic"):
        return data
    # Tell Claude when the installed add-in and this catalogue disagree (e.g. after updating only one of them).
    try:
        live = call("list_actions")
        if not isinstance(live, dict) or not isinstance(live.get("actions"), list):
            raise BridgeError("the add-in answered list_actions without an action list (older or mismatched add-in)")
        plugin_actions = {a["name"] for a in live["actions"] if isinstance(a, dict) and "name" in a}
        plugin_ops = set(live.get("editOperations", []))
        data["plugin"] = {
            "bridgeVersion": live.get("bridgeVersion"),
            "revitVersion": live.get("revitVersion"),
            "editPermission": live.get("editPermission"),
            "missingInPlugin": sorted((set(catalog.READ_ACTIONS) | {"edit"}) - plugin_actions)
                               + sorted(f"edit:{o}" for o in set(catalog.EDIT_OPS) - plugin_ops),
            "undocumentedInCatalogue": sorted(plugin_actions - set(catalog.READ_ACTIONS) - {"edit"})
                                       + sorted(f"edit:{o}" for o in plugin_ops - set(catalog.EDIT_OPS)),
        }
    except BridgeError as exc:
        data["plugin"] = {"unreachable": str(exc)}
    return data


def _sessions() -> list[dict[str, Any]]:
    """Every Revit with the add-in loaded, checked with a quick ping."""
    from bridge_client import sessions_dir
    out = []
    folder = sessions_dir()
    try:
        default_pid = int(json.loads(bridge_client.bridge_file().read_text(encoding="utf-8"))["pid"])
    except (OSError, ValueError, KeyError, TypeError):
        default_pid = None
    for f in sorted(folder.glob("*.json")) if folder.exists() else []:
        try:
            info = json.loads(f.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if not isinstance(info, dict) or type(info.get("pid")) is not int or f.stem != str(info["pid"]):
            continue
        if not bridge_client.pid_alive(info["pid"]):
            continue    # left behind by a Revit that crashed
        entry = {"pid": info.get("pid"), "revit": info.get("revit"), "startedUtc": info.get("startedUtc"),
                 "selected": bridge_client.selected() == f, "default": info.get("pid") == default_pid}
        try:
            ping = call("ping", connection=f, timeout=5)
            entry.update(reachable=True, activeModel=ping.get("activeModel"), editingAllowed=ping.get("editingAllowed"))
        except BridgeError as exc:
            # ping runs on Revit's main thread: a Revit busy with a command or dialog still listens but cannot answer
            # within 5 s. Report it as busy rather than unreachable, so select_revit can still pick it.
            listening = False
            try:
                listening = bridge_client.listening(bridge_client.pin(f))
            except BridgeError:
                pass
            entry.update(reachable=listening, busy=listening or None, problem=str(exc)[:160])
        out.append(entry)
    return out


def _pid(value: Any) -> int:
    digits = str(value)
    if not digits.isascii() or not digits.isdecimal() or len(digits) > 10 or not 1 <= int(digits) <= 0xffffffff:
        raise ValueError("pid must be a positive Windows process ID.")
    return int(digits)


def _select(params: dict[str, Any]) -> dict[str, Any]:
    from bridge_client import sessions_dir
    pid, revit = params.get("pid"), params.get("revit")
    if pid is not None and revit is not None:
        raise ValueError("Select by pid or Revit year, one at a time.")
    if str(pid).lower() == "auto" or (pid is None and revit is None):
        bridge_client.select(None)
        return {"selected": "auto", "note": "Calls go to the only open Revit; with several open, reads go to the default one "
                                            "(first started, or where MCP Status was pressed) and edits need select_revit."}
    wanted = _pid(pid) if pid is not None else None
    live = [s for s in _sessions() if s.get("reachable")]
    if pid is not None:
        match = [s for s in live if s.get("pid") is not None and int(s["pid"]) == wanted]
    else:
        match = [s for s in live if str(s.get("revit")) == str(revit).strip()]
    if len(match) != 1:
        raise ValueError(f"{'No' if not match else 'More than one'} reachable Revit matches. Sessions: {live}. "
                         "Give 'pid' from list_revit_sessions.")
    bridge_client.select(sessions_dir() / f"{match[0]['pid']}.json")
    return {"selected": {**match[0], "selected": True}, "note": "All following calls go to this Revit until select_revit is called again."}


def _excel(name: str):
    """excel_io functions are imported on first use, so the server starts even before openpyxl is installed."""
    def run(params: dict[str, Any]) -> Any:
        import excel_io
        fn = getattr(excel_io, name)
        if name == "excel_template":
            return fn(params)
        pinned, target = _workflow(params)
        return fn(pinned, params, target)
    return run


def _bbs_report(params: dict[str, Any]) -> Any:
    import bbs_report
    pinned, _ = _workflow(params)     # same connection and model check as qs_report
    return bbs_report.build(pinned, params)


def _qs_report(params: dict[str, Any]) -> Any:
    import qs_report
    pinned, _ = _workflow(params)
    return qs_report.build(pinned, params)


def _mep_report(params: dict[str, Any]) -> Any:
    import mep_report
    pinned, _ = _workflow(params)
    return mep_report.build(pinned, params)


def _room_report(params: dict[str, Any]) -> Any:
    import room_report
    pinned, _ = _workflow(params)
    return room_report.build(pinned, params)


COMMON_PARAMS = {"units", "document_id", "operation_id"}     # accepted by every action


def _note_ignored(name: str, params: dict[str, Any], result: Any) -> Any:
    """Turn the add-in's unread request keys into result['ignoredParams'].

    A key is reported only when both sides agree: the add-in's handler never read it AND the catalogue does not list it
    for this action or operation. That is a misspelled or unsupported option, which used to be ignored silently.
    """
    if not isinstance(result, dict) or "unreadParams" not in result:
        return result
    result = dict(result)
    unread = result.pop("unreadParams")
    if not isinstance(unread, dict):
        return result
    found: dict[str, Any] = {}
    entry = catalog.READ_ACTIONS.get(name) or catalog.WORKSHARE_ACTIONS.get(name)
    if entry is not None and isinstance(unread.get("params"), list):
        known = {key.rstrip("?") for key in entry[1]} | COMMON_PARAMS
        extra = sorted(key for key in unread["params"] if isinstance(key, str) and key not in known)
        if extra:
            found["params"] = extra
    operations = params.get("operations")
    if name == "edit" and isinstance(operations, list) and isinstance(unread.get("operations"), dict):
        rows = []
        for index, keys in unread["operations"].items():
            if not (isinstance(index, str) and index.isdecimal() and 1 <= int(index) <= len(operations) and isinstance(keys, list)):
                continue
            op = operations[int(index) - 1].get("op")
            known = {key.rstrip("?") for key in catalog.EDIT_OPS[op][1]} | {"op"} if op in catalog.EDIT_OPS else None
            extra = sorted(key for key in keys if isinstance(key, str) and (known is None or key not in known))
            if extra:
                rows.append({"operation": int(index), "op": op, "params": extra})
        if rows:
            found["operations"] = sorted(rows, key=lambda row: row["operation"])
    if found:
        result["ignoredParams"] = {**found, "note": "Not read by the add-in and not in the catalogue for this action: check the "
                                                    "spelling (action='help', topic=<name>). They had no effect."}
    return result


def _recent_operations() -> dict[str, Any]:
    """get_operation_status without an id: the writes this server dispatched, each checked again in its Revit."""
    operations = bridge_client.recent_operations()
    for entry in operations:
        if entry["state"] in ("answered", "rejected"):   # the add-in replied: its answer is already recorded
            continue
        try:
            live = call("get_operation_status", {"operation_id": entry["operationId"]},
                        connection=bridge_client.sessions_dir() / f"{entry['pid']}.json", timeout=10)
            entry["revit"] = {k: live.get(k) for k in ("state", "result", "error")} if isinstance(live, dict) else live
        except BridgeError as exc:
            entry["revit"] = {"unreachable": str(exc)[:200]}
    return {"operations": operations,
            "note": "Writes dispatched by this MCP server process, newest first. replyDelivered false means the client had "
                    "cancelled or timed out before Revit answered: read 'applied' (or revit.result) before sending that "
                    "edit again. An empty list after a server restart says nothing about earlier edits."}


def _batch_timeout(params: dict[str, Any]) -> float:
    """batch_read checks its max_seconds budget only between files: wait longer than the budget, never less."""
    budget = params.get("max_seconds", 600)
    if isinstance(budget, bool) or not isinstance(budget, (int, float)) or not 0 < budget <= 3000:
        raise ValueError("max_seconds must be a number between 1 and 3000.")
    return float(budget) + 300.0


def _workflow(params: dict[str, Any]):
    """Resolve the connection once and require the same active document on every step."""
    pinned = partial(call, connection=bridge_client.pin())
    target = pinned("ping")
    document_id = target.get("documentId")
    if not document_id:
        raise ValueError("Open the intended Revit model and install the matching add-in before using this workflow.")
    if "document_id" in params and params["document_id"] != document_id:
        raise ValueError("The active model does not match document_id. Check ping before continuing.")

    def on_document(method: str, args: dict[str, Any] | None = None, **kwargs) -> Any:
        return pinned(method, {**(args or {}), "document_id": document_id}, **kwargs)

    return on_document, target


# Actions the server answers itself (the rest go to the add-in). Reading ones and model-changing ones are kept apart
# so the split tools (REVIT_MCP_SPLIT=1) can allow one without the other.
LOCAL_READ = {
    "help": _help,
    "qs_report": _qs_report,
    "bbs_report": _bbs_report,
    "mep_report": _mep_report,
    "room_report": _room_report,
    "params_to_excel": _excel("params_to_excel"),
    "excel_template": _excel("excel_template"),
    "list_revit_sessions": lambda params: {"sessions": _sessions()},
    "select_revit": _select,
}
LOCAL_EDIT = {
    "params_from_excel": _excel("params_from_excel"),
    "excel_to_model": _excel("excel_to_model"),
}


def _dispatch(action: str, params: dict[str, Any] | None, allow_read: bool, allow_edit: bool) -> Any:
    params = {} if params is None else params
    if not isinstance(params, dict):
        raise ValueError("params must be an object, e.g. {\"category\": \"columns\"}.")
    name = (action or "").strip()

    if name in LOCAL_EDIT:
        if not allow_edit:
            raise ValueError(f"{name} changes the model: use the revit_edit tool.")
        unknown = params.keys() - {key.rstrip("?") for key in catalog.LOCAL_ACTIONS[name][1]}
        if unknown:
            raise ValueError("Unknown workbook edit parameters: " + ", ".join(sorted(unknown)))
        return LOCAL_EDIT[name](params)

    if name in catalog.WORKSHARE_ACTIONS:
        if not allow_edit:
            raise ValueError(f"{name} changes the central model: use the revit_edit tool.")
        unknown = params.keys() - {key.rstrip("?") for key in catalog.WORKSHARE_ACTIONS[name][1]}
        if unknown:
            raise ValueError(f"Unknown {name} parameters: " + ", ".join(sorted(unknown)))
        for key in ("document_id", "operation_id"):
            if key in params and (not isinstance(params[key], str) or not params[key]):
                raise ValueError(f"{key} must be a non-empty text when given.")
        return _note_ignored(name, params, call(name, params, timeout=EDIT_TIMEOUT, report_unread=True))

    if name == "edit":
        if not allow_edit:
            raise ValueError("This tool only reads. Use the revit_edit tool to change the model.")
        ops = params.get("operations")
        unknown_params = params.keys() - {key.rstrip("?") for key in catalog.EDIT_PARAMS}
        if unknown_params:
            raise ValueError("Unknown edit parameters: " + ", ".join(sorted(unknown_params)))
        if "dry_run" in params and not isinstance(params["dry_run"], bool):
            raise ValueError("dry_run must be true or false.")
        if not isinstance(ops, list) or not ops:
            raise ValueError("edit needs params.operations: a non-empty list of {op: ..., ...}. See action='help', topic='edit'.")
        if any(not isinstance(o, dict) for o in ops):
            raise ValueError("Every edit operation must be an object with an op name.")
        if len(ops) > 500:
            raise ValueError("An edit batch supports at most 500 operations.")
        if any("dry_run" in op or "dryRun" in op for op in ops):
            raise ValueError("dry_run belongs on the edit batch, outside operations.")
        for key in ("document_id", "operation_id"):
            if key in params and (not isinstance(params[key], str) or not params[key]):
                raise ValueError(f"{key} must be a non-empty text when given.")
        unknown = sorted({str(o.get("op")) for o in ops} - set(catalog.EDIT_OPS))
        if unknown:
            raise ValueError(f"Unknown edit operation(s): {', '.join(unknown)}. Available: {', '.join(catalog.EDIT_OPS)}")
        for op in ops:
            if op["op"] == "purge":
                allowed = {key.rstrip("?") for key in catalog.EDIT_OPS["purge"][1]} | {"op"}
                if op.keys() - allowed or any(value is None for value in op.values()):
                    raise ValueError("Purge requires recognized, non-null options. Check categories, keep_ids and keep_names.")
        return _note_ignored("edit", params, call("edit", params, timeout=EDIT_TIMEOUT, report_unread=True))

    if not allow_read:
        raise ValueError("This tool only edits (action='edit'). Use the revit_read tool for everything else.")
    if name in LOCAL_READ:
        return LOCAL_READ[name](params)
    if name == "get_operation_status" and not params.get("operation_id"):
        return _recent_operations()
    if name == "get_operation_status" and params.get("pid") is not None:
        pid = _pid(params["pid"])
        return call(name, params, connection=bridge_client.sessions_dir() / f"{pid}.json")
    if name in catalog.READ_ACTIONS:
        # Opening and creating models can take minutes; batch_read has its own budget.
        timeout = (_batch_timeout(params) if name == "batch_read"
                   else EDIT_TIMEOUT if name in {"model_open", "model_new", "pick", "performance_adviser"} else READ_TIMEOUT)
        return _note_ignored(name, params, call(name, params, timeout=timeout, report_unread=True))
    if name in catalog.EDIT_OPS:
        raise ValueError(f"'{name}' is an edit operation: call action='edit' with params={{\"operations\": [{{\"op\": \"{name}\", ...}}]}}.")
    raise ValueError(f"Unknown action '{name}'. Use action='help' for the list.")


# The read tool also writes report workbooks (params_to_excel, qs_report, bbs_report, excel_template; replacing an
# existing file needs overwrite: true) and changes which Revit later calls go to (select_revit). It therefore does not
# claim readOnlyHint: clients may auto-approve tools that do.
READ_TOOL = ToolAnnotations(title="Revit (model reads and file exports)", readOnlyHint=False, destructiveHint=True, idempotentHint=False,
                            openWorldHint=False)
READ_WRITE = ToolAnnotations(title="Revit", readOnlyHint=False, destructiveHint=True, idempotentHint=False, openWorldHint=False)


async def _run(action: str, params: dict[str, Any] | None, allow_read: bool, allow_edit: bool,
               ctx: Context | None = None) -> Any:
    """Run the blocking bridge call in a worker thread.

    FastMCP calls synchronous tools directly on its event loop, so a 10-minute edit would stop the stdio loop from
    answering pings or reading cancellations. In a thread the loop stays responsive. A cancelled call returns at once;
    an edit that had already reached Revit keeps its operation_id receipt (get_operation_status).
    """
    signal = Event()
    token = request_lifetime.cancelled.set(signal)
    result, failure = None, None
    try:
        async with anyio.create_task_group() as heartbeat:
            if ctx is not None:
                heartbeat.start_soon(_heartbeat, ctx)
            try:
                result = await anyio.to_thread.run_sync(partial(_dispatch, action, params, allow_read, allow_edit),
                                                        abandon_on_cancel=True)
            except Exception as exc:  # noqa: BLE001 - re-raised below: a task group would wrap it in an ExceptionGroup
                failure = exc
            finally:
                heartbeat.cancel_scope.cancel()
    finally:
        signal.set()
        request_lifetime.cancelled.reset(token)
    if failure is not None:
        raise failure
    if action.strip() in {"edit", *LOCAL_EDIT, *catalog.WORKSHARE_ACTIONS} and isinstance(result, dict):
        outcome = result.get("result", result)
        if (_failed(outcome) or result.get("conflicts")
                or any(_failed(batch) for batch in result.get("batches", []))):
            text = json.dumps(result, ensure_ascii=False)
            if isinstance(outcome, dict) and outcome.get("applied") is True:
                # Committed, then a save / export after the commit failed: resending would apply the edit twice.
                text = ("The model changes WERE COMMITTED; only a step after the commit failed (see postCommit). "
                        "Do not send this edit again. " + text)
            raise ValueError(text)
    return result


def _failed(outcome: Any) -> bool:
    return isinstance(outcome, dict) and (outcome.get("failedAt") is not None
        or outcome.get("succeeded") is False or bool(outcome.get("error"))
        or outcome.get("dryRun") is True and outcome.get("previewSucceeded") is False)


if os.environ.get("REVIT_MCP_SPLIT", "").strip().lower() in ("1", "true", "yes"):
    @mcp.tool(name="revit_read", description=catalog.tool_description("read"), annotations=READ_TOOL)
    async def revit_read(action: str, params: dict[str, Any] | None = None, ctx: Context | None = None) -> Any:
        return await _run(action, params, allow_read=True, allow_edit=False, ctx=ctx)

    @mcp.tool(name="revit_edit", description=catalog.tool_description("edit"),
              annotations=ToolAnnotations(title="Revit (edit)", readOnlyHint=False, destructiveHint=True, openWorldHint=False))
    async def revit_edit(operations: list[dict[str, Any]] | None = None, dry_run: bool = False, units: str = "mm",
                         excel_action: str | None = None, path: str | None = None, document_id: str | None = None,
                         operation_id: str | None = None, worksharing_action: str | None = None,
                         worksharing_params: dict[str, Any] | None = None, ctx: Context | None = None) -> Any:
        """Submit an operation batch, a workbook edit, or one standalone worksharing action."""
        target = {key: value for key, value in {"document_id": document_id, "operation_id": operation_id}.items()
                  if value is not None}
        if worksharing_action is not None:
            if (worksharing_action not in catalog.WORKSHARE_ACTIONS or operations is not None or excel_action is not None
                    or path is not None or dry_run or units != "mm"):
                raise ValueError("Choose one worksharing action. Worksharing has no dry run or unit conversion.")
            args = dict(worksharing_params or {})
            if target.keys() & args.keys():
                raise ValueError("Specify each document_id and operation_id only once.")
            return await _run(worksharing_action, {**args, **target}, allow_read=False, allow_edit=True, ctx=ctx)
        if worksharing_params is not None:
            raise ValueError("worksharing_params requires worksharing_action.")
        if excel_action is not None:
            if excel_action not in LOCAL_EDIT or operations is not None or not path or units != "mm":
                raise ValueError("Choose one supported Excel action with its workbook path and default mm units.")
            return await _run(excel_action, {**target, "path": path, "dry_run": dry_run}, allow_read=False, allow_edit=True,
                              ctx=ctx)
        if path is not None:
            raise ValueError("path requires excel_action.")
        return await _run("edit", {**target, "operations": operations, "dry_run": dry_run, "units": units},
                          allow_read=False, allow_edit=True, ctx=ctx)
else:
    @mcp.tool(name="revit", description=catalog.tool_description("all"), annotations=READ_WRITE)
    async def revit(action: str, params: dict[str, Any] | None = None, ctx: Context | None = None) -> Any:
        return await _run(action, params, allow_read=True, allow_edit=True, ctx=ctx)


if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1 and sys.argv[1] in ("--configure-clients", "--remove-clients"):
        # Run by install.bat / uninstall.bat: edit the MCP clients' settings, then exit (no server is started).
        import client_config
        if sys.argv[1] == "--configure-clients" and len(sys.argv) != 3:
            sys.exit("usage: revit-mcp-server.exe --configure-clients <path of the installed exe>")
        sys.exit(client_config.run(sys.argv[2] if sys.argv[1] == "--configure-clients" else None))
    try:
        mcp.run()  # stdio transport: nothing in this process may write to stdout
    finally:
        # The stdio transport closes the standard streams at end of input. The frozen exe's launcher flushes them once
        # more after Python returns, which printed "ValueError: I/O operation on closed file" to stderr. Point any
        # closed standard stream at the null device so that last flush is harmless.
        import os
        for name in ("stdout", "stderr", "__stdout__", "__stderr__"):
            stream = getattr(sys, name, None)
            if stream is not None and getattr(stream, "closed", False):
                setattr(sys, name, open(os.devnull, "w", encoding="utf-8"))
