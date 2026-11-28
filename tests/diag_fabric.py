r"""Diagnostic for fabric_sheet_place in Task 04: DRY RUNS only, varying the sheet origin's z and position.
Started by run_diag_fabric.bat; output tests\results\diag_fabric.txt."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import bridge_client  # noqa: E402

c = bridge_client.pin()
info = bridge_client.call("get_project_info", connection=c)
if not info.get("title", "").startswith("Revit Task 04"):
    raise SystemExit("not Task 04")
doc = info["documentId"]
call = lambda m, p: bridge_client.call(m, {**p, "document_id": doc}, connection=c, timeout=300)
slab = call("get_elements", {"ids": [1726001], "box": True, "params": ["bip:LEVEL_PARAM", "Height Offset From Level"]})["elements"][0]
print(json.dumps(slab, indent=1))
lo, hi = slab["box"]["min"], slab["box"]["max"]
x, y = lo[0] + 0.3 * (hi[0] - lo[0]), lo[1] + 0.3 * (hi[1] - lo[1])
types = call("list_reinforcement_types", {"limit": 5})
sheet = types["fabricSheetTypes"][0]["name"] if types["fabricSheetTypes"] else "default"
print("sheet types:", len(types["fabricSheetTypes"]), "wire types:", types["fabricWireTypes"], "using", sheet, flush=True)
for label, z in [("top", hi[2]), ("top-25", hi[2] - 25), ("mid", (lo[2] + hi[2]) / 2), ("bottom+25", lo[2] + 25), ("zero", 0.0)]:
    res = call("edit", {"dry_run": True, "operations": [{"op": "fabric_sheet_place", "host": 1726001, "sheet_type": sheet,
                                                         "point": [x, y, z], "location": "top"}]})
    print(label, round(z, 1), "->", res.get("previewSucceeded"), (res.get("error") or "")[:120], flush=True)
res = call("edit", {"dry_run": True, "operations": [{"op": "fabric_area", "host": 1726001, "sheet_type": sheet, "location": "top"}]})
r = res["results"][0]["result"] if res.get("results") else {}
print("fabric_area:", json.dumps({k: r.get(k) for k in ("sheets", "massKg", "sheetType", "lapMajor")}), res.get("error"), flush=True)
after = bridge_client.call("get_project_info", connection=c)
print("unchanged:", after.get("hasUnsavedChanges") is False and after.get("counts") == info.get("counts"))
