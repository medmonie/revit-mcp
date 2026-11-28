r"""Live test of 0.20.0 (phase 7, second part): numbering, rebar containers in rebar_list, Revit 2027 splices.

  python live_p7b.py task04   reads and DRY RUNS only; refuses unless the active model is 'Revit Task 04...' (Revit 2024)
  python live_p7b.py scratch  REAL runs; refuses unless the active model is a new, never saved project. Works in 2024
                              (splices must be refused) and 2027 (splices must work). Close it without saving afterwards.

Values are test values only. What the model lacks is reported as SKIPPED, never as passed.
Started by run_live_p7b_task04.bat / run_live_p7b_scratch.bat; output in tests\results\live_p7b_<mode>_<revit>.txt.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

MODE = sys.argv[1] if len(sys.argv) > 1 else ""
FAILED, SKIPPED = [], []

connection = bridge_client.pin()
info = bridge_client.call("get_project_info", connection=connection)
document_id = info.get("documentId")
ping = bridge_client.call("ping", connection=connection)
REVIT = str(ping.get("revitVersion"))
print("bridge", ping.get("bridgeVersion"), "Revit", REVIT, flush=True)
if not document_id:
    raise SystemExit("The bridge did not identify the active test document.")


def call(method, params=None, **kwargs):
    return bridge_client.call(method, {**(params or {}), "document_id": document_id}, connection=connection, **kwargs)


def out(title, value):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < 4000 else text[:4000] + "\n... (truncated)"), flush=True)


def read(title, method, params=None, show=True):
    try:
        res = call(method, params or {}, timeout=300)
        if show:
            out(title, res)
        return res
    except Exception as exc:
        print(f"===== {title}\nFAILED: {exc}", flush=True)
        FAILED.append(title)
        return None


def edit(title, operations, dry_run, expect_error=False, contains=None):
    try:
        res = call("edit", {"operations": operations, "dry_run": dry_run}, timeout=600)
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error or (contains and contains not in str(exc)):
            FAILED.append(title)
        return None
    out(title, res)
    ok = (res.get("previewSucceeded") if dry_run else res.get("applied") and res.get("succeeded")) and not res.get("error")
    if bool(ok) == expect_error:
        FAILED.append(title + (" (expected an error)" if expect_error else ""))
    if expect_error and contains and contains not in json.dumps(res):
        FAILED.append(title + f" (error should mention '{contains}')")
    return res


def result(res, i=0):
    try:
        return res["results"][i]["result"]
    except Exception:
        return {}


def check(title, condition, detail=""):
    print(f"----- check {title}: {'OK' if condition else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not condition:
        FAILED.append(title)


def skip(title, why):
    print(f"----- SKIPPED {title}: {why}", flush=True)
    SKIPPED.append(f"{title} ({why})")


def allowed():
    try:
        return call("get_edit_permission", {}).get("editingAllowed")
    except Exception:
        return False


def ensure_permission():
    if allowed():
        return
    print("Asking Revit for edit permission: click Yes in Revit's dialog (waiting up to 3 minutes) ...", flush=True)
    try:
        call("edit", {"operations": [{"op": "view_3d_create", "name": "MCP permission probe"}], "dry_run": True}, timeout=60)
    except Exception as exc:
        print("probe:", exc, flush=True)
    for _ in range(90):
        time.sleep(2)
        if allowed():
            print("permission granted", flush=True)
            return
    raise SystemExit("Edit permission was not granted; stopping.")


def rebar_schema(numbering):
    return next((s for s in (numbering or {}).get("schemas", []) if s.get("kind") == "rebar"), None)


before = {k: info.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")}
out("get_project_info (before)", before)
ensure_permission()
types = read("list_reinforcement_types", "list_reinforcement_types", {"limit": 20}, show=False) or {}
print("splice types:", json.dumps(types.get("spliceTypes"))[:400], "| crank types:", json.dumps(types.get("crankTypes"))[:200], flush=True)
check("splice / crank types listed for this Revit", ("2027" in str(types.get("spliceTypes"))) if REVIT < "2027" else isinstance(types.get("spliceTypes"), list))
bars_types = types.get("barTypes", [])
bar = min(bars_types, key=lambda b: abs(b["diameterMm"] - 16)) if bars_types else None

# ============================================================================== Task 04: reads and dry runs
if MODE == "task04":
    if not info.get("title", "").startswith("Revit Task 04"):
        raise SystemExit("task04 mode: the active model is not Task 04; nothing edited.")
    num = read("list_numbering", "list_numbering", {})
    rs = rebar_schema(num)
    check("list_numbering has the rebar schema", rs is not None, json.dumps([s.get("name") for s in (num or {}).get("schemas", [])]))
    parts = [p for p in (rs or {}).get("partitions", []) if p.get("count")]
    if parts:
        p0 = parts[0]
        edit("rebar_numbering remove_gaps (dry run)", [{"op": "rebar_numbering", "action": "remove_gaps", "partition": p0["partition"]}], True)
        edit("rebar_numbering shift to 101 (dry run)", [{"op": "rebar_numbering", "action": "shift", "partition": p0["partition"], "first": 101}], True)
        edit("rebar_numbering change last -> last+1 (dry run)", [{"op": "rebar_numbering", "action": "change", "partition": p0["partition"],
             "from": p0["last"], "to": p0["last"] + 1}], True)
        edit("rebar_numbering unknown partition (refused)", [{"op": "rebar_numbering", "action": "remove_gaps", "partition": "No Such Partition"}],
             True, expect_error=True, contains="no partition")
        edit("rebar_numbering no action (refused)", [{"op": "rebar_numbering", "partition": p0["partition"]}], True, expect_error=True,
             contains="'action' is required")
    else:
        skip("rebar_numbering dry runs", "no numbered rebar partition")
    rl = read("rebar_list (5)", "rebar_list", {"limit": 5}, show=False) or {}
    ids = [b["id"] for b in rl.get("bars", []) if not b.get("system")][:1]
    if ids:
        edit("rebar_splice in Revit 2024 (refused)", [{"op": "rebar_splice", "ids": ids, "type": "x", "rules": {"max_length": 6000}}], True,
             expect_error=True, contains="Revit 2027")
        edit("splice_type_create in Revit 2024 (refused)", [{"op": "splice_type_create", "name": "x", "lap_d": 40}], True,
             expect_error=True, contains="Revit 2027")
    after = bridge_client.call("get_project_info", connection=connection)
    after = {k: after.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")}
    out("get_project_info (after)", after)
    check("Task 04 unchanged (no unsaved changes, same counts)", after == before, json.dumps(after))

# ============================================================================== scratch: real runs
elif MODE == "scratch":
    if info.get("path") or info.get("title", "").startswith(("Revit Task 04", "KTR")):
        raise SystemExit("scratch mode needs a new, never saved project (no path); nothing edited.")
    level = (read("levels", "list_levels", {}, show=False) or {}).get("levels", [{}])[0].get("name")
    fnd = (read("foundation types", "list_types", {"category": "foundations"}, show=False) or {}).get("types", [])
    beams = (read("beam types", "list_types", {"category": "beams"}, show=False) or {}).get("types", [])
    print("beam types:", [(b.get("family"), b.get("type")) for b in beams][:12], flush=True)

    # Numbering on footing bars.
    bars = []
    if fnd and bar:
        ftg = result(edit("footing (real)", [{"op": "footing_create", "type": str(fnd[0]["id"]), "level": level, "points": [[0, 0], [5000, 0]]}], False))
        fids = (ftg or {}).get("ids", [])
        if fids:
            rf = edit("rebar_footing (real)", [{"op": "rebar_footing", "ids": fids, "bar": bar["name"], "spacing": 200, "cover": 75}], False)
            for it in (result(rf) or {}).get("items", []) if rf else []:
                bars += it.get("rebarIds", [])
    if bars:
        num = read("list_numbering", "list_numbering", {}) or {}
        rs = rebar_schema(num)
        check("rebar schema lists the new bars", rs is not None and sum(p.get("count", 0) for p in rs.get("partitions", [])) > 0, json.dumps(rs)[:300])
        half = bars[: max(1, len(bars) // 2)]
        edit("move half the bars to partition P7B (set_parameters)", [{"op": "set_parameters", "ids": half,
             "values": {"bip:NUMBER_PARTITION_PARAM": "P7B"}}], False)
        rs = rebar_schema(read("list_numbering (after partition)", "list_numbering", {}) or {})
        p7 = next((p for p in (rs or {}).get("partitions", []) if p["partition"] == "P7B"), None)
        check("partition P7B appears", p7 is not None and p7["count"] > 0, json.dumps(p7))
        sh = edit("rebar_numbering shift P7B to 101 (real)", [{"op": "rebar_numbering", "action": "shift", "partition": "P7B", "first": 101}], False)
        r = result(sh) if sh else {}
        check("P7B now starts at 101", (r.get("after") or {}).get("first") == 101, json.dumps(r.get("after")))
        if p7 and p7["count"] >= 2:
            ch = edit("rebar_numbering change 101 -> 150 (real)", [{"op": "rebar_numbering", "action": "change", "partition": "P7B", "from": 101, "to": 150}], False)
            r = result(ch) if ch else {}
            check("a gap appears after the change", bool((r.get("after") or {}).get("gaps")), json.dumps(r.get("after")))
            rg = edit("rebar_numbering remove_gaps P7B (real)", [{"op": "rebar_numbering", "action": "remove_gaps", "partition": "P7B"}], False)
            r = result(rg) if rg else {}
            check("gaps removed", not (r.get("after") or {}).get("gaps"), json.dumps(r.get("after")))
    else:
        skip("numbering", "no footing type or bar type to make numbered bars")

    # Splices: long beam bars.
    concrete_beam = next((b for b in beams if "concrete" in (b.get("family", "") + b.get("type", "")).lower()), None)
    if not concrete_beam:
        # Revit's own library family (US Imperial content installed with Revit); a missing file is reported, not passed.
        lib = rf"C:\ProgramData\Autodesk\RVT {REVIT}\Libraries\English-Imperial\US\Structural Framing\Concrete\Concrete-Rectangular Beam.rfa"
        try:
            fl = call("edit", {"operations": [{"op": "family_load", "path": lib}], "dry_run": False}, timeout=300)
        except Exception as exc:
            fl = {"error": str(exc)}
        out("family_load concrete beam from Revit's library (real)", fl)
        if fl and fl.get("applied"):
            beams = (read("beam types (after load)", "list_types", {"category": "beams"}, show=False) or {}).get("types", [])
            concrete_beam = next((b for b in beams if "concrete" in (b.get("family", "") + b.get("type", "")).lower()), None)
    long_bars = []
    if concrete_beam and bar:
        bm = result(edit("concrete beam 12 m (real)", [{"op": "beam_create", "type": str(concrete_beam["id"]), "level": level,
                         "beams": [{"start": [0, 6000], "end": [12000, 6000]}]}], False))
        bid = (bm or {}).get("ids", [None])[0] if bm else None
        if bid:
            rb = edit("rebar_beam none-ends (real)", [{"op": "rebar_beam", "ids": [bid], "bottom_bar": bar["name"], "bottom_count": 3,
                      "top_bar": bar["name"], "top_count": 2, "stirrup": min(bars_types, key=lambda b: b["diameterMm"])["name"], "stirrup_spacing": 200, "cover": 40,
                      "ends": "none"}], False)
            hosted = (read("beam bars", "rebar_list", {"hosts": [bid], "limit": 50}, show=False) or {}).get("bars", [])
            long_bars = [b["id"] for b in hosted if (b.get("barLengthMm") or 0) > 7000]
            print("beam bar sets:", [(b["id"], b.get("barLengthMm"), b.get("quantity")) for b in hosted], flush=True)
    if REVIT < "2027":
        edit("splice_type_create in Revit 2024 (refused)", [{"op": "splice_type_create", "name": "Lap 40", "lap_d": 40}], False,
             expect_error=True, contains="Revit 2027")
        if long_bars:
            edit("rebar_splice in Revit 2024 (refused)", [{"op": "rebar_splice", "ids": long_bars[:1], "type": "Lap 40",
                 "rules": {"max_length": 6000}}], False, expect_error=True, contains="Revit 2027")
    else:
        st = edit("splice_type_create lap 40 d (real)", [{"op": "splice_type_create", "name": "Lap 40d", "lap_d": 40, "stagger_d": 0}], False)
        r = result(st) if st else {}
        check("splice type has the given lap", r.get("lapD") == 40, json.dumps(r))
        edit("splice_type_create without lap (refused)", [{"op": "splice_type_create", "name": "No lap"}], False, expect_error=True, contains="lap_d")
        if long_bars:
            sp = edit("rebar_splice by rules, max 6000 (real)", [{"op": "rebar_splice", "ids": long_bars[:1], "type": "Lap 40d",
                      "rules": {"max_length": 6000}}], False)
            r = result(sp) if sp else {}
            made = sum(len(i.get("bars", [])) for i in r.get("items", []))
            check("splice made more than one bar set", made >= 2, json.dumps(r))
            rl = read("rebar_list after splice", "rebar_list", {"limit": 200}, show=False) or {}
            lens = [x for i in r.get("items", []) for x in i.get("barLengthsMm", [])]
            check("no spliced bar longer than 6000 (max_length)", bool(lens) and max(lens) <= 6000 + 1, f"bar lengths {lens}")
            edit("rebar_splice unknown type (refused)", [{"op": "rebar_splice", "ids": long_bars[1:2] or long_bars[:1], "type": "No Such Splice",
                 "rules": {"max_length": 6000}}], False, expect_error=True, contains="No splice type")
        else:
            skip("rebar_splice", "no concrete beam type to make long bars")
    rl = read("rebar_list (containers / kinds)", "rebar_list", {"limit": 5}, show=False) or {}
    check("rebar_list answers with kinds", all("systemKind" in b for b in rl.get("bars", [])))
else:
    raise SystemExit("mode: task04 | scratch")

print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
