"""Writes docs/ACTIONS.md from server/catalog.py.

    python tools/gen_docs.py            rewrite the file
    python tools/gen_docs.py --check    exit 1 if the file is out of date (used by tests/smoke_test.py)
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "server"))
import catalog  # noqa: E402

TARGET = ROOT / "docs" / "ACTIONS.md"


def _cell(text: str) -> str:
    return text.replace("|", "\\|")


def _table(group: catalog.Group) -> list[str]:
    rows = ["| Name | Parameters | What it does |", "|---|---|---|"]
    for name, (summary, params) in group.items():
        cells = "<br>".join(f"`{p}` {_cell(d)}".rstrip() for p, d in params.items()) or "-"
        rows.append(f"| `{name}` | {cells} | {_cell(summary)} |")
    return rows


def render() -> str:
    out = ["# Actions and edit operations",
           "",
           "Generated from `server/catalog.py` by `tools/gen_docs.py` - do not edit by hand.",
           "",
           "Everything goes through one MCP tool: `revit(action, params)`. A parameter ending in `?` is optional.",
           "",
           catalog.UNITS_NOTE,
           "",
           f"## Read actions ({len(catalog.READ_ACTIONS)}) - never change the model",
           ""]
    for title, group in catalog.READ_GROUPS.items():
        out += [f"### {title}", "", *_table(group), ""]
    out += ["### Help (computed in the MCP server)", "", *_table(catalog.LOCAL_ACTIONS), "",
            "## The `edit` action",
            "",
            catalog.EDIT_ACTION,
            "",
            f"## Edit operations ({len(catalog.EDIT_OPS)}) - items of `operations`",
            ""]
    for title, group in catalog.EDIT_GROUPS.items():
        out += [f"### {title}", "", *_table(group), ""]
    return "\n".join(out)


if __name__ == "__main__":
    text = render()
    if "--check" in sys.argv:
        current = TARGET.read_text(encoding="utf-8") if TARGET.exists() else ""
        if current != text:
            sys.exit("docs/ACTIONS.md is out of date: run  python tools/gen_docs.py")
        print("docs/ACTIONS.md is up to date")
    else:
        TARGET.write_text(text, encoding="utf-8", newline="\n")
        print(f"wrote {TARGET}")
