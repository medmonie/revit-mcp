"""Excel round trips, done in the MCP server (no Excel needed):

- params_to_excel   : element parameters -> .xlsx (one row per element, id first)
- params_from_excel : the edited .xlsx -> set_parameters for the cells that changed (dry run supported)
- excel_template    : an empty input workbook for excel_to_model
- excel_to_model    : levels, grids, columns, beams, walls, slabs and footings from that workbook -> ONE edit batch
"""
from __future__ import annotations

import hashlib
import json
import math
import uuid
from typing import Any, Callable

from openpyxl import Workbook, load_workbook
from openpyxl.styles import Font, PatternFill

from bridge_client import EDIT_TIMEOUT, BridgeError
from xlsx_util import CellBudget, flag, header as _header, literal_cell, save_workbook, xlsx_path as _xlsx_path

KEY_FILL = PatternFill("solid", fgColor="D9D9D9")
FIXED = ["id", "category", "family", "type", "level"]   # read-only columns written by params_to_excel
MAX_OPS = 500                                           # the add-in's limit per edit batch
MODEL_SHEET = "_RevitBridge"
SCHEMA = "revit-mcp-parameters-v1"


# ------------------------------------------------------------------ parameters out and back

def _parameter_names(names: Any) -> list[str]:
    if not isinstance(names, list) or not 1 <= len(names) <= 40:
        raise ValueError("Give one to 40 parameter names or selectors.")
    if any(not isinstance(n, str) or not n.strip() for n in names):
        raise ValueError("Parameter headers must contain non-empty text.")
    if len({n.casefold() for n in [*FIXED, *names]}) != len(FIXED) + len(names):
        raise ValueError("Parameter headers must be unique and cannot duplicate a fixed column. Use a parameter selector if needed.")
    return names


def _raw(v: Any, kind: str | None) -> Any:
    if isinstance(v, dict):
        return str(v["id"]) if "id" in v else v.get("value")
    if kind == "ElementId" and isinstance(v, int):
        return str(v)
    return v


def _element_id(value: Any) -> str:
    if isinstance(value, bool) or not isinstance(value, (str, int, float)):
        raise ValueError(f"Invalid element id {value!r}.")
    if isinstance(value, (int, float)):
        if not (0 < value < 10**15) or value != int(value):
            raise ValueError("Long element IDs must be text. Re-export the workbook if Excel rounded them.")
        value = str(int(value))
    value = value.strip()
    if not value.isascii() or not value.isdecimal() or not 0 < int(value) < 2**63:
        raise ValueError(f"Invalid element id {value!r}.")
    return str(int(value))


def params_to_excel(call: Callable[..., Any], params: dict[str, Any], target: dict[str, Any]) -> dict[str, Any]:
    path = _xlsx_path(params, must_exist=False)
    names = _parameter_names(params.get("params"))
    if not target.get("modelKey"):
        raise ValueError("Revit did not identify the source model.")
    query = {k: params[k] for k in ("category", "ids", "type", "level", "view", "after_id") if params.get(k) is not None}
    if "category" not in query and "ids" not in query:
        raise ValueError("Give 'category' (or 'ids').")
    data = call("get_elements", {**query, "params": names, "location": False, "parameter_details": True,
                                 "limit": int(params.get("limit", 5000))})
    wb = Workbook()
    ws = wb.active
    ws.title = "Parameters"
    _header(ws, FIXED + names, key_columns=len(FIXED))
    metadata = wb.create_sheet(MODEL_SHEET)
    metadata.sheet_state = "hidden"
    metadata.append([SCHEMA, target["modelKey"]])
    metadata.append(["id", "uniqueId", *[name for n in names for name in (n, n + " baseline")]])
    for r, e in enumerate(data["elements"], 2):
        eid, uid = str(e["id"]), e["uniqueId"]
        row = [e["id"] if e["id"] < 10**15 else eid, e.get("category"), e.get("family"),
               e.get("type") or e.get("name"), e.get("level")]
        for c, v in enumerate(row, 1):
            literal_cell(ws, r, c, v).fill = KEY_FILL
        literal_cell(metadata, r + 1, 1, eid)
        literal_cell(metadata, r + 1, 2, uid)
        for i, name in enumerate(names):
            detail = e["parameterInfo"].get(name)
            value = _raw(e["params"].get(name), detail["kind"] if detail else None)
            cell = literal_cell(ws, r, len(FIXED) + i + 1, value)
            if detail is None or detail["readOnly"] or detail["target"] != "instance":
                cell.fill = KEY_FILL
            literal_cell(metadata, r + 1, 3 + 2 * i, json.dumps(detail) if detail else None)
            literal_cell(metadata, r + 1, 4 + 2 * i, value)
    info = wb.create_sheet("Read me")
    info["A1"] = "Edit the parameter columns (white), then run params_from_excel with this file."
    info["A2"] = "Keep the IDs, headers, and hidden metadata. Grey cells are reference only."
    info["A3"] = "Only edits to exported values are imported. Use literal values, lengths in mm and TRUE/FALSE for yes/no."
    info["A4"] = "Blank clears text or an element reference. Element references are exported as IDs and can also be entered by name."
    save_workbook(wb, path, params)
    return {"file": str(path), "elements": len(data["elements"]), "truncated": data.get("truncated"), "params": names,
            "nextAfterId": data.get("nextAfterId"), "documentId": target["documentId"]}


def _typed(value: Any, kind: str) -> Any:
    if value is None or value == "":
        return "" if kind == "String" else None
    if kind == "String":
        return str(value)
    if kind == "Boolean":
        text = str(value).strip().casefold()
        if text in ("true", "yes", "1"):
            return True
        if text in ("false", "no", "0"):
            return False
        raise ValueError("Use TRUE or FALSE for a Yes/No parameter.")
    if kind in ("Double", "Integer"):
        if isinstance(value, bool):
            raise ValueError("Use a number for a numeric parameter.")
        try:
            number = float(value)
        except (TypeError, ValueError):
            if isinstance(value, str):
                return value  # Revit parses formatted values that include units.
            raise ValueError("Use a number or formatted numeric text.") from None
        if not math.isfinite(number):
            raise ValueError("Numeric values must be finite.")
        if kind == "Integer" and (number != int(number) or not -(2**31) <= number < 2**31):
            raise ValueError("Use a whole number in Revit's integer range.")
        return int(number) if kind == "Integer" else number
    if kind == "ElementId":
        if isinstance(value, (int, float)):
            if isinstance(value, bool) or not (-10**15 < value < 10**15) or int(value) != value:
                raise ValueError("Use whole number IDs, and enter long IDs as text.")
            return str(int(value))
        if isinstance(value, str):
            return value.strip()
        raise ValueError("Use a Revit element ID or name.")
    return value


def _same(a: Any, b: Any, kind: str) -> bool:
    a, b = _typed(a, kind), _typed(b, kind)
    if kind == "Double" and isinstance(a, float) and isinstance(b, float):
        return math.isclose(a, b, abs_tol=1e-6, rel_tol=0)
    return a == b


def _parameters_sheet(path, target: dict[str, Any], explicit_target: bool):
    """Read keys and literal cells. Legacy sheets need a caller-confirmed model because they have no origin."""
    wb = load_workbook(path, data_only=False, read_only=True)
    try:
        ws = wb["Parameters"]
        headers = [cell.value for cell in ws[1]]
        if headers[:len(FIXED)] != FIXED:
            raise ValueError("The Parameters sheet must retain its first five headers from params_to_excel.")
        names = _parameter_names(headers[len(FIXED):])
        rows = {}
        fixed = {}
        budget = CellBudget()
        for index, cells in enumerate(ws.iter_rows(min_row=2), 2):
            budget.row("Parameters", index, len(cells))
            if not cells or cells[0].value is None:
                continue
            eid = _element_id(cells[0].value)
            if eid in rows:
                raise ValueError(f"Duplicate element row for id {eid}.")
            if any(c.data_type == "f" for c in cells):
                raise ValueError(f"Element {eid} contains a formula. Paste literal values into the Parameters sheet.")
            rows[eid] = [c.value for c in cells[len(FIXED):len(headers)]]
            fixed[eid] = [c.value for c in cells[1:len(FIXED)]]
        metadata = {}
        modern = MODEL_SHEET in wb.sheetnames
        if modern:
            sheet = wb[MODEL_SHEET]
            if sheet["A1"].value != SCHEMA or sheet["B1"].value != target.get("modelKey"):
                raise ValueError("This workbook was exported from a different Revit model or uses unknown metadata.")
            expected_headers = ["id", "uniqueId", *[label for n in names for label in (n, n + " baseline")]]
            if [c.value for c in sheet[2]] != expected_headers:
                raise ValueError("Parameter headers differ from the exported workbook.")
            seen_uids = set()
            for data in sheet.iter_rows(min_row=3, values_only=True):
                eid, uid = _element_id(data[0]), data[1]
                if eid in metadata or not isinstance(uid, str) or not uid or uid in seen_uids:
                    raise ValueError("Workbook element identity is missing or duplicated.")
                seen_uids.add(uid)
                columns = [(json.loads(data[2 + 2 * i]) if data[2 + 2 * i] else None, data[3 + 2 * i])
                           for i in range(len(names))]
                metadata[eid] = (uid, columns)
            if rows.keys() - metadata.keys():
                raise ValueError("A row has an ID that is absent from the source workbook metadata.")
        elif not explicit_target:
            raise ValueError("Legacy workbook has no source identity. Confirm its model with document_id from ping, or export a new workbook.")
        return names, rows, fixed, metadata, modern
    finally:
        wb.close()


def params_from_excel(call: Callable[..., Any], params: dict[str, Any], target: dict[str, Any]) -> dict[str, Any]:
    path = _xlsx_path(params, must_exist=True)
    dry = flag(params, "dry_run")
    names, rows, fixed, metadata, modern = _parameters_sheet(path, target, "document_id" in params)

    current = {}
    groups: dict[tuple[str, ...], list[str]] = {}
    for eid in rows:
        selectors = tuple(d["key"] if d else name for name, (d, _) in zip(names, metadata[eid][1])) if modern else tuple(names)
        groups.setdefault(selectors, []).append(eid)
    for selectors, ids in groups.items():
        for i in range(0, len(ids), 1000):
            chunk = ids[i:i + 1000]
            refs = {"unique_ids": [metadata[eid][0] for eid in chunk]} if modern else {"ids": chunk}
            got = call("get_elements", {**refs, "params": list(selectors), "parameter_details": True,
                                        "allow_missing": True, "location": False, "limit": 1000})
            for e in got["elements"]:
                key = e["uniqueId"] if modern else str(e["id"])
                current[key] = (e, selectors)

    ops, sample, missing, conflicts = [], [], [], []
    for eid, values in rows.items():
        uid, baselines = metadata[eid] if modern else (None, [(None, None)] * len(names))
        found = current.get(uid or eid)
        if found is None:
            missing.append(eid)
            continue
        e, selectors = found
        if not modern and fixed[eid] != [e.get("category"), e.get("family"), e.get("type") or e.get("name"), e.get("level")]:
            conflicts.append({"id": eid, "reason": "Legacy workbook element category, family, type, or level differs."})
            continue
        changed, expected, detail = {}, {}, {}
        for name, selector, value, (saved, baseline) in zip(names, selectors, values, baselines):
            live = e["parameterInfo"].get(selector)
            kind = (saved or live or {}).get("kind", "String")
            before = _raw(e["params"].get(selector), kind)
            try:
                if (modern and _same(value, baseline, kind)) or _same(value, before, kind):
                    continue
                if not live or (modern and saved != live) or live["readOnly"] or live["target"] != "instance":
                    raise ValueError("Parameter identity or writability changed, or this is a type/reference-only cell.")
                if modern and not _same(before, baseline, kind):
                    raise ValueError("The model value changed since export. Re-export or reconcile the cell before importing.")
                if live["key"] in changed:
                    raise ValueError("Several columns resolve to the same parameter.")
                new = _typed(value, kind)
                if new is None and kind != "ElementId":
                    raise ValueError("This parameter requires a value.")
                changed[live["key"]] = new
                expected[live["key"]] = before
                detail[name] = {"model": before, "excel": value}
            except ValueError as exc:
                conflicts.append({"id": eid, "parameter": name, "reason": str(exc)})
        if changed:
            refs = {"unique_ids": [uid]} if modern else {"ids": [eid]}
            ops.append({"op": "set_parameters", **refs, "values": changed, "expected": expected})
            if len(sample) < 20:
                sample.append({"id": eid, "changes": detail})

    applied = 0
    results = []
    if not conflicts:
        for i in range(0, len(ops), MAX_OPS):
            batch = ops[i:i + MAX_OPS]
            request = {"operations": batch, "dry_run": dry}
            if params.get("operation_id"):
                content = json.dumps(request, sort_keys=True, allow_nan=False)
                request["operation_id"] = hashlib.sha256(f"{params['operation_id']}\n{content}".encode()).hexdigest()
            try:
                res = call("edit", request, timeout=EDIT_TIMEOUT)
            except BridgeError as exc:
                results.append({"error": str(exc), "state": "unknown" if exc.operation_id else "failed",
                                "operationId": exc.operation_id, "pid": exc.pid, "planned": len(batch)})
                break
            summary = ("applied", "dryRun", "previewSucceeded", "permission", "reason", "failedAt", "error", "errors",
                       "regenerationError", "trialCommit", "revitErrors", "warnings", "operationId", "pid", "state")
            results.append({"planned": len(batch), **{k: res[k] for k in summary if k in res}})
            if res.get("applied"):
                applied += len(batch)
            elif not (dry and res.get("previewSucceeded")):
                break
    return {"changes": len(ops), "elementsPlanned": len(ops), "elementsChanged": applied, "dryRun": dry,
            "batches": results, "sample": sample, "missingIds": missing, "conflicts": conflicts,
            "note": "Resolve the conflicts before applying this workbook." if conflicts
                    else "Each committed batch of up to 500 rows is a separate Undo step."}


# ------------------------------------------------------------------ Excel -> model

SHEETS: dict[str, tuple[list[str], list[Any], str]] = {
    "Levels": (["name", "elevation", "plan"], ["L1", 4000, "structural"],
               "Existing names are skipped. plan: none / structural / floor / ceiling (TRUE = structural)."),
    "Grids": (["name", "x1", "y1", "x2", "y2", "xm", "ym"], ["A", 0, -2000, 0, 20000, None, None],
              "Straight grid from (x1,y1) to (x2,y2); give xm,ym (a point on the arc) for a curved one."),
    "Columns": (["type", "base_level", "top_level", "x", "y", "rotation", "base_offset", "top_offset"],
                ["KTR15-S-C9 450x450", "L0", "L1", 0, 0, 0, 0, 0], "One row per column; rotation in degrees."),
    "Beams": (["type", "level", "x1", "y1", "x2", "y2", "z1", "z2"], ["KTR15-S-B 300x600", "L1", 0, 0, 6000, 0, None, None],
              "Top of beam at the level; give z1/z2 (absolute) for a sloped beam."),
    "Walls": (["type", "level", "top_level", "height", "x1", "y1", "x2", "y2"], ["Generic - 200mm", "L0", "L1", None, 0, 0, 6000, 0],
              "Give top_level or height."),
    "Slabs": (["type", "level", "boundary", "offset"], ["KTR15-S-Slab Flat 200m", "L1", "0,0; 6000,0; 6000,6000; 0,6000", 0],
              "boundary: corner points 'x,y; x,y; ...' in order."),
    "Footings": (["type", "level", "x", "y", "rotation"], ["F1 2000x2000x600", "L0", 0, 0, 0], "One row per isolated footing."),
}


def excel_template(params: dict[str, Any]) -> dict[str, Any]:
    path = _xlsx_path(params, must_exist=False)
    wb = Workbook()
    info = wb.active
    info.title = "Read me"
    lines = ["Fill the sheets you need (leave the others empty), then run excel_to_model with this file.",
             "All lengths and coordinates in mm (internal origin, like the Revit coordinates Claude reads).",
             "Types, levels and grid names must exist in the model or be created on the Levels sheet of the same file.",
             "Rows are read from row 2; the example rows below are NOT in the input sheets.", ""]
    for i, t in enumerate(lines, 1):
        info.cell(row=i, column=1, value=t)
    r = len(lines) + 1
    for name, (headers, example, note) in SHEETS.items():
        ws = wb.create_sheet(name)
        _header(ws, headers)
        info.cell(row=r, column=1, value=name).font = Font(bold=True)
        info.cell(row=r, column=2, value=note)
        info.cell(row=r + 1, column=1, value="example:")
        for c, v in enumerate(example, 2):
            info.cell(row=r + 1, column=c, value=v)
        r += 3
    info.column_dimensions["A"].width = 14
    info.column_dimensions["B"].width = 70
    save_workbook(wb, path, params)
    return {"file": str(path), "sheets": wb.sheetnames}


def _rows(wb, name: str, budget: CellBudget | None = None) -> list[tuple[int, dict[str, Any]]]:
    budget = budget or getattr(wb, "_mcp_budget", None) or CellBudget()
    if name not in wb.sheetnames:
        return []
    ws = wb[name]
    headers = [str(c.value).strip().lower() if c.value is not None else "" for c in ws[1]]
    named = [h for h in headers if h]
    if len(named) != len(set(named)):
        raise ValueError(f"{name}: duplicate column headers.")
    out = []
    for i, r in enumerate(ws.iter_rows(min_row=2, values_only=True), 2):
        budget.row(name, i, len(r) if r else 0)
        if r and any(v not in (None, "") for v in r):
            out.append((i, dict(zip(headers, r))))
    return out


def _need(row: dict[str, Any], key: str, where: str) -> Any:
    v = row.get(key)
    if v in (None, ""):
        raise ValueError(f"{where}: column '{key}' is empty.")
    return v


def _num(row: dict[str, Any], key: str, where: str, default: float | None = None) -> float:
    v = row.get(key)
    if v in (None, ""):
        if default is not None:
            return default
        raise ValueError(f"{where}: column '{key}' is empty.")
    try:
        number = float(v)
        if isinstance(v, bool) or not math.isfinite(number):
            raise ValueError()
        return number
    except (TypeError, ValueError):
        raise ValueError(f"{where}: '{key}' must be a number, not '{v}'.") from None


def excel_to_model(call: Callable[..., Any], params: dict[str, Any], target: dict[str, Any]) -> dict[str, Any]:
    if "document_id" in params and params["document_id"] != target["documentId"]:
        raise ValueError("The active model does not match document_id. Check ping before continuing.")
    params = {**params, "operation_id": params.get("operation_id") or uuid.uuid4().hex}
    path = _xlsx_path(params, must_exist=True)
    wb = load_workbook(path, data_only=True, read_only=True)
    wb._mcp_budget = CellBudget()   # one budget across every sheet this call reads
    ops: list[dict[str, Any]] = []
    counts: dict[str, int] = {}

    for i, row in _rows(wb, "Levels"):
        w = f"Levels row {i}"
        op = {"op": "level_create", "name": str(_need(row, "name", w)), "elevation": _num(row, "elevation", w), "if_exists": "skip"}
        plan = row.get("plan")
        if plan is True or str(plan).strip().lower() in ("true", "yes", "y", "1"):
            op["plan"] = "structural"   # Excel users type TRUE: make the usual structural plan
        elif plan not in (None, False, "") and str(plan).strip().lower() not in ("false", "no", "n", "0"):
            op["plan"] = str(plan).strip().lower()
        ops.append(op)
    for i, row in _rows(wb, "Grids"):
        w = f"Grids row {i}"
        op = {"op": "grid_create", "start": [_num(row, "x1", w), _num(row, "y1", w)], "end": [_num(row, "x2", w), _num(row, "y2", w)]}
        if row.get("name") not in (None, ""):
            op["name"] = str(row["name"])
        if row.get("xm") not in (None, "") or row.get("ym") not in (None, ""):
            op["mid"] = [_num(row, "xm", w), _num(row, "ym", w)]
        ops.append(op)

    groups: dict[tuple, dict[str, Any]] = {}
    for i, row in _rows(wb, "Columns"):
        w = f"Columns row {i}"
        key = ("col", str(_need(row, "type", w)), str(_need(row, "base_level", w)), str(_need(row, "top_level", w)),
               _num(row, "rotation", w, 0), _num(row, "base_offset", w, 0), _num(row, "top_offset", w, 0))
        g = groups.setdefault(key, {"op": "column_create", "type": key[1], "level": key[2], "top_level": key[3],
                                    "rotation": key[4], "base_offset": key[5], "top_offset": key[6], "points": []})
        g["points"].append([_num(row, "x", w), _num(row, "y", w)])
    for i, row in _rows(wb, "Beams"):
        w = f"Beams row {i}"
        key = ("beam", str(_need(row, "type", w)), str(_need(row, "level", w)))
        g = groups.setdefault(key, {"op": "beam_create", "type": key[1], "level": key[2], "beams": []})
        start, end = [_num(row, "x1", w), _num(row, "y1", w)], [_num(row, "x2", w), _num(row, "y2", w)]
        if row.get("z1") not in (None, "") or row.get("z2") not in (None, ""):
            start.append(_num(row, "z1", w)); end.append(_num(row, "z2", w))
        g["beams"].append({"start": start, "end": end})
    for i, row in _rows(wb, "Walls"):
        w = f"Walls row {i}"
        top = row.get("top_level")
        height = row.get("height")
        if top in (None, "") and height in (None, ""):
            raise ValueError(f"{w}: give top_level or height.")
        key = ("wall", str(_need(row, "type", w)), str(_need(row, "level", w)), str(top or ""), str(height or ""))
        g = groups.setdefault(key, {"op": "wall_create", "type": key[1], "level": key[2], "walls": []})
        if top not in (None, ""):
            g["top_level"] = str(top)
        else:
            g["height"] = _num(row, "height", w)
        g["walls"].append({"start": [_num(row, "x1", w), _num(row, "y1", w)], "end": [_num(row, "x2", w), _num(row, "y2", w)]})
    for i, row in _rows(wb, "Footings"):
        w = f"Footings row {i}"
        key = ("ftg", str(_need(row, "type", w)), str(_need(row, "level", w)), _num(row, "rotation", w, 0))
        g = groups.setdefault(key, {"op": "footing_create", "type": key[1], "level": key[2], "rotation": key[3], "points": []})
        g["points"].append([_num(row, "x", w), _num(row, "y", w)])
    for group in groups.values():
        key = next(k for k in ("points", "beams", "walls") if k in group)
        items = group[key]
        for start in range(0, len(items), 2000):
            ops.append({**group, key: items[start:start + 2000]})

    for i, row in _rows(wb, "Slabs"):
        w = f"Slabs row {i}"
        pts = []
        for pair in str(_need(row, "boundary", w)).replace("\n", ";").split(";"):
            if pair.strip():
                xy = [s.strip() for s in pair.split(",")]
                if len(xy) != 2:
                    raise ValueError(f"{w}: boundary point '{pair.strip()}' must be 'x,y'.")
                try:
                    pts.append([float(xy[0]), float(xy[1])])
                except ValueError:
                    raise ValueError(f"{w}: boundary point '{pair.strip()}' is not two numbers.") from None
        op = {"op": "floor_create", "type": str(_need(row, "type", w)), "level": str(_need(row, "level", w)), "boundary": pts}
        if row.get("offset") not in (None, ""):
            op["offset"] = _num(row, "offset", w)
        ops.append(op)

    for op in ops:
        counts[op["op"]] = counts.get(op["op"], 0) + max(1, len(op.get("points", op.get("beams", op.get("walls", [])))))
    if not ops:
        return {"operations": 0, "note": "The workbook has no input rows."}
    if len(ops) > MAX_OPS:
        raise ValueError(f"{len(ops)} operations: split the workbook (the limit is {MAX_OPS} per batch).")
    request = {"operations": ops, "dry_run": flag(params, "dry_run"), "units": "mm", "operation_id": params["operation_id"]}
    res = call("edit", request, timeout=EDIT_TIMEOUT)
    return {"elementsByOperation": counts, "result": res}
