"""MEP quantities workbook: reads MEP quantities from Revit through the bridge and writes one .xlsx on this computer.

Every number comes from a read action (mep_quantities, mep_insulation, electrical_circuits); this module only lays them
out, adds totals as Excel formulas and writes the file. Nothing is sized or priced here.
"""
from __future__ import annotations

import datetime as _dt
from typing import Any, Callable

from openpyxl import Workbook
from openpyxl.styles import Font

from qs_report import INT, NUM, WARN_FILL, _table
from xlsx_util import flag, literal_cell, save_workbook, xlsx_path

GROUPS = (("system", "By system"), ("size", "By size"), ("type", "By type"), ("level", "By level"))


def build(call: Callable[..., Any], params: dict[str, Any]) -> dict[str, Any]:
    path = xlsx_path(params, must_exist=False)
    scope = {k: params[k] for k in ("level", "view", "category", "link") if params.get(k)}
    info = call("get_project_info")

    quantities = {}
    for i, (by, _title) in enumerate(GROUPS):
        # the fittings / accessories / terminals / equipment counts once, with the first grouping
        quantities[by] = call("mep_quantities", {**scope, "group_by": by, "fittings": i == 0})
    insulation = None
    if flag(params, "insulation", True):
        ins_scope = {k: scope[k] for k in ("category", "link") if k in scope}
        insulation = call("mep_insulation", {**ins_scope, "missing_only": True, "limit": 2000}, timeout=300)
    electrical = None
    if flag(params, "electrical", True):
        electrical = call("electrical_circuits", {**({"link": scope["link"]} if "link" in scope else {}), "limit": 2000}, timeout=300)

    wb = Workbook()
    summary = wb.active
    summary.title = "Summary"
    sheets_rows: dict[str, int] = {}

    # ---------------- runs by system / size / type / level
    for by, title in GROUPS:
        ws = wb.create_sheet(title)
        rows = [[r["category"], r["key"], r["pieces"], r["lengthM"], r.get("surfaceAreaM2")] for r in quantities[by]["lengths"]]
        _table(ws, ["Category", by.capitalize(), "Pieces", "Length m", "Duct surface m2"], rows,
               totals={"Pieces": "", "Length m": "", "Duct surface m2": ""},
               formats={"Pieces": INT, "Length m": NUM, "Duct surface m2": NUM})
        ws.column_dimensions["A"].width = 22
        ws.column_dimensions["B"].width = 40
        sheets_rows[title] = len(rows)

    # ---------------- fittings, accessories, terminals, equipment, fixtures by type
    counts = quantities[GROUPS[0][0]].get("counts") or []
    ws = wb.create_sheet("Fittings and devices")
    crow = [[c["category"], t["type"], t["count"]] for c in counts for t in c.get("byType", [])]
    _table(ws, ["Category", "Type", "Count"], crow, totals={"Count": ""}, formats={"Count": INT})
    ws.column_dimensions["A"].width = 24
    ws.column_dimensions["B"].width = 50
    capped = [c["category"] for c in counts if sum(t["count"] for t in c.get("byType", [])) < c["count"]]

    # ---------------- insulation
    warnings: list[str] = []
    if insulation is not None:
        ws = wb.create_sheet("Insulation")
        irow = [[t["type"], t["elements"], t["runLength"]] for t in insulation.get("byTypeAndThickness", [])]
        nxt = _table(ws, ["Insulation / lining and thickness", "Elements", "Run length"], irow,
                     totals={"Elements": "", "Run length": ""}, formats={"Elements": INT, "Run length": NUM})
        ws.cell(row=nxt + 1, column=1, value="Elements without insulation").font = Font(bold=True, size=12)
        mrow = [[m["id"], m.get("category"), m.get("system"), m.get("size"), m.get("lining")] for m in insulation.get("items", [])]
        _table(ws, ["Id", "Category", "System", "Size", "Lining"], mrow, formats={"Id": "0"}, start_row=nxt + 2)
        ws.column_dimensions["A"].width = 40
        if insulation.get("withNeither"):
            warnings.append(f"{insulation['withNeither']} element(s) have neither insulation nor lining (check against the specification).")

    # ---------------- electrical
    if electrical is not None and (electrical.get("panelCount") or electrical.get("circuitCount") or electrical.get("notOnACircuit")):
        ws = wb.create_sheet("Panels")
        prow = [[p["id"], p.get("name"), p.get("type"), p.get("distributionSystem"), p.get("circuits"), p.get("connectedLoad"),
                 p.get("demandLoad"), p.get("demandCurrent"), p.get("mainsRating")] for p in electrical.get("panels", [])]
        _table(ws, ["Id", "Panel", "Type", "Distribution system", "Circuits", "Connected load", "Demand load", "Demand current", "Mains"],
               prow, totals={"Circuits": ""}, formats={"Id": "0", "Circuits": INT})
        ws = wb.create_sheet("Circuits")
        crow2 = [[c["id"], c.get("panel"), c.get("circuit"), c.get("kind"), c.get("loadName"), c.get("poles"), c.get("voltage"),
                  c.get("apparentLoad"), c.get("current"), c.get("rating"), c.get("wireSize"), c.get("length"), c.get("elementCount")]
                 for c in electrical.get("circuits", [])]
        _table(ws, ["Id", "Panel", "Circuit", "Kind", "Load name", "Poles", "Voltage", "Apparent load", "Current", "Rating", "Wire",
                    "Length", "Loads"], crow2, totals={"Loads": ""}, formats={"Id": "0", "Loads": INT})
        if electrical.get("notOnACircuit"):
            warnings.append(f"{electrical['notOnACircuit']} electrical load(s) are on no circuit.")
            ws = wb.create_sheet("Not on a circuit")
            _table(ws, ["Id", "Category", "Type", "Level"],
                   [[u["id"], u.get("category"), u.get("type"), u.get("level")] for u in electrical.get("unassigned", [])], formats={"Id": "0"})

    # ---------------- notes
    ws = wb.create_sheet("Notes")
    notes = [("Runs", quantities["system"].get("note"))]
    if insulation is not None:
        notes.append(("Insulation", insulation.get("note")))
    if electrical is not None:
        notes.append(("Electrical", electrical.get("note")))
    if capped:
        notes.append(("Fittings and devices", "Types listed per category are capped at 30; see the category totals on the Summary sheet."))
    _table(ws, ["Sheet", "Note"], [[a, b] for a, b in notes if b])
    ws.column_dimensions["B"].width = 140

    # ---------------- summary (lengths are formulas over the By system sheet, so edits there update it)
    proj = info.get("project") or {}
    s = summary
    s["A1"] = "MEP quantities"
    s["A1"].font = Font(bold=True, size=16)
    scope_text = ", ".join(f"{k}={v}" for k, v in scope.items()) or "Whole model: ducts, pipes, cable trays, conduits, flex"
    meta = [("Project", proj.get("name")), ("Project number", proj.get("number")), ("Model", info.get("title")),
            ("Scope", scope_text), ("Generated", _dt.datetime.now().strftime("%Y-%m-%d %H:%M")),
            ("Source", "Revit MCP bridge - lengths and counts as Revit computes them (centreline lengths, fittings counted, not measured)")]
    for i, (k, v) in enumerate(meta, 3):
        s.cell(row=i, column=1, value=k).font = Font(bold=True)
        literal_cell(s, i, 2, v)
    r = 3 + len(meta) + 1
    s.cell(row=r, column=1, value="Runs by category").font = Font(bold=True, size=12)
    s.cell(row=r, column=2, value="Length m").font = Font(bold=True)
    s.cell(row=r, column=3, value="Pieces").font = Font(bold=True)
    r += 1
    n = sheets_rows["By system"] + 1
    totals = {}
    for t in quantities["system"].get("totalsByCategory", []):
        literal_cell(s, r, 1, t["category"])
        s.cell(row=r, column=2, value=f"=SUMIF('By system'!A2:A{n},A{r},'By system'!D2:D{n})").number_format = NUM
        s.cell(row=r, column=3, value=f"=SUMIF('By system'!A2:A{n},A{r},'By system'!C2:C{n})").number_format = INT
        totals[t["category"]] = t["lengthM"]
        r += 1
    r += 1
    s.cell(row=r, column=1, value="Counts").font = Font(bold=True, size=12)
    r += 1
    for c in counts:
        literal_cell(s, r, 1, c["category"])
        s.cell(row=r, column=2, value=c["count"]).number_format = INT
        r += 1
    if warnings:
        r += 1
        s.cell(row=r, column=1, value="Attention").font = Font(bold=True, size=12)
        for w in warnings:
            r += 1
            s.cell(row=r, column=1, value=w).fill = WARN_FILL
    s.column_dimensions["A"].width = 34
    s.column_dimensions["B"].width = 60

    save_workbook(wb, path, params)
    return {
        "path": str(path), "sheets": wb.sheetnames, "totalsByCategoryM": totals,
        "counts": {c["category"]: c["count"] for c in counts}, "warnings": warnings,
        "note": "Lengths, counts, insulation and electrical values as Revit computes them; totals are Excel formulas.",
    }
