"""Revit MCP server (stdio). Claude launches this process; it forwards every call to the RevitMcpBridge add-in
running inside Revit (see bridge_client.py).

ONE tool by default - `revit(action, params)` - so the Claude app has a single thing to allow, once. Reading is
always available; editing is gated inside Revit by a one-time permission per Revit session (dialog or the
Allow Editing button).

Set REVIT_MCP_SPLIT=1 to expose two tools instead (`revit_read`, `revit_edit`) if you prefer to allow reading
without allowing edits in the Claude app.
"""
from __future__ import annotations

import os
from functools import partial
from typing import Any

from mcp.server.fastmcp import FastMCP
from mcp.types import ToolAnnotations

import bridge_client
import catalog
from bridge_client import EDIT_TIMEOUT, BridgeError, call

mcp = FastMCP("revit")


def _help(params: dict[str, Any]) -> dict[str, Any]:
    data = catalog.help_data(params.get("topic"))
    if params.get("topic"):
        return data
    # Tell Claude when the installed add-in and this catalogue disagree (e.g. after updating only one of them).
    try:
        live = call("list_actions")
        plugin_actions = {a["name"] for a in live["actions"]}
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
    import json
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
        entry = {"pid": info.get("pid"), "revit": info.get("revit"), "startedUtc": info.get("startedUtc"),
                 "selected": bridge_client.selected() == f, "default": info.get("pid") == default_pid}
        try:
            ping = call("ping", connection=f, timeout=5)
            entry.update(reachable=True, activeModel=ping.get("activeModel"), editingAllowed=ping.get("editingAllowed"))
        except BridgeError as exc:
            entry.update(reachable=False, problem=str(exc)[:160])
        out.append(entry)
    return out


def _select(params: dict[str, Any]) -> dict[str, Any]:
    from bridge_client import sessions_dir
    pid, revit = params.get("pid"), params.get("revit")
    if str(pid).lower() == "auto" or (pid is None and revit is None):
        bridge_client.select(None)
        return {"selected": "auto", "note": "Calls go to the only open Revit; with several open, reads go to the default one "
                                            "(first started, or where MCP Status was pressed) and edits need select_revit."}
    live = [s for s in _sessions() if s.get("reachable")]
    if pid is not None:
        try:
            wanted = int(float(pid))
        except (TypeError, ValueError):
            raise ValueError("'pid' must be a process id from list_revit_sessions, or 'auto'.") from None
        match = [s for s in live if s.get("pid") is not None and int(s["pid"]) == wanted]
    else:
        match = [s for s in live if str(s.get("revit")) == str(revit).strip()]
    if len(match) != 1:
        raise ValueError(f"{'No' if not match else 'More than one'} reachable Revit matches. Sessions: {live}. "
                         "Give 'pid' from list_revit_sessions.")
    bridge_client.select(sessions_dir() / f"{match[0]['pid']}.json")
    return {"selected": match[0], "note": "All following calls go to this Revit until select_revit is called again."}


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
    return bbs_report.build(call, params)


def _qs_report(params: dict[str, Any]) -> Any:
    import qs_report
    pinned, _ = _workflow(params)
    return qs_report.build(pinned, params)


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
    params = params or {}
    if not isinstance(params, dict):
        raise ValueError("params must be an object, e.g. {\"category\": \"columns\"}.")
    name = (action or "").strip()

    if name in LOCAL_EDIT:
        if not allow_edit:
            raise ValueError(f"{name} changes the model: use the revit_edit tool.")
        return LOCAL_EDIT[name](params)

    if name == "edit":
        if not allow_edit:
            raise ValueError("This tool only reads. Use the revit_edit tool to change the model.")
        ops = params.get("operations")
        if not isinstance(ops, list) or not ops:
            raise ValueError("edit needs params.operations: a non-empty list of {op: ..., ...}. See action='help', topic='edit'.")
        if any(not isinstance(o, dict) for o in ops):
            raise ValueError("Every edit operation must be an object with an op name.")
        for key in ("document_id", "operation_id"):
            if key in params and (not isinstance(params[key], str) or not params[key]):
                raise ValueError(f"{key} must be a non-empty text when given.")
        unknown = sorted({str(o.get("op")) for o in ops} - set(catalog.EDIT_OPS))
        if unknown:
            raise ValueError(f"Unknown edit operation(s): {', '.join(unknown)}. Available: {', '.join(catalog.EDIT_OPS)}")
        return call("edit", params, timeout=EDIT_TIMEOUT)

    if not allow_read:
        raise ValueError("This tool only edits (action='edit'). Use the revit_read tool for everything else.")
    if name in LOCAL_READ:
        return LOCAL_READ[name](params)
    if name == "get_operation_status" and params.get("pid") is not None:
        pid = params["pid"]
        if isinstance(pid, bool) or not str(pid).isascii() or not str(pid).isdecimal() or int(pid) < 1:
            raise ValueError("pid must be a positive integer from the edit receipt or list_revit_sessions.")
        return call(name, params, connection=bridge_client.sessions_dir() / f"{int(pid)}.json")
    if name in catalog.READ_ACTIONS:
        return call(name, params)
    if name in catalog.EDIT_OPS:
        raise ValueError(f"'{name}' is an edit operation: call action='edit' with params={{\"operations\": [{{\"op\": \"{name}\", ...}}]}}.")
    raise ValueError(f"Unknown action '{name}'. Use action='help' for the list.")


READ_ONLY = ToolAnnotations(title="Revit (read)", readOnlyHint=True, destructiveHint=False, openWorldHint=False)
READ_WRITE = ToolAnnotations(title="Revit", readOnlyHint=False, destructiveHint=True, idempotentHint=False, openWorldHint=False)

if os.environ.get("REVIT_MCP_SPLIT", "").strip() in ("1", "true", "yes"):
    @mcp.tool(name="revit_read", description=catalog.tool_description("read"), annotations=READ_ONLY)
    def revit_read(action: str, params: dict[str, Any] | None = None) -> Any:
        return _dispatch(action, params, allow_read=True, allow_edit=False)

    @mcp.tool(name="revit_edit", description=catalog.tool_description("edit"),
              annotations=ToolAnnotations(title="Revit (edit)", readOnlyHint=False, destructiveHint=True, openWorldHint=False))
    def revit_edit(operations: list[dict[str, Any]] | None = None, dry_run: bool = False, units: str = "mm",
                   excel_action: str | None = None, path: str | None = None, document_id: str | None = None,
                   operation_id: str | None = None) -> Any:
        """operations: an edit batch. Or excel_action 'params_from_excel' / 'excel_to_model' with the workbook path."""
        target = {key: value for key, value in {"document_id": document_id, "operation_id": operation_id}.items()
                  if value is not None}
        if excel_action:
            return _dispatch(excel_action, {**target, "path": path, "dry_run": dry_run}, allow_read=False, allow_edit=True)
        return _dispatch("edit", {**target, "operations": operations, "dry_run": dry_run, "units": units},
                         allow_read=False, allow_edit=True)
else:
    @mcp.tool(name="revit", description=catalog.tool_description("all"), annotations=READ_WRITE)
    def revit(action: str, params: dict[str, Any] | None = None) -> Any:
        return _dispatch(action, params, allow_read=True, allow_edit=True)


if __name__ == "__main__":
    mcp.run()  # stdio transport
