r"""Live test of 0.30.0: what Revit has loaded (revit_addins), the analytical model built from nothing (members, panel,
releases, supports, load cases, combination, loads) and read back, HVAC zones (Revit 2027: system zones), staged exports,
the close guard (unsaved changes need discard_changes and edit permission), per-open document identity, the audit
result on edits and the bounded UI queue.

Real edits only in a scratch project made by model_new (never saved again, closed with its changes discarded). Task 04
is opened in the background read-only to test document identity and closed unchanged (never saved). A second, empty scratch is left active at the end;
close it without saving. Started by run_live_v030.bat; output tests\results\live_v030.txt.
"""
import json
import sys
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

TASK04 = r"C:\Users\mohamed.hamed\Desktop\New folder\Revit Task 04_Mohamed A.Hamed_66088960.rvt"
EXPORT = HERE / "results" / "v030_export"
FAILED, SKIPPED = [], []
connection = bridge_client.pin()
ping = bridge_client.call("ping", connection=connection)
REVIT = str(ping.get("revitVersion"))
print("bridge", ping.get("bridgeVersion"), "Revit", REVIT, flush=True)
doc_id = None


def call(method, params=None, timeout=300):
    return bridge_client.call(method, dict(params or {}), connection=connection, timeout=timeout)


def out(title, value, limit=3500):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < limit else text[:limit] + "\n... (truncated)"), flush=True)


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


def must_fail(title, method, params, needle):
    try:
        res = call(method, params)
    except Exception as exc:
        check(title, needle.lower() in str(exc).lower(), str(exc)[:300])
        return
    check(title, False, "no error: " + json.dumps(res, default=str)[:300])


LAST = {}


def edit(title, ops, expect_error=None):
    try:
        res = call("edit", {"document_id": doc_id, "dry_run": False, "operations": ops}, timeout=900)
    except Exception as exc:
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {exc}", flush=True)
        if not expect_error or expect_error.lower() not in str(exc).lower():
            FAILED.append(title)
        return None
    LAST.clear()
    LAST.update(res)
    out(title, res)
    if not res.get("applied"):
        print(f"{'EXPECTED ERROR' if expect_error else 'FAILED'}: {res.get('error')}", flush=True)
        if not expect_error or expect_error.lower() not in str(res.get("error")).lower():
            FAILED.append(title)
        return None
    if expect_error:
        FAILED.append(title + " (expected an error)")
        return None
    return [r.get("result") for r in res.get("results") or []]


def read(method, params=None, timeout=300):
    return call(method, {**(params or {}), "document_id": doc_id}, timeout=timeout)


def allowed():
    try:
        return bool(call("get_edit_permission", timeout=60).get("editingAllowed"))
    except Exception:
        return False


def ensure_permission():
    if allowed():
        return
    print("Asking Revit for edit permission: click Yes in Revit's dialog ...", flush=True)
    try:
        call("edit", {"document_id": doc_id, "dry_run": True, "operations": [{"op": "view_3d_create", "name": "MCP permission probe"}]}, timeout=60)
    except Exception as exc:
        print("probe:", exc, flush=True)
    for _ in range(150):
        time.sleep(2)
        if allowed():
            print("permission granted", flush=True)
            return
    raise SystemExit("Edit permission was not granted; stopping.")


# ------------------------------------------------------------------ start: what is open, what Revit has loaded
docs = call("list_documents").get("documents", [])
start = next((d for d in docs if d.get("active")), None)
out("open at start", docs)
if not start:
    print("no model active at the start (fine: the test makes its own scratch)", flush=True)

addins = call("revit_addins")
out("revit_addins", {k: v for k, v in addins.items() if k != "applications"}, 6000)
print("applications:", ", ".join(a.get("application", "?") for a in addins.get("applications", [])), flush=True)
check("revit_addins lists loaded applications", addins.get("count", 0) > 0, str(addins.get("count")))
print("structural analysis add-ins:", addins.get("structuralAnalysis"), "| steel connections:", addins.get("steelConnections"), flush=True)

# ------------------------------------------------------------------ UI queue: many requests at once, none left hanging
results, errors = [], []


def one_ping():
    try:
        results.append(bridge_client.call("ping", connection=connection, timeout=120).get("bridgeVersion"))
    except Exception as exc:   # a refused request must say why
        errors.append(str(exc))


threads = [threading.Thread(target=one_ping) for _ in range(40)]
t0 = time.time()
for t in threads:
    t.start()
for t in threads:
    t.join(150)
took = time.time() - t0
print(f"40 pings at once: {len(results)} answered, {len(errors)} refused in {took:.1f} s; refusals: {set(e[:120] for e in errors)}", flush=True)
check("40 concurrent requests all answered or refused with a reason, none hanging",
      len(results) + len(errors) == 40 and all("waiting" in e or "busy" in e.lower() for e in errors) and not any(t.is_alive() for t in threads))

# ------------------------------------------------------------------ scratch project
new = call("model_new", {"activate": True}, timeout=600)
out("model_new (scratch, active)", new)
doc_id = new.get("documentId")
if not doc_id or not new.get("active") or call("ping").get("documentId") != doc_id:
    raise SystemExit("The scratch project did not become active; nothing edited.")
ensure_permission()
levels = read("list_levels").get("levels", [])
lv = levels[0]["name"]
print("level", lv, flush=True)

# ------------------------------------------------------------------ analytical model from nothing
res = edit("analytical members and a panel", [
    {"op": "analytical_member_create", "start": [0, 0, 0], "end": [0, 0, 3000], "role": "column"},
    {"op": "analytical_member_create", "start": [6000, 0, 0], "end": [6000, 0, 3000], "role": "column"},
    {"op": "analytical_member_create", "start": [0, 0, 3000], "end": [6000, 0, 3000], "role": "beam"},
    {"op": "analytical_panel_create", "points": [[0, 0, 3000], [6000, 0, 3000], [6000, 5000, 3000], [0, 5000, 3000]], "thickness": 200},
])
c1, c2, beam, panel = [(r or {}).get("id") for r in (res or [{}] * 4)]
check("members and panel created", all([c1, c2, beam, panel]), json.dumps([c1, c2, beam, panel]))
check("edit result says the audit entry was written", LAST.get("auditLogged") is True, str(LAST.get("auditLogged")))

edit("releases: beam pinned both ends; column 1 top user (my, mz released)", [
    {"op": "analytical_release_set", "members": [beam], "release": "pinned"},
    {"op": "analytical_release_set", "members": [c1], "end": "end", "release": "user", "my": True, "mz": True},
])
edit("release user without directions (refused)", [{"op": "analytical_release_set", "members": [beam], "release": "user"}],
     expect_error="released directions")
edit("release on a panel (refused)", [{"op": "analytical_release_set", "members": [panel], "release": "fixed"}],
     expect_error="not an analytical member")

res = edit("supports: fixed and pinned at the column feet, line roller... area pinned on the panel", [
    {"op": "boundary_condition_create", "kind": "point", "host": c1, "end": "start", "condition": "fixed"},
    {"op": "boundary_condition_create", "kind": "point", "host": c2, "end": "start", "condition": "pinned"},
    {"op": "boundary_condition_create", "kind": "line", "host": beam, "condition": "user", "x": "released", "y": "fixed", "z": "fixed", "rx": "released"},
    {"op": "boundary_condition_create", "kind": "area", "host": panel, "condition": "pinned"},
])
check("four supports created", res is not None and all((r or {}).get("id") for r in res), json.dumps(res)[:400])
edit("point support on a panel (refused)", [{"op": "boundary_condition_create", "kind": "point", "host": panel, "end": "start", "condition": "fixed"}],
     expect_error="analytical member")
edit("area support with rx (refused)", [{"op": "boundary_condition_create", "kind": "area", "host": panel, "condition": "user", "rx": "fixed"}],
     expect_error="x, y and z only")

edit("load cases, combination and loads (test values)", [
    {"op": "load_case_create", "name": "MCP DL", "category": "dead"},
    {"op": "load_case_create", "name": "MCP LL", "category": "live"},
    {"op": "load_combination_create", "name": "MCP 1.4D+1.6L", "components": [{"case": "MCP DL", "factor": 1.4}, {"case": "MCP LL", "factor": 1.6}]},
    {"op": "load_place", "kind": "point", "load_case": "MCP LL", "host": beam, "force": [0, 0, -10], "point": [3000, 0, 3000]},
    {"op": "load_place", "kind": "line", "load_case": "MCP DL", "host": beam, "force": [0, 0, -5]},
    {"op": "load_place", "kind": "area", "load_case": "MCP DL", "host": panel, "force": [0, 0, -2.5]},
])

am = read("analytical_model")
out("analytical_model", am, 6000)
members = {m["id"]: m for m in (am.get("members") or {}).get("items", [])}
check("beam released pinned at both ends", members.get(beam, {}).get("releaseStart") == "Pinned" and members.get(beam, {}).get("releaseEnd") == "Pinned",
      json.dumps(members.get(beam)))
check("column 1 top release user", members.get(c1, {}).get("releaseEnd") == "UserDefined", json.dumps(members.get(c1)))
sup = {(x.get("host"), x.get("kind")): x for x in am.get("supports") or []}
d1 = (sup.get((c1, "Point")) or {}).get("directions") or ""
d2 = (sup.get((c2, "Point")) or {}).get("directions") or ""
dl = (sup.get((beam, "Line")) or {}).get("directions") or ""
da = (sup.get((panel, "Area")) or {}).get("directions") or ""
print("supports read:", json.dumps(am.get("supports"))[:800], flush=True)
check("supports read back on their hosts: fixed foot = all one state, pinned foot = same translations, other rotations",
      len(d1) == 6 and len(set(d1)) == 1 and d2[:3] == d1[:3] and set(d2[3:]) != set(d1[3:]), f"{d1} / {d2}")
check("line support: x released, y and z fixed (as given)", len(dl) == 4 and dl[0] != dl[1] and dl[1] == dl[2] and dl[0] == dl[3], dl)
check("area support: three directions", len(da) == 3 and len(set(da)) == 1, da)
loads = read("list_loads")
out("list_loads", loads, 5000)
text = json.dumps(loads)
check("cases, combination and three loads read back", "MCP DL" in text and "MCP 1.4D+1.6L" in text
      and ((loads.get("loads") or {}).get("count") or 0) >= 3, json.dumps((loads.get("loads") or {}).get("byCase")))
cnt = call("revit_addins").get("activeModel") or {}
out("revit_addins counts in the scratch", cnt)
check("counts: 3 members, 1 panel, 4 supports, 2 cases", cnt.get("analyticalMembers") == 3 and cnt.get("analyticalPanels") == 1
      and cnt.get("boundaryConditions") == 4 and cnt.get("loadCases", 0) >= 2, json.dumps(cnt))

# ------------------------------------------------------------------ HVAC zone (2024: HVAC zone; 2027: system zone)
walls = read("list_types", {"category": "walls", "limit": 50}).get("types", [])
wt = next((w for w in walls if "generic" in (w.get("type", "") + w.get("family", "")).lower()), walls[0] if walls else None)
if not wt:
    SKIPPED.append("HVAC zone: no wall type in the scratch")
else:
    edit("walls: two rooms 4 x 5 m", [{"op": "wall_create", "type": wt["type"], "level": lv, "height": 3000, "structural": False, "walls": [
        {"start": [10000, 0], "end": [18000, 0]}, {"start": [18000, 0], "end": [18000, 5000]}, {"start": [18000, 5000], "end": [10000, 5000]},
        {"start": [10000, 5000], "end": [10000, 0]}, {"start": [14000, 0], "end": [14000, 5000]}]}])
    sp = edit("spaces in both rooms", [{"op": "room_create", "kind": "spaces", "level": lv, "points": [[12000, 2500], [16000, 2500]],
                                        "names": ["Office A", "Office B"], "numbers": ["S01", "S02"]}])
    items = ((sp or [{}])[0] or {}).get("items") or []
    ids = [i.get("id") for i in items if i.get("id")]
    check("two spaces", len(ids) == 2, json.dumps(items)[:300])
    if len(ids) == 2:
        zone_op = {"op": "hvac_zone_create", "spaces": ids, "name": "MCP Zone 1"}
        if REVIT == "2027":
            zone_op.update({"type": "MCP test zone type", "create_type": True})
        z = edit("hvac_zone_create", [zone_op])
        zr = (z or [{}])[0] or {}
        check("zone holds both spaces", zr.get("spaces") == 2, json.dumps(zr))
        if REVIT == "2027":
            check("Revit 2027 made a system zone", "system zone" in str(zr.get("kind")), str(zr.get("kind")))
            edit("the same spaces again (refused)", [{"op": "hvac_zone_create", "spaces": ids, "name": "again", "type": "MCP test zone type"}],
                 expect_error="Already in a system zone")
            edit("unknown zone type without create_type (refused)", [{"op": "hvac_zone_create", "spaces": ids[:1], "type": "No such type"}],
                 expect_error="create_type")

# ------------------------------------------------------------------ staged exports
edit("schedule of spaces", [{"op": "schedule_create", "category": "spaces", "fields": ["Number", "Name", "Area"], "name": "MCP spaces"}])

# ------------------------------------------------------------------ phase 16: schedule depth
r = edit("schedule styles and split in two", [{"op": "schedule_set", "schedule": "MCP spaces",
         "fields": {"Number": {"style": {"bold": True, "color": "#1F4E79", "background": "#DDEBF7"}}}, "split": 2}])
check("style set and schedule split in 2 parts", r is not None and "split into 2 parts" in json.dumps(r), json.dumps(r)[:300])
try:
    pr = call("edit", {"document_id": doc_id, "dry_run": False, "operations": [{"op": "schedule_set", "schedule": "MCP spaces",
              "percentage": [{"of": "Area", "heading": "Share of area"}]}]}, timeout=300)
    if not pr.get("applied"):
        raise RuntimeError(str(pr.get("error")))
    sched = read("get_schedule_data", {"schedule": "MCP spaces"})
    out("schedule data with the percentage field", sched, 2000)
    check("percentage field: 50 % for each equal space", "50" in json.dumps(sched), "")
except Exception as exc:
    print("percentage:", exc, flush=True)
    check("percentage field: made, or refused with the reason (Revit API limit)", "cannot be made through Revit's API" in str(exc), str(exc)[:200])
edit("join the split schedule", [{"op": "schedule_set", "schedule": "MCP spaces", "unsplit": True}])
edit("calculated formula field (refused with the reason)", [{"op": "schedule_set", "schedule": "MCP spaces", "calculated": {"name": "x", "formula": "Area * 2"}}],
     expect_error="cannot be made through Revit's API")
if REVIT and wt:
    rm = edit("rooms in the two regions", [{"op": "room_create", "kind": "rooms", "level": lv, "points": [[12000, 2500], [16000, 2500]],
                                            "names": ["Office A", "Office B"], "numbers": ["R01", "R02"]}])
    rooms = [i.get("id") for i in (((rm or [{}])[0] or {}).get("items") or []) if i.get("id")]
    k = edit("key schedule for rooms with two keys", [{"op": "schedule_key_create", "category": "rooms", "name": "MCP Room Style",
              "fields": ["Department", "Floor Finish"],
              "keys": [{"key": "OFF", "values": {"Department": "Office", "Floor Finish": "Carpet"}},
                       {"key": "WC", "values": {"Department": "Services", "Floor Finish": "Tiles"}}]}])
    kr = (k or [{}])[0] or {}
    check("key schedule with 2 keys", len(kr.get("keys") or []) == 2, json.dumps(kr))
    if rooms:
        edit("assign key OFF to both rooms", [{"op": "schedule_key_assign", "schedule": "MCP Room Style", "key": "OFF", "ids": rooms}])
        got = read("get_elements", {"ids": rooms, "params": ["Department", "Floor Finish"]}).get("elements", [])
        check("rooms show the key's values", all((g.get("params") or {}).get("Department") == "Office" and (g.get("params") or {}).get("Floor Finish") == "Carpet" for g in got) and len(got) == 2,
              json.dumps([g.get("params") for g in got]))
        edit("unknown key (refused)", [{"op": "schedule_key_assign", "schedule": "MCP Room Style", "key": "NOPE", "ids": rooms}], expect_error="No key 'NOPE'")

# ------------------------------------------------------------------ phase 16: family parameters and formulas
fam = None
for cat in ("doors", "windows", "furniture", "structural_columns", "generic_models", "casework", "plumbing_fixtures"):
    try:
        ts = read("list_types", {"category": cat, "limit": 50}).get("types", [])
    except Exception:
        ts = []
    if ts:
        fam = ts[0].get("family")
        break
if not fam:
    SKIPPED.append("family_parameters: no loadable family in the scratch")
else:
    print("family for the parameter test:", fam, flush=True)
    fr = edit("family parameters: two lengths, a formula, a new type with a value", [{"op": "family_parameters", "families": [fam],
              "add": [{"name": "MCP Test Len", "type": "length", "group": "dimensions"}, {"name": "MCP Test Twice", "type": "length", "group": "dimensions"}],
              "formulas": {"MCP Test Twice": "MCP Test Len * 2"},
              "new_types": [{"name": "MCP Type B"}],
              "values": {"MCP Type B": {"MCP Test Len": 450}}}])
    fam_rep = ((((LAST.get("results") or [{}])[0].get("result") or {}).get("results")) or [{}])[0]
    pars = {x.get("name"): x for x in fam_rep.get("parameters") or []}
    check("family loaded back: new type 450, formula gives 900", fam_rep.get("reloaded") is True and LAST.get("postCommitFailed") == 0
          and (pars.get("MCP Test Len", {}).get("types") or {}).get("MCP Type B") == 450
          and (pars.get("MCP Test Twice", {}).get("types") or {}).get("MCP Type B") == 900
          and pars.get("MCP Test Twice", {}).get("formula") == "MCP Test Len * 2", json.dumps(pars)[:600])
    types_now = [t.get("type") for t in read("list_types", {"category": cat, "limit": 200}).get("types", []) if t.get("family") == fam]
    check("the project has the new type", "MCP Type B" in types_now, json.dumps(types_now))
    try:
        bad = call("edit", {"document_id": doc_id, "dry_run": False, "operations": [{"op": "family_parameters", "families": [fam],
                    "values": {"MCP Type B": {"MCP Test Twice": 100}}}]}, timeout=900)
    except Exception as exc:
        bad = {"error": str(exc)}
    out("a value on a formula parameter", bad, 2000)
    check("a value on a formula parameter is refused (reported for the family, nothing loaded back)",
          "formula" in json.dumps(bad) and (bad.get("postCommitFailed", 0) > 0 or not bad.get("applied")), "")
EXPORT.mkdir(parents=True, exist_ok=True)
for f in EXPORT.glob("*"):
    if f.is_file():
        f.unlink()
r = edit("export_schedule into an empty folder", [{"op": "export_schedule", "schedule": "MCP spaces", "folder": str(EXPORT), "name": "spaces"}])
time.sleep(1)
files = sorted(p.name for p in EXPORT.iterdir())
check("published, and no staging folder left", "spaces.csv" in files and not any(n.startswith(".revit-mcp-stage") for n in files), json.dumps(files))
edit("export again without overwrite (refused)", [{"op": "export_schedule", "schedule": "MCP spaces", "folder": str(EXPORT), "name": "spaces"}],
     expect_error="already exists")
(EXPORT / "spaces.csv").write_text("old", encoding="utf-8")
edit("export again with overwrite", [{"op": "export_schedule", "schedule": "MCP spaces", "folder": str(EXPORT), "name": "spaces", "overwrite": True}])
time.sleep(1)
text = (EXPORT / "spaces.csv").read_text(encoding="utf-8", errors="replace")
check("overwrite replaced the file", text != "old" and "S01" in text, text[:120])
check("still no staging folder", not any(p.name.startswith((".revit-mcp-stage", "_revit-mcp-unpublished")) for p in EXPORT.iterdir()))

# ------------------------------------------------------------------ per-open document identity (Task 04, read-only, never saved)
t = call("model_open", {"path": TASK04, "activate": False}, timeout=900)
out("model_open Task 04 in the background", t)
first_id = t.get("documentId")
if t.get("alreadyOpen"):
    SKIPPED.append("document identity: Task 04 was already open")
else:
    c_before = call("count_by_category", {"document_id": first_id})
    cl = call("model_close", {"document_id": first_id})
    out("model_close Task 04 (unchanged: no discard needed)", cl)
    check("Task 04 closed without discarding anything", cl.get("discardedChanges") is False, json.dumps(cl))
    t2 = call("model_open", {"path": TASK04, "activate": False}, timeout=900)
    second_id = t2.get("documentId")
    check("reopened Task 04 has a new document id", second_id and second_id != first_id, f"{first_id} -> {second_id}")
    must_fail("the old id no longer reaches the reopened model", "count_by_category", {"document_id": first_id}, "No open model matches")
    c_after = call("count_by_category", {"document_id": second_id})
    check("same counts through the new id", json.dumps(c_after.get("categories") or c_after, sort_keys=True)[:2000]
          == json.dumps(c_before.get("categories") or c_before, sort_keys=True)[:2000])
    cl = call("model_close", {"document_id": second_id})
    check("Task 04 closed again, unchanged", cl.get("discardedChanges") is False, json.dumps(cl))

# ------------------------------------------------------------------ the close guard: the modified scratch, moved to the background
second = call("model_new", {"activate": True}, timeout=600)
out("a second scratch, active (the first one goes to the background)", second)
check("second scratch active", call("ping").get("documentId") == second.get("documentId") and second.get("active"), "")
must_fail("closing the modified first scratch without discard_changes (refused)", "model_close", {"document_id": doc_id}, "unsaved changes")
check("the first scratch is still open after the refusal", any(d.get("documentId") == doc_id for d in call("list_documents").get("documents", [])))
cl = call("model_close", {"document_id": doc_id, "discard_changes": True})
out("model_close first scratch with discard_changes", cl)
check("first scratch closed, changes discarded", cl.get("discardedChanges") is True, json.dumps(cl))
print("The second scratch stays active (nothing in it); close it in Revit without saving.", flush=True)
status = call("get_edit_permission")
out("edit permission and audit health", status)
check("audit log healthy, shared lock in use", (status.get("audit") or {}).get("healthy") is True and (status.get("audit") or {}).get("sharedLock") is True,
      json.dumps(status.get("audit")))

print("\nSKIPPED:", "; ".join(SKIPPED) if SKIPPED else "none")
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED))
sys.exit(1 if FAILED else 0)
