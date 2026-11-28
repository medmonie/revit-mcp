r"""Live READ-ONLY test of 0.21.0 on the KTR15 federated model (links: architecture, MEP, electrical, structure).

Never edits, never dry-runs: only read actions. Refuses unless the active model is the federated model.
Started by run_live_federated.bat; output in tests\results\live_federated.txt.
"""
import json
import math
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

FAILED, SKIPPED = [], []
connection = bridge_client.pin()
info = bridge_client.call("get_project_info", connection=connection)
if "Federated" not in info.get("title", ""):
    raise SystemExit("The active model is not the federated model; nothing read.")
document_id = info["documentId"]
before = {k: info.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")}
print("bridge", bridge_client.call("ping", connection=connection).get("bridgeVersion"), flush=True)


def call(method, params=None, timeout=600):
    return bridge_client.call(method, {**(params or {}), "document_id": document_id}, connection=connection, timeout=timeout)


def out(title, value, cut=3500):
    text = json.dumps(value, indent=1, default=str, ensure_ascii=False)
    print(f"===== {title}\n" + (text if len(text) < cut else text[:cut] + "\n... (truncated)"), flush=True)


def read(title, method, params=None, expect_error=None, cut=3500):
    t0 = time.time()
    try:
        res = call(method, params or {})
        out(f"{title}  ({time.time() - t0:.1f} s)", res, cut)
        if expect_error:
            FAILED.append(title + " (expected an error)")
        return res
    except Exception as exc:
        msg = str(exc)
        print(f"===== {title}\n{'EXPECTED ERROR' if expect_error else 'FAILED'}: {msg}", flush=True)
        if not expect_error or expect_error not in msg:
            FAILED.append(title)
        return None


def check(title, condition, detail=""):
    print(f"----- check {title}: {'OK' if condition else 'NOT AS EXPECTED'} {detail}", flush=True)
    if not condition:
        FAILED.append(title)


def skip(title, why):
    print(f"----- SKIPPED {title}: {why}", flush=True)
    SKIPPED.append(f"{title} ({why})")


links = (read("list_links", "list_links", {}, cut=600) or {}).get("revitLinks", [])
def link(code):
    hit = [l for l in links if code in l["file"]]
    return hit[0]["id"] if hit else None

L = {k: link(c) for k, c in {"str": "-B-S-RC-", "str_e": "-E-S-RC-", "hvac": "-M-HV-", "plumb": "-M-PL-", "fire": "-M-FF-",
                             "elec": "-B-E-GN-", "arch": "-B-A-GN-", "arch_fr": "-B-A-FR-", "arch_x": "-X-A-GN-"}.items()}
print("links:", json.dumps(L), flush=True)

# ---------------------------------------------------------------- the missing-choice fix (0.20.0, pending live check)
read("check_names without kind (refused, clear)", "check_names", {"pattern": ".*"}, expect_error="'kind' is required")

# ---------------------------------------------------------------- MEP systems and quantities
for key in ("hvac", "plumb", "fire", "elec"):
    if not L[key]:
        skip(f"mep {key}", "link not found")
        continue
    s = read(f"mep_systems {key}", "mep_systems", {"link": L[key], "limit": 15})
    if s is not None:
        check(f"mep_systems {key} answers", "systems" in s, f"{s.get('count')} systems, by kind {s.get('byKind')}")
    q = read(f"mep_quantities {key} by size", "mep_quantities", {"link": L[key]})
    if q is not None:
        tot = sum(t["lengthM"] for t in q.get("totalsByCategory", []))
        check(f"mep_quantities {key} lengths add up", abs(tot - sum(g["lengthM"] for g in q.get("lengths", []))) < 0.1 * max(1, len(q.get("lengths", []))),
              f"{tot:.1f} m in {q.get('runs')} runs")
if L["hvac"]:
    read("mep_quantities hvac by system", "mep_quantities", {"link": L["hvac"], "group_by": "system", "fittings": False}, cut=1500)

# ---------------------------------------------------------------- rooms / spaces
for key, kind in (("arch", "rooms"), ("arch_x", "rooms"), ("arch_fr", "rooms"), ("hvac", "spaces")):
    if L[key]:
        r = read(f"list_spaces {kind} in {key}", "list_spaces", {"link": L[key], "kind": kind, "limit": 10}, cut=2500)
        if r is not None:
            check(f"list_spaces {key} totals", abs(r.get("totalAreaM2", 0) - sum(x["areaM2"] for x in r.get("byLevel", []))) < 0.05,
                  f"{r.get('count')} {kind}, {r.get('totalAreaM2')} m2, unplaced {r.get('unplaced')}, not enclosed {r.get('notEnclosed')}")

# ---------------------------------------------------------------- analytical, loads, connections
for key in ("str", "str_e"):
    if not L[key]:
        continue
    a = read(f"analytical_model {key}", "analytical_model", {"link": L[key], "limit": 5}, cut=2500)
    if a is not None:
        check(f"analytical_model {key} answers", "members" in a and "panels" in a,
              f"members {a['members']['count']} (no physical {a['members']['withoutPhysical']}), panels {a['panels']['count']}")
    read(f"list_loads {key}", "list_loads", {"link": L[key], "limit": 5}, cut=2000)
    read(f"steel_connections {key}", "steel_connections", {"link": L[key], "limit": 5}, cut=1200)

# ---------------------------------------------------------------- ray casting and clearances
if L["str"]:
    beams = (read("structure beams", "get_elements", {"link": L["str"], "category": "beams", "limit": 5, "box": True}, cut=800) or {}).get("elements", [])
    if beams:
        b = beams[0]
        rc = read("ray_cast from a linked beam: up and down", "ray_cast", {"id": b["id"], "link": L["str"], "direction": ["up", "down"], "limit": 4})
        if rc:
            ok = True
            for ray in rc["rays"]:
                for h in ray["hits"]:
                    d = math.dist(ray["origin"], h["at"])
                    if abs(d - h["distance"]) > 2:
                        ok = False
            check("ray hit distances match their points", ok)
            check("ray_cast does not hit the source beam", all(h["id"] != b["id"] for ray in rc["rays"] for h in ray["hits"]))
        box = b["box"]
        mid = [(box["min"][0] + box["max"][0]) / 2, (box["min"][1] + box["max"][1]) / 2, box["max"][2] + 500]
        read("ray_cast from a point, 6 directions, structure only", "ray_cast", {"origin": mid,
             "direction": ["up", "down", "east", "west", "north", "south"], "category": ["floors", "beams", "walls", "columns"], "limit": 2}, cut=2500)
    else:
        skip("ray_cast", "no beams in the structure link")

if L["hvac"]:
    c = read("clearances: ducts down to the first element (60 s)", "clearances", {"link": L["hvac"], "category": "ducts", "direction": "down",
             "limit": 15, "max_seconds": 60}, cut=3000)
    if c:
        vals = [i["clearance"] for i in c.get("items", [])]
        check("clearances sorted, equal to their hit distance", vals == sorted(vals) and all(i["clearance"] == i["to"]["distance"] for i in c.get("items", [])),
              f"{c.get('count')} measured of {c.get('scanned')} scanned (no hit {c.get('noHit')}); smallest {vals[:3]}")
    read("clearances: ducts up to structure (floors / beams), under 300 mm", "clearances", {"link": L["hvac"], "category": "ducts", "direction": "up",
         "target_category": ["floors", "beams"], "less_than": 300, "limit": 15, "max_seconds": 60}, cut=2500)

# ---------------------------------------------------------------- MEP crossings through structure
for key in ("hvac", "plumb", "fire", "elec"):
    if L[key] and L["str"]:
        x = read(f"mep_crossings {key} x structure (90 s)", "mep_crossings", {"mep_link": L[key], "structure_link": L["str"], "max_seconds": 90, "limit": 8}, cut=3000)
        if x is not None:
            check(f"mep_crossings {key} answers", "items" in x,
                  f"{x.get('crossings')} crossings, {x.get('scanned')} of {x.get('runsInScope')} runs, complete {x.get('complete')}, by pair {x.get('byPair')}")
            for it in x.get("items", [])[:1]:
                # cross-check one crossing: a ray from the entry point along the run must hit that structural element at once
                d = [it["exit"][i] - it["entry"][i] for i in range(3)]
                n = math.sqrt(sum(v * v for v in d)) or 1
                back = [it["entry"][i] - d[i] / n * 50 for i in range(3)]
                rc = read(f"verify one {key} crossing by ray_cast", "ray_cast", {"origin": back, "direction": [d[0] / n, d[1] / n, d[2] / n], "limit": 3}, cut=1500)
                hit_ids = [h["id"] for ray in (rc or {}).get("rays", []) for h in ray["hits"]]
                check(f"{key} crossing confirmed by a ray (structure element hit)", it["structure"] in hit_ids, f"structure {it['structure']}, ray hits {hit_ids}")

after = bridge_client.call("get_project_info", connection=connection)
after = {k: after.get(k) for k in ("title", "path", "hasUnsavedChanges", "counts")}
check("federated model unchanged", after == before, json.dumps(after)[:300])
print("\nSKIPPED: " + "; ".join(SKIPPED) if SKIPPED else "\nSKIPPED: none", flush=True)
print("ALL CHECKS AS EXPECTED" if not FAILED else "NOT AS EXPECTED: " + "; ".join(FAILED), flush=True)
