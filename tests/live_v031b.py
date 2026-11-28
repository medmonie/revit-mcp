r"""Live test of 0.31.0, second part (phase 17 P2 / P3), in a scratch project made by model_new (real edits, closed with
its changes discarded): area plan, area boundaries from the walls' exterior faces and an area; a ceiling; a gable footprint
roof; curtain grid lines, mullions and a panel type; stairs with two runs, a landing and railings (built after the commit);
a railing along a path; slab edges; a wall sweep; a toposolid. Output tests\results\live_v031b_<revit>.txt.
"""
import json
import math
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "server"))
import bridge_client  # noqa: E402

FAILED, SKIPPED = [], []
conn = bridge_client.pin()
REVIT = str(bridge_client.call("ping", connection=conn).get("revitVersion"))
print("Revit", REVIT, flush=True)
doc = None


def call(m, p=None, timeout=900):
    return bridge_client.call(m, dict(p or {}), connection=conn, timeout=timeout)


def read(m, p=None, timeout=900):
    return call(m, {**(p or {}), "document_id": doc}, timeout=timeout)


def out(title, v, limit=3000):
    t = json.dumps(v, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (t if len(t) < limit else t[:limit] + "\n... (truncated)"), flush=True)


def check(title, ok, detail=""):
    print(f"----- check {title}: {'OK' if ok else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not ok:
        FAILED.append(title)


def edit(title, ops, expect_error=None, dry=False):
    try:
        res = call("edit", {"document_id": doc, "dry_run": dry, "operations": ops})
    except Exception as exc:
        res = {"applied": False, "error": str(exc)}
    out(title, res)
    err = res.get("error") if not (res.get("applied") or (dry and res.get("succeeded"))) else None
    if not err and res.get("postCommitFailed"):
        err = json.dumps(res.get("postCommit"))
    if expect_error:
        check(f"{title} refused", bool(err) and expect_error.lower() in str(err).lower(), str(err)[:250])
        return None
    if err:
        FAILED.append(title)
        return None
    return [r.get("result") for r in res.get("results") or []]


def first(r):
    return (r or [{}])[0] or {}


def near(a, b, tol):
    return a is not None and b is not None and abs(a - b) <= tol


def types(cat):
    try:
        return read("list_types", {"category": cat}).get("types") or []
    except Exception as exc:
        print(f"list_types {cat}: {exc}", flush=True)
        return []


new = call("model_new", {"activate": True})
doc = new["documentId"]
for i in range(150):
    try:
        if call("get_edit_permission", timeout=60).get("editingAllowed"):
            break
    except Exception as exc:
        print("waiting for Revit:", str(exc)[:120], flush=True)
    if i == 0:
        print("Asking for edit permission: click Yes in Revit ...", flush=True)
        try:
            call("edit", {"document_id": doc, "dry_run": True, "operations": [{"op": "view_3d_create", "name": "probe"}]}, timeout=60)
        except Exception as exc:
            print("probe:", exc, flush=True)
    time.sleep(2)

levels = sorted(read("list_levels")["levels"], key=lambda l: l.get("elevation", 0))
L1, L2 = levels[0]["name"], levels[1]["name"]
rise = levels[1]["elevation"] - levels[0]["elevation"]
print("levels", L1, L2, "rise", rise, flush=True)
wall_types = types("walls")
basic = next(t for t in wall_types if "generic" in t["type"].lower() and "curtain" not in t["family"].lower())
curtain = next((t for t in wall_types if "curtain" in (t["family"] + t["type"]).lower()), None)
wwidth = (read("type_layers", {"types": [str(basic["id"])]}).get("types") or [{}])[0].get("totalThickness")
print("basic wall", basic, "width", wwidth, "curtain", curtain, flush=True)

w = edit("a 10 x 6 m box of walls L1 to L2", [{"op": "wall_create", "type": str(basic["id"]), "level": L1, "top_level": L2, "structural": False,
          "walls": [{"start": [0, 0], "end": [10000, 0]}, {"start": [10000, 0], "end": [10000, 6000]}, {"start": [10000, 6000], "end": [0, 6000]}, {"start": [0, 6000], "end": [0, 0]}]}])
walls = first(w).get("ids") or []

# ------------------------------------------------------------------ areas
ap = edit("area plan of L1 (gross building)", [{"op": "area_plan_create", "level": L1, "name": "MCP Gross L1"}])
check("area plan made in a gross building scheme", first(ap).get("grossBuildingArea") is True, json.dumps(first(ap)))
ab = edit("area boundary from the walls' outside faces (the walls are drawn anticlockwise: their exterior side faces in)", [{"op": "area_boundary", "view": "MCP Gross L1", "from_walls": "outside"}])
check("4 boundary lines", first(ab).get("created") == 4, json.dumps(first(ab))[:300])
ar = edit("an area inside", [{"op": "area_create", "view": "MCP Gross L1", "points": [[5000, 3000]], "names": ["GBA L1"], "numbers": ["A1"]}])
gross = (first(ar).get("items") or [{}])[0].get("areaM2")
want = (10 + wwidth / 1000) * (6 + wwidth / 1000) if wwidth else None
check("gross area = outside faces of the walls", near(gross, want, 0.05), f"{gross} m2 vs {want}")
edit("area_boundary with lines and from_walls (refused)", [{"op": "area_boundary", "view": "MCP Gross L1", "from_walls": "exterior",
     "lines": [{"start": [0, 0], "end": [1000, 0]}]}], expect_error="not both")

# ------------------------------------------------------------------ ceiling, roof
ce = edit("ceiling 2700 above L1 inside the walls", [{"op": "ceiling_create", "level": L1, "height": 2700,
          "boundary": [[200, 200], [9800, 200], [9800, 5800], [200, 5800]]}])
check("ceiling at 2700, 9.6 x 5.6 m", near(first(ce).get("height"), 2700, 1) and near(first(ce).get("areaM2"), 53.76, 0.05), json.dumps(first(ce)))
rf = edit("gable roof on L2: 30 degrees on the long sides", [{"op": "roof_create", "level": L2, "footprint": [[0, 0], [10000, 0], [10000, 6000], [0, 6000]],
          "slopes": [30, None, 30, None]}])
r = first(rf)
ridge = 3000 * math.tan(math.radians(30))
check("roof rises 3000 x tan 30 = 1732 to the ridge (plus its thickness)", r.get("top") is not None and ridge <= r["top"] <= ridge + 700, json.dumps(r)[:300])
edit("roof slopes count mismatch (refused)", [{"op": "roof_create", "level": L2, "footprint": [[0, 0], [10000, 0], [10000, 6000], [0, 6000]], "slopes": [30, 30]}], expect_error="one value per")

# ------------------------------------------------------------------ curtain wall
if not curtain:
    SKIPPED.append("curtain wall: no curtain wall type in the template")
else:
    cw = edit("a curtain wall 6 m long, 3 m high", [{"op": "wall_create", "type": str(curtain["id"]), "level": L1, "height": 3000, "structural": False,
               "walls": [{"start": [0, -3000], "end": [6000, -3000]}]}])
    cwid = (first(cw).get("ids") or [None])[0]
    mullions = types("mullions")
    panels = types("curtain_panels")
    solid = next((t for t in panels if "solid" in t["type"].lower()), panels[0] if panels else None)
    print("mullions", mullions[:3], "panels", panels[:5], flush=True)
    cg = edit("grid: 3 vertical, 1 horizontal, mullions, one solid panel", [{"op": "curtain_grid_set", "wall": cwid, "u": [1500, 3000, 4500], "v": [1000],
               **({"mullion_type": str(mullions[0]["id"])} if mullions else {}),
               **({"panels": [{"along": 750, "height": 500, "type": solid["type"]}]} if solid else {})}])
    g = first(cg)
    check("4 new grid lines; panels = (vertical + 1) x (horizontal + 1)", len(g.get("gridLines") or []) == 4 and g.get("horizontalLines") >= 1 and g.get("verticalLines") >= 3
          and g.get("panelCount") == (g["verticalLines"] + 1) * (g["horizontalLines"] + 1), json.dumps({k: g.get(k) for k in ("panelCount", "verticalLines", "horizontalLines")}))
    if solid:
        check("one panel changed to the solid type", len(g.get("panelsChanged") or []) == 1, json.dumps(g.get("panelsChanged")))
    edit("grid line outside the wall (refused)", [{"op": "curtain_grid_set", "wall": cwid, "u": [7000]}], expect_error="outside the wall")

# ------------------------------------------------------------------ stairs and railings
n = max(2, round(rise / 175))
n1 = n // 2
n2 = n - n1
x1 = 1000 + (n1 - 1) * 280
rails = types("railings")
st_ops = [{"op": "stairs_create", "base_level": L1, "top_level": L2, "risers": n, "tread_depth": 280, "width": 1200,
           "runs": [{"start": [1000, -8000], "toward": [9000, -8000], "risers": n1}, {"start": [x1 + 1200, -8000], "toward": [20000, -8000], "risers": n2}],
           **({"railing_type": str(rails[0]["id"])} if rails else {})}]
dry = edit("stairs (dry run)", st_ops, dry=True)
check("stairs dry run lays out two runs and builds nothing", (first(dry).get("status") or "").startswith("dry run") and len(first(dry).get("runs") or []) == 2, json.dumps(first(dry))[:300])
st = edit("stairs: two straight runs, landing, railings", st_ops)
s = first(st)
check("stairs built with the risers given", s.get("status") == "built" and s.get("actualRisers") == n, json.dumps(s)[:500])
check("a landing between the runs", len(s.get("landings") or []) == 1, "")
if rails:
    check("railings on the stairs, of the type given", len(s.get("railings") or []) >= 1 and all(r.get("type") == rails[0]["type"] for r in s.get("railings")), json.dumps(s.get("railings")))
edit("stairs runs that do not add up (refused)", [{**st_ops[0], "risers": n + 1}], expect_error="risers in all")
rl = edit("railing along a path on L1", [{"op": "railing_create", "level": L1, "path": [[0, -1000], [6000, -1000], [6000, -2000]]}])
check("railing 7 m long", near(first(rl).get("length"), 7000, 1), json.dumps(first(rl)))

# ------------------------------------------------------------------ slab edge, wall sweep, toposolid
fl = edit("a floor on L2", [{"op": "floor_create", "type": str(types("floors")[0]["id"]), "level": L2, "structural": False,
           "boundary": [[0, 0], [10000, 0], [10000, 6000], [0, 6000]]}])
floor = first(fl).get("id") or (first(fl).get("ids") or [None])[0]
se_types = types("slab_edges")
if not se_types:
    SKIPPED.append("slab edge: no slab edge type in the template")
else:
    se = edit("slab edge round the L2 floor", [{"op": "slab_edge_create", "floor": floor, "type": se_types[0]["type"]}])
    check("slab edge on 4 edges, 32 m", first(se).get("edges") == 4 and near(first(se).get("length"), 32000, 50), json.dumps(first(se)))
    se2 = edit("slab edge on the edge nearest a point", [{"op": "slab_edge_create", "floor": floor, "type": se_types[0]["type"], "near": [[5000, 6100]]}], dry=True)
    check("one edge, 10 m", first(se2).get("edges") == 1 and near(first(se2).get("length"), 10000, 50), json.dumps(first(se2)))
sw_types = types("wall_sweeps") + types("reveals")
if not sw_types:
    SKIPPED.append("wall sweep: no wall sweep or reveal type in the template")
else:
    sw = edit("a sweep / reveal on the south wall at 900", [{"op": "wall_sweep_create", "walls": [walls[0]], "type": sw_types[0]["type"], "height": 900}])
    check("sweep / reveal made", first(sw).get("created") == 1, json.dumps(first(sw)))
pts = [[x, y, 0 if x < 20000 else 1500] for x in range(-20000, 40001, 20000) for y in range(-20000, 30001, 25000)]
tp = edit("toposolid from survey points", [{"op": "toposolid_create", "level": L1, "points": pts}])
t = first(tp)
check("toposolid spans the survey heights", t.get("id") is not None and (t.get("highest") or 0) - (t.get("lowest") or 0) >= 1000, json.dumps(t))

call("model_new", {"activate": True})
cl = call("model_close", {"document_id": doc, "discard_changes": True})
check("scratch closed, nothing saved", cl.get("discardedChanges") is True, json.dumps(cl))
print("\nSKIPPED:", "; ".join(SKIPPED) if SKIPPED else "none")
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED))
sys.exit(1 if FAILED else 0)
