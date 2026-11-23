"""QS workbook: reads quantities from Revit through the bridge and writes one .xlsx on this computer.

Every number comes from a read action (takeoff, material_takeoff, steel_takeoff, rebar_takeoff, formwork_takeoff,
check_materials, find_overlaps); this module only lays them out, adds totals as Excel formulas and writes the file.
"""
from __future__ import annotations

import datetime as _dt
import math
from typing import Any, Callable

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

from xlsx_util import flag, literal_cell, save_workbook, style_header, xlsx_path

TOTAL_FILL = PatternFill("solid", fgColor="D9E1F2")
WARN_FILL = PatternFill("solid", fgColor="FCE4D6")
THIN = Side(style="thin", color="B4B4B4")
NUM = "#,##0.00"
INT = "#,##0"


def _table(ws, headers: list[str], rows: list[list[Any]], totals: dict[str, str] | None = None,
           formats: dict[str, str] | None = None, start_row: int = 1) -> int:
    """Write a table with a styled header, borders and (optionally) a SUM total row. Returns the next free row."""
    formats = formats or {}
    style_header(ws, headers, start_row)
    for c in range(1, len(headers) + 1):
        ws.cell(row=start_row, column=c).alignment = Alignment(wrap_text=True, vertical="center")
    for r, row in enumerate(rows, start_row + 1):
        for c, v in enumerate(row, 1):
            cell = literal_cell(ws, r, c, v)
            cell.border = Border(bottom=THIN)
            if headers[c - 1] in formats:
                cell.number_format = formats[headers[c - 1]]
    end = start_row + len(rows)
    if totals is not None and rows:
        r = end + 1
        ws.cell(row=r, column=1, value="TOTAL").font = Font(bold=True)
        for c, h in enumerate(headers, 1):
            ws.cell(row=r, column=c).fill = TOTAL_FILL
            if h in totals:
                col = get_column_letter(c)
                cell = ws.cell(row=r, column=c, value=f"=SUM({col}{start_row + 1}:{col}{end})")
                cell.font = Font(bold=True)
                cell.number_format = formats.get(h, NUM)
        end = r
    if start_row == 1:
        ws.freeze_panes = "A2"
        if rows:
            ws.auto_filter.ref = f"A1:{get_column_letter(len(headers))}{start_row + len(rows)}"
    for c, h in enumerate(headers, 1):
        width = max([len(str(h))] + [len(str(row[c - 1])) for row in rows if c - 1 < len(row) and row[c - 1] is not None])
        ws.column_dimensions[get_column_letter(c)].width = min(max(10, width + 2), 60)
    return end + 2


def build(call: Callable[..., Any], params: dict[str, Any]) -> dict[str, Any]:
    path = xlsx_path(params, must_exist=False)

    scope = {k: params[k] for k in ("level", "view") if params.get(k)}
    cats = {"category": params["category"]} if params.get("category") else {}
    steel_words = {"family_contains": params["family_contains"]} if params.get("family_contains") else {}
    density = float(params.get("density", 7850))
    if not math.isfinite(density) or density <= 0:
        raise ValueError("density must be positive and finite.")
    overrides = params.get("concrete_materials", [])
    if not isinstance(overrides, list) or any(not isinstance(name, str) or not name.strip() for name in overrides):
        raise ValueError("concrete_materials must contain exact material names.")
    concrete_names = {name.strip().casefold() for name in overrides}

    info = call("get_project_info")
    conc = call("material_takeoff", {**scope, **cats, **steel_words, "group_by": "level", "exclude_steel": True})
    elems = call("takeoff", {**scope, **cats, "group_by": "type_level"})
    steel = call("steel_takeoff", {**scope, **cats, **steel_words, "density": density})
    rebar_scope = {"host_category": cats["category"]} if cats else {}
    rebar = call("rebar_takeoff", {**scope, **rebar_scope, "group_by": "diameter_host_category"})
    form = call("formwork_takeoff", {**scope, **cats, **steel_words, "group_by": "type_level"})
    qa = call("check_materials", {**scope, **cats, **steel_words})
    overlaps = None
    if flag(params, "check_overlaps", True):
        overlaps = call("find_overlaps", {**scope, **cats, **steel_words, "max_seconds": 25, "limit": 300}, timeout=110)

    wb = Workbook()
    summary = wb.active
    summary.title = "Summary"

    # ---------------- Concrete by grade and level
    ws = wb.create_sheet("Concrete")
    crow, other = [], []
    for row in conc["rows"]:
        kind = row.get("materialKind", "unclassified")
        values = [row["category"], row.get("level"), row["material"], row["elements"], row["volume_m3"], row["area_m2"]]
        if kind == "concrete" or row["material"].strip().casefold() in concrete_names:
            crow.append(values)
        else:
            other.append([*values, kind])
    _table(ws, ["Category", "Level", "Material", "Elements", "Volume m3", "Area m2"], crow,
           totals={"Volume m3": "", "Area m2": ""}, formats={"Volume m3": NUM, "Area m2": NUM, "Elements": INT})
    n_conc = len(crow)
    if other:
        ws = wb.create_sheet("Other Materials")
        _table(ws, ["Category", "Level", "Material", "Elements", "Volume m3", "Area m2", "Classification"], other,
               totals={"Volume m3": ""}, formats={"Volume m3": NUM, "Area m2": NUM, "Elements": INT})

    # ---------------- Elements by type and level
    ws = wb.create_sheet("Elements")
    erow = [[r["category"], r.get("type"), r.get("level"), r["count"], r.get("length_m"), r.get("area_m2"), r.get("volume_m3")]
            for r in elems["rows"]]
    _table(ws, ["Category", "Type", "Level", "Count", "Length m", "Area m2", "Volume m3"], erow,
           totals={"Count": "", "Length m": "", "Volume m3": ""},
           formats={"Count": INT, "Length m": NUM, "Area m2": NUM, "Volume m3": NUM})

    # ---------------- Steel
    ws = wb.create_sheet("Steel")
    srow = [[r["category"], r.get("type"), r["count"], r["length_m"], r["volume_m3"], r["weight_kg"], r.get("kg_per_m")]
            for r in steel["rows"]]
    _table(ws, ["Category", "Section", "Count", "Length m", "Volume m3", "Weight kg", "kg/m"], srow,
           totals={"Count": "", "Length m": "", "Volume m3": "", "Weight kg": ""},
           formats={"Count": INT, "Length m": NUM, "Volume m3": "0.0000", "Weight kg": NUM, "kg/m": NUM})
    n_steel = len(srow)

    # ---------------- Rebar
    ws = wb.create_sheet("Rebar")
    rrow = [[r["group"], r["rebarSets"], r["bars"], r["totalLength_m"], r["weight_kg"]] for r in rebar["rows"]]
    _table(ws, ["Diameter | host", "Rebar sets", "Bars", "Length m", "Weight kg"], rrow,
           totals={"Rebar sets": "", "Bars": "", "Length m": "", "Weight kg": ""},
           formats={"Rebar sets": INT, "Bars": INT, "Length m": NUM, "Weight kg": NUM})
    if not rrow:
        ws.cell(row=3, column=1, value="No reinforcement is modelled in this scope.")
    n_rebar = len(rrow)

    # ---------------- Formwork
    ws = wb.create_sheet("Formwork")
    frow = [[r["category"], r.get("type"), r.get("level"), r["count"], r["sides_m2"], r["soffit_m2"], r["top_m2"], r["formwork_m2"]]
            for r in form["rows"]]
    _table(ws, ["Category", "Type", "Level", "Count", "Sides m2", "Soffit m2", "Top m2 (not formed)", "Formwork m2"], frow,
           totals={"Count": "", "Sides m2": "", "Soffit m2": "", "Formwork m2": ""},
           formats={"Count": INT, "Sides m2": NUM, "Soffit m2": NUM, "Top m2 (not formed)": NUM, "Formwork m2": NUM})
    n_form = len(frow)

    # ---------------- QA
    ws = wb.create_sheet("QA")
    ws.cell(row=1, column=1, value="Material problems").font = Font(bold=True, size=12)
    qrow = [[r["category"], r["problem"], r["material"], r["elements"], r["volume_m3"], ", ".join(map(str, r["sampleIds"][:10]))]
            for r in qa["rows"]]
    nxt = _table(ws, ["Category", "Problem", "Material", "Elements", "Volume m3", "Sample element ids"], qrow,
                 totals={"Elements": "", "Volume m3": ""}, formats={"Elements": INT, "Volume m3": NUM}, start_row=2)
    if overlaps is not None:
        ws.cell(row=nxt, column=1, value="Unjoined overlaps (sum of measured pairs)").font = Font(bold=True, size=12)
        orow = [[g["categories"], g["pairs"], g.get("overlap_m3")] for g in overlaps["byCategories"]]
        nxt = _table(ws, ["Categories", "Pairs", "Overlap m3"], orow, totals={"Pairs": "", "Overlap m3": ""},
                     formats={"Pairs": INT, "Overlap m3": NUM}, start_row=nxt + 1)
        prow = [[x["a"], x["aLabel"], x["b"], x["bLabel"], x.get("level"), x.get("overlap_m3")] for x in overlaps["pairs"]]
        nxt = _table(ws, ["Element A", "A", "Element B", "B", "Level", "Overlap m3"], prow, formats={"Overlap m3": "0.0000"},
                     start_row=nxt)
        if overlaps.get("reviewPairs"):
            ws.cell(row=nxt, column=1, value="Overlap pairs for review").font = Font(bold=True, size=12)
            drow = [[d["a"]["id"], d["a"]["label"], d["b"]["id"], d["b"]["label"], d["kind"], d.get("overlap_m3"),
                     d["a"].get("dependentCount"), d["b"].get("dependentCount")] for d in overlaps["reviewPairs"]]
            _table(ws, ["Element A", "A", "Element B", "B", "Relationship", "Overlap m3", "A dependents", "B dependents"], drow,
                   formats={"Overlap m3": "0.0000"}, start_row=nxt + 1)

    # ---------------- Notes
    ws = wb.create_sheet("Notes")
    notes = [("Concrete", conc.get("note")), ("Concrete", "Classified by Revit structural asset, material class or concrete_materials."),
             ("Elements", elems.get("note")), ("Steel", steel.get("note")), ("Rebar", rebar.get("note")),
             ("Formwork", form.get("note")), ("QA", qa.get("note"))]
    if overlaps is not None:
        notes.append(("Overlaps", overlaps.get("note")))
    _table(ws, ["Sheet", "Note"], [[a, b] for a, b in notes if b])
    ws.column_dimensions["B"].width = 140

    # ---------------- Summary (formulas point at the other sheets, so edits there update it)
    proj = info.get("project") or {}
    s = summary
    s["A1"] = "Quantity take-off"
    s["A1"].font = Font(bold=True, size=16)
    scope_text = ", ".join(f"{key}={value}" for key, value in {**scope, **cats}.items()) or "Whole model, default categories by takeoff"
    meta = [("Project", proj.get("name")), ("Project number", proj.get("number")), ("Model", info.get("title")),
            ("Scope", scope_text), ("Generated", _dt.datetime.now().strftime("%Y-%m-%d %H:%M")),
            ("Source", "Revit MCP bridge - quantities as Revit computes them (net of joins)")]
    for i, (k, v) in enumerate(meta, 3):
        s.cell(row=i, column=1, value=k).font = Font(bold=True)
        literal_cell(s, i, 2, v)
    r = 3 + len(meta) + 1
    s.cell(row=r, column=1, value="Concrete by material").font = Font(bold=True, size=12)
    r += 1
    first = r
    for mat in sorted({row[2] for row in crow}):
        literal_cell(s, r, 1, mat)
        cell = s.cell(row=r, column=2, value=f'=SUMPRODUCT(--(Concrete!C2:C{n_conc + 1}=A{r}),Concrete!E2:E{n_conc + 1})')
        cell.number_format = NUM
        s.cell(row=r, column=3, value="m3")
        if "Cast-in-Place" in mat or "Default" in mat or "(no material" in mat:
            for c in (1, 2, 3):
                s.cell(row=r, column=c).fill = WARN_FILL
        r += 1
    s.cell(row=r, column=1, value="Total concrete").font = Font(bold=True)
    s.cell(row=r, column=2, value=f"=SUM(B{first}:B{r - 1})" if n_conc else 0).number_format = NUM
    s.cell(row=r, column=3, value="m3")
    r += 2
    lines = [
        ("Structural steel", f"=SUM(Steel!F2:F{n_steel + 1})/1000" if n_steel else 0, "t"),
        ("Reinforcement", f"=SUM(Rebar!E2:E{n_rebar + 1})/1000" if n_rebar else 0, "t"),
        ("Formwork", f"=SUM(Formwork!H2:H{n_form + 1})" if n_form else 0, "m2"),
        ("Elements with material problems", qa.get("elementsWithProblems"), "elements"),
    ]
    if overlaps is not None:
        lines.append(("Unjoined overlap (sum over pairs)", overlaps.get("doubleCounted_m3"), "m3"))
        if overlaps.get("reviewPairCount"):
            lines.append(("Overlap pairs for review", overlaps["reviewPairCount"], "pairs"))
    for k, v, unit in lines:
        s.cell(row=r, column=1, value=k).font = Font(bold=True)
        cell = s.cell(row=r, column=2, value=v)
        cell.number_format = NUM
        s.cell(row=r, column=3, value=unit)
        r += 1
    warnings = []
    if qa.get("elementsWithProblems"):
        warnings.append(f"{qa['elementsWithProblems']} element(s) have material problems (sheet QA): fix them before using the grade split.")
    if any(row[-1] == "unclassified" for row in other):
        warnings.append("Unclassified material is excluded from concrete totals. Review Other Materials.")
    if conc.get("measurementFailures"):
        warnings.append(f"{conc['measurementFailures']} material measurement(s) could not be completed.")
    if overlaps is not None and (overlaps.get("doubleCounted_m3") or 0) > 0:
        warnings.append(f"Measured unjoined pair volumes sum to {overlaps['doubleCounted_m3']} m3. Review the pairs in QA.")
    if overlaps is not None and overlaps.get("reviewPairCount"):
        warnings.append(f"{overlaps['reviewPairCount']} overlap pair(s) need review. See the QA sheet for each relationship.")
    if overlaps is not None and not overlaps.get("complete"):
        warnings.append("The overlap check is incomplete. QA totals cover the scanned portion only.")
    if overlaps is not None and (overlaps.get("unmeasuredPairs") or overlaps.get("geometrySkipped")):
        warnings.append("Some overlap volumes or elements could not be measured. See find_overlaps for details.")
    r += 1
    for w in warnings:
        cell = s.cell(row=r, column=1, value="! " + w)
        cell.fill = WARN_FILL
        r += 1
    s.column_dimensions["A"].width = 45
    s.column_dimensions["B"].width = 50

    save_workbook(wb, path, params)
    return {
        "file": str(path),
        "sheets": wb.sheetnames,
        "rows": {"concrete": n_conc, "otherMaterials": len(other), "elements": len(erow), "steel": n_steel, "rebar": n_rebar, "formwork": n_form,
                 "qa": len(qrow)},
        "totals": {
            "concrete_m3": round(sum(row[4] for row in crow), 2),
            "steel_t": steel.get("totalWeight_t"),
            "rebar_kg": rebar.get("totalWeight_kg"),
            "formwork_m2": form.get("totalFormwork_m2"),
            "elementsWithMaterialProblems": qa.get("elementsWithProblems"),
            "doubleCounted_m3": overlaps.get("doubleCounted_m3") if overlaps else None,
            "overlapCheckComplete": overlaps.get("complete") if overlaps else None,
        },
        "warnings": warnings,
    }
