"""Room data and finishes workbook: reads rooms from Revit through the bridge and writes one .xlsx on this computer.

Every number comes from read actions (room_finishes, arch_qa); this module only lays them out, adds totals as Excel
formulas and writes the file. Finishes are the room parameters the architect filled in; nothing is chosen or priced here.
"""
from __future__ import annotations

import datetime as _dt
from typing import Any, Callable

from openpyxl import Workbook
from openpyxl.styles import Font

from qs_report import INT, NUM, WARN_FILL, _table
from xlsx_util import flag, literal_cell, save_workbook, xlsx_path


def build(call: Callable[..., Any], params: dict[str, Any]) -> dict[str, Any]:
    path = xlsx_path(params, must_exist=False)
    scope = {k: params[k] for k in ("level",) if params.get(k)}
    info = call("get_project_info")
    rf = call("room_finishes", {**scope, "limit": int(params.get("limit") or 2000)}, timeout=600)
    qa = call("arch_qa", {**scope, "limit": 200}, timeout=300) if flag(params, "qa", True) else None
    rooms = rf.get("rooms") or []

    wb = Workbook()
    summary = wb.active
    summary.title = "Summary"

    # ---------------- room data
    ws = wb.create_sheet("Rooms")
    rows = [[r["id"], r.get("number"), r.get("name"), r.get("level"), r.get("areaM2"), r.get("perimeter"), r.get("height"),
             r.get("department"), r.get("occupancy")] for r in rooms]
    _table(ws, ["Id", "Number", "Name", "Level", "Area m2", "Perimeter", "Height", "Department", "Occupancy"], rows,
           totals={"Area m2": ""}, formats={"Id": "0", "Area m2": NUM})

    # ---------------- finishes with measured areas
    ws = wb.create_sheet("Finishes")
    frow = []
    for r in rooms:
        f, m = r.get("finishes") or {}, r.get("measured") or {}
        frow.append([r.get("number"), r.get("name"), r.get("level"), f.get("floor"), m.get("floorM2"), f.get("ceiling"), m.get("ceilingM2"),
                     f.get("wall"), m.get("wallGrossM2"), m.get("openingsM2"), m.get("wallNetM2"), f.get("base"), r.get("perimeter"),
                     m.get("openingsWithoutSize")])
    fin_headers = ["Number", "Name", "Level", "Floor finish", "Floor m2", "Ceiling finish", "Ceiling m2", "Wall finish", "Wall gross m2",
                   "Openings m2", "Wall net m2", "Base finish", "Perimeter", "Openings without size"]
    _table(ws, fin_headers, frow, totals={"Floor m2": "", "Ceiling m2": "", "Wall gross m2": "", "Openings m2": "", "Wall net m2": ""},
           formats={"Floor m2": NUM, "Ceiling m2": NUM, "Wall gross m2": NUM, "Openings m2": NUM, "Wall net m2": NUM, "Openings without size": INT})
    n = len(frow) + 1

    # ---------------- totals per finish (formulas over the Finishes sheet)
    ws = wb.create_sheet("By finish")
    r0 = 1
    for title, fcol, acol, key in (("Floor finishes", "D", "E", "floor"), ("Ceiling finishes", "F", "G", "ceiling"), ("Wall finishes (net)", "H", "K", "wall")):
        names = sorted({(x.get("finishes") or {}).get(key) or "" for x in rooms})
        ws.cell(row=r0, column=1, value=title).font = Font(bold=True, size=12)
        ws.cell(row=r0 + 1, column=1, value="Finish").font = Font(bold=True)
        ws.cell(row=r0 + 1, column=2, value="m2").font = Font(bold=True)
        ws.cell(row=r0 + 1, column=3, value="Rooms").font = Font(bold=True)
        r = r0 + 2
        for name in names:
            literal_cell(ws, r, 1, name or "(none given)")
            crit = f'"{name}"' if name else '""'
            ws.cell(row=r, column=2, value=f"=SUMIF(Finishes!{fcol}2:{fcol}{n},{crit},Finishes!{acol}2:{acol}{n})").number_format = NUM
            ws.cell(row=r, column=3, value=f"=COUNTIF(Finishes!{fcol}2:{fcol}{n},{crit})").number_format = INT
            r += 1
        r0 = r + 1
    ws.column_dimensions["A"].width = 40

    # ---------------- wall faces by bounding type, and the openings
    agg: dict[tuple[str, str], float] = {}
    for r in rooms:
        for b in r.get("bounding") or []:
            k = (b.get("category") or "-", b.get("type") or "-")
            agg[k] = agg.get(k, 0) + (b.get("areaM2") or 0)
    ws = wb.create_sheet("Wall faces by type")
    _table(ws, ["Category", "Type", "Gross face m2"], [[k[0], k[1], round(v, 4)] for k, v in sorted(agg.items(), key=lambda kv: -kv[1])],
           totals={"Gross face m2": ""}, formats={"Gross face m2": NUM})
    ws = wb.create_sheet("Openings")
    orow = [[r.get("number"), r.get("name"), o.get("wall"), o["id"], o.get("category"), o.get("type"), o.get("width"), o.get("height"), o.get("areaM2")]
            for r in rooms for o in r.get("openings") or []]
    _table(ws, ["Room", "Name", "Wall", "Id", "Category", "Type", "Width", "Height", "Area m2"], orow,
           totals={"Area m2": ""}, formats={"Wall": "0", "Id": "0", "Area m2": NUM})

    # ---------------- QA
    warnings: list[str] = []
    if qa is not None:
        ws = wb.create_sheet("QA")
        qrow = []
        for issue in qa.get("items") or []:
            warnings.append(f"{issue['what']}: {issue['problem']} ({issue['count']})")
            for it in issue.get("items") or []:
                qrow.append([issue["what"], issue["problem"], it.get("id"), it.get("number") or it.get("mark"), it.get("name") or it.get("type"), it.get("level")])
        _table(ws, ["What", "Problem", "Id", "Number / mark", "Name / type", "Level"], qrow, formats={"Id": "0"})
    if rf.get("skipped"):
        warnings.append(f"{len(rf['skipped'])} room(s) not measured (unplaced, not enclosed or no geometry).")
    if rf.get("truncated"):
        warnings.append("More rooms than the limit: the workbook covers the first ones only.")

    ws = wb.create_sheet("Notes")
    _table(ws, ["Sheet", "Note"], [["Finishes", rf.get("note")], ["QA", (qa or {}).get("note")]])
    ws.column_dimensions["B"].width = 140

    # ---------------- summary
    proj = info.get("project") or {}
    s = summary
    s["A1"] = "Room data and finishes"
    s["A1"].font = Font(bold=True, size=16)
    meta = [("Project", proj.get("name")), ("Project number", proj.get("number")), ("Model", info.get("title")),
            ("Scope", ", ".join(f"{k}={v}" for k, v in scope.items()) or "All rooms"),
            ("Generated", _dt.datetime.now().strftime("%Y-%m-%d %H:%M")),
            ("Source", "Revit MCP bridge - room parameters as entered; areas measured from the room geometry at the finish faces")]
    for i, (k, v) in enumerate(meta, 3):
        s.cell(row=i, column=1, value=k).font = Font(bold=True)
        literal_cell(s, i, 2, v)
    r = 3 + len(meta) + 1
    for label, formula, fmt in (("Rooms", f"=COUNTA(Finishes!A2:A{n})", INT), ("Room area m2", f"=SUM(Rooms!E2:E{n})", NUM),
                                ("Floor m2", f"=SUM(Finishes!E2:E{n})", NUM), ("Ceiling m2", f"=SUM(Finishes!G2:G{n})", NUM),
                                ("Wall net m2", f"=SUM(Finishes!K2:K{n})", NUM)):
        s.cell(row=r, column=1, value=label).font = Font(bold=True)
        s.cell(row=r, column=2, value=formula).number_format = fmt
        r += 1
    if warnings:
        r += 1
        s.cell(row=r, column=1, value="Attention").font = Font(bold=True, size=12)
        for w in warnings:
            r += 1
            s.cell(row=r, column=1, value=w).fill = WARN_FILL
    s.column_dimensions["A"].width = 40
    s.column_dimensions["B"].width = 60

    save_workbook(wb, path, params)
    return {
        "path": str(path), "sheets": wb.sheetnames, "rooms": len(rooms), "skipped": len(rf.get("skipped") or []), "warnings": warnings,
        "note": "Room parameters as entered and areas measured from the room geometry; totals are Excel formulas.",
    }
