"""Bar bending schedule workbook: reads every bar set with rebar_list and writes one .xlsx on this computer.

Sheets: BBS (one row per bar set: member, level, mark, type, diameter, shape, number, length, A..J, total length,
weight), Summary (by diameter and by member category, totals as formulas), Notes.
"""
from __future__ import annotations

import datetime as _dt
from typing import Any, Callable

from openpyxl import Workbook

from qs_report import INT, NUM, _table
from xlsx_util import save_workbook, xlsx_path

LETTERS = list("ABCDEFGHJ")


def build(call: Callable[..., Any], params: dict[str, Any]) -> dict[str, Any]:
    path = xlsx_path(params, must_exist=False)
    query = {k: params[k] for k in ("hosts", "level", "view") if k in params}
    data = call("rebar_list", {**query, "limit": 50000})
    bars = data.get("bars", [])
    if data.get("truncated") or data.get("count", len(bars)) != len(bars):
        raise ValueError("The reinforcement selection is incomplete. Narrow the report by hosts, level or view before exporting.")
    if not bars:
        raise ValueError("The model (or the chosen hosts / level / view) has no reinforcement to schedule.")
    used = [x for x in LETTERS if any(x in (b.get("dims") or {}) for b in bars)]

    wb = Workbook()
    ws = wb.active
    ws.title = "BBS"
    headers = ["Member", "Category", "Level", "Bar mark", "Bar type", "Dia (mm)", "Shape", "No. of bars",
               "Length each (mm)"] + [f"{x} (mm)" for x in used] + ["Total length (m)", "Weight (kg)", "Element id"]
    order = sorted(bars, key=lambda b: (b.get("hostCategory") or "", b.get("hostLevel") or "", b.get("hostLabel") or "",
                                        b.get("host") or 0, -(b.get("diameterMm") or 0)))
    rows = []
    for b in order:
        dims = b.get("dims") or {}
        rows.append([f'{b.get("hostLabel") or ""} ({b.get("host")})', b.get("hostCategory"), b.get("hostLevel"), b.get("mark"),
                     b.get("barType"), b.get("diameterMm"), b.get("shape"), b.get("quantity"), b.get("barLengthMm")]
                    + [dims.get(x) for x in used] + [b.get("totalLengthM"), b.get("weightKg"), b.get("id")])
    _table(ws, headers, rows, totals={"No. of bars": "sum", "Total length (m)": "sum", "Weight (kg)": "sum"},
           formats={"No. of bars": INT, "Length each (mm)": INT, "Total length (m)": NUM, "Weight (kg)": NUM,
                    **{f"{x} (mm)": INT for x in used}})

    s = wb.create_sheet("Summary")
    by_dia = data.get("byDiameter", [])
    nxt = _table(s, ["Dia (mm)", "No. of bars", "Total length (m)", "Weight (kg)"],
                 [[d["diameterMm"], d["bars"], d["lengthM"], d["weightKg"]] for d in by_dia],
                 totals={"No. of bars": "sum", "Total length (m)": "sum", "Weight (kg)": "sum"},
                 formats={"No. of bars": INT, "Total length (m)": NUM, "Weight (kg)": NUM})
    by_cat: dict[str, float] = {}
    for b in bars:
        by_cat[b.get("hostCategory") or "(no host)"] = by_cat.get(b.get("hostCategory") or "(no host)", 0) + (b.get("weightKg") or 0)
    _table(s, ["Member category", "Weight (kg)"], [[k, round(v, 1)] for k, v in sorted(by_cat.items())],
           totals={"Weight (kg)": "sum"}, formats={"Weight (kg)": NUM}, start_row=nxt)

    n = wb.create_sheet("Notes")
    for r, line in enumerate([
        f"Bar bending schedule written {_dt.datetime.now():%Y-%m-%d %H:%M} from the model open in Revit.",
        "Lengths, shapes and dimensions A..J are Revit's own values for each bar set (rebar and area / path systems).",
        "Weight = total length x d^2 / 162.2 kg/m (steel 7850 kg/m3, nominal diameter).",
        "Check bar marks and shape codes against your detailing standard (e.g. BS 8666) before issue.",
    ], 1):
        n.cell(row=r, column=1, value=line)
    n.column_dimensions["A"].width = 110

    save_workbook(wb, path, params)
    return {"file": str(path), "barSets": len(bars), "totalWeightKg": data.get("totalWeightKg"), "byDiameter": by_dia}
