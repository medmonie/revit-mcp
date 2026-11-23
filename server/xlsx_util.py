"""Shared Excel helpers for the workbooks the server writes and reads (qs_report, excel_io)."""
from __future__ import annotations

import os
from pathlib import Path
from tempfile import NamedTemporaryFile
from typing import Any

from openpyxl.styles import Font, PatternFill
from openpyxl.utils import get_column_letter

HEAD_FILL = PatternFill("solid", fgColor="1F3864")
HEAD_FONT = Font(bold=True, color="FFFFFF")


def xlsx_path(params: dict[str, Any], must_exist: bool) -> Path:
    """params['path'] as a full .xlsx path; an existing file is refused unless overwrite is true (when writing)."""
    path = Path(str(params.get("path") or ""))
    if not path.is_absolute() or path.suffix.lower() != ".xlsx":
        raise ValueError("'path' must be a full path ending in .xlsx, e.g. C:\\Projects\\KTR15\\QS.xlsx")
    if must_exist and not path.exists():
        raise ValueError(f"'{path}' does not exist.")
    if not must_exist and path.exists() and not flag(params, "overwrite"):
        raise ValueError(f"'{path}' already exists. Pass overwrite: true to replace it.")
    return path


def flag(params: dict[str, Any], name: str, default: bool = False) -> bool:
    value = params.get(name, default)
    if not isinstance(value, bool):
        raise ValueError(f"{name} must be true or false.")
    return value


def literal_cell(ws, row: int, column: int, value: Any):
    cell = ws.cell(row=row, column=column, value=value)
    if isinstance(value, str):
        cell.data_type = "s"
    return cell


def save_workbook(wb, path: Path, params: dict[str, Any]) -> None:
    overwrite = flag(params, "overwrite")
    path.parent.mkdir(parents=True, exist_ok=True)
    with NamedTemporaryFile(dir=path.parent, suffix=".xlsx", delete=False) as stage:
        temporary = Path(stage.name)
    try:
        wb.save(temporary)
        if overwrite:
            os.replace(temporary, path)
        elif os.name == "nt":
            os.rename(temporary, path)  # Windows rename rejects an existing destination.
        else:
            os.link(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def style_header(ws, headers: list[str], row: int = 1) -> None:
    """Write a header row in the house style (white bold on dark blue)."""
    for c, h in enumerate(headers, 1):
        cell = literal_cell(ws, row, c, h)
        cell.fill, cell.font = HEAD_FILL, HEAD_FONT


def header(ws, headers: list[str], key_columns: int = 0) -> None:
    """A header row with column widths from the header text, frozen below it (and right of the key columns)."""
    style_header(ws, headers)
    for c, h in enumerate(headers, 1):
        ws.column_dimensions[get_column_letter(c)].width = max(12, min(40, len(h) + 4))
    ws.freeze_panes = ws.cell(row=2, column=key_columns + 1) if key_columns else "A2"
