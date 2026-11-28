"""Shared Excel helpers for the workbooks the server writes and reads (qs_report, excel_io)."""
from __future__ import annotations

import os
from pathlib import Path
from tempfile import NamedTemporaryFile
from typing import Any
from request_lifetime import checkpoint

from openpyxl.styles import Font, PatternFill
from openpyxl.utils import get_column_letter

HEAD_FILL = PatternFill("solid", fgColor="1F3864")
HEAD_FONT = Font(bold=True, color="FFFFFF")


def allowed_roots() -> list[str]:
    """Folders the workbooks may be read from and written to: one per line in allowed_roots.txt next to bridge.json
    (# starts a comment). No file, or no folder in it: no restriction. The add-in applies the same file to every path
    it is given (plugin/Core/PathPolicy.cs)."""
    from bridge_client import BridgeError, bridge_file
    try:
        listing = bridge_file().parent / "allowed_roots.txt"
    except BridgeError:     # no data folder on this machine: there is no list
        return []
    try:
        lines = listing.read_text(encoding="utf-8-sig").splitlines()
    except FileNotFoundError:
        return []
    except (OSError, UnicodeError) as exc:      # an unreadable list must not open everything up
        raise ValueError(f"The allowed folders list {listing} cannot be read: {exc}") from None
    roots = []
    for line in lines:
        line = line.strip()
        if line and not line.startswith("#"):
            roots.append(os.path.normcase(os.path.abspath(os.path.expandvars(line))).rstrip("\\/") + os.sep)
    return roots


def check_allowed(path: Path) -> None:
    text = str(path)
    if text.startswith(("\\\\?\\", "\\\\.\\")):
        raise ValueError("'path' must be an ordinary file path, not a device path.")
    roots = allowed_roots()
    if not roots:
        return
    candidate = os.path.normcase(os.path.abspath(text)).rstrip("\\/") + os.sep
    if not any(candidate.startswith(root) for root in roots):
        raise ValueError(f"'{path}' is outside the folders Claude may use on this PC ({'; '.join(r.rstrip(os.sep) for r in roots)}). "
                         "To allow another folder, add it to allowed_roots.txt in %LOCALAPPDATA%\\RevitMcpBridge.")


def xlsx_path(params: dict[str, Any], must_exist: bool) -> Path:
    """params['path'] as a full .xlsx path; an existing file is refused unless overwrite is true (when writing)."""
    path = Path(str(params.get("path") or ""))
    if not path.is_absolute() or path.suffix.lower() != ".xlsx":
        raise ValueError("'path' must be a full path ending in .xlsx, e.g. C:\\Projects\\KTR15\\QS.xlsx")
    check_allowed(path)
    if must_exist and not path.exists():
        raise ValueError(f"'{path}' does not exist.")
    if must_exist:
        check_input_size(path)
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
    checkpoint()
    overwrite = flag(params, "overwrite")
    path.parent.mkdir(parents=True, exist_ok=True)
    with NamedTemporaryFile(dir=path.parent, suffix=".xlsx", delete=False) as stage:
        temporary = Path(stage.name)
    try:
        wb.save(temporary)
        checkpoint()
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


# Budgets for workbooks read as input, checked before openpyxl expands anything.
MAX_INPUT_BYTES = 25 * 1024 * 1024          # the .xlsx file itself
MAX_EXPANDED_BYTES = 200 * 1024 * 1024      # all parts unzipped
MAX_PARTS = 2000                            # zip entries
MAX_RATIO = 200                             # expanded / compressed per part (zip bombs)
MAX_ROWS = 50_000                           # rows per sheet read
MAX_CELLS = 2_000_000                       # cells read in one call


def check_input_size(path: Path) -> None:
    """Refuse a workbook that is too large, or expands too far, before it is opened."""
    import zipfile
    size = path.stat().st_size
    if size > MAX_INPUT_BYTES:
        raise ValueError(f"'{path.name}' is {size / 1048576:.1f} MB; the limit for an input workbook is {MAX_INPUT_BYTES // 1048576} MB. Split it.")
    try:
        with zipfile.ZipFile(path) as z:
            parts = z.infolist()
    except zipfile.BadZipFile:
        raise ValueError(f"'{path.name}' is not a valid .xlsx file.") from None
    if len(parts) > MAX_PARTS:
        raise ValueError(f"'{path.name}' has {len(parts)} parts; the limit is {MAX_PARTS}.")
    total = 0
    for part in parts:
        total += part.file_size
        if part.compress_size and part.file_size / part.compress_size > MAX_RATIO and part.file_size > 1048576:
            raise ValueError(f"'{path.name}': part {part.filename} expands {part.file_size // part.compress_size}x; refused as unsafe.")
    if total > MAX_EXPANDED_BYTES:
        raise ValueError(f"'{path.name}' expands to {total / 1048576:.0f} MB; the limit is {MAX_EXPANDED_BYTES // 1048576} MB. Split it.")


class CellBudget:
    """Counts rows and cells while a workbook is streamed, so reading stops before anything is sent to Revit."""

    def __init__(self, max_cells: int = MAX_CELLS, max_rows: int = MAX_ROWS):
        self.cells = 0
        self.max_cells = max_cells
        self.max_rows = max_rows

    def row(self, sheet: str, index: int, width: int) -> None:
        if index - 1 > self.max_rows:
            raise ValueError(f"{sheet}: more than {self.max_rows} rows; split the workbook. Nothing was sent to Revit.")
        self.cells += width
        if self.cells > self.max_cells:
            raise ValueError(f"The workbook has more than {self.max_cells} cells to read; split it. Nothing was sent to Revit.")
