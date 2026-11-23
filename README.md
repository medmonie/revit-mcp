# Revit MCP bridge

**Version 0.13 · Autodesk Revit 2027 and 2024 · Windows**

Lets Claude work in the model that is open in Revit on this computer: read it, measure it, check it, and, after you allow
it once per Revit session, build and fix it. One tool covers modelling, editing, quantity surveying, model QA, CAD to
Revit, Excel round trips, sheets, and import and export.

```
Claude ──stdio──> MCP server (python server\server.py)       answers help, Excel and QS workbooks itself
                      │  127.0.0.1 only, random port + per-session token
                      v
                  RevitMcpBridge.dll inside Revit ──> Revit API   (2024 and 2027 can be open together)
```

The bridge connection stays on this machine. MCP results are returned to the configured client. The Civil 3D bridge has its own add-in, server, tools, and data folder.

## Contents

1. [Install](#1-install)
2. [Permissions](#2-permissions)
3. [How to use it](#3-how-to-use-it)
4. [Workflows](#4-workflows)
5. [Feature reference](#5-feature-reference)
6. [How the CAD readers work](#6-how-the-cad-readers-work)
7. [What it cannot do](#7-what-it-cannot-do)
8. [Safeguards and limits](#8-safeguards-and-limits)
9. [Troubleshooting](#9-troubleshooting)
10. [For developers](#10-for-developers)
11. [Roadmap](#11-roadmap)
12. [Licence](#12-licence)

---

## 1. Install

Needs Revit 2027 and/or 2024, the .NET 10 SDK, Python 3.10+, and an MCP client.

1. Close Revit.
2. Double-click **`build.bat`**. It builds the add-in once per installed Revit and copies it to
   `%APPDATA%\Autodesk\Revit\Addins\<year>\` (`RevitMcpBridge.addin` + `RevitMcpBridge\RevitMcpBridge.dll`).
   The result is in `build_result.txt` (`EXIT_2027=0` / `EXIT_2024=0` means built and deployed).
3. Double-click **`setup_server.bat`** (once). It creates `server\.venv`, installs `mcp` and `openpyxl` and writes
   `claude_config_snippet.json` and `codex_config_snippet.toml` using the current project folder.
4. In Claude: **Settings > Developer > Edit Config**. Add the `"revit"` entry from the snippet to `mcpServers`,
   next to `"civil3d"`:
   ```json
   {
     "mcpServers": {
       "civil3d": { "...": "unchanged" },
       "revit": {
         "command": "C:\\Users\\<you>\\Desktop\\Revit MCP\\revit-mcp\\server\\.venv\\Scripts\\python.exe",
         "args": ["C:\\Users\\<you>\\Desktop\\Revit MCP\\revit-mcp\\server\\server.py"]
       }
     }
   }
   ```
5. Restart Claude completely (quit from the tray icon). Start Revit; the first time it asks whether to load the
   unsigned add-in: choose **Always Load**. A panel **Claude MCP** appears on the **Add-Ins** tab.
6. Open a model and ask your client to ping Revit. The answer shows the add-in version (0.13.3), the Revit version, and the active model's `documentId`.

For **local Codex on Windows**, merge the generated `codex_config_snippet.toml` into the Codex MCP configuration and reload the client. The snippet enables separate `revit_read` and `revit_edit` tools.

For **ChatGPT web**, a workspace with custom MCP and Secure MCP Tunnel access can route the local stdio command through a private tunnel. See the [OpenAI Secure MCP Tunnel setup](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels).

**After an update**, restart Revit and the MCP client to load matching versions. The 0.13.3 server requires an add-in with bridge protocol 4 for editing. See the [implementation log](docs/IMPLEMENTATION_LOG.md) for the changes.

> **Revit 2024 note.** The 2024 build targets .NET Framework 4.8 and downloads the package
> `Microsoft.NETFramework.ReferenceAssemblies` from nuget.org on the first build. Without internet only the 2024
> build fails; 2027 is unaffected.

---

## 2. Permissions

You are asked once in each program, and never per edit.

| Where | What you are asked | How often |
|---|---|---|
| Claude | Allow the tool `revit` | Once ("always allow") |
| Revit | "Allow Claude to edit models in this session?" | Once per Revit session, at the first edit |

- **Reading never asks anything** in Revit.
- After **Yes**, no later edit asks again, in any open model, until Revit is closed.
- After **No**, nothing is changed, and edits are refused for 30 seconds so that a retry cannot nag you.
- The permission is deliberately not remembered: after every Revit restart the first edit asks again.

Buttons on **Add-Ins > Claude MCP**:

| Button | Effect |
|---|---|
| **MCP Status** | Shows version, port and whether editing is allowed. With several Revit windows open (e.g. 2024 and 2027), makes **this** one the one Claude talks to. |
| **Allow Editing** | Allows editing for this session without the dialog. |
| **Stop Editing** | Switches editing off again. Reading keeps working. |

> **Read-only from the Claude side.** Set the environment variable `REVIT_MCP_SPLIT=1` for the server: Claude then
> shows two tools, `revit_read` and `revit_edit`, which you can allow separately.

---

## 3. How to use it

Open the model in Revit, then ask Claude in plain language. Claude picks the actions, reads what it needs first
(levels, types, grids, the CAD layers) and then sends one edit batch.

### Rules that apply to every request

| Rule | What it means for you |
|---|---|
| **Millimetres by default** | Say "in metres" and Claude sends `units: "m"`. Results are always m, m2, m3, kg and t. |
| **Model transaction** | Model operations use one transaction and one Undo step. Save, export, view activation, and family reloads have separate `postCommit` outcomes. |
| **Dry run first** | Ask for a dry run before anything large: the batch is executed and rolled back, so you see counts, errors and warnings with no change. Purge previews list first-pass candidates without deleting. A purge-only preview opens no transaction. Exports, saves and family changes are skipped in a dry run. |
| **Types come from the model** | Claude only uses loaded families and types ("list the beam types"). `family_load`, `type_duplicate`, `types_create` and `create_types` add more. |
| **Select, then ask** | "the selected elements" works: Claude reads your current selection. `show_elements` selects and zooms to what it found. |
| **Already modelled is skipped** | The CAD builders skip columns, beams, walls, slabs and grids that already exist at that place (`skip_existing: false` to switch off). |
| **Coordinates** | Geometry uses Revit model coordinates. In `list_levels`, `elevation` is project-relative and `displayElevation` follows Revit's elevation base. |
| **Workshared models** | Elements owned by another user make the batch fail with a message. Test on a detached copy. |

### Several Revits at once

With Revit 2024 and 2027 both open: *"list the open Revits"* (`list_revit_sessions`), then *"use Revit 2027"*
(`select_revit`). Every following call goes to that one. **MCP Status** in a Revit window does the same.

**Edit targeting and retries.** The server captures the active document in the selected process before sending an edit.
Revit checks the document again when the edit executes. Supply the `document_id` from an earlier `ping` to target the
model you inspected. After a lost reply, use `get_operation_status` on the original process. An identical retry needs
the same operation ID, document ID and payload. Check `result.applied`, `result.succeeded` and `result.postCommit`.
A permission-only answer ("not granted") is final for that request: after clicking Yes, send the edit again as a new
request. Receipts last until Revit closes (the latest 128 results are kept).

### What a call looks like (you normally never write this)

```json
{ "action": "takeoff", "params": { "category": ["columns", "beams", "floors"], "group_by": "type_level" } }

{ "action": "edit",
  "params": {
    "units": "mm", "dry_run": true,
    "operations": [
      { "op": "level_create", "name": "L1", "elevation": 4000, "plan": "structural", "if_exists": "skip" },
      { "op": "grid_layout", "x": [0, 6000, 12000], "y": [0, 5000, 10000] },
      { "op": "column_create", "type": "M_Concrete-Rectangular-Column : 400 x 400mm", "level": "GF", "top_level": "L1",
        "points": [[0, 0], [6000, 0], [12000, 0]] }
    ] } }
```

Every action and parameter: **[docs/ACTIONS.md](docs/ACTIONS.md)**, or ask Claude for `help` (optionally on one topic).

---

## 4. Workflows

Each workflow lists what to ask, what runs, and what to check. Run the edit steps as a dry run first on a real project.

### 4.1 Understand a model

| Ask | Runs |
|---|---|
| "What is in this model?" | `get_project_info`, `list_levels`, `list_grids`, `count_by_category` |
| "Which beam types are loaded and how many of each?" | `list_types` |
| "Show me Revit's warnings grouped by message." | `list_warnings` |
| "What is inside family KTR-S-STL-BU?" | `family_inspect`: parameters, solids with their material and linked parameter, nested families |

### 4.2 Quantities and the QS workbook

| Ask | Runs |
|---|---|
| "Concrete volume of columns, beams and slabs per level." | `takeoff` grouped by type and level |
| "Volume by material." | `material_takeoff` (wall and floor layers split by material) |
| "Steel tonnage by section." | `steel_takeoff` (Revit volume x 7850 kg/m3, independent of the material assigned) |
| "Formwork areas." | `formwork_takeoff` (sides for all, soffits for beams and slabs, never tops; steel skipped) |
| "Rebar weight by diameter." | `rebar_takeoff` |
| "Bar bending schedule of the GF columns to `C:\Projects\P1\BBS.xlsx`." | `bbs_report` (from `rebar_list`) |
| "Make a QS workbook in `C:\Projects\P1\QS.xlsx`." | `qs_report`: summary, concrete by grade and level, elements by type, steel, rebar, formwork, QA, notes; totals are Excel formulas |

**Check before you use volumes in a BOQ:** quantities are Revit's computed values *after joins*. Run the QA in 4.3 first;
unjoined overlaps count the same concrete twice.

### 4.3 Model QA and clean-up

A sound order, each step a dry run first:

| Step | Ask | Runs |
|---|---|---|
| 1 | "Fix the warnings that have a safe fix." | `fix_warnings`: unjoins 'joined but do not intersect' pairs; every other warning is listed under `notFixed` |
| 2 | "Find overlapping elements that are not joined." | `find_overlaps`: measured unjoined pairs and a separate list of coincidence or containment pairs to review |
| 3 | "Join them, columns cut beams, beams cut slabs." | `join_overlaps` with a cut priority (default foundations > columns > walls > beams > floors); large models continue with `resume_after` |
| 4 | "Check whether these coincident columns are the same." | Compare type, phase, design option, dependents, and instance parameters before choosing which elements to edit. |
| 5 | "Check the materials." | `check_materials`: no material, Revit default materials, names outside your pattern, steel with a concrete material |
| 6 | "Fix them: concrete C30, built-up steel ST-52, rolled ST-44." | `fix_materials` with rules (first match wins); verifies the material actually shown afterwards and reports `notEffective` |
| 7 | "The steel families still show concrete." | `family_material_set` (one material for every solid in chosen families; families loaded back, project type values kept) |
| 8 | "Check names against our convention." | `check_names` with a regular expression |

Why step 7 exists: a loadable family whose solids are on *By Category* takes its material from **Manage > Object
Styles**, not from the type's Structural Material. `family_inspect` shows this; `family_material_set` fixes it.
`family_material_link` (link the solids to Structural Material) is the alternative when one family carries several grades.

### 4.4 CAD to Revit

1. *"Link `C:\Projects\P1\FF.dwg` in millimetres, origin to origin, on First Floor."* → `cad_link`
2. *"List the layers of the CAD file."* → `list_links`, then `get_cad_geometry` with `layers_only`
3. Build, one element family at a time, dry run first:

| Ask | Runs | Notes |
|---|---|---|
| "Which columns are on layer S-COLS and which types would they get?" | `cad_columns` | Reads only. Size, rotation, matched type and unmatched sizes |
| "Place them from GF to FF; make missing sizes from 'KTR15-S-C 600x600'." | `columns_from_cad` with `create_types` | Steel outlines (H / I) only match steel families; `include: steel` or `all` |
| "Grids from S-GRID, names 1-17 and A-H." | `grids_from_cad` | CAD text is unreadable, so give names when they differ from 1, 2, 3 / A, B, C |
| "Beams from S-BEAM on FF, top at the level." | `beams_from_cad` | Typed by width; `types` maps a width to a type; curved beams from concentric arcs |
| "Walls from S-WALL, GF to FF." | `walls_from_cad` | Typed by thickness |
| "Slabs from the closed outlines on S-SLAB." | `slabs_from_cad` (mode `closed`) | Outlines inside an outline become openings |
| "The slab edge is broken at columns: use the outer outline of S-SLAB, S-BEAM and S-COLS, voids from CE-VOID, preview first." | `slabs_from_cad` mode `outer` with `preview: true` | Draws the outline as model lines to check; run again without `preview` to make the slab |
| "One sheet per structural plan, S-201 upwards." | `sheets_from_views` | See 4.6 |

### 4.5 Model from scratch, or from Excel

| Ask | Runs |
|---|---|
| "Levels B1 -3.50, GF 0.00, L1 +4.00 with structural plans, and a 6 x 4 grid of 6.0 m bays." | `level_create` x 3, `grid_layout` (one batch) |
| "A 400x400 column at every grid intersection from GF to L1." | `list_grids`, then `column_create` |
| "Create all column types C1 to C12 from this schedule." | `types_create` |
| "Give me an input workbook." → fill it → "Build the model from it." | `excel_template`, then `excel_to_model` (levels, grids, columns, beams, walls, slabs, footings in one batch) |
| "Export Mark, Comments and Volume of all columns to Excel." → edit → "Write it back." | `params_to_excel`, then `params_from_excel`. Keep the hidden metadata and paste literal values into editable cells. The import checks the source model and reports conflicts, missing rows, planned changes, and applied changes. |

### 4.6 Views, sheets and export

| Ask | Runs |
|---|---|
| "A section along grid C and a 3D view of level 2." | `section_create`, `view_3d_create` |
| "Tag all beams in the FF plan and dimension the grids." | `tag_elements` (skips tagged ones), `dimension_grids` |
| "One sheet per structural plan, S-201 upwards, title block A1, scale 1:100, skip copies." | `sheets_from_views` with `exclude_contains: ["Copy"]` |
| "Export sheets S-2xx to one PDF and the model to IFC4 in `C:\Projects\P1\out`." | `export_pdf`, `export_ifc` |
| "Each sheet as its own DWG named `{number} - {name}`." | `export_dwg` with `name_template` |

### 4.7 Drafting and detailing

| Ask | Runs |
|---|---|
| "What line styles, hatch types and detail components does this model have?" | `list_detailing` |
| "Make a drafting view 'Typical column base' at 1:10." | `drafting_view_create` |
| "Draw the footing outline as a closed polyline in Wide Lines, and hatch it with Concrete." | `detail_lines_create` (points, closed, style), `region_create` (type) |
| "Mask this area" / "a 300 mm circular hatch here" | `region_create` with `masking: true` / `circle` |
| "Place the anchor-bolt detail component at these points; the break line along this edge." | `detail_component_place` (points, or lines for line-based components) |
| "Brick symbol every 215 mm along this wall face." | `detail_component_place` with `along` (no sample needed), or `repeating_detail_place` (copies a placed repeating detail, from this model or another open one) |
| "A new legend 'Slab legend' at 1:50." | `legend_create` (duplicates an existing legend as an empty one) |
| "Copy the standard details whose name contains 'TYP' from the open detail library." | `list_detailing` with `model`, then `views_copy_from` |
| "Callout around grid E3/EC in the FF plan at 1:20." | `callout_create` |
| "Duplicate the FF plan with detailing, three dependent views." | `view_duplicate` |
| "Crop the plan to this area, fine detail, cut plane at 1200." | `view_set` (crop, detail_level, view_range) |

Coordinates in a drafting view are its own sheet-free space: [0, 0] is the view's origin, in model units (mm at full
size, not paper size). Revit's API has no create method for repeating details or legends: `repeating_detail_place` copies
a placed sample and reshapes it, `legend_create` duplicates an existing legend as an empty one.

### 4.8 Documentation

| Ask | Runs |
|---|---|
| "Colour the FF plan's beams by Type Mark and give me the legend." | `color_by_parameter` |
| "Filter 'Steel members' (Structural Material contains ST) in red on all structural plans." | `filter_create`, `filter_apply` (views or a view template) |
| "Halftone the walls, hide the CAD link." | `graphics_override` (categories / ids; hide, reset) |
| "Make a template from the FF plan and apply it to all floor plans." | `view_template_create`, `view_template_apply` |
| "Dimension the columns on grid line E in the FF plan, both ways." | `dimension_elements` (outer faces plus grids, chain beyond the elements) |
| "Spot levels on the slabs and footings." | `spot_elevation` (top face) |
| "Revision 'IFC issue' on sheets S-201 to S-205, cloud around this area." | `revision_create`, `revision_cloud_create` |
| "Put the three section views and the column schedule on S-301." | `sheet_layout` (rows inside the sheet area; schedules too) |
| "Title-block fields (Drawn by, Checked by, Status) for all sheets from Excel." | `params_to_excel` / `params_from_excel` with category `sheets` (or `title_blocks`) |
| "What templates, filters, revisions and scope boxes are in the model?" | `list_documentation` |

### 4.9 Reinforcement

| Ask | Runs |
|---|---|
| "Reinforce these columns: 4 x 3 bars T20, ties T10 @ 150, cover 40." | `rebar_column` (bars_x 4, bars_y 3); laps into the column above, feet on the footing, ties @ 100 at the ends |
| "Roof columns: bend the bars at the top, no closer ties." | `rebar_column` (top bend, end_zone 0) |
| "Beams on FF: 3 T16 bottom, 2 T12 top, stirrups T8 @ 150." | `rebar_beam`: anchored into supports (40 Ø, bent if needed), stirrups @ 100 over 2h at the ends |
| "Anchorage 50 Ø, laps 60 Ø, stirrups @ 75 at the ends." | `rebar_beam` (anchorage_d 50, lap_d 60, end_spacing 75) |
| "Footings F1: T16 @ 200 bottom both ways, top mesh T12 @ 250, bottom bars turned up." | `rebar_footing` (end_bends) |
| "Slab: T12 @ 200 top and bottom both ways." | `rebar_area` |
| "BBS of everything on the GF to Excel." | `bbs_report` |

Bar types are named or given by diameter (16 or "T16" finds the 16 mm type). Sizes come from each element's own
geometry; cover is to the outer face of the outermost bar. Review the bars in a 3D view before issuing a schedule.

### 4.10 Modelling

| Ask | Runs |
|---|---|
| "A 2 % sloped ramp slab, low side at grid A." | `floor_create` with `slope_arrow` and `slope` |
| "Drop this slab corner by 50 mm for the drain." | `slab_shape` (move or add points) |
| "Joists at 600 mm in this bay, spanning north-south." | `beam_system_create` |
| "X-braces between grids 3 and 4 from GF to L1." | `brace_create` |
| "Piles under the pile caps." | `footing_create` / `family_place` with the pile and pile-cap families |

### 4.11 Model management

| Ask | Runs |
|---|---|
| "How healthy is this model?" | `model_health`: size, warnings, in-place families, imported CAD, views not on sheets, purgeable count, an attention list |
| "Add 'Pour Zone' (text) to columns, beams and slabs." | `parameter_add` (taken from, or added to, the shared parameter file, then bound) |
| "Which project parameters are there, and what is in our shared parameter file?" | `list_parameters` (`shared_file`) |
| "Purge the model, but show me the list first." | `purge` with `dry_run`, then for real |
| "Copy the view templates and filters from the office template." | `standards_copy` (template open in the same Revit) |
| "Create worksets S-Columns and S-Framing and move the columns." | `workset_create`, `workset_set` (workshared models only) |
| "Who owns these elements?" | `list_worksets` with `ids` |
| "Mark these walls as demolished in Existing." | `phase_set` |

---

## 5. Feature reference

One tool, `revit(action, params)`: 39 read actions, 9 server actions and 97 edit operations inside the single `edit`
action. Parameters for every one: [docs/ACTIONS.md](docs/ACTIONS.md).

### 5.1 Read actions (never change the model)

| Group | Action | What it returns |
|---|---|---|
| Model | `ping` | Bridge and Revit version, active model and view, edit permission |
| | `get_project_info` | Title, path, workshared / read-only, project information, phases, structural counts |
| | `list_levels`, `list_grids` | Levels by elevation; grids with end points (arc grids with mid point and radius) |
| | `list_views`, `list_sheets` | Views by type, level, scale; sheets with their views |
| | `count_by_category` | Elements per category, largest first |
| | `list_types` | Types of a category with instance counts (the names edit operations use) |
| | `list_materials` | Materials and their class |
| | `list_warnings` | Revit's warnings with the elements involved, counted per message |
| Elements | `get_elements` | Elements by category, id, type, level or view: location and the parameters you name |
| | `get_element` | One element in full: every parameter, location, bounding box, host |
| | `get_selection` | Your current selection |
| | `show_elements` | Selects and zooms to elements (changes the view only) |
| Links and CAD | `list_links` | Revit links and linked / imported CAD files |
| | `get_cad_geometry` | Lines, arcs, circles, polylines of a DWG / DXF by layer, in model coordinates |
| | `cad_columns` | Column outlines on a layer with size, rotation and matched type (what `columns_from_cad` would place) |
| Quantities | `takeoff` | Count, length, area, volume, grouped by type, level or both |
| | `material_takeoff` | Volume and area per material and category |
| | `formwork_takeoff` | Formwork areas: sides, soffits |
| | `steel_takeoff` | Steel by section: count, length, kg, kg/m |
| | `rebar_takeoff` | Bars, length, weight, grouped |
| | `rebar_list` | Every bar set for a bar bending schedule: member, mark, type, diameter, shape, number, lengths, A..J, weight |
| | `list_schedules`, `get_schedule_data` | Schedules, and the rows of one as displayed |
| Model QA | `check_materials` | Missing, default or wrongly named materials; steel with a non-metal material |
| | `find_overlaps` | Measured overlap pairs and direct coincidence or containment evidence |
| | `check_names` | Names outside a naming convention (views, sheets, types, levels, grids, materials, families) |
| Model management | `model_health` | File size, warnings, in-place families, CAD, links, views not on sheets, unused templates and groups, purgeable count, attention list |
| | `list_parameters` | Project / shared parameters with GUID, data type, group, binding and categories; shared parameter file contents |
| | `list_worksets` | Worksets, and per element: workset, checkout status, owner, creator, last changed by |
| | `list_phases` | Phases, phase filters, design option sets and options |
| Detailing and documentation | `list_documentation` | View templates, filters, revisions, scope boxes, title blocks |
| | `list_detailing` | Line styles, filled region types, detail components, detail groups, drafting views and legends (this model or another open one) |
| Meta | `family_inspect` | Inside a loaded family: parameters, solids with material and link, nested families |
| | `get_edit_permission` | Whether editing is allowed now |
| | `list_actions`, `describe_api` | What the installed add-in supports; Revit API members (developer aid) |

### 5.2 Server actions (answered by the MCP server)

| Action | What it does |
|---|---|
| `help` | Every action and parameter, or one topic; flags differences between server and add-in |
| `qs_report` | QS workbook (.xlsx) from the read actions; reads only, writes the file |
| `bbs_report` | Bar bending schedule workbook (.xlsx): BBS sheet, summary by diameter and member |
| `params_to_excel` / `params_from_excel` | Parameters out to Excel and the changed cells back (the second changes the model) |
| `excel_template` / `excel_to_model` | Input workbook, and the model built from it in one batch (the second changes the model) |
| `list_revit_sessions` / `select_revit` | Running Revits with the add-in, and which one receives the calls |

### 5.3 Edit operations (inside `edit`, after the one-time permission)

| Group | Operation | What it does |
|---|---|---|
| Levels and grids | `level_create` | Level, optionally with its plan view |
| | `grid_create`, `grid_layout` | One straight or arc grid; a whole orthogonal grid named 1, 2, 3 / A, B, C |
| | `grid_extents` | Stretch grids so they show in every level's plans |
| Structure | `column_create` | Columns at points, base to top level or by height |
| | `beam_create` | Beams: straight, curved (mid point) or sloped (z per point) |
| | `wall_create` | Straight or curved walls, to a top level or by height |
| | `floor_create` | Floor, structural slab or foundation slab from a boundary, with openings; sloped with a slope arrow |
| | `footing_create`, `wall_foundation_create` | Isolated footings; strip foundations under walls |
| | `family_place` | Any level-based family at points |
| Modelling | `slab_shape`, `beam_system_create`, `brace_create` | Slab shape points; beam systems at a spacing; braces between 3D points |
| Documentation | `filter_create`, `filter_apply`, `color_by_parameter`, `graphics_override` | View filters and overrides; colour by parameter with a legend; hide / halftone |
| | `view_template_create`, `view_template_apply` | View templates |
| | `dimension_elements`, `spot_elevation` | Chain dimensions through element faces and grids; spot elevations on top faces |
| | `revision_create`, `revision_cloud_create`, `sheet_layout` | Revisions on sheets, clouds; several views and schedules on one sheet |
| Reinforcement | `rebar_column`, `rebar_beam`, `rebar_footing`, `rebar_area` | Column bars (laps, top bends, footing feet) and zoned ties; beam bars anchored into supports, lapped past stock length, zoned stirrups; footing meshes with optional U bars; slab area reinforcement. Anchorage and laps are multiples of Ø you set (defaults 40 Ø / 50 Ø), not code-calculated |
| From CAD | `columns_from_cad` | Columns from outlines, typed by size, rotated, missing sizes created |
| | `grids_from_cad` | Grids from grid lines |
| | `beams_from_cad`, `walls_from_cad` | Beams / walls from pairs of edge lines or concentric arcs, typed by width |
| | `slabs_from_cad` | Slabs from closed outlines, or from the outer outline of several layers, with openings and preview |
| Modify | `set_parameters` | Instance or type parameters; units and level / material names understood |
| | `change_type`, `type_duplicate`, `types_create` | Change type; duplicate one type; many types from a schedule |
| | `move`, `copy`, `rotate`, `mirror`, `delete`, `pin`, `rename` | The usual modify tools (`copy` with `count` makes an array) |
| | `join`, `unjoin`, `switch_join_order` | Geometry joins for chosen pairs |
| | `join_overlaps` | Join measured pairs when both elements meet the filters. Coincidence, containment and failed measurements go to review. |
| | `family_load` | Load an .rfa file |
| Materials | `fix_materials` | Materials by rule, on the type where safe, else per element, verified |
| | `family_material_set` | One material on every solid of chosen families |
| | `family_material_link` | Link the families' solids to Structural Material |
| Model management | `parameter_add`, `parameter_remove`, `purge`, `standards_copy`, `workset_create`, `workset_set`, `phase_set` | Shared parameters from a file, bound to categories; remove parameters; purge unused in passes; copy templates, filters, types, patterns, materials, title blocks, line styles from another open model; worksets; phases |
| Warnings | `fix_warnings` | Fix warnings that have one safe fix; list the rest |
| Views and annotation | `view_plan_create`, `view_3d_create`, `section_create`, `elevation_create`, `view_activate` | Views (sections along a line, a grid or through an element) |
| | `sheet_create`, `viewport_place`, `schedule_create` | Sheets, viewports, schedules |
| | `tag_elements`, `dimension_grids`, `text_note_create`, `model_lines_create` | Tags, grid chain dimensions, notes, model lines and arcs |
| Drafting and detailing | `drafting_view_create`, `callout_create`, `view_duplicate`, `view_set` | Drafting views; callouts; duplicates (plain, with detailing, dependent); scale, detail level, crop, view range |
| | `detail_lines_create`, `region_create` | Lines, arcs and polylines with a line style; filled and masking regions (polygons, holes, circles) |
| | `detail_component_place`, `detail_group_place` | Detail components at points or along lines; detail groups (active view) |
| | `views_copy_from`, `legend_create` | Drafting views and legends copied from another open model; new empty legends |
| | `repeating_detail_place` | Repeating details along lines (copied from a placed sample) |
| Sheets | `sheets_from_views` | One sheet per view: numbered, named from a template, title block, view template, scale |
| Openings | `opening_create` | Slab / roof openings, beam web sleeves and top holes, wall openings |
| | `shaft_create` | Shaft through every slab between two levels |
| Links | `cad_link`, `revit_link` | Link or import DWG / DXF; link a Revit model |
| Export and save | `export_dwg`, `export_pdf`, `export_ifc`, `export_nwc`, `export_image`, `export_schedule` | Views and sheet sets by number pattern with file names from a template; IFC, NWC, PNG, CSV |
| | `save`, `save_as` | Save; save as (not for workshared models) |

---

## 6. How the CAD readers work

Revit exposes a linked DWG's curves by layer but not its text or blocks' attributes, so recognition is geometric.

| Element | Method | Parameters that matter |
|---|---|---|
| Columns | Curves that touch (within `gap`) are grouped into one outline; the outline's oriented bounding box gives width, depth and rotation; circles give round columns; H / I shapes count as steel. Sizes are matched to the types' b / h (or bf / d, Width / Depth, Diameter, else the first `AxB` in the type name), within `tolerance`. | `gap` 25 mm, `tolerance` 10 mm, `include`, `types`, `create_types` |
| Grids | Straight lines longer than `min_length`, sorted west to east and south to north. | `min_length` 3000 mm, `x_names`, `y_names` |
| Beams, walls | Parallel lines a member width apart that overlap along their length form one member on the centre line; concentric arcs form a curved one. | `min_width`, `max_width`, `min_length`, `tolerance` |
| Slabs, `closed` | Polylines, circles, and lines / arcs meeting end to end (within `gap`) form outlines; outlines inside outlines are openings. | `gap` 5 mm, `min_area` 1 m2 |
| Slabs, `outer` | Every curve on the layers is drawn on a grid of `cell` size and thickened to close gaps; the outside is flood-filled; the boundary is traced, simplified, snapped back to CAD vertices and checked for self-crossing (falling back to the unsnapped outline). | `cell` 25 mm, `gap` 500 mm, `opening_layer`, `preview` |

Compare the result with the drawing before building on it: `cad_columns` and `preview` exist for that.

---

## 7. What it cannot do

| Not possible (yet) | What to do instead |
|---|---|
| Reinforce curved or sloped beams, walls, irregular members; path reinforcement; rebar couplers | Model them in Revit; `rebar_list` / `bbs_report` include every bar. |
| Dimensions of rotated elements, angular or radial dimensions | `dimension_elements` covers faces square to x and y (plus grids); do the rest in Revit. |
| Repeating details or legends from nothing | Revit's API has no create method. Legends: `legend_create` duplicates an existing one (copy one in first if the model has none). Repeating details: `repeating_detail_place` copies a placed sample (this model or another open one), or `detail_component_place` with `along` repeats a component. |
| Draw new geometry in the Family Editor | Families can be inspected, loaded, have types duplicated and their solids' materials set or linked. |
| Read text, dimensions or block attributes in a linked DWG | Revit does not expose them; give names (grids) or types (`types`) in the request. |
| Synchronise with central, relinquish, reload links | Do it in Revit. `save` saves the local file only. |
| Project parameters that are not shared; moving elements into a design option | Revit's API adds only shared parameters (`parameter_add` puts them in a shared parameter file first) and has no call to change an element's design option. |
| Copy object styles, browser organisation, project units, Revit links | `standards_copy` covers templates, filters, types, patterns, materials, title blocks and line styles; use Transfer Project Standards in Revit for the rest. |
| Run arbitrary code or Dynamo scripts | Left out on purpose. |
| Revit 2025 / 2026 | The sources are ready (`build -p:RevitYear=2025`); not built or tested here. |

Also good to know:

- A request waits while a command is running, a dialog is open or a sketch is in progress in Revit.
- Family changes (`family_material_set`, `family_material_link`) run after the batch is committed and can take a
  minute per family; the result lists each family with the step reached.

---

## 8. Safeguards and limits

- **Local only.** The add-in listens on `127.0.0.1` on a random port. Each Revit session writes a new random token to
  `%LOCALAPPDATA%\RevitMcpBridge\bridge.json`; a request without it is refused.
- **Model and file outcomes.** Transaction failures are reported separately from post-commit failures. CAD and bulk-join operations can report skipped or failed individual items. Dry runs roll model changes back and skip deferred work.
- **Audit log.** Every permission decision and every edit batch is written to `%LOCALAPPDATA%\RevitMcpBridge\audit.log`.
- **Overwrite protection.** Exporting, saving or writing a workbook onto an existing file needs `overwrite: true`.
- **Sanity limits.** 500 operations per batch; coordinates more than about 30 km from the origin are rejected as unit
  mistakes; requests up to 4 MB; lists are cut at their `limit` and say so (`truncated: true`); long checks
  (`find_overlaps`, `join_overlaps`) stop at a time budget and continue with `resume_after`.
- **Busy Revit.** A queued request is cancelled if it cannot start within 30 seconds. A running edit may finish after a client timeout. Check the operation receipt before submitting again.

> **Note.** The Yes / No dialog stops the tool from approving itself. It cannot stop a Claude session that also
> controls your screen from clicking "Yes", so only give such a session edit work you would approve yourself.

---

## 9. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "Revit is not reachable" | Revit is not running, or the add-in did not load. Look for **Add-Ins > Claude MCP**. If it is missing, check `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpBridge.addin` and restart Revit; answer the load question with *Always Load*. |
| "Could not reach Revit on port ..." | That Revit was closed, or two are open. Ask "use Revit 2027" or press **MCP Status** in the one you want. |
| "No model is open in Revit" | Open a project. |
| "Revit did not become available within 30 s" | A command, dialog or sketch is active in Revit. Finish or cancel it (Esc) and ask again. |
| "Unknown action" / "Unknown edit operation" | Claude is still running the old server. Restart Claude completely. |
| `help` shows `missingInPlugin` | Revit is still running the old add-in. Restart Revit. |
| "No type '...'" | The family or type is not loaded. The message lists what is available. |
| "Editing was declined in Revit a moment ago" | You answered No. Wait 30 seconds, or press **Allow Editing**. |
| The edit request seems to hang | The permission dialog is waiting in Revit, possibly behind another window. |
| Material fixed but the element still shows the old one | The family's solids are on *By Category* or a fixed material: `family_inspect`, then `family_material_set`. |
| A CAD builder finds nothing | Wrong layer or broken outlines: read the layer with `get_cad_geometry`; for slabs try mode `outer` with `preview`. |
| `EXIT_2024` is not 0 but 2027 built | No internet for the one-time NuGet download, see the note in section 1. |

Where to look: `build_result.txt` (build), `setup_result.txt` (server setup), `tests\results\` (tests),
`%LOCALAPPDATA%\RevitMcpBridge\audit.log` (what was edited).

---

## 10. For developers

```
plugin/                       the Revit add-in (C#; .NET 10 for 2027, .NET Framework 4.8 for 2024)
  App.cs, BridgeInfo.cs       entry point, ribbon buttons, version
  Bridge/                     socket server; marshalling onto Revit's main thread (ExternalEvent)
  Core/
    Json.cs, Args.cs          JSON and request parameters (Str, Dbl, Int, Choice, Ref ...)
    Units.cs                  every unit conversion (request units <-> feet, mm / m / m2 / m3)
    Registry.cs               read actions; WriteGate.cs permission; Audit.cs log
    Find.cs                   lookups: levels, types, views, categories, element scope, families
    Describe.cs               element output, labels, parameter values, paging of long lists
    Materials.cs              an element's materials, steel test, material by name
    Geo.cs, Poly.cs           solids and overlaps; 2D polygons (area, inside, tidy, self-crossing, CurveLoop)
    Cad.cs, CadPairs.cs,      CAD curves by layer, column outlines, edge-line pairs, closed loops,
    CadRaster.cs,             outer outline on a grid,
    CadSections.cs            section sizes of types and creating sized types
  Read/                       read actions, one file per group (ModelRead, ElementRead, QuantityRead, QaRead ...)
  Edit/                       EditEngine (one transaction, dry run, after-commit actions, failure handling),
                              EditContext, and one *Ops.cs file per group of operations
server/
  server.py                   the MCP tool(s) and the dispatch tables
  catalog.py                  all wording Claude sees; groups mirror the add-in files
  bridge_client.py            connection, token, session selection
  qs_report.py, excel_io.py   workbooks; xlsx_util.py shared header style, path checks, cell values
tests/                        smoke_test.py (no Revit needed), live_read.py (against the open model)
tools/gen_docs.py             writes docs/ACTIONS.md from the catalogue
```

**Adding an action**

- Read action: a method in `plugin/Read/<Group>Read.cs`, one `Read("name", ...)` line in `Core/Registry.cs`, one entry in
  `server/catalog.py`.
- Edit operation: a method in `plugin/Edit/<Group>Ops.cs` registered in its `Register`, one catalogue entry.
- Server action: a function in `server.py` added to `LOCAL_READ` or `LOCAL_EDIT`, one entry in `catalog.LOCAL_ACTIONS`.
- Then `python tools/gen_docs.py` and `tests\run_smoke_test.bat`. The test fails when the catalogue and the add-in
  disagree on names or parameter names, when the versions in `BridgeInfo.cs` and `catalog.py` differ, or when this README
  does not state the version and the counts.

**Conventions**

- Lengths cross the bridge through `U.In` / `U.Out` / `U.Len` (`Core/Units.cs`); Revit itself works in feet. No unit
  literals elsewhere.
- Use the shared helpers (`Find`, `Describe`, `Materials`, `Poly`, `Sections`) rather than local copies; the smoke test and
  a compile against both Revit APIs are the gate for every change.
- Built-in parameters are looked up by name at run time (`Find.Builtin`), so a name missing in one Revit release cannot
  break the build for the other.
- Work that cannot run inside the batch transaction (exports, family edits) is added to `EditContext.AfterCommit` and is
  skipped in a dry run.
- Revit elsewhere: copy `plugin\local.props.example` to `local.props` and set `RevitDir`.

---

## 11. Roadmap

Planned features with effort and status, by phase: [ROADMAP.md](ROADMAP.md). History: [CHANGELOG.md](CHANGELOG.md).

## 12. Licence

Copyright (c) 2026 Mohamed Ahmed Hamed.

This project is source-available under the [PolyForm Noncommercial License 1.0.0](LICENSE.md):

- You may read, modify and use it for non-commercial purposes (personal projects, study, research, hobby work, and use by
  charities, schools and public bodies).
- Commercial use, including use on paid projects and selling, needs written permission from the copyright holder. Contact
  the author via GitHub to ask for a commercial licence.

Contributions are welcome through pull requests. By submitting one, you agree that it may be distributed under this
licence.

Autodesk and Revit are trademarks of Autodesk, Inc. This project is not affiliated with or endorsed by Autodesk. No
Autodesk files are included; the add-in is built against the Revit API installed on your computer.
