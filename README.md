# Revit MCP bridge

**Version 0.31 · Autodesk Revit 2027 and 2024 · Windows**

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

### Option A - installer zip (any PC, recommended)

Needs Revit 2024 and/or 2027 and an MCP client: Claude Desktop, the ChatGPT desktop app, or Codex. **Nothing else**:
the package includes the MCP server as one exe, so no Python, no .NET SDK and no build.

1. Close Revit and quit Claude Desktop completely (tray icon > Quit). Claude Desktop can otherwise write its own copy
   of its settings over the new entry, and it keeps the server exe in use; `install.bat` stops while either runs
   (`install.bat /claude_open` installs anyway).
2. Download **`RevitMcp.zip`** (from this repository's Releases, or built with `build_package.bat`), unzip it and
   double-click **`install.bat`** as the Windows user who runs Revit. Everything goes into that user's profile, so
   do not use *Run as administrator* (it is not needed): an elevated window running under another account (e.g.
   an admin's credentials on a domain controller) would install into that account. `install.bat` stops when its
   account is not the desktop user; `install.bat /this_user` installs for the running account on purpose. It
   - installs the add-in for each Revit it finds: `%APPDATA%\Autodesk\Revit\Addins\<year>\` (`RevitMcpBridge.addin` +
     `RevitMcpBridge\RevitMcpBridge.dll`), and clears the "downloaded from the internet" mark that stops Revit 2024
     loading a DLL from a zip;
   - installs the server: `%LOCALAPPDATA%\RevitMcpBridge\revit-mcp-server.exe`;
   - **adds `"revit"` to Claude Desktop** (`claude_desktop_config.json`, normal and Microsoft Store installs) **and to
     Codex** (`~/.codex/config.toml`), keeping every other server (`civil3d` ...) and setting. The previous file is
     saved next to it as `.bak-<date>`. The server exe does this itself (`--configure-clients`), so company policies
     that block PowerShell scripts cannot stop it; it also creates the file when Claude Desktop was never started, and
     lists every file it changed (or why it could not) in the window and in `install_result.txt`.
3. Start Claude Desktop again.
4. Start Revit. The first time it asks whether to load the unsigned add-in: choose **Always Load**. A panel
   **Claude MCP** appears on the **Add-Ins** tab.
5. Open a model and ask: *"Ping Revit and tell me which model is open."* The answer shows the add-in version (0.31.0),
   the Revit version, and the active model's `documentId`.

**ChatGPT desktop app.** It shares Codex's settings file (`~/.codex/config.toml`), so the `revit` entry that
`install.bat` writes there serves it too: restart the app and `revit` appears under Settings > MCP servers. By hand:
Settings > MCP servers > Add server, name `revit`, STDIO, the server path printed by `install.bat` as the command (no
arguments), Save, Restart. The installer's entry also sets `tool_timeout_sec = 3600`: Codex and ChatGPT stop a tool
call after 60 s by default, shorter than a long edit or batch read.

If `install.bat` could not change a client's settings, it says so; add this inside `"mcpServers"` by hand (the
snippets are also saved next to the server):

```json
"revit": { "command": "C:\\Users\\<you>\\AppData\\Local\\RevitMcpBridge\\revit-mcp-server.exe" }
```

**Update:** close Revit and the MCP client, run the new `install.bat`, start both again. **Uninstall:** `uninstall.bat`
removes the add-in, the server and the `revit` entry from Claude Desktop and Codex.

### Option B - from this source folder (the development PC)

Needs the .NET 10 SDK and Python 3.10+.

1. Close Revit (or not: the loaded DLL is moved aside, and the new one loads at the next start).
2. Double-click **`build.bat`**. It builds the add-in once per installed Revit and copies it to
   `%APPDATA%\Autodesk\Revit\Addins\<year>\`. The result is in `build_result.txt` (`EXIT_2027=0` / `EXIT_2024=0` means
   built and deployed).
3. Double-click **`setup_server.bat`** (once). It creates `server\.venv`, installs `mcp` and `openpyxl` and writes
   `claude_config_snippet.json` and `codex_config_snippet.toml` using the current project folder.
4. In Claude: **Settings > Developer > Edit Config**. Add the `"revit"` entry from the snippet to `mcpServers`:
   ```json
   "revit": {
     "command": "C:\\path\\to\\revit-mcp\\server\\.venv\\Scripts\\python.exe",
     "args": ["C:\\path\\to\\revit-mcp\\server\\server.py"]
   }
   ```
5. Restart Claude completely and continue with steps 4 and 5 of option A.

**`build_package.bat`** makes, in `dist\`, the installer zip of option A (`RevitMcp.zip`) and a clean source zip
(`RevitMcp-source.zip`). It builds each Revit year against the installed Revit or against `refs\<year>\RevitAPI.dll`
(which `build.bat` copies there), else reuses `plugin\prebuilt\<year>\`; `package_build.txt` ends with `RESULT=OK`.

### Other clients and notes

For **local Codex on Windows** the installer (option A) or `codex_config_snippet.toml` (option B) adds the server with
separate `revit_read` and `revit_edit` tools.

For **ChatGPT web**, a workspace with custom MCP and Secure MCP Tunnel access can route the local stdio command through a private tunnel. See the [OpenAI Secure MCP Tunnel setup](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels).

**After an update**, restart Revit and the MCP client to load matching versions. The server requires an add-in with
bridge protocol 4 for editing. See the [implementation log](docs/IMPLEMENTATION_LOG.md) for the changes.

> **Revit 2024 note (building only).** The 2024 build targets .NET Framework 4.8 and downloads the package
> `Microsoft.NETFramework.ReferenceAssemblies` from nuget.org on the first build. Without internet only the 2024
> build fails; 2027 is unaffected.

---

## 2. Permissions

You are asked once in each program, and never per edit.

| Where | What you are asked | How often |
|---|---|---|
| Claude | Allow the tool `revit` | Once ("always allow") |
| Revit | "Allow Claude to edit models in this session?" | Once per Revit session, at the first edit |
| Revit | "Claude - change the central model?" | Every `sync_with_central` and every `relinquish` |

- **Reading never asks anything** in Revit.
- After **Yes**, no later edit asks again, in any open model, until Revit is closed.
- After **No**, nothing is changed, and edits are refused for 30 seconds so that a retry cannot nag you.
- The permission is deliberately not remembered: after every Revit restart the first edit asks again.
- **Synchronising with central and relinquishing ask every time.** They reach the model other people work in and cannot
  be undone, so the session permission is not enough: Revit shows its own Yes / No dialog for that one action. The first
  call returns "not granted"; after **Yes** the same request, sent again within 5 minutes, runs once.

Buttons on **Add-Ins > Claude MCP**:

| Button | Effect |
|---|---|
| **MCP Status** | Shows version, port and whether editing is allowed. With several Revit windows open (e.g. 2024 and 2027), makes **this** one the one Claude talks to. |
| **Allow Editing** | Allows editing for this session without the dialog. |
| **Stop Editing** | Switches editing off again. Reading keeps working. |

> **Read-only from the Claude side.** Set the environment variable `REVIT_MCP_SPLIT=1` for the server: Claude then
> shows two tools, `revit_read` and `revit_edit`, which you can allow separately.

> **Tool description size.** By default the description Claude receives lists every action and edit operation with its
> summary and parameters (about 49,000 characters, carried by every conversation). Set `REVIT_MCP_DESCRIPTION=compact`
> for the server to list names and parameter names only (about 20,000 characters); Claude then asks `help` for what an
> action does and what its parameters mean.

---

## 3. How to use it

Open the model in Revit, then ask Claude in plain language. Claude picks the actions, reads what it needs first
(levels, types, grids, the CAD layers) and then sends one edit batch.

### Rules that apply to every request

| Rule | What it means for you |
|---|---|
| **No design** | The bridge models, reads, checks and documents. It never sizes members or reinforcement and applies no design code: engineering values come from you. |
| **Millimetres by default** | Say "in metres" and Claude sends `units: "m"`. Results are always m, m2, m3, kg and t. |
| **Model transaction** | Model operations use one transaction and one Undo step. Save, export, view activation, and family reloads have separate `postCommit` outcomes. |
| **Dry run first** | Ask for a dry run before anything large: the batch is executed and rolled back, so you see counts, errors and warnings with no change. Purge previews list first-pass candidates without deleting. A purge-only preview opens no transaction. Exports, saves and family changes are skipped in a dry run. |
| **Types come from the model** | Claude only uses loaded families and types ("list the beam types"). `family_load`, `type_duplicate`, `types_create` and `create_types` add more. |
| **Select, then ask** | "the selected elements" works: Claude reads your current selection. `show_elements` selects and zooms to what it found. |
| **Already modelled is skipped** | The CAD builders skip columns, beams, walls, slabs and grids that already exist at that place (`skip_existing: false` to switch off). |
| **Coordinates** | Geometry uses Revit model coordinates. In `list_levels`, `elevation` is project-relative and `displayElevation` follows Revit's elevation base. |
| **Workshared models** | Elements owned by another user make the batch fail with a message. Test on a detached copy. |

### Several Revits at once

Two or more Revit windows (2024 and 2027, or two of the same year) can run at once:

- With **one** Revit open, Claude uses it, whatever was open or crashed before.
- With **several** open, reads go to the *default* one: the first Revit started, or the one where **MCP Status** was
  last pressed. Opening a second Revit does **not** move Claude to it. `ping` then lists the other sessions.
- **Edits are refused** while several are open and none is chosen: *"list the open Revits"* (`list_revit_sessions`
  shows each one's model), then *"use the Revit with model X"* (`select_revit` with its pid). Every following call
  goes to that one; `select_revit` with pid `auto` goes back to automatic choice.
- Closing the default Revit makes another open one the default; a crashed Revit's leftover files are ignored and
  removed by the next Revit that starts.

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

### 4.1a Read a linked Revit model

| Ask | Runs |
|---|---|
| "Which Revit models are linked, and where are they placed?" | `list_links`: file, path, loaded, document, transform (origin and plan rotation) |
| "How many columns and walls are in the architectural link?" | `count_by_category` with `link` |
| "List the link's columns on Level 2 with their Mark, in our coordinates." | `get_elements` with `link`, `category`, `level` (the link's level name), `params`; locations in host coordinates |
| "Compare the link's grids and levels with ours." | `list_grids` and `list_levels`, once without and once with `link` |
| "Only the link's elements visible in plan FIRST FLOOR." | `get_elements` with `link` and `view` (a host view) |
| "Where are our base point and survey point, and the angle to true north?" | `get_coordinates` |
| "Is the architectural link placed by shared coordinates?" | `get_coordinates` with `link`: `sharedCoordinatesAgree`, else the offset and rotation |
| "Clash our beams and columns against the MEP link." | `link_clashes` with `link`, `category`; pairs with volume and a point, by category pair |
| "Reload the architectural link" / "reload it from the new issue" / "unload it" | `link_reload` (with `path`), `link_unload` (after the commit) |
| "Pin every link and set them to overlay." | `link_set` with `pinned`, `attachment` |
| "Which dimensions on sheet S-101 have overridden text?" | `list_annotations` with `sheet`, `only_issues` |

Check: ids, types and levels in the result belong to the linked model; `coordinates: link` gives the linked model's own
coordinates instead of host coordinates. A link must be loaded to be read.

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
| "Column schedule: only GF, sorted by type with a header per type, volume totalled in m3 with 2 decimals." | `schedule_set` (filters, sort / group, fields format) |
| "Dimension these walls along their 30-degree direction." | `dimension_rotated` (angle 30) |
| "Angle between grid A and grid 1R." | `dimension_angular` |
| "Radius of the curved grid and the curved beam." (Revit 2027) | `dimension_radial` |
| "Spot coordinates on the corner columns." | `spot_coordinate` |

### 4.9 Reinforcement

| Ask | Runs |
|---|---|
| "Reinforce these columns: 4 x 3 bars T20, ties T10 @ 150, cover 40, top auto, lap 50 Ø, top bend 300, bottom on the footing with 300 feet, footing cover 75." | `rebar_column` (bars_x 4, bars_y 3, top auto, lap_d 50, bend_leg 300, bottom footing, foot_length 300, footing_cover 75) |
| "Same, with ties @ 100 over 600 at each end and T10 @ 150 inside the footing." | add end_zone 600, end_spacing 100, starter_tie_spacing 150 |
| "Beams on FF: 3 T16 bottom, 2 T12 top, stirrups T8 @ 150, cover 40, anchorage 40 Ø with 250 bends, laps 50 Ø, 12 m bars." | `rebar_beam` (anchorage_d 40, bend_leg 250, lap_d 50, stock_length 12000) |
| "Footings F1: T16 @ 200 bottom both ways, cover 75, top mesh T12 @ 250, bottom bars turned up." | `rebar_footing` (top, top_bar, top_spacing, end_bends) |
| "Slab: T12 @ 200 top and bottom both ways." | `rebar_area` (layers both) |
| "Which bar, hook, fabric and coupler types does the model have?" | `list_reinforcement_types` |
| "Slab edge: T12 @ 200, 1500 long, along this line, bottom face." | `path_reinforcement` (bar, spacing, length, face) |
| "Wall dowels: T16 @ 150, 1200 long, along the wall base, alternating T12 1000 long." | `path_reinforcement` (alternating) |
| "Mesh A393 over this slab, bottom, laps 400 / 300." | `fabric_area` (sheet_type, location, lap_major, lap_minor) |
| "Add 90° hooks at the start of these bars, turned right." | `rebar_hooks` (start, start_orientation) |
| "Couple bar 1234 (end) to bar 1240 (start) with the T20 coupler." | `coupler_place` |
| "Annotate the bottom bars in Section 1: dimension and tag." | `rebar_annotate` |
| "Show the rebar numbering and remove the gaps in partition B1." | `list_numbering`, `rebar_numbering` (remove_gaps) |
| "(Revit 2027) Splice these bars: 12 m maximum, lap 50 Ø." | `splice_type_create` (lap_d 50), `rebar_splice` (rules max_length 12000) |
| "BBS of everything on the GF to Excel." | `bbs_report` |

**No design.** The reinforcement operations place the reinforcement you specify; they do not design it. Every
engineering value (counts, spacings, cover, anchorage, laps, bend lengths, end zones, stock length) must be given, and
nothing is taken from a code. A missing value stops the operation with a list of what to give. Where the geometry
cannot take a value (an anchorage that does not fit straight, a foot or bend limited by the member), the result says so,
for you to check against your design.

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
| "Flip these doors / this wall." | `flip` (facing, hand, work plane, wall orientation) |
| "Don't let this beam join at its end; extend it 150 mm past the start." | `end_join` (allow false; extension) |
| "Put the embed plate family on the side face of beam 1723276 at these points." | `family_place_hosted` (host face, or a level / reference plane) |
| "Group these footings and place the group at 3 more points; ungroup the old one." | `group_create`, `group_place`, `ungroup` (`list_groups`) |
| "Make an assembly of this precast beam and its bars, with 3D, front and section views and a parts list." | `assembly_create`, then `assembly_views` (`list_assemblies`) |
| "What exactly is this element's geometry: faces, volume, original shape?" | `get_geometry` (also inside a link) |
| "Fire dampers of type FD-1 where supply duct 512345 crosses the fire walls 98765 and 98766 of the architecture link." | `mep_crossings` (find them), then `mep_accessory_place` with `items` [{run, crossing}] and `crossing_link` |
| "A volume damper on this duct 1.5 m after the tee; a gate valve on pipe 61234 at this point." | `mep_accessory_place` with `items` [{run, point}] |
| "50 mm duct insulation of type X on the whole supply system; which ducts have none?" | `mep_insulation_add` with `system`, `type`, `thickness`; `mep_insulation` with `missing_only` |
| "Sleeves of type PS-1 where the drainage stacks pass the slabs: outside diameter plus 50 mm, length the slab depth." | `mep_crossings` (find them), then `sleeve_place` with `set` {"Diameter": "run_outside_diameter + 50", "Length": "crossing_length"} |
| "Pendent sprinklers at these points 2.8 m above Level 1, each dropped to branch pipe 70123." | `mep_fixture_place` with `type`, `points`, `height`, `main` |
| "WC and basin at these points, then a 110 mm soil pipe from the WC at 2 % fall to the stack." | `mep_fixture_place`, then `mep_route` with `from` {element, classification: Sanitary} and `slope` 2 |
| "Double sockets on wall 4521 of the architecture link at 300 mm; circuit them on DB-1, 20 A, 'Sockets L1'." | `mep_fixture_place` with `walls` and `wall_link`, then `electrical_circuit_create` |
| "Which panels and circuits are there, their loads, and which devices are on no circuit?" | `electrical_circuits` |
| "A cable tray along this path, teed into the main tray." | `mep_route` with `kind` cable_tray and `to_main` (or `mep_connect`) |
| "MEP quantities to Excel: lengths by system, size and type, fittings, insulation, circuits." | `mep_report` with `path` |

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
| "Global parameter 'Slab Cover' = 25 mm, driving the cover of these slabs." | `global_parameter_create`, `global_parameter_associate` (`list_global_parameters`) |
| "What are the structural and thermal properties of C30?" | `get_material` |
| "Set C30's density to 2500 kg/m3 and E to 27000 MPa." | `material_asset_set` (a shared asset is copied for this material unless `change_shared`) |
| "Which types have no keynote or assembly code?" | `list_keynotes` |
| "Export the GA sheets to DWF and DGN, the 3D view to FBX, the model to gbXML." | `export_dwf`, `export_dgn`, `export_fbx`, `export_gbxml` |

### 4.12 Architecture

| Ask | Runs |
|---|---|
| "Make an external wall type: 15 gypsum, 200 block (core), 50 insulation, 15 gypsum." | `layered_type_create` (kind wall; floors, roofs and ceilings too); `type_layers` reads them back |
| "Put door D01 in wall 4521, 3 m from its start; window W01 at this point on the south wall with a 900 sill." | `door_window_place` (by distance, by point, or by point alone with `level`) |
| "Tag the rooms, doors and windows on L1." | `tag_elements` with `category` rooms, doors, windows |
| "Door, window, room and finish schedules." | `schedule_create` with `preset` doors / windows / rooms / room_finishes |
| "Floor, ceiling and wall areas per room with their finishes, net of the doors and windows." | `room_finishes`, or the workbook: `room_report` with `path` |
| "Room names, numbers, finishes from Excel." | `params_to_excel` (category rooms), edit, then `params_from_excel` |
| "Any unplaced or untagged rooms, doors without marks, duplicate marks?" | `arch_qa` |
| "Gross building area of L1: area plan, boundary on the outside faces of the walls, one area." | `area_plan_create`, `area_boundary` with `from_walls` outside, `area_create` |
| "A ceiling 2700 above L1 inside the walls; a gable roof on L2 at 30 degrees on the long sides." | `ceiling_create` with `height`; `roof_create` with `slopes` [30, null, 30, null] |
| "Curtain wall grid every 1500, a transom at 1000, 2.5 in circular mullions, a solid panel bottom left." | `curtain_grid_set` with `u`, `v`, `mullion_type`, `panels` |
| "Stairs L1 to L2: 21 risers, 280 treads, 1200 wide, two runs with a landing, glass railings." | `stairs_create` with `risers`, `tread_depth`, `width`, `runs`, `railing_type` |
| "A guardrail along this edge; slab edges round the L2 slab; a cornice at 900 on the south wall; the site from survey points." | `railing_create`, `slab_edge_create`, `wall_sweep_create`, `toposolid_create` |

---

## 5. Feature reference

One tool, `revit(action, params)`: 83 read actions, 10 server actions, 4 worksharing actions and 184 edit operations inside the single `edit`
action. Parameters for every one: [docs/ACTIONS.md](docs/ACTIONS.md).

### 5.1 Read actions (never change the model)

| Group | Action | What it returns |
|---|---|---|
| Model | `ping` | Bridge and Revit version, active model and view, edit permission |
| | `get_project_info` | Title, path, workshared / read-only, project information, phases, structural counts |
| | `list_levels`, `list_grids` | Levels by elevation; grids with end points (arc grids with mid point and radius); also of a link |
| | `list_views`, `list_sheets` | Views by type, level, scale; sheets with their views |
| | `count_by_category` | Elements per category, largest first; also inside a link |
| | `list_types` | Types of a category with instance counts (the names edit operations use); also of a link |
| | `list_materials` | Materials and their class |
| | `list_warnings` | Revit's warnings with the elements involved, counted per message |
| Elements | `get_elements` | Elements by category, id, type, level or view: location and the parameters you name; with `link`, a linked model's elements in host coordinates |
| | `get_element` | One element in full: every parameter, location, bounding box, host; also inside a link |
| | `get_selection` | Your current selection |
| | `show_elements` | Selects and zooms to elements (changes the view only) |
| Links and CAD | `list_links` | Revit links (path, loaded, IFC, transform) and linked / imported CAD files |
| | `get_coordinates` | Base points, survey point, internal origin, true north, site; for a link, whether shared coordinates agree |
| | `link_clashes` | Hard clashes with a Revit / IFC link: pairs, volume, point, by category pair |
| | `list_annotations` | Dimension values and shown text (overrides), spots, tag texts, text notes, with QA flags |
| | `get_cad_geometry` | Lines, arcs, circles, polylines of a DWG / DXF by layer, in model coordinates |
| | `cad_columns` | Column outlines on a layer with size, rotation and matched type (what `columns_from_cad` would place) |
| Quantities | `takeoff` | Count, length, area, volume, grouped by type, level or both |
| | `material_takeoff` | Volume and area per material and category |
| | `formwork_takeoff` | Formwork areas: sides, soffits |
| | `steel_takeoff` | Steel by section: count, length, kg, kg/m |
| | `rebar_takeoff` | Bars, length, weight, grouped |
| | `rebar_list` | Every bar set for a bar bending schedule: member, mark, type, diameter, shape, number, lengths, A..J, weight; fabric sheets and couplers |
| Clearances and crossings | `ray_cast` | What is above / below / beside a point or element, into links |
| | `clearances` | Clear distance under (or over) each element of a category, smallest first |
| | `mep_crossings` | MEP runs passing through beams, walls, slabs, columns (MEP and structure links) |
| MEP | `mep_systems`, `mep_quantities` | Systems; run lengths by size / type / system; fittings and equipment counts |
| Spaces, analysis | `list_spaces` | Rooms, areas, spaces: area, volume, unplaced / not enclosed |
| | `analytical_model`, `list_loads` | Analytical members / panels, releases; load cases, combinations, loads |
| | `steel_connections` | Steel connections: type, members, approval |
| MEP / HVAC checks | `mep_flow`, `mep_connectivity`, `air_terminals` | Revit's flow / velocity / friction / pressure drop with limits you give; open ends and elements in no system; terminals vs space airflow |
| | `mep_insulation` | Insulation and lining per duct / pipe / fitting / accessory: type, thickness, totals by type, elements with none |
| | `electrical_circuits` | Panels, circuits and their loads as Revit computes them; loads on no circuit |
| Documents | `list_documents`, `model_files` | Open models (document_id to read any of them); file facts of a folder without opening |
| | `model_open`, `model_close`, `model_new` | Open (workshared always detached), close what Claude opened, new scratch project; never saves |
| | `batch_read` | One read over many files: open in the background, read, close without saving |
| Worksharing | `worksharing_status` | Central path, what you hold, per element: owner, checkout, current with central |
| | `sync_with_central`, `reload_latest`, `relinquish`, `checkout` | Own actions (not edit operations), each needs the edit permission; sync and relinquish also `confirm: true` |
| | `list_numbering` | Rebar, fabric and coupler numbering: partitions, numbers in use, gaps |
| | `list_reinforcement_types` | Bar, hook, path / area / fabric, fabric sheet and wire, coupler and rebar annotation types |
| | `list_schedules`, `get_schedule_data` | Schedules, and the rows of one as displayed |
| Model QA | `check_materials` | Missing, default or wrongly named materials; steel with a non-metal material |
| Architecture | `type_layers`, `room_finishes`, `arch_qa` | Layers of wall / floor / roof / ceiling types; finishes and measured floor, ceiling and net wall areas per room with its doors and windows; room, door and window QA |
| | `find_overlaps` | Measured overlap pairs and direct coincidence or containment evidence |
| | `check_names` | Names outside a naming convention (views, sheets, types, levels, grids, materials, families) |
| Model management | `model_health` | File size, warnings, in-place families, CAD, links, views not on sheets, unused templates and groups, purgeable count, attention list |
| | `list_parameters` | Project / shared parameters with GUID, data type, group, binding and categories; shared parameter file contents |
| | `list_worksets` | Worksets, and per element: workset, checkout status, owner, creator, last changed by |
| | `list_phases` | Phases, phase filters, design option sets and options |
| Detailing and documentation | `list_documentation` | View templates, filters, revisions, scope boxes, title blocks |
| | `list_detailing` | Line styles, filled region types, detail components, detail groups, drafting views and legends (this model or another open one) |
| Groups, assemblies and geometry | `list_groups`, `list_assemblies` | Group types with instances and members; assemblies with members and their views |
| | `get_geometry` | Raw solids, faces, curves of one element (as modelled or original; also in a link) |
| Standards and data | `list_global_parameters` | Global parameters with value, formula, reporting, driven elements |
| | `get_material` | Material identity, graphics, structural / thermal / appearance assets and who shares them |
| | `list_keynotes` | Keynote and assembly code files, and each type's keynote / assembly code |
| Meta | `family_inspect` | Inside a loaded family: parameters, solids with material and link, nested families |
| | `get_edit_permission` | Whether editing is allowed now |
| | `list_actions`, `describe_api` | What the installed add-in supports; Revit API members (developer aid) |

### 5.2 Server actions (answered by the MCP server)

| Action | What it does |
|---|---|
| `help` | Every action and parameter, or one topic; flags differences between server and add-in |
| `qs_report` | QS workbook (.xlsx) from the read actions; reads only, writes the file |
| `room_report` | Room data and finishes workbook (.xlsx): rooms, finishes with measured areas, totals per finish, wall faces by type, openings, QA; reads only, writes the file |
| `mep_report` | MEP quantities workbook (.xlsx): runs by system / size / type / level, fittings and devices, insulation, panels and circuits; reads only, writes the file |
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
| Reinforcement | `rebar_column`, `rebar_beam`, `rebar_footing`, `rebar_area`, `path_reinforcement`, `fabric_area`, `fabric_sheet_place`, `rebar_hooks`, `coupler_place`, `rebar_annotate`, `rebar_numbering`, `splice_type_create` and `rebar_splice` (2027) | Column bars (laps, top bends, footing feet) and zoned ties; beam bars anchored into supports, lapped past stock length, zoned stirrups; footing meshes with optional U bars; slab area reinforcement. All values from the engineer (no defaults, no code checks) |
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
| | `flip`, `end_join` | Flip facing / hand / work plane / wall; allow or disallow end joins, framing end extensions |
| | `family_place_hosted` | Face-based families on a host face; work-plane-based families on a level or reference plane |
| Groups and assemblies | `group_create`, `group_place`, `ungroup` | Model / detail groups; place model groups at points; ungroup |
| | `assembly_create`, `assembly_views` | Assemblies; 3D, elevation / section views, parts list, take-off and sheet of an assembly |
| Standards and data | `global_parameter_create`, `global_parameter_set`, `global_parameter_associate` | Global parameters: create, value or formula, drive element / type parameters |
| | `material_asset_set` | Structural / thermal asset values and appearance colour of a material |
| Materials | `fix_materials` | Materials by rule, on the type where safe, else per element, verified |
| | `family_material_set` | One material on every solid of chosen families |
| | `family_material_link` | Link the families' solids to Structural Material |
| Model management | `parameter_add`, `parameter_remove`, `purge`, `standards_copy`, `workset_create`, `workset_set`, `phase_set` | Shared parameters from a file, bound to categories; remove parameters; purge unused in passes; copy templates, filters, types, patterns, materials, title blocks, line styles from another open model; worksets; phases |
| Warnings | `fix_warnings` | Fix warnings that have one safe fix (unjoin; on request delete identical instances or overlapped lines); list the rest |
| Views and annotation | `view_plan_create`, `view_3d_create`, `section_create`, `elevation_create`, `view_activate` | Views (sections along a line, a grid or through an element) |
| | `sheet_create`, `viewport_place`, `schedule_create` | Sheets, viewports, schedules |
| | `tag_elements`, `dimension_grids`, `text_note_create`, `model_lines_create` | Tags, grid chain dimensions, notes, model lines and arcs |
| | `schedule_set` | Schedule filters, sorting / grouping, itemize, totals, headings, hidden fields, number formats |
| | `dimension_rotated`, `dimension_angular`, `dimension_radial`, `spot_coordinate` | Dimensions along any direction; angles; radius / diameter (2027); spot coordinates |
| Drafting and detailing | `drafting_view_create`, `callout_create`, `view_duplicate`, `view_set` | Drafting views; callouts; duplicates (plain, with detailing, dependent); scale, detail level, crop, view range |
| | `detail_lines_create`, `region_create` | Lines, arcs and polylines with a line style; filled and masking regions (polygons, holes, circles) |
| | `detail_component_place`, `detail_group_place` | Detail components at points or along lines; detail groups (active view) |
| | `views_copy_from`, `legend_create` | Drafting views and legends copied from another open model; new empty legends |
| | `repeating_detail_place` | Repeating details along lines (copied from a placed sample) |
| Sheets | `sheets_from_views` | One sheet per view: numbered, named from a template, title block, view template, scale |
| Openings | `opening_create` | Slab / roof openings, beam web sleeves and top holes, wall openings |
| | `shaft_create` | Shaft through every slab between two levels |
| | `crossing_opening` | Opening where a duct, pipe, tray or conduit (host or link) crosses a host wall, slab or beam: run size plus the clearance given |
| MEP / HVAC | `mep_route`, `mep_connect`, `mep_system_create` | Connected runs with elbows; branch + tap from a terminal / equipment to a main; systems from terminals / equipment |
| | `mep_accessory_place` | Dampers, fire dampers and valves of a given type inserted in a run at a point or where it crosses a given wall / slab (host or link) |
| | `mep_insulation_add`, `mep_insulation_remove` | Duct / pipe insulation and duct lining of a given type and thickness on elements or a whole system; replace or remove |
| | `sleeve_place` | Sleeves of a given type at crossings (host or link), sized from the engineer's values or measured lengths plus clearance |
| | `mep_fixture_place` | Plumbing fixtures, sprinklers, electrical / lighting / low-voltage devices at points: level, ceiling plane or wall face (host or link); optionally connected to a main |
| | `electrical_circuit_create`, `electrical_circuit_edit`, `electrical_panel_set` | Circuits from the engineer's list (loads, panel, rating, load name); change them; panel distribution system and name |
| MEP | `mep_run_create` | Straight ducts, pipes, cable trays and conduits between points with given types, systems and sizes |
| Rooms and spaces | `room_create`, `room_separation` | Rooms or spaces at points (names, numbers) or in every enclosed region; separation lines |
| Architecture | `door_window_place` | Doors and windows in walls at a point or a distance along the wall; sill / head height, flips, mark |
| | `layered_type_create` | Wall, floor, roof and ceiling types from given layers (function, material, thickness, core) |
| | `area_plan_create`, `area_boundary`, `area_create` | Area plans in an existing scheme; boundaries from lines or the walls' outside / inside faces; areas with names and numbers |
| | `ceiling_create`, `roof_create` | Ceilings at a height (holes, slope arrow); footprint roofs with a slope per edge, overhang, base offset |
| | `curtain_grid_set` | Curtain wall grid lines along / up the wall, mullions on them, panel types at points |
| | `stairs_create`, `railing_create` | Stairs from risers, tread, width and runs with landings and railings; railings along a path or on stairs |
| | `slab_edge_create`, `wall_sweep_create`, `toposolid_create` | Slab edges on a floor's top edges; wall sweeps and reveals; toposolids from survey points |
| Analytical model and loads | `analytical_member_create`, `analytical_panel_create` | Analytical members and panels, associated with physical elements |
| | `load_case_create`, `load_combination_create`, `load_place` | Load cases; combinations from given factors; point, line and area loads with given values on analytical elements |
| Links | `cad_link`, `revit_link`, `link_reload`, `link_unload`, `link_set` | Link or import DWG / DXF; link a Revit model; reload (also from another file), unload, pin, overlay / attachment, path type |
| Export and save | `export_dwg`, `export_pdf`, `export_ifc`, `export_nwc`, `export_image`, `export_schedule` | Views and sheet sets by number pattern with file names from a template; IFC, NWC, PNG, CSV |
| | `export_dwf`, `export_dgn`, `export_fbx`, `export_gbxml` | DWF (merged or per view), DGN per view, FBX of a 3D view, gbXML |
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
| Reinforce curved or sloped beams, irregular members; Revit 2027 cranks and new numbering schemas | Model them in Revit; `rebar_list` / `bbs_report` include every bar. |
| Radial / diameter dimensions in Revit 2024; spot slopes | The 2024 API has no call to create radial dimensions (`dimension_radial` works in 2027); no Revit version can create spot slopes through the API. Place them in Revit. |
| DGN without a DGN setup or seed file; gbXML of a model without a usable energy model | `export_dgn` needs a DGN setup saved in the model or `seed`; `export_gbxml` failed live on a bare test project (no compatible energy analysis model). Set them up in Revit, or export from Revit. |
| Appearance assets other than Generic, or properties beyond the render colour | `material_asset_set` changes the Generic diffuse colour; edit other appearance properties in the Material Browser. |
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
- **Allowed folders (optional).** Create `%LOCALAPPDATA%\RevitMcpBridge\allowed_roots.txt` with one folder per line
  (`#` starts a comment), for example your project drive and share. Every path a request names (models to open,
  families, links, exports, workbooks) must then lie inside one of them. Without the file any full path is accepted.
  Device paths (`\\?\`, `\\.\`) are always refused.
- **Ignored options are reported.** An option that the add-in never read and that the catalogue does not list for that
  action (usually a misspelling) comes back as `ignoredParams` instead of being dropped silently.
- **Sanity limits.** 500 operations per batch; coordinates more than about 30 km from the origin are rejected as unit
  mistakes; requests up to 4 MB; lists are cut at their `limit` and say so (`truncated: true`); long checks
  (`find_overlaps`, `join_overlaps`) stop at a time budget and continue with `resume_after`.
- **Busy Revit.** A queued request is cancelled if it cannot start within 30 seconds. A running edit may finish after a client timeout. Check the operation receipt before submitting again: `get_operation_status` without an `operation_id` lists the edits this server sent, with `applied` and whether the reply was delivered.

> **Note.** The Yes / No dialog stops the tool from approving itself. It cannot stop a Claude session that also
> controls your screen from clicking "Yes", so only give such a session edit work you would approve yourself.

---

## 9. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "Revit is not reachable" | Revit is not running, or the add-in did not load. Look for **Add-Ins > Claude MCP**. If it is missing, check `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitMcpBridge.addin` and restart Revit; answer the load question with *Always Load*. |
| "Could not reach Revit on port ..." | That Revit was closed, or is frozen. With another Revit open, ask "use the other Revit" (`select_revit`) or press **MCP Status** in it. |
| "2 Revit sessions are open ... Edits are refused" | Several Revits are open. Ask Claude to list them and use the one with your model (`select_revit`). |
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

Where to look: `install_result.txt` (installer), `build_result.txt` (build), `package_build.txt` (installer zip), `setup_result.txt` (server setup), `tests\results\` (tests),
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
  qs_report.py, mep_report.py, excel_io.py   workbooks; xlsx_util.py shared header style, path checks, cell values
tests/                        smoke_test.py (no Revit needed), live_read.py (against the open model)
tools/                        gen_docs.py (docs/ACTIONS.md), write_client_configs.py, deploy_addin.ps1 (used by build.bat)
package/                      install.bat, uninstall.bat, configure_clients.ps1, INSTALL.txt (go into RevitMcp.zip)
build_package.bat             dist\RevitMcp.zip (installer) and dist\RevitMcp-source.zip; log package_build.txt
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
