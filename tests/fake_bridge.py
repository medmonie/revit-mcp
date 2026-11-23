"""Stand-in for the Revit add-in, with canned data, so the MCP server can be tested without Revit.

    python fake_bridge.py [path-to-bridge.json]

Point the MCP server at the same file with the REVIT_BRIDGE_FILE environment variable. Speaks the same
newline-delimited JSON protocol as the real add-in, including the token check and the one-time edit permission
(FAKE_EDIT=allow grants it, FAKE_EDIT=decline refuses; default allow).
"""
from __future__ import annotations

import json
import os
import secrets
import socketserver
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import catalog  # noqa: E402

TOKEN = secrets.token_hex(16)
STATE = {"editingAllowed": False, "edits": 0}
DOCUMENT_ID = "fake-session:fake-model"
MODEL_KEY = "fake-model"
RECEIPTS = {}

LEVELS = [{"id": 311, "name": "Level 1", "elevation": 0.0}, {"id": 694, "name": "Level 2", "elevation": 3500.0}]
COLUMNS = [
    {"id": 1001, "uniqueId": "fake-1001", "category": "Structural Columns", "family": "M_Concrete-Rectangular-Column", "type": "400 x 400mm",
     "level": "Level 1", "location": {"point": [0.0, 0.0, 0.0]}},
    {"id": 1002, "uniqueId": "fake-1002", "category": "Structural Columns", "family": "M_Concrete-Rectangular-Column", "type": "400 x 400mm",
     "level": "Level 1", "location": {"point": [6000.0, 0.0, 0.0]}},
]


def permission() -> dict:
    return {"editingAllowed": STATE["editingAllowed"], "scope": "this Revit session"}


def handle(method: str, params: dict):
    if method == "get_operation_status":
        op_id = params["operation_id"]
        return {"operationId": op_id, "state": "completed", "result": RECEIPTS[op_id][1]} if op_id in RECEIPTS else {
            "operationId": op_id, "state": "unknown"}
    if method == "edit" or "document_id" in params:
        active = os.environ.get("FAKE_EDIT_DOCUMENT", DOCUMENT_ID) if method == "edit" else DOCUMENT_ID
        if params.get("document_id") != active:
            raise ValueError("Active document does not match document_id.")
    if method == "edit":
        op_id = params["operation_id"]   # the server always sends one (generated when omitted)
        if op_id in RECEIPTS:
            payload, receipt = RECEIPTS[op_id]
            if params != payload:
                raise ValueError("operation_id is already in use for a different request.")
            return receipt
        receipt = edit(params)
        RECEIPTS[op_id] = (params, receipt)
        return receipt
    if method == "ping":
        return {"bridgeVersion": catalog.BRIDGE_VERSION, "revitVersion": "2027", "activeModel": "Fake.rvt",
                "editingAllowed": STATE["editingAllowed"], "documentId": DOCUMENT_ID, "modelKey": MODEL_KEY}
    if method == "list_actions":
        return {"bridgeVersion": catalog.BRIDGE_VERSION, "revitVersion": "2027",
                "actions": [{"name": n, "kind": "read"} for n in catalog.READ_ACTIONS] + [{"name": "edit", "kind": "write"}],
                "editOperations": sorted(catalog.EDIT_OPS), "editPermission": permission()}
    if method == "get_edit_permission":
        return permission()
    if method == "list_levels":
        return {"units": params.get("units", "mm"), "levels": LEVELS}
    if method == "get_elements":
        names = params.get("params") or []
        refs = {str(k) for k in params.get("ids", [])} | set(params.get("unique_ids", []))
        elems = [c for c in COLUMNS if (not refs or str(c["id"]) in refs or c["uniqueId"] in refs)
                 and c["id"] > int(params.get("after_id", 0))]
        details = {n: {"key": n if n.startswith("guid:") else "guid:" + n, "name": n.removeprefix("guid:"),
                       "target": "instance", "kind": "String", "readOnly": False} for n in names}
        elems = [{**c, "params": {n: STATE.get(f"{c['id']}:{details[n]['key']}") for n in names},
                  "parameterInfo": details} for c in elems]
        limit = params.get("limit", 100)
        out = {"units": "mm", "count": min(limit, len(elems)), "truncated": len(elems) > limit, "elements": elems[:limit]}
        if out["truncated"]:
            out["nextAfterId"] = str(elems[limit - 1]["id"])
        return out
    if method == "takeoff":
        return {"groupBy": params.get("group_by", "type"), "note": "n",
                "rows": [{"category": "Structural Columns", "type": "400 x 400mm", "level": "L1", "count": 2, "length_m": 7.0,
                          "volume_m3": 1.12}]}
    if method == "last_ops":
        return STATE.get("last_ops")
    if method == "get_project_info":
        return {"title": "Fake.rvt", "documentId": DOCUMENT_ID, "modelKey": MODEL_KEY,
                "project": {"name": "Test project", "number": "T-1"}}
    if method == "material_takeoff":
        return {"rows": [{"category": "Floors", "level": "L1", "material": "C30", "elements": 2, "volume_m3": 10.5, "area_m2": 50.0,
                          "materialKind": "concrete"},
                         {"category": "Structural Columns", "level": "L1", "material": "Concrete, Cast-in-Place gray",
                          "elements": 4, "volume_m3": 2.25, "area_m2": 20.0, "materialKind": "concrete"},
                         {"category": "Walls", "level": "L1", "material": "Brick", "elements": 1, "volume_m3": 5, "area_m2": 10,
                          "materialKind": "non_concrete"}], "note": "n"}
    if method == "steel_takeoff":
        return {"rows": [{"category": "Structural Framing", "type": "IPE400", "count": 3, "length_m": 18.0, "volume_m3": 0.153,
                          "weight_kg": 1201.05, "kg_per_m": 66.73}], "totalWeight_t": 1.201, "note": "n"}
    if method == "rebar_takeoff":
        return {"rows": [], "totalWeight_kg": 0, "note": "n"}
    if method == "formwork_takeoff":
        return {"rows": [{"category": "Floors", "type": "Slab 200", "level": "L1", "count": 2, "sides_m2": 8.0, "soffit_m2": 50.0,
                          "top_m2": 50.0, "formwork_m2": 58.0}], "totalFormwork_m2": 58.0, "note": "n"}
    if method == "check_materials":
        return {"elementsWithProblems": 4, "rows": [{"category": "Structural Columns", "problem": "Revit default material",
                "material": "Concrete, Cast-in-Place gray", "elements": 4, "volume_m3": 2.25, "sampleIds": [1, 2, 3, 4]}], "note": "n"}
    if method == "find_overlaps":
        return {"doubleCounted_m3": 0.36, "complete": True, "byCategories": [{"categories": "Floors / Structural Framing", "pairs": 2,
                "overlap_m3": 0.36}], "pairs": [{"a": 1, "aLabel": "Floors : Slab", "b": 2, "bLabel": "Framing : B1", "level": "L1",
                "overlap_m3": 0.18}], "note": "n",
                "reviewPairCount": 1,
                "reviewPairs": [{"a": {"id": 8, "label": "Columns : C9"}, "b": {"id": 9, "label": "Columns : C9"},
                                 "kind": "coincident_geometry", "overlap_m3": 1.15}]}
    if method in catalog.READ_ACTIONS:
        return {"note": f"fake bridge has no canned data for {method}"}
    raise ValueError(f"unknown method '{method}'")


def edit(params: dict) -> dict:
    ops = params.get("operations") or []
    dry = bool(params.get("dry_run"))
    if not STATE["editingAllowed"]:
        if os.environ.get("FAKE_EDIT", "allow") != "decline":
            STATE["editingAllowed"] = True
        return {"applied": False, "dryRun": dry, "permission": "not granted",
                "reason": "Revit is now asking the user to allow editing. Nothing was changed."}
    if not dry:
        STATE["edits"] += 1
        for op in ops:
            if op.get("op") == "set_parameters":
                wanted = set(op.get("unique_ids", [])) | {str(i) for i in op.get("ids", [])}
                for element in COLUMNS:
                    if str(element["id"]) in wanted or element["uniqueId"] in wanted:
                        for k, v in op["values"].items():
                            STATE[f"{element['id']}:{k}"] = v
    STATE["last_ops"] = ops
    return {"applied": not dry, "dryRun": dry, "previewSucceeded": dry, "units": params.get("units", "mm"),
            "operations": len(ops), "modelEditCount": STATE["edits"], "warnings": [],
            "results": [{"index": i, "op": op["op"], "result": {}} for i, op in enumerate(ops, 1)]}


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        for raw in self.rfile:
            req = json.loads(raw)
            if req.get("token") != TOKEN:
                reply = {"id": req.get("id"), "ok": False, "error": "unauthorized"}
            else:
                try:
                    reply = {"id": req.get("id"), "ok": True,
                             "result": handle(req.get("method", ""), req.get("params") or {})}
                except Exception as exc:  # mirror the add-in: errors go back as ok=false
                    reply = {"id": req.get("id"), "ok": False, "error": f"{type(exc).__name__}: {exc}"}
            self.wfile.write((json.dumps(reply) + "\n").encode("utf-8"))


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> None:
    target = Path(sys.argv[1] if len(sys.argv) > 1 else os.environ.get("REVIT_BRIDGE_FILE", "bridge.json"))
    with Server(("127.0.0.1", 0), Handler) as srv:
        port = srv.server_address[1]
        target.parent.mkdir(parents=True, exist_ok=True)
        info = json.dumps({"port": port, "token": TOKEN, "pid": os.getpid(), "revit": "2027",
                           "protocol": int(os.environ.get("FAKE_PROTOCOL", "4"))})
        target.write_text(info, encoding="utf-8")
        session = target.parent / "sessions" / f"{os.getpid()}.json"   # like the add-in: one file per Revit
        session.parent.mkdir(parents=True, exist_ok=True)
        session.write_text(info, encoding="utf-8")
        print(f"fake bridge on 127.0.0.1:{port}, connection file {target}", flush=True)
        try:
            srv.serve_forever()
        finally:
            target.unlink(missing_ok=True)
            session.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
