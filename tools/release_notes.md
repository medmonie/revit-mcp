## Install (no Python, no build)

1. Close Revit, and quit Claude Desktop and the ChatGPT desktop app (tray icon > Quit): the installer replaces the
   server exe they run.
2. Download **RevitMcp.zip** below, unzip it and double-click **install.bat** as the Windows user who runs Revit
   (not *Run as administrator*: it installs into the current user's profile and stops if it runs as another account).
   It installs the add-in for Revit 2024 and/or 2027, the MCP server (one exe), and adds `revit` to Claude Desktop
   (all its settings files, 6.x included) and to Codex. Your other servers and your own `revit` settings are kept; each
   settings file is backed up first.
3. Start Claude Desktop, start Revit, choose **Always Load** when asked, open a model and ask
   *"Ping Revit and tell me which model is open."* (it shows 0.30.0).

ChatGPT desktop shares the Codex settings (`~/.codex/config.toml`), so it gets the `revit` server from the same install
(tool timeout 3600 s, startup 30 s). Uninstall: `uninstall.bat` from the same zip.
Full guide: [README](https://github.com/medmonie/revit-mcp#readme).

## New in 0.30.0 (80 read actions, 171 edit operations)

Roadmap phases 15, 16 and 18 closed, and the open items of the 0.26.3 audit fixed. Tested live on Revit 2024 and 2027.

**Analytical model:** end releases (fixed, pinned, bending moment or user directions) and supports (point, line, area;
fixed, pinned, roller or user) as the engineer gives them; supports read back with their direction states.
`revit_addins` lists what Revit has loaded (structural analysis and steel connection add-ins, exporters).

**Model QA and query depth (phase 15):** parts (create, divide along lines or datums, merge); linear and radial arrays;
reference planes and alignment locks; Revit's Performance Adviser; picking in Revit while Claude waits; watching what
changes in a model; temporary preview lines and boxes (never in the model).

**Documents (phase 16):** key schedules and key assignment; schedule cell styles and splitting; family parameters,
types, formulas and type values inside loaded families; colour fill schemes with legends; dimension types (ordinate);
Revit 2027 sheet collections and STEP export; parameters from the Autodesk (ACC) parameters service.

**MEP (phase 18):** HVAC zones on Revit 2027 (system zones).

**Safety and robustness:** staged exports that never half-replace a file; a document id per open (a reopened model gets
a new one); unsaved work is discarded only on request with edit permission; a bounded queue to Revit with a clear
"busy" answer; size and cell budgets for input workbooks; the audit log safe across Revit sessions with its health
reported; the package build probes the new server before replacing the old one; CAD link reload, gbXML (2024 and 2027)
and DGN (seed fallback) export fixed.

## Since 0.17

**MEP and HVAC (phase 18, complete in 0.29)**
- Connected duct / pipe / cable tray / conduit runs with elbows, taps and tees from the type's routing preferences;
  runs that start on an equipment, terminal or panel connector and end on a main; sanitary runs at the engineer's slope.
- Duct and piping systems; spaces from rooms, HVAC zones, design airflow; air terminals with their flow, connected.
- Dampers, fire dampers and valves inserted in runs at points or where they cross walls / slabs (also in a link);
  sleeves at crossings sized from the engineer's values or measured lengths plus clearance; insulation and lining.
- Plumbing fixtures, sprinklers, electrical / lighting / low-voltage devices on the level, a ceiling or floor plane or a
  wall face (also a linked wall), each optionally connected to a main.
- Electrical: panels, circuits and loads read as Revit computes them; circuits from the engineer's list (loads, panel,
  rating, load name), edited; panel distribution system and name.
- Reads: computed flow / velocity / pressure drop, connectivity, connectors, MEP quantities, insulation; the
  **mep_report** workbook (runs by system / size / type / level, fittings, insulation, panels, circuits).

**Coordination, reinforcement, documentation, worksharing (0.18 - 0.24)**
- Coordination and links: clashes with links, crossings, clearances, coordinates, federated reads.
- Reinforcement completion (most of phase 7): numbering, rebar containers, Revit 2027 splices and cranks.
- Analytical model and loads, architectural and site modelling, model QA and query depth, steel connections read
  (phases 10, 11, 15 and 14), with edits that take the engineer's values.
- Worksharing: status, sync, relinquish (on a central model Revit asks you Yes / No first); models as documents:
  open in the background, batch reads over a folder, close without saving.

**Safety and robustness**
- Protocol and code audits: operation journal and receipts, a 4 MB request guard, `allowed_roots.txt` to limit the
  folders Claude may read and write, unknown parameters reported (`ignoredParams`), grouped help.
- Nothing designs: sizes, spacings, ratings, positions and types always come from the engineer.

Known limits: radial dimensions need Revit 2027; spot slopes, schedule calculated fields (formula or percentage) and
arc-length dimensions in projects cannot be created by Revit's API; DGN export needs a DGN setup or seed file when none is
installed with Revit; a joint that the pipe type's routing preferences cannot make (e.g. a missing reducing tee) is
reported, not forced.

Details: [CHANGELOG](https://github.com/medmonie/revit-mcp/blob/main/CHANGELOG.md).

**RevitMcp-source.zip** is the clean source for building on another PC (needs the .NET 10 SDK and Python 3.10+).

Licence: PolyForm Noncommercial 1.0.0 - free for non-commercial use; commercial use needs written permission.
