"""End-to-end test of the MCP server WITHOUT Revit.

Starts fake_bridge.py, launches server.py over stdio exactly like Claude does, and exercises the single
`revit` tool: reads, help, the edit path, the declined-permission path and error handling.
Also checks that the catalogue matches the add-in sources (names AND parameter names), that the version
numbers agree and that docs/ACTIONS.md is current.

    python tests/smoke_test.py
"""
from __future__ import annotations

import asyncio
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

ROOT = Path(__file__).resolve().parent.parent
SERVER = ROOT / "server" / "server.py"
sys.path.insert(0, str(ROOT / "server"))
import catalog  # noqa: E402
from fake_bridge import DOCUMENT_ID


def parse(result):
    if result.isError:
        raise AssertionError(result.content[0].text)
    return json.loads(result.content[0].text)


async def run(session, action, params=None):
    args = params or {}
    if action in ("edit", "excel_to_model"):
        args = edit_request(args)
    return parse(await session.call_tool("revit", {"action": action, "params": args}))


def edit_request(params):
    return {"document_id": DOCUMENT_ID, "operation_id": uuid.uuid4().hex, **params}


async def expect_error(session, action, params, needle):
    res = await session.call_tool("revit", {"action": action, "params": params})
    assert res.isError, f"{action} should have failed"
    assert needle.lower() in res.content[0].text.lower(), res.content[0].text
    return res.content[0].text


def server_params(bridge_file: Path, **extra) -> StdioServerParameters:
    return StdioServerParameters(command=sys.executable, args=[str(SERVER)],
                                 env={**os.environ, "REVIT_BRIDGE_FILE": str(bridge_file), **extra})


async def single_tool(bridge_file: Path) -> None:
    async with stdio_client(server_params(bridge_file)) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = (await session.list_tools()).tools
            assert [t.name for t in tools] == ["revit"], [t.name for t in tools]
            desc = tools[0].description
            for name in list(catalog.READ_ACTIONS) + list(catalog.EDIT_OPS) + list(catalog.LOCAL_ACTIONS):
                assert name in desc, f"{name} missing from the tool description"
            print(f"one tool 'revit', description {len(desc)} chars, "
                  f"{len(catalog.READ_ACTIONS)} read actions, {len(catalog.EDIT_OPS)} edit operations")

            levels = await run(session, "list_levels")
            assert [lv["name"] for lv in levels["levels"]] == ["Level 1", "Level 2"], levels
            cols = await run(session, "get_elements", {"category": "columns"})
            assert cols["count"] == 2, cols
            page = await run(session, "get_elements", {"category": "columns", "limit": 1})
            next_page = await run(session, "get_elements", {"category": "columns", "limit": 1, "after_id": page["nextAfterId"]})
            assert page["elements"][0]["id"] == 1001 and next_page["elements"][0]["id"] == 1002
            qty = await run(session, "takeoff", {"category": "columns"})
            assert qty["rows"][0]["volume_m3"] == 1.12, qty
            print("read actions ok")

            h = await run(session, "help")
            assert h["plugin"]["missingInPlugin"] == [] and h["plugin"]["undocumentedInCatalogue"] == [], h["plugin"]
            one = await run(session, "help", {"topic": "column_create"})
            assert "top_level?" in one["column_create"]["params"], one
            print("help ok")

            assert (await run(session, "ping"))["editingAllowed"] is False
            await expect_error(session, "edit", edit_request({"document_id": "wrong", "operations": [{"op": "save"}]}),
                               "does not match")
            await expect_error(session, "edit", {"document_id": "", "operations": [{"op": "save"}]}, "document_id")
            # no document_id / operation_id: still accepted (the server generates the operation id)
            first = parse(await session.call_tool("revit", {"action": "edit", "params": {"operations": [{"op": "save"}]}}))
            assert first["applied"] is False and first["permission"] == "not granted" and first["operationId"], first
            dry = await run(session, "edit", {"dry_run": True, "operations": [{"op": "level_create", "name": "L3", "elevation": 7000}]})
            assert dry["applied"] is False and dry["dryRun"] is True, dry
            batch = edit_request({"operations": [
                {"op": "grid_layout", "x": [0, 6000, 12000], "y": [0, 5000]},
                {"op": "column_create", "type": "400 x 400mm", "level": "Level 1", "top_level": "Level 2",
                 "points": [[0, 0], [6000, 0], [12000, 0]]}]})
            done = await run(session, "edit", batch)
            assert done["applied"] is True and done["operations"] == 2, done
            assert (await run(session, "edit", batch)) == done
            receipt = await run(session, "get_operation_status", {"operation_id": batch["operation_id"], "pid": done["pid"]})
            assert receipt["state"] == "completed" and receipt["result"]["modelEditCount"] == 1, receipt
            await expect_error(session, "edit", {**batch, "operations": [{"op": "save"}]}, "different request")
            assert (await run(session, "ping"))["editingAllowed"] is True   # granted once, stays granted
            print("edit path ok (permission granted once, then reused)")

            with tempfile.TemporaryDirectory() as out:
                xlsx = Path(out) / "QS.xlsx"
                rep = await run(session, "qs_report", {"path": str(xlsx)})
                assert xlsx.exists() and rep["totals"]["concrete_m3"] == 12.75, rep
                assert rep["sheets"] == ["Summary", "Concrete", "Other Materials", "Elements", "Steel", "Rebar", "Formwork", "QA", "Notes"], rep["sheets"]
                assert any("material problems" in w for w in rep["warnings"]), rep["warnings"]
                assert any("overlap pair(s) need review" in w for w in rep["warnings"]), rep["warnings"]
                from openpyxl import load_workbook
                wb = load_workbook(xlsx)
                assert wb["Concrete"]["E4"].value == "=SUM(E2:E3)", wb["Concrete"]["E4"].value   # totals are live formulas
                qa_text = [c.value for row in wb["QA"].iter_rows() for c in row if c.value is not None]
                assert "coincident_geometry" in qa_text and 1.15 in qa_text
                assert wb["Other Materials"]["C2"].value == "Brick"
                await expect_error(session, "qs_report", {"path": str(xlsx)}, "already exists")
                await expect_error(session, "qs_report", {"path": "relative.xlsx"}, "full path")
            print("qs_report ok: concrete classification, formulas, overlap review and overwrite protection")

            with tempfile.TemporaryDirectory() as out:
                from openpyxl import load_workbook
                px = Path(out) / "params.xlsx"
                exp = await run(session, "params_to_excel", {"path": str(px), "category": "columns", "params": ["Mark", "Comments"]})
                assert exp["elements"] == 2, exp
                wb = load_workbook(px)
                ws = wb["Parameters"]
                assert [c.value for c in ws[1]] == ["id", "category", "family", "type", "level", "Mark", "Comments"]
                ws["F2"] = "C1"                                   # edit one cell
                wb.save(px)
                dry = await run(session, "params_from_excel", {"path": str(px), "dry_run": True})
                assert dry["elementsPlanned"] == 1 and dry["elementsChanged"] == 0, dry
                assert dry["sample"][0]["changes"]["Mark"]["excel"] == "C1", dry
                done = await run(session, "params_from_excel", {"path": str(px)})
                assert done["batches"][0]["applied"] is True and done["elementsChanged"] == 1, done
                again = await run(session, "params_from_excel", {"path": str(px)})
                assert again["changes"] == 0, again               # written once, nothing left to change
                wb["_RevitBridge"]["B1"] = "another-model"
                wb.save(px)
                await expect_error(session, "params_from_excel", {"path": str(px)}, "different Revit model")

                tx = Path(out) / "model.xlsx"
                tpl = await run(session, "excel_template", {"path": str(tx)})
                assert "Columns" in tpl["sheets"], tpl
                wb = load_workbook(tx)
                wb["Levels"].append(["L1", 4000, "structural"])
                wb["Grids"].append(["A", 0, -2000, 0, 8000])
                wb["Grids"].append(["1", -2000, 0, 8000, 0])
                for x, y in ((0, 0), (6000, 0)):
                    wb["Columns"].append(["C9", "L0", "L1", x, y])
                wb["Beams"].append(["B1", "L1", 0, 0, 6000, 0])
                wb["Slabs"].append(["S200", "L1", "0,0; 6000,0; 6000,6000; 0,6000"])
                wb.save(tx)
                built = await run(session, "excel_to_model", {"path": str(tx), "dry_run": True})
                assert built["elementsByOperation"] == {"level_create": 1, "grid_create": 2, "column_create": 2,
                                                        "beam_create": 1, "floor_create": 1}, built
                assert built["result"]["dryRun"] is True, built
                wb = load_workbook(tx)
                wb["Columns"].append(["C9", "L0", "L1", "abc", 0])
                wb.save(tx)
                await expect_error(session, "excel_to_model", edit_request({"path": str(tx)}), "Columns row 4: 'x' must be a number")
            print("Excel round trip and Excel-to-model ok")

            sessions = (await run(session, "list_revit_sessions"))["sessions"]
            assert len(sessions) == 1 and sessions[0]["reachable"] and sessions[0]["revit"] == "2027", sessions
            chosen = await run(session, "select_revit", {"revit": "2027"})
            assert chosen["selected"]["pid"] == sessions[0]["pid"], chosen
            assert (await run(session, "list_levels"))["levels"], "calls must still work after select_revit"
            assert (await run(session, "select_revit", {"pid": "auto"}))["selected"] == "auto"
            await expect_error(session, "select_revit", {"pid": 1}, "no reachable revit")
            print("session listing and selection ok")

            await expect_error(session, "edit", edit_request({"operations": [{"op": "no_such_op"}]}), "unknown edit operation")
            await expect_error(session, "edit", {}, "non-empty list")
            await expect_error(session, "column_create", {"type": "X"}, "is an edit operation")
            await expect_error(session, "no_such_action", {}, "unknown action")
            print("error paths ok")


async def declined(bridge_file: Path) -> None:
    async with stdio_client(server_params(bridge_file)) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            res = await run(session, "edit", {"operations": [{"op": "level_create", "elevation": 1}]})
            assert res["applied"] is False and res["permission"] == "not granted", res
            print("declined permission ok: nothing applied")


async def split_tools(bridge_file: Path) -> None:
    async with stdio_client(server_params(bridge_file, REVIT_MCP_SPLIT="1")) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = sorted(t.name for t in (await session.list_tools()).tools)
            assert tools == ["revit_edit", "revit_read"], tools
            res = await session.call_tool("revit_read", {"action": "edit", "params": {"operations": [{"op": "save"}]}})
            assert res.isError, "the read tool must refuse edits"
            res = await session.call_tool("revit_read", {"action": "excel_to_model", "params": {"path": "C:/x.xlsx"}})
            assert res.isError and "revit_edit" in res.content[0].text, "the read tool must refuse excel_to_model"
            first = parse(await session.call_tool("revit_edit", edit_request({"operations": [{"op": "save"}]})))
            assert first["permission"] == "not granted", first   # the first edit only asks
            ok = parse(await session.call_tool("revit_edit", edit_request({"operations": [{"op": "save"}]})))
            assert ok["applied"] is True, ok
            print("split mode ok: revit_read refuses edits, revit_edit applies them")


async def old_addin(bridge_file: Path) -> None:
    async with stdio_client(server_params(bridge_file)) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            await expect_error(session, "edit", edit_request({"operations": [{"op": "save"}]}), "incompatible add-in")
            assert (await run(session, "ping"))["editingAllowed"] is False


async def switched_document(bridge_file: Path) -> None:
    async with stdio_client(server_params(bridge_file)) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            await expect_error(session, "edit", {"operations": [{"op": "save"}]}, "does not match document_id")
            assert (await run(session, "get_edit_permission"))["editingAllowed"] is False


def bbs_regression() -> None:
    import bbs_report
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "schedule.xlsx"
        path.write_bytes(b"original workbook")
        for truncated in (True, False):
            data = {"truncated": truncated, "count": 2, "bars": [{"id": 1}], "totalWeightKg": 20}
            try:
                bbs_report.build(lambda *args: data, {"path": str(path), "overwrite": True})
            except ValueError as exc:
                assert "incomplete" in str(exc)
            else:
                raise AssertionError("Incomplete reinforcement schedule accepted")
            assert path.read_bytes() == b"original workbook"


def workbook_regressions() -> None:
    from openpyxl import Workbook
    from excel_io import FIXED, _same, params_from_excel

    assert not _same("001", "1", "String") and not _same("A ", "A", "String")
    assert not _same("FALSE", True, "Boolean") and _same("FALSE", False, "Boolean")
    detail = {"key": "bip:ALL_MODEL_MARK", "kind": "String", "readOnly": False, "target": "instance"}
    planned = []

    def call(method, params, **_):
        if method == "get_elements":
            return {"elements": [{"id": int(eid), "category": "Columns", "family": "F", "type": "C1", "level": "L1",
                                  "params": {"Mark": "001"}, "parameterInfo": {"Mark": detail}} for eid in params["ids"]]}
        planned.append(len(params["operations"]))
        return {"applied": len(planned) == 1, "dryRun": False}

    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp).resolve() / "legacy.xlsx"
        wb = Workbook()
        ws = wb.active
        ws.title = "Parameters"
        ws.append(FIXED + ["Mark"])
        for i in range(1, 502):
            ws.append([i, "Columns", "F", "C1", "L1", "1"])
        wb.save(path)
        args = {"path": str(path), "document_id": DOCUMENT_ID}
        result = params_from_excel(call, args, {"documentId": DOCUMENT_ID})
        assert planned == [500, 1] and result["elementsPlanned"] == 501 and result["elementsChanged"] == 500, result
        ws["F2"] = "=1+1"
        wb.save(path)
        try:
            params_from_excel(call, args, {"documentId": DOCUMENT_ID})
        except ValueError as exc:
            assert "formula" in str(exc)
        else:
            raise AssertionError("formula cell was accepted as an edit")
    print("typed comparisons and partial Excel apply verified")


def static_checks() -> None:
    cs = (ROOT / "plugin" / "BridgeInfo.cs").read_text(encoding="utf-8")
    version = re.search(r'Version = "([\d.]+)"', cs).group(1)
    assert version == catalog.BRIDGE_VERSION, "server/catalog.py BRIDGE_VERSION differs from plugin/BridgeInfo.cs"

    # every catalogue name must be registered in the add-in sources, and the other way round
    files = {p.name: p.read_text(encoding="utf-8") for p in (ROOT / "plugin").rglob("*.cs")
             if "obj" not in p.parts and "bin" not in p.parts}
    src = "\n".join(files.values())
    reads = set(re.findall(r'Read\("([a-z_0-9]+)"', src))
    ops = set(re.findall(r'ops\["([a-z_0-9]+)"\]', src))
    assert reads == set(catalog.READ_ACTIONS), sorted(reads ^ set(catalog.READ_ACTIONS))
    assert ops == set(catalog.EDIT_OPS), sorted(ops ^ set(catalog.EDIT_OPS))

    # the server's own actions: the catalogue and the dispatch tables list the same names
    import server as srv
    local = set(srv.LOCAL_READ) | set(srv.LOCAL_EDIT)
    assert local == set(catalog.LOCAL_ACTIONS), sorted(local ^ set(catalog.LOCAL_ACTIONS))
    assert not set(srv.LOCAL_READ) & set(srv.LOCAL_EDIT)

    # every documented parameter must be read somewhere in the add-in (as a quoted name)
    quoted = set(re.findall(r'"([a-z_0-9]+)"', src))
    missing = sorted({f"{name}.{p.rstrip('?')}" for name, (_, params) in {**catalog.READ_ACTIONS, **catalog.EDIT_OPS}.items()
                      for p in params if p.rstrip("?") not in quoted})
    assert not missing, f"documented but never read by the add-in: {missing}"

    # the README quotes the version and the number of actions
    readme = (ROOT / "README.md").read_text(encoding="utf-8")
    short = ".".join(version.split(".")[:2])
    for needle in (f"Version {short}", f"{len(catalog.READ_ACTIONS)} read actions", f"{len(catalog.EDIT_OPS)} edit operations"):
        assert needle in readme, f"README.md does not say '{needle}'"
    docs = subprocess.run([sys.executable, str(ROOT / "tools" / "gen_docs.py"), "--check"], capture_output=True, text=True)
    assert docs.returncode == 0, docs.stderr or docs.stdout
    print(f"static checks ok: version {version}, catalogue matches the add-in sources, docs/ACTIONS.md is current")


def with_fake(coro, **env) -> None:
    with tempfile.TemporaryDirectory() as tmp:
        bridge_file = Path(tmp) / "bridge.json"
        fake = subprocess.Popen([sys.executable, str(ROOT / "tests" / "fake_bridge.py"), str(bridge_file)],
                                env={**os.environ, **env}, stdout=subprocess.DEVNULL)
        try:
            for _ in range(50):
                if bridge_file.exists():
                    break
                time.sleep(0.1)
            else:
                raise SystemExit("fake bridge did not start")
            asyncio.run(coro(bridge_file))
        finally:
            fake.terminate()
            fake.wait(timeout=5)


if __name__ == "__main__":
    static_checks()
    workbook_regressions()
    bbs_regression()
    with_fake(single_tool)
    with_fake(declined, FAKE_EDIT="decline")
    with_fake(split_tools)
    with_fake(old_addin, FAKE_PROTOCOL="2")
    with_fake(old_addin, FAKE_PROTOCOL="3")
    with_fake(switched_document, FAKE_EDIT_DOCUMENT="other-model")
    print("\nALL OK")
