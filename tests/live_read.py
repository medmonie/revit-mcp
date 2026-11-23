r"""Runs every read action against the LIVE Revit and prints the results (no changes to the model).
Started by run_live_read.bat, which saves the output to results\live_read.txt. Open a model in Revit first.

The actions come from the catalogue, so a new read action is covered without touching this file: every action whose
parameters are all optional is called (with limit 5 where it has one); the few that need parameters are listed below.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "server"))
import catalog  # noqa: E402
from bridge_client import call  # noqa: E402

# Read actions with required parameters that can be given without knowing the model.
WITH_PARAMS = {
    "describe_api": [{"type_name": "Autodesk.Revit.DB.Floor", "contains": "Create"},
                     {"type_name": "Autodesk.Revit.DB.Structure.Rebar", "contains": "CreateFrom"}],
    "list_types": [{"category": c, "limit": 8} for c in ("columns", "beams", "walls", "floors", "foundations", "title_blocks")],
    "get_elements": [{"category": ["columns", "beams"], "limit": 3, "params": ["Mark", "Volume"]}],
}
SKIP = {"show_elements"}   # changes the user's selection
FAILED = []


def show(title, method, params=None):
    print(f"===== {title}", flush=True)
    try:
        out = call(method, params or {})
        text = json.dumps(out, indent=1, default=str, ensure_ascii=False)
        print(text if len(text) < 6000 else text[:6000] + "\n... (truncated)", flush=True)
        return out
    except Exception as exc:
        FAILED.append(title)
        print("FAILED:", exc, flush=True)
        return None


live = show("list_actions", "list_actions")
if live:
    names = {a["name"] for a in live["actions"]}
    print("catalogue vs add-in: missing in add-in =", sorted(set(catalog.READ_ACTIONS) - names),
          "| undocumented =", sorted(names - set(catalog.READ_ACTIONS) - {"edit"}),
          "| ops differ =", sorted(set(live["editOperations"]) ^ set(catalog.EDIT_OPS)))

skipped = []
for name, (_, params) in catalog.READ_ACTIONS.items():
    if name in SKIP:
        continue
    if name in WITH_PARAMS:
        for given in WITH_PARAMS[name]:
            show(f"{name} {given}", name, given)
    elif all(p.endswith("?") for p in params):
        show(name, name, {"limit": 5} if "limit?" in params else {})
    else:
        skipped.append(name)   # needs ids or names from the model

links = show("list_links (for get_cad_geometry)", "list_links")
if links and links.get("cadFiles"):
    show("get_cad_geometry layers", "get_cad_geometry", {"id": links["cadFiles"][0]["id"], "layers_only": True})

print("\nnot called here (need ids / names from the model):", ", ".join(skipped))
print("ALL READS ANSWERED" if not FAILED else f"FAILED: {', '.join(FAILED)}")
