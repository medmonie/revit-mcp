# Changelog

## 0.31.0 - 2026-11-28

Roadmap phase 17 (architecture) done, with the architectural rows of phase 11. Tested live in scratch projects on Revit
2027 and Revit 2024 (tests/live_v031.py and tests/live_v031b.py, nothing saved). Not possible through the API: creating
ramps and area schemes.

- area_plan_create (existing area scheme, gross building by default), area_boundary (segments, or lines along the walls at
  the building's outside / inside faces found from the walls' outline, a wall's own exterior / interior side, or its centre;
  extended so corners close), area_create (names, numbers; warns when not enclosed).
- ceiling_create (height above the level, holes, slope arrow); roof_create (footprint; one slope, a slope per edge or flat
  edges; overhang; base offset; reports the roof's top above the level).
- curtain_grid_set: vertical grid lines at distances along a curtain wall, horizontal ones at heights, mullions of a type on
  the new lines, panel types at points; reports the panel count and lines by direction.
- stairs_create: from the architect's risers, tread depth, width and runs (start, direction, risers each): each run laid out
  and raised from the risers before it, automatic landings, the railings Revit adds given the type asked for. Built after
  the commit in Revit's stairs edit scope; a dry run lays the runs out without building.
- railing_create along an open or closed path, or on stairs / a ramp; slab_edge_create on a floor's top outer edges (all or
  those nearest points); wall_sweep_create (sweeps and reveals, horizontal or vertical, either side); toposolid_create from
  survey points (optionally within an outline).
- Category aliases: slab_edges, wall_sweeps, reveals, mullions, railings, toposolids.

- door_window_place: doors and windows in walls at a distance along the wall, at a point (projected onto the wall's line,
  refused when it lies off the wall), or at a point alone with a level (the wall through it is found); sill or head height,
  flips and mark as given. Returns the rooms on either side of a door and the room of a window.
- layered_type_create: wall, floor, roof and ceiling types from the layers given (structure, substrate, insulation,
  membrane, finish 1 / 2; material; thickness); the core spans the structure layers unless 'core' is given; if_exists
  update replaces an existing type's layers. The kind's end cap and opening wrapping are kept. Read back with type_layers.
- room_finishes: per room the finish parameters, department, occupancy, and the floor, ceiling and wall areas measured from
  the room's geometry at the finish faces; wall area per bounding element; the doors and windows in its walls with their
  sizes; net wall area (gross minus openings with a known size).
- room_report (server): workbook with rooms, finishes and measured areas, totals per finish (formulas), wall faces by type,
  openings, QA and notes.
- arch_qa: rooms unplaced / not enclosed / without name or number / duplicate number / untagged; doors and windows without
  a mark, with a duplicate mark, untagged or without a host; doors with no room either side.
- schedule_create: presets doors, windows, rooms, room_finishes (usual fields, those missing skipped and listed), sort_by.
- tag_elements: rooms, areas and spaces get room / area / space tags (already tagged ones skipped).

## 0.30.1 - 2026-11-28

Roadmap phases 5, 6, 8, 9 and 13 closed. Tested live in one session per release (tests/live_close_chain.py) on Revit
2024 and 2027: scratch projects for real edits (nothing saved), Task 04 opened by the bridge for dry runs and closed unchanged.

- sleeve_place takes opening families: a wall-hosted family goes in the crossed wall of this model; a work-plane-based
  family with axis z through a wall (face-based openings) goes on the wall's face, square to it, instead of being tilted off
  a level (which Revit refuses). Its voids cut the wall; the result says whether they did and how much volume the wall lost
  (cut, wallVolumeRemoved). Tested with a face-based Wall Opening family: 500 x 400 x wall thickness at a 400 x 300 duct,
  0.061 m3 out of the wall, on both releases.
- family_inspect reports the family's placement and hosting: host (wall, floor, face ...), work-plane-based, always
  vertical, cut with voids when loaded.
- Phase 5: shared parameters added (text instance, length type) and removed for real (before, only dry runs).
- Phases 6, 8 and 9 (live_p6, live_p89) re-run on 0.30 on both releases; dimension_radial confirmed on Revit 2027 (refused on 2024).
- ACC parameters service closed without a live add: no ACC parameter is available to test with (refusals tested in 0.30.0).

## 0.30.0 - 2026-11-28

Roadmap phases 15, 16 and 18 closed, and the open items of the 0.26.3 audit fixed. Tested live in scratch projects on
Revit 2027 and Revit 2024 (nothing saved), Task 04 opened read-only and closed unchanged.

Audit items (0.26.3):
- Exports are staged: each exporter writes into a hidden folder beside the destination, and the files are then moved in
  with a move the file system refuses when the destination exists (no replace), unless overwrite: true. If another writer
  creates a file in between, the files already moved are taken back and everything is kept in a '_revit-mcp-unpublished-...'
  folder: nothing is half-replaced or lost.
- Document identity per open: a document_id now also counts how many times the file was opened in this Revit session, so
  an id handed out before a close and reopen no longer reaches the reopened model (the stable model key used by workbooks
  is unchanged).
- model_close: a model with unsaved changes is closed only with discard_changes: true and edit permission (before, the read
  tool could discard them).
- Revit's UI queue is bounded (32 waiting requests), runs at most 4 items per idle callback, and on shutdown answers every
  waiting request (cancelled, nothing changed). Over 16 connections the add-in answers "Revit is busy ... retry" instead of
  dropping the connection.
- Input workbooks: file size, expanded size, zip-bomb ratio, rows and cells are checked before and while reading; nothing
  is sent to Revit when a budget is exceeded.
- build_package.bat builds into dist\_staging, starts the new exe against a fake Revit (handshake, version, ping,
  validation, clean exit at end of input) and only then replaces dist\ (moving the previous build aside and back on
  failure). A prebuilt add-in DLL is used only with a manifest of this version and its SHA-256.
- The audit log is shared safely by several Revit processes (named lock), and its health (failed writes, last error) is
  reported in get_edit_permission and on any edit whose entry could not be written (auditLogged).
- The frozen server exe no longer prints "ValueError: I/O operation on closed file" when its client closes.
- install.bat /no_pause for scripted installs.

New:
- revit_addins (read): the add-ins Revit has loaded (names, files, versions), optional exporters, the structural analysis
  and steel connection add-ins by name, and the analytical members, panels, supports, load cases and steel connection
  types in the active model.
- analytical_release_set and boundary_condition_create: end releases (fixed, pinned, bending moment, or user directions)
  and supports (point at a member end, line along a member, area on a panel; fixed, pinned, roller or user directions) as
  the engineer gives them. analytical_model now lists each support with its host and direction states.
- hvac_zone_create on Revit 2027: a space-based system zone (GenericZone) of a system zone type; create_type makes the
  type, its set points left for the engineer.
- Schedules: schedule_set takes cell styles per field (bold, italic, underline, colours, font, size), split (parts or
  heights) and unsplit; schedule_key_create (key schedules with keys and values) and schedule_key_assign. Calculated fields
  (formula or percentage) are refused with the reason: Revit's API cannot make them.
- family_parameters: inside loadable families of any category, add parameters, rename, add types, set formulas, set type
  values (in request units), remove parameters, and load them back with these values; a value sent to a formula-driven
  parameter is refused.
- Phase 15: parts_create / parts_divide (lines on a level, grids, levels, reference planes) / parts_merge; array_create
  (linear or radial, grouped or plain copies); reference_plane_create; align_lock (Align's padlock to a grid or reference
  plane, optionally moving the element onto it first); performance_adviser (Revit's Performance Adviser rules and their
  findings); pick (the user picks an element, elements or a point while Claude waits; Esc cancels); watch_start /
  watch_changes / watch_stop (what is added, changed and deleted, by anyone); preview_show / preview_clear (temporary lines
  and boxes drawn in the views, never in the model).
- Phase 16: color_fill_scheme_set (copy or pick a scheme, colour by a parameter, colours and captions per value, apply to
  views, legend); dimension_rotated and dimension_elements take a dimension type (an Ordinate type gives ordinate
  dimensions); sheet_collection_set and export_step (Revit 2027); acc_parameter_add (a parameter from the Autodesk
  parameters service by its id).
- Category aliases spaces, mep_spaces and areas.
- Fixed: link_reload of a CAD link (it needs a transaction of its own); export_gbxml now works on Revit 2024 and 2027 (ground
  plane and export category set; building elements first, then rooms / spaces); export_dgn falls back to a seed file
  installed with Revit (when there is none, a DGN setup or 'seed' is still needed); array_create reports only the copies.

## 0.29.0 - 2026-11-28

Roadmap phase 18 (MEP and HVAC) completed: sleeves, plumbing and fire fighting, electrical, cable trays and conduits,
MEP quantities to Excel. Tested live on the detached FF, PL and E copies (nothing saved); Task 04 untouched.

- New edit sleeve_place: sleeves of the engineer's family type where runs cross walls / slabs / beams (host or link),
  centred on the crossing (wall centre plane, slab mid-depth), turned so the family's own axis (x, y or z) follows the run,
  with the parameter values given; a value can be a length measured at the crossing (crossing_length, run_outside_diameter,
  run_diameter, run_width, run_height) plus or minus the engineer's clearance. A level-based family that would have to tilt
  is refused with the way out.
- New edit mep_fixture_place: plumbing fixtures, sprinklers, electrical / lighting / fire alarm / data / security /
  communication devices and plumbing or mechanical equipment at the engineer's points: on the level, on a plane at height
  facing down (ceiling sprinklers, downlights) or up (floor drains), or on the nearer side face of a wall (host or link);
  optional rotation, parameter values and a connection of each one to a main (later points use the piece a tee left).
- mep_route: slope (percent, the engineer's) for sanitary and drainage runs; starts on a cable tray / conduit connector
  too. mep_route to_main and mep_connect: tees into cable trays and conduits (split by a copy; Revit has no BreakCurve for
  them).
- New read electrical_circuits: panels (distribution system, mains, connected / demand load and current, circuits) and
  circuits (number, panel, kind, load name, poles, voltage, loads, current, rating, wire, length) as Revit computes them, and
  the loads on no circuit.
- New edits electrical_circuit_create (circuits from the engineer's list: loads, panel, kind, load name, rating),
  electrical_circuit_edit (add / remove loads, panel on / off, load name, rating) and electrical_panel_set (distribution
  system, panel name). Nothing is grouped, balanced or sized.
- New server action mep_report: MEP quantities workbook (.xlsx) - runs by system, size, type and level with duct surface,
  fittings / accessories / terminals / equipment / fixtures by type, insulation totals and the elements without, panels and
  circuits, loads on no circuit; totals are Excel formulas.
- Fixed: mep_insulation (and mep_report) failed on pipes: Revit answers lining only for the duct side.

## 0.28.0 - 2026-11-28

Roadmap phase 18 (MEP and HVAC), fourth part: duct and pipe accessories, insulation and lining. Tested live on the
detached HVAC copy (nothing saved); Task 04 untouched.

- New edit mep_accessory_place: dampers, fire dampers and valves of the type the engineer gives, inserted in existing
  straight ducts / pipes at a point on the run ([x, y] matched in plan) or where the run crosses a given wall (its centre
  plane, so an opening already cut there does not matter), slab (mid-depth) or other element, in the host or a link
  (crossing_link: the fire damper at a fire wall of the architectural model). The run is split, the accessory lined up with
  it (rectangular ones turned to the run's width), sized to the run through the family parameters its connectors are tied
  to, and both pieces trimmed to it and connected. A size driven by a type parameter is left alone and reported (the
  engineer picks the type for that size). Several items, or later calls, on one run follow the pieces it was split into.
  Revit's API places an accessory on a duct host without breaking the run, so the bridge does the split itself.
- New edits mep_insulation_add / mep_insulation_remove: duct / pipe insulation and duct lining of the engineer's type and
  thickness (no defaults) on ducts, pipes, their fittings and accessories, or on a whole system's network (equipment and
  terminals in it are left out); existing insulation is listed, or changed with replace.
- New read mep_insulation: insulation and lining per element (type, thickness), totals by type and thickness with run
  length, elements with neither (missing_only), host or link.

## 0.27.0 - 2026-11-28

Roadmap phase 18 (MEP and HVAC), third part: connectors, runs from equipment and terminals, quantities, schedules.
Tested on the detached HVAC copy (nothing saved).

- New read mep_connectors: every connector of the elements given (or of a category): domain, shape, size, classification,
  flow direction, flow, system, what it is connected to, origin, facing, and the width / height axes of rectangular ones.
- mep_route can start on an equipment or terminal connector (from: {element, classification?, stub?}): the first segment
  is drawn from the connector itself, so it takes the connector's size and orientation and is connected; a first point
  within 2 degrees of the connector's axis is snapped onto it. It can end on a main (to_main) with a tap or tee. Sizes are
  refused with from (the run takes the connector's size).
- mep_route: when an elbow between two rectangular segments fails, the next segment's width and height are swapped once
  and the elbow tried again.
- mep_connect: classification picks which open connector (e.g. SupplyAir or ReturnAir on a unit with both).
- mep_quantities: duct surface area (m2) per group.
- Client setup checked against the CSI (ETABS / SAP2000) installers and OpenAI's current MCP page: the same three Claude
  Desktop files (incl. 6.x Claude-Data) and Codex (CODEX_HOME). The ChatGPT desktop app shares Codex's
  ~/.codex/config.toml, so the installer's entry serves it too (no separate step). That entry now sets
  startup_timeout_sec = 30 and tool_timeout_sec = 3600 (Codex / ChatGPT stop a tool call after 60 s by default). An
  existing Claude "revit" entry keeps its other settings (env), old python + server.py arguments are dropped; backups get
  unique names. The hand-copy Codex snippet carries the full entry.
- MEP schedules: schedule_create tested on air terminals (Family and Type, System Classification, Flow) and ducts
  (System Type, Size, Length).

## 0.26.5 - 2026-11-27

Installation fixed for Claude Desktop 6.x (compared with the working SAP2000 MCP package).

- install.bat (revit-mcp-server.exe --configure-clients) now also writes the "revit" entry to
  %LOCALAPPDATA%\Claude-Data\claude_*\claude_desktop_config.json, the file Claude Desktop 6.x reads. Before, only
  %APPDATA%\Claude and the Microsoft Store copy were written, so Claude Desktop 6.x never saw the server although the
  install log said "already configured". Its other settings and servers (sap2000, etabs, civil3d ...) are kept.
- Codex: %CODEX_HOME% is honoured; the new config.toml is checked as TOML (and for the revit entry) before it replaces the
  old one; an unchanged entry is reported as "already configured" instead of being rewritten.
- uninstall.bat removes the entry from the same files (the PowerShell fallback too).
- claude_config_snippet.json / codex_config_snippet.toml point to the installed server exe instead of the development
  Python environment.

## 0.26.5 (code audit fixes, made in the 'MCP implementation code audit' chat) - 2026-11-27

Fixes for the 0.26.4 code and protocol audit (two High, six Medium, ten Low findings). Python side tested with the smoke
test and raw stdio probes; the add-in compiles for Revit 2024 and 2027 and its bridge code was run against the Python
client outside Revit. NOT yet run inside Revit: build both add-ins and run the live suites before release.

- Lost replies: an edit whose MCP request was cancelled or timed out still finishes in Revit, and its generated
  operation_id never reached the caller. The server now keeps a journal of the writes it dispatched;
  get_operation_status without an operation_id lists them (applied, replyDelivered). Long calls send progress
  notifications when the client asks for them (progressToken).
- Central model: sync_with_central and relinquish need the user's Yes in a Revit dialog every time (one approval, one
  action, valid 5 minutes), not only confirm: true. tests/live_ws.py needs a click per sync.
- Bridge: one failed accept no longer ends the listener for the session (retry with back-off; a fatal error stops the
  bridge cleanly and removes the session file). A request over 4 MB is refused by the server before sending, instead of
  surfacing as "outcome unknown". The reply envelope carries the request keys no handler read ("unread").
- ignoredParams: an option the add-in never read and the catalogue does not list for that action or operation is
  reported in the result (a misspelling used to be ignored silently).
- Tool description: REVIT_MCP_DESCRIPTION=compact lists names and parameter names only (about 20,000 characters
  instead of 49,000); the full description stays the default (Mohamed, 28 Nov). help takes a name, a list of names or a
  group title.
- Paths: family_load, cad_link, revit_link, link_reload and shared_file must be full paths, like the output paths.
  Optional %LOCALAPPDATA%\RevitMcpBridge\allowed_roots.txt limits every path to the listed folders (add-in and
  workbooks). Device paths are refused.
- batch_read: max_seconds must be 1 to 3000 and the server waits the budget plus 300 s.
- worksharing_status, sync and relinquish: the "elements held" scan stops after 5 s (scanComplete, elementsScanned).
- Numbers and lists: a fractional value for a whole-number parameter is refused (it was rounded); a list of names
  refuses objects and nulls. Values above a documented maximum are still brought down to it.
- Receipts: at 65,536 reserved operation ids the oldest expired one is released instead of asking for a Revit restart.
- parameter_add warns that a definition written to the shared parameter file stays there if the batch rolls back.
- A dry run reports applied: true if Revit did not confirm the roll-back of its trial commit.
- Scratch projects are deleted on model_close and after 7 days untouched.
- Sessions of a Revit that crashed are skipped without probing their port.
- serverInfo.version is the server's version; the Codex config rewrite is atomic, idempotent and recognises
  [mcp_servers."revit"]; requirements are pinned and no longer pull the mcp CLI extra (recreate server\.venv to drop
  typer and rich from the exe).

## 0.26.4 - 2026-11-26

ChatGPT's 0.26.3 audit fixes (docs/0.26.3-AUDIT-CHANGES.md), reviewed, merged and tested live, plus one open item fixed.

- Documents: model_close only closes the very document Claude opened (a manual close and reopen of the same file no longer
  inherits the permission); Revit's close result is checked; model_open with activate on a model already open in the
  background activates it; scratch projects get a unique name and are never overwritten; batch_read refuses
  model_open / model_new / model_close as nested actions, rejects a non-positive max_seconds, always reads at least one
  file, deduplicates paths, refuses an ambiguous resume_after and reports background close failures in closeErrors.
- join_overlaps retries only after a confirmed rollback of a batch that holds nothing but join_overlaps.
- spaces_from_rooms: the duplicate check looks at spaces of the target phase, including the ones made in the same call;
  max now counts new spaces after the level filter, rooms are taken in a stable order (level, number) and a stop at max is
  reported (truncated).
- Server: split mode (revit_read / revit_edit) can run the four worksharing actions (worksharing_action and
  worksharing_params), and their failures are reported like edit failures.
- Tests: the HVAC tests pin their edits and the close to the HVAC copy they opened and exit non-zero on failure;
  exe_check no longer sends a real purge in its malformed-request probe.

## 0.26.3 - 2026-11-26

Roadmap phase 18 (MEP and HVAC), second part: spaces, zones, design airflow, air terminals, system membership. Tested on
the detached HVAC copy with the detached architecture model linked into it (nothing saved).

- spaces_from_rooms: one MEP space for every placed room of a linked architectural model, on the host level at the same
  elevation, with the room's name, number and height; the link type is made room-bounding; rooms that already have a
  space (found at the room point, or by number and name on that level) are skipped.
- space_airflow_set: spaces' design supply / return / exhaust airflow, as the engineer gives them (L/s).
- hvac_zone_create: an HVAC zone of given spaces on one level (Revit 2024; Revit 2027 replaced zones with system zones,
  so it refuses there for now).
- air_terminal_place: terminals of a given type at points and a height, with the given flows (L/s), optionally each
  connected to a main duct above (branch + tap / tee as mep_connect, following the main's pieces after a split). The
  flow is written to the parameter that drives the connector ("Flow") and checked; face-based types are hosted on a
  downward-facing reference plane. Each terminal joins the main's system before it is connected, so Revit does not make
  a new default system for it.
- mep_system_add / mep_system_remove: add elements' matching free connectors to an existing system; remove members
  (refused when it would empty the system, or for the base equipment, or for elements not in it).
- mep_connect is unchanged for callers (its core is shared with air_terminal_place).

## 0.25.4 - 2026-11-26

Roadmap phase 18 (MEP and HVAC), first part, tested on detached HVAC and plumbing copies (edits never saved).

- New reads: mep_flow (Revit's computed flow, velocity, friction and pressure drop per duct or pipe, totals per system;
  max_velocity / max_friction given by the engineer list the runs over them), mep_connectivity (open duct / pipe ends
  and equipment or terminal connectors in no system), air_terminals (flow and system per terminal, the MEP space it is
  in, and per space the terminals' supply total vs the space's design supply airflow).
- batch_read takes `actions`: up to 20 read actions run on each file while it is open.
- New edit operations: mep_route (a connected duct / pipe run along given points, elbows from the routing preferences;
  points in a line or turning back are refused), mep_connect (a branch from a terminal / equipment / run connector to a
  main: a tap, or a tee by splitting the main when the routing preferences have no tap; refused when the connector's
  line misses the main), mep_system_create (duct or piping system from the free connectors of given elements; the
  system type is given or found from the connectors' classification; connectors of other classifications are left out
  and listed; the base equipment's connector is the system's source, not a member, so a system needs one more element).

## 0.24.1 - 2026-11-26

Roadmap phase 16, first part: models as documents. Nothing here saves a model the user works on.

- list_documents: open models with their documentId, active, path, workshared, cloud, unsaved, opened by Claude.
- Reads take document_id of ANY open model (not only the active one) and read it without activating it. Reads that need
  the view or the selection refuse a background model; edits still need the active model.
- model_files: Revit version, workshared / central / local, central path, saved by, size and date of the .rvt files in a
  folder, without opening them (backups skipped).
- model_open: in the background or active; workshared files ALWAYS detached from their central (worksets kept or
  discarded); a file already open is reused. model_close: only models Claude opened, never the active one, never saved.
- model_new: a project from a template, in memory (read by document_id) or active (saved once as a scratch file under
  %TEMP%\RevitMcpScratch, since Revit can only activate a document with a path).
- batch_read: one read action over many files: each opened in the background, read, closed without saving; per-file
  result or error; max_files, max_seconds and resume_after.

## 0.23.1 - 2026-11-26

Roadmap phase 12 (worksharing), tested on a cloud (Autodesk Docs) test model.

- New actions, each its own request (never an edit operation, no dry run), behind the edit permission, with a
  document_id check and an operation_id receipt like edit: sync_with_central (comment, relinquish all / borrowed / none,
  compact, save local; needs confirm: true), reload_latest, relinquish (all / borrowed / worksets; needs confirm: true),
  checkout (elements and / or worksets). New read: worksharing_status (central path, cloud, user, elements and worksets
  held; per element checkout, owner, creator, last changed by, workset, model-updates status).
- Cloud models always save the local cache before and after a sync (Revit requires it; 0.23.0 skipped it and the sync
  was refused).
- Write actions other than edit mark their receipt as running only after their own checks, so a refused request
  (missing confirm, unknown workset) is a plain error, not "outcome unknown".

## 0.22.2 - 2026-11-26

- join_overlaps on real models: when Revit refuses a join at commit ("can't cut joined element"), the batch is run again
  (up to 3 times) without joining the elements Revit named, instead of the whole batch being rolled back. The pairs left
  out are listed in skippedCutConflict and a warning names the elements. Existing joins are never undone. Revit reports
  this failure with one element and under more than one failure definition, so the match is by element, not by message
  (0.22.1 matched one definition and did not trigger live; 0.22.2 fixes that).
- Dry runs of join_overlaps on workshared models warn that commit-time join failures show only on a real run (these
  dry runs do not commit on trial).

## 0.22.0 - 2026-11-26

Edit features of roadmap phases 10, 11 and 13. All values come from the engineer: nothing is sized or derived.

- crossing_opening: an opening in a host wall, slab, roof, ceiling, beam or brace where a duct, pipe, tray or conduit (host
  or link) passes through it: the run's cross-section plus the clearance given on each side, square to the face (walls
  rectangular, slabs and beams round or rectangular like the run). Refused when the run is more than 15 degrees off
  square or no longer passes through (an opening already clears it). Structure inside links cannot be cut.
- mep_run_create: straight ducts, pipes, cable trays and conduits between points with the type, system and size given
  (no fittings).
- room_create, room_separation: rooms or MEP spaces at points with names and numbers, or a room in every enclosed region
  that has none; room / space separation lines.
- analytical_member_create, analytical_panel_create: analytical members and panels, optionally associated with their
  physical element.
- load_case_create, load_combination_create, load_place: load cases; combinations from the factors given; point, line
  and area loads (kN, kN/m, kN/m2) on analytical members and panels, or on a physical element through its analytical one.
- list_spaces and room_create report the room name without the number (Revit's Room.Name joins both).

## 0.21.0 - 2026-11-25

Read features of roadmap phases 15 (P1), 13, 11, 10 and 14, built to work across linked models (a federated model
with architecture, MEP and structure links). Reads only; their edit counterparts come later.

- ray_cast: what lies above, below or beside a point or an element (host or link), through a 3D view and into links;
  hits nearest first with distance and point.
- clearances: clear distance from each element of a category (host or link) to the first element below / above;
  target categories, threshold (less_than), time limit with resume.
- mep_crossings: ducts, pipes, cable trays and conduits passing through beams, walls, slabs, columns and foundations,
  MEP and structure each in the host or a link: point, entry / exit, length inside, size, system, structural element.
- mep_systems, mep_quantities: systems (type, classification, members, base equipment, well connected); run lengths by
  size / type / system / level and counts of fittings, accessories, terminals, equipment and fixtures.
- list_spaces: rooms, areas or spaces with area, perimeter, volume, unplaced / not-enclosed status, totals by level.
- analytical_model, list_loads, steel_connections: analytical members and panels (role, section, releases, associated
  physical element), boundary conditions; load cases, combinations and loads in kN / kN/m / kN/m2; connections.

## 0.20.0 - 2026-11-25

Roadmap phase 7, second part: numbering, rebar containers, Revit 2027 splices. First live runs in Revit 2027.

- list_numbering (read): Revit's rebar, fabric and coupler numbering: each partition's numbers in use (ranges), count
  and gaps. Schemas are recognised by the parameter they number (Revit 2024 names every schema "NumberingSchema").
- rebar_numbering: remove gaps, shift a partition to start at a number, or change one number (calls kept in both
  Revit 2024 and 2027). Bars move to another partition with set_parameters (bip:NUMBER_PARTITION_PARAM).
- splice_type_create (Revit 2027): a splice type with the engineer's lap (and stagger) in bar diameters.
- rebar_splice (Revit 2027): splice bar sets by rules (max / min bar length, run-out) or at a line in a view; the
  result lists the new bar sets and their lengths. Refused in Revit 2024 with a clear message.
- list_reinforcement_types: Revit 2027 splice and crank types.
- rebar_list: rebar container items (one row per item).
- A missing required choice (e.g. 'action', 'kind') is reported as required instead of a null-reference error
  (built and compiled; live check with the next build).

## 0.19.0 - 2026-11-25

Roadmap phase 7 (reinforcement completion), first part. No design: bars, spacings, lengths, laps, hooks and cover are
the engineer's values or the model's own types; nothing is chosen by rule.

- path_reinforcement: bars perpendicular to a sketched path in a wall or slab (bar, spacing, length, face, flip,
  alternating bars, hooks, extra cover). Bars that leave the host are flagged (`barsBeyondHost`, with a hint to flip).
- fabric_area: welded mesh over a slab or wall (the whole host or a boundary), sheet type, location, cover and laps.
  Laps and cover are set after Revit lays out the sheets and read back: a value Revit does not keep is an error.
- fabric_sheet_place: one sheet at a point, rotated in the host plane; a sheet outside its host is refused with its size.
- Fabric sheet types without wire types are refused (they make sheets of 0 kg), and 'default' is refused in a model
  without fabric wire types.
- rebar_hooks: hooks of the model's hook types at the start / end of bar sets, with orientation and rotation; checks that
  the bar type permits the hook and the bar's style / shape can take it (Revit 2024 / 2027 API differences handled).
- coupler_place: a coupler of a given type on one or two bar ends; Revit's refusal reason is explained.
- rebar_annotate: multi-reference annotation (dimension across the bars plus tag) in a section, plan or elevation; the
  dimension runs along the set's distribution direction; a set laid out along the view is refused clearly; an
  annotation type without a tag type is reported.
- list_reinforcement_types (read): bar types (hooks allowed, 2027 mass), hook, path / area / fabric area types, fabric
  sheet and wire types, coupler and rebar annotation types.
- rebar_list: `systemKind` (area / path) for system bars, fabric sheets (cut size, kg) and couplers.

Not in this release: rebar in sloped / curved / irregular members, Revit 2027 splices, cranks and numbering schemas.

## 0.18.1 - 2026-11-25

API alignment audit of everything built so far (docs/API_ALIGNMENT_AUDIT.md). Every Revit API member the 2024 and 2027
add-ins reference (362 / 359 types, about 985 members, read from the compiled assemblies) exists and is not obsolete in
its Revit version. Fixes:

- Units: every spec with a length dimension follows the request `units`, not only `SpecTypeId.Length` (bar diameter,
  rebar cover / spacing / length, section dimensions, displacement ...); the same for area, volume and angle specs.
  Applies to reported values and to numbers given to set_parameters, filters, schedule filters and global parameters.
  parameterDetails name the unit of every measured value.
- Dry runs on models that are not workshared commit inside a transaction group that is rolled back, so they report the
  errors and warnings Revit raises only at commit (`trialCommit`, `revitErrors`, `revitWarnings`).
- rebar_list: in Revit 2027 the weight is Revit's own rebar mass (`weightSource: "model"`); Revit 2024 keeps d^2/162.2.
- Overlap volumes: a solid pair Revit cannot intersect no longer blanks the volume of the other pairs.

## 0.18.0 - 2026-11-25

Roadmap phase 6 completed (coordination and links). Reads only measure; nothing is moved or fixed for you.

- get_coordinates: internal origin, project base point and survey point (internal and shared positions, clipped),
  the active site location (angle to true north, east/west, north/south, elevation), all named locations, the site
  (place, latitude / longitude, time zone, GIS coordinate system) and a self-check of the shared transform. With
  `link=` it says whether a linked model sits where its own shared coordinates say (offset, rotation).
- list_annotations: dimension values per segment (shown text, value, override, prefix / suffix), spot dimensions, tag
  text and tagged element, text notes, in a view, a sheet or the whole model; flags overridden text, zero values,
  dimensions without references, orphaned tags and empty text (`only_issues`).
- link_clashes: hard clashes between host elements and a linked Revit or IFC model (solids intersected, volume and a
  point in host coordinates, grouped by category pair); category filters, minimum volume, time limit with resume.
- Edits link_reload (from the same or another file), link_unload (for everyone or only me in a workshared model;
  moved shared positions are not saved back to the link) and link_set (pin, overlay / attachment, relative /
  absolute path) for Revit links; reload and pin for CAD links. Reload and unload run after the commit; dry runs only
  check.
- list_links reports `fromIfc` for links created from IFC files.

## 0.17.1 - 2026-11-25

Protocol and robustness audit (done in a separate ChatGPT session, reviewed and corrected here). No new actions; full
record in `docs/0.17.0-AUDIT-CHANGES.md` and `docs/PROTOCOL_AUDIT.md`.

- Stricter input: unknown or misspelled tool / edit arguments (e.g. `dryRun`), `dry_run` that is null or inside an
  operation, unknown or null purge options, fractional pids and duplicate Excel headers are refused instead of ignored
  (checked in the server and again in the add-in). An edit batch is limited to 500 operations; 240 tool calls per minute.
- Failed edits (rollback, failed dry run, Excel conflicts) are now MCP tool errors carrying the full result and receipt.
  When the edit was committed and only a later save / export failed, the error says so and not to resend the edit.
- Edit receipts: an exception after the transaction starts is reported as `outcome_unknown` and that operation_id is
  never run again; a refused permission or a check that fails before the transaction (busy Revit, family document,
  wrong model, bad units) can be retried with the same operation_id.
- The server runs each call in a worker thread (stays responsive during long edits) and stops a cancelled workflow
  before its next bridge call or workbook save. Reply reading has an overall deadline, a 128 MB cap and envelope checks.
- select_revit binds to the session's connection (a recycled pid cannot redirect calls); a busy Revit is listed as busy,
  not unreachable; a corrupt bridge.json no longer hides an open Revit.
- Add-in: the default-Revit hand-over is serialised across Revit processes (a lock that times out no longer stops the
  bridge); stricter JSON parsing; incomplete request lines are not run; write timeout; failed start-up cleans up.
- Integer values (global parameters, schedule filters) must be whole numbers; geometry boxes use all eight corners and
  total volume is summed before rounding; geometry traversal is bounded; list_groups / list_assemblies index once.
- CAD outlines: limits on grid size and joins instead of running out of memory; build_package.bat stops when a
  compile or copy fails. MCP SDK pinned to 1.30.0.
- Installer: the server exe writes the MCP client settings itself (`--configure-clients` / `--remove-clients`)
  instead of a PowerShell script, which company script policies could block, leaving Claude Desktop without the
  "revit" entry and no message. It also writes the file when Claude Desktop is installed but was never started (the
  script skipped it), reads it back to confirm, leaves a broken JSON file untouched, and every result goes to the
  window and install_result.txt. install.bat now stops while Claude is running (it can write its settings back over
  the entry); `/claude_open` overrides.
- Review fixes on the audit: the receipt is marked running by the edit engine just before the transaction (the audit
  marked it before the checks, so "Revit is busy" would have read as an unknown outcome); the post-commit message above.

## 0.17.0 - 2026-11-24

Roadmap phases 8 (modify and documentation depth) and 9 (standards and data). No design: every value placed is given.

Phase 8
- flip: facing, hand or work plane of family instances (as the family allows); wall orientation.
- end_join: allow / disallow joins at the start / end of walls and framing; framing end extension (the length given).
- family_place_hosted: face-based families on a host face (nearest, top, bottom or side), work-plane-based families on a
  level or named reference plane, with a reference direction.
- Groups: group_create, group_place (model groups at points), ungroup; read list_groups.
- Assemblies: assembly_create, assembly_views (3D, elevations / detail sections, parts list, material take-off, sheet);
  read list_assemblies.
- schedule_set: filters (14 operators, numbers in request units / m2 / m3 / degrees), sorting / grouping with headers,
  footers, counts and blank lines, itemize, grand total, title / headers, and per field heading, hidden, alignment,
  totals, unit and decimals.
- Dimensions: dimension_rotated (chain along any plan direction), dimension_angular (two grids / reference planes /
  lines), dimension_radial (radius or diameter; Revit 2027 only, the 2024 API has no call), spot_coordinate.
  Spot slopes cannot be created through any Revit API.
- fix_warnings: new kinds duplicate_instances (copies deleted, lowest id kept) and overlapping_lines (a line within
  another deleted); both only when named in kinds, the default stays joined_disjoint.
- get_geometry: solids (volume, area, centroid, box), faces (kind, area, normal / axis, radius), curves, meshes; as
  modelled or original; also inside a Revit link.

Phase 9
- Global parameters: list_global_parameters, global_parameter_create (length, area, volume, angle, number, integer,
  text, yes_no, material; value or formula), global_parameter_set, global_parameter_associate (drive or clear an element
  / type parameter).
- Materials: get_material (identity, graphics, structural asset in kg/m3 / MPa / 1/C, thermal asset, appearance colour,
  materials sharing each asset); material_asset_set (structural / thermal values, render and shading colour,
  transparency; an asset shared with other materials is copied into a new asset for this material unless change_shared).
- list_keynotes: keynote file and entries, assembly code table, keynote / assembly code per type in use, missing counts.
- Export: export_dwf (merged or per view), export_dgn (per view; Revit's defaults, then the model's DGN setups),
  export_fbx (3D view), export_gbxml (from the building elements; the energy model it needs is made and rolled back).
  DWF, DGN and gbXML run inside a rolled-back transaction (Revit needs a modifiable document for them).
- Several Revit sessions: opening a second Revit no longer moves Claude to it (bridge.json now names the first one
  started, or the one where MCP Status was pressed); with several open, reads go to that default and `ping` lists the
  others, and edits are refused until `select_revit` picks one. With one Revit open it is used whatever bridge.json
  says, so closing the default (it hands over to another open Revit) or a crash no longer leaves "Revit is not
  reachable" while a Revit is open. Session files of crashed Revits are removed by the next Revit that starts.
  `list_revit_sessions` marks the default.
- Installer: install.bat / uninstall.bat show the account they install for and stop when it is not the desktop user
  (an elevated "Run as administrator" window under another account, e.g. on a domain controller, used to install
  into that account's profile); /this_user overrides. New package\check_user.ps1.
- Not working live yet: export_dgn without a DGN setup in the model (give `seed`), export_gbxml on a model without a
  compatible energy analysis model.
- Shared fix: spot_elevation and spot_coordinate place the spot on a point of the face itself (the centre of the
  face's box could fall outside an L-shaped or holed slab: "Spot Dimension does not lie on its reference").

## 0.16.0 - 2026-11-24

Roadmap phase 6, first item: read elements inside linked Revit models. Reads only; no design.

- New `link` parameter (instance id, link type id or file name from list_links) on get_elements, get_element,
  count_by_category, list_types, list_levels and list_grids: the action reads the loaded link's document. Ids, types and
  levels in the result are the linked model's own.
- New `coordinates` parameter with link: `host` (default) applies the link's total transform to points, curves and
  rotations; `link` reports the linked model's own internal coordinates. Boxes in host coordinates are the axis-aligned
  box of the transformed corners. Every result with link carries a `link` block (id, name, document, coordinates,
  transform) so the convention is always stated.
- get_elements / count_by_category with link and `view`: the link's elements visible in that host view (Revit's
  host-view link collector).
- list_levels with link: `elevation` in the reported coordinates and `linkElevation` in the linked model.
- list_links: per Revit link also `path`, `attachment`, `nested`, `loaded`, `document`, `workshared`, and `transform`
  (origin, plan rotation, tilted / mirrored flags).
- A link that is not loaded, an unknown name, or a file placed more than once gives a message listing what to give.
- New `plugin/Core/LinkScope.cs`; `Describe.Location` / `Describe.Box` take an optional transform (host results
  unchanged).

## 0.15.0 - 2026-11-24

No design in the bridge: reinforcement and joist spacing take the engineer's values only.

- rebar_column, rebar_beam, rebar_footing, rebar_area: removed every built-in engineering default (bar counts, spacings,
  cover, 40 Ø anchorage, 50 Ø laps, 12 Ø / 150 mm bend legs, 200 mm feet, end zones of max(long side, H/6, 450) and 2 x
  depth, 100 mm end spacing, 150 mm starter ties, 12 m stock length). A missing value stops the operation with the list
  of values to give.
- New parameters: rebar_column bend_leg, foot_length, starter_tie_spacing; rebar_beam bend_leg. top_bar is required.
- End zones are applied only when end_zone is given; starter ties only with starter_tie_spacing; bars are split with
  laps only when stock_length is given. top / bottom "auto" stays available as an explicit choice (a geometric rule).
- A beam anchorage that does not fit straight goes to the far face with the given bend_leg and is reported for checking;
  it no longer lengthens the bend to make up the anchorage.
- beam_system_create: spacing is required.
- The tool description now states the no-design rule (NO_DESIGN_NOTE).
- From the live test on Task 04 (dry runs): values that depend on top / bottom / ends are asked for only once that choice
  is given; a bend or U-bar leg shortened by the member size is now reported (column top bend, beam bend, footing U
  bars); a missing top_bar for a top mesh joins the list of missing values.

## 0.14.0 - 2026-11-23

Installer package, as in the Civil 3D bridge: nothing to install by hand on the user's PC.
- build_package.bat: builds the add-in for Revit 2024 (net48) and 2027 (net10) against the installed Revit or
  refs\<year>\ (else plugin\prebuilt\<year>\), the MCP server as one exe (PyInstaller, revit-mcp-server.exe), and
  dist\RevitMcp.zip + dist\RevitMcp-source.zip. Log package_build.txt.
- package\install.bat: installs the add-in for each Revit found, the server in %LOCALAPPDATA%\RevitMcpBridge, clears the
  internet mark on the unzipped files (Revit 2024 / .NET Framework will not load a marked DLL), and adds "revit" to
  Claude Desktop (normal and Microsoft Store config) and Codex through package\configure_clients.ps1, keeping the other
  servers and backing each file up. Log install_result.txt.
- package\uninstall.bat: removes the add-in from every Revit year, the server, and the "revit" entries.
- README: install section split into option A (installer zip) and option B (source folder), as in civil3d-mcp.
- No add-in or server behaviour changed since 0.13.3.

## 0.13.3 - 2026-11-23

- Fixed a Revit 2024 native crash during purge preview rollback. Purge previews enumerate first-pass candidates. The updated server requires protocol 4 for edits to keep previews off older add-ins.

## 0.13.2 - 2026-11-23

- Purge protects dependents named in keep lists and counts all deleted IDs. Parameter binding merges categories per definition. Phase updates succeed or roll back per element.
- Rebar placement deduplicates hosts, applies side cover using each layer’s diameter and splits beam and footing bars within stock, including end legs and laps. Incomplete bar bending schedules are refused before saving.
- Each edit targets a captured document. Transaction failure handling permits automatic detach only for the exact join that produced the identified failure. Overlap scope applies to both elements.
- Duplicate parameter names require a stable selector except for recognized Revit column and sheet built-ins. Deployment stages the DLL and manifest and restores previous files if replacement fails.

## 0.13.1 - 2026-11-23

From the first live test of 0.12 / 0.13 on Revit Task 04:
- Revit 2024 closed during a dry-run batch (parameter_add, phase_set, standards_copy, purge). Suspected cause:
  standards_copy copied line styles as GraphicsStyle elements between models. Line styles are now recreated as
  sub-categories of Lines (weight, colour, line pattern by name, pattern recreated if missing).
- parameter_add: a dry run works on a temporary copy of the shared parameter file (the file is outside Revit's undo).
- rebar_beam: a member meeting the beam at an L-corner (overlapping its end) is found as a support; bars run to
  cover and bend 90 degrees (was reported as a free end).
- rebar_column on a footing: locating ties for the starter bars inside the footing (hosted by the footing).
- Verified live in 0.13.0: column on footing (bars on the mesh, 200 mm feet outward, 800 lap above, ties 100/150/100),
  roof column (start at column below, 188 mm inward bend), beam start anchored to far face of the column + 192 bend,
  footing U bars (244 up, under the top mesh); model_health, list_parameters, list_phases, list_worksets.

## 0.13.0 - 2026-11-23

Roadmap phase 5, model management:
- Reads: model_health (size, warnings top 10, in-place families, CAD linked / imported, unloaded links, views not on
  sheets, unused templates and group types, purgeable count, attention list), list_parameters (project / shared
  parameters with GUID, data type, group, binding, categories; shared parameter file contents), list_worksets
  (worksets; per element workset, checkout status, owner, creator, last changed by), list_phases (phases, phase
  filters, design option sets).
- Edits: parameter_add (from the shared parameter file, created there if missing; extends categories of a bound
  parameter), parameter_remove, purge (Document.GetUnusedElements, several passes, keep lists), standards_copy (view
  templates, filters, text / dimension types, patterns, materials, wall / floor types, title blocks, line styles from
  another open model; same names kept), workset_create, workset_set, phase_set.
- The bridge's own shared parameter file (%LOCALAPPDATA%\RevitMcpBridge\SharedParameters.txt) is used when the
  model has none; the application's shared parameter file setting is restored after each call.

## 0.12.0 - 2026-11-23

Reinforcement detailing:
- rebar_beam: bars anchored into the supports found past each end face (column, girder, wall): straight when the
  support is deep enough for the anchorage, else as far as the support allows plus a 90 degree bend (bottom bars up,
  top bars down); lapped into a collinear next span; stop at cover at a free end. Bars longer than stock_length are
  split with laps. Stirrups closer over end zones (default 2h @ min(spacing, 100)). New: ends, anchorage_d, lap_d,
  end_zone, end_spacing, stock_length. Result lists how each end was detailed and the clear span.
- rebar_column: top auto = lap above (column above) or bend inward (no column above); bottom auto = stand on the
  footing mesh with outward feet (footing below), start at the member below otherwise. Ties closer at both ends
  (default zone max(long side, h/6, 450) @ min(spacing, 100)). New: top, bottom, lap_d, footing_cover, end_zone, end_spacing.
- rebar_footing: end_bends (bottom bars turned up to under the top mesh), bend_leg, lap_d, stock_length.
- Bends are drawn into the bar path (no hook types), so 2024 and 2027 behave the same.
- Anchorage / lap are multiples of the bar diameter set by the user (defaults 40 / 50), not calculated to a code.

## 0.11.4 - 2026-11-23

- rebar_beam: the bar length comes from the beam's placed concrete (support face to support face); only the section
  comes from the shape before joins. In 0.11.3 the length came from the family's default geometry (bars 495 mm).
- rebar_beam reports lengthMm and a note when a slab is joined to the beam (top bars in the slab zone may get
  Revit's "outside of its host" warning).
- Column and footing unchanged: verified in 0.11.3 (column bars full height with 40 mm cover).

## 0.11.3 - 2026-11-23

- Reinforcement: bars are sized from the member's own concrete before joins. Before, a slab joined over a column
  or beam cut their tops, so column bars stopped ~190 mm short, beam top bars sat ~260 mm below the beam top, and
  beam bars started at the column face. Falls back to the joined shape if the original geometry is not usable.

## 0.11.2 - 2026-11-23

Re-test of 0.11.1 on "Revit Task 04" (dry runs): revisions, sheet export, view range, slab corner move / point add and
spot elevations confirmed. Clearer messages: a slab Revit offers no shape editing for; a beam whose top face is covered
by a joined slab.

## 0.11.1 - 2026-11-23

Fixes from the live test of 0.11.0 (Revit 2024, "Revit Task 04" with 273 bar sets; all edits as dry runs).

- Revisions: models that number revisions per sheet made revision_create and list_documentation fail (Revit refuses
  the project-wide revision number there); the number is now reported as null.
- Same-name parameters with different values: when exactly one is editable it is used (a sheet's Sheet Number beside
  the read-only viewer sheet number made params_to_excel fail on sheets).
- slab_shape: corners are listed after shape editing is switched on (regenerate); a refused point gives a clear
  message with the slab's corners.
- spot_elevation: falls back to the element's full geometry when its top face is hidden in the view (beams under a
  slab).
- view_set view_range: a refused range reports the view's current planes.

## 0.11.0 - 2026-11-23

Roadmap phases 2 (documentation), 3 (reinforcement) and 4 (modelling gaps). Not yet tested live.

- Documentation: `filter_create`, `filter_apply`, `color_by_parameter`, `graphics_override`, `view_template_create`,
  `view_template_apply`, `dimension_elements` (chains through element faces and grids), `spot_elevation`,
  `revision_create`, `revision_cloud_create`, `sheet_layout` (several views / schedules on a sheet); `viewport_place`
  places schedules; `view_set` assigns scope boxes; read action `list_documentation`.
- Reinforcement: `rebar_column`, `rebar_beam`, `rebar_footing`, `rebar_area`; read action `rebar_list`; server
  action `bbs_report` (bar bending schedule workbook). Rebar creation uses the 2024 hook overload or the 2027
  BarTerminationsData overload.
- Modelling: `floor_create` slope arrow, `slab_shape`, `beam_system_create`, `brace_create`.
- Describe.ToInternal shared by set_parameters and filter rules.

## 0.10.1 - 2026-11-23

- join_overlaps: Revit's default fix is applied again to errors on elements joined in the batch ("Can't cut joined
  element"); 0.9.2 still insisted on the detach resolution, which Revit did not offer, and rolled back (live test).
- Blocked items worked around: `legend_create` (empty legend by duplicating an existing one);
  `repeating_detail_place` (copies a placed repeating detail from this model or another open one and reshapes it);
  `detail_component_place` with `along` (a component repeated along lines at a spacing, turned to the line).

## 0.10.0 - 2026-11-23

Roadmap phase 1: drafting and detailing.

- New read action `list_detailing`: line styles, filled region types, detail components, detail groups, repeating
  detail types, drafting views and legends, in the active model or another open one.
- New edit operations: `drafting_view_create`, `region_create` (filled and masking regions: polygons, holes, circles,
  boundary line style), `detail_component_place` (point- and line-based), `detail_group_place` (active view),
  `callout_create`, `view_duplicate` (plain, with detailing, dependent), `view_set` (name, scale, detail level,
  discipline, crop, view range), `views_copy_from` (drafting views and legends from another open model).
- `detail_lines_create` draws arcs (`mid`, as documented; the old code drew a straight line) and polylines, with a
  line style per call or per line; moved to Edit/DetailOps.cs.
- `model_lines_create` draws arcs (`mid`) on the arc's own plane.

## 0.9.2 - 2026-11-22

Fixes from the live test of 0.9.1 on the KTR15 detached model (Revit 2024).

- Parameters with the same name: Revit's own pairs (Base Level, Base Offset ... on columns: schedule + family
  parameter) hold the same value, so the editable one is used; only same-name parameters with different values are
  refused. 0.9.1 refused get_elements and params_to_excel on every column.
- join_overlaps: "Can't cut joined element" is resolved again (Revit's detach) when it concerns an element joined in
  this batch; 0.9.1 needed both elements named in the error, Revit names one, and the whole batch rolled back.
  Pairs whose overlap volume Revit cannot compute are joined again (they intersect), as in 0.8.0.
- Build with Revit open: after a successful compile the loaded DLL is moved aside and the new one copied (restart
  Revit to load it); a failed compile still leaves the installed add-in untouched.

## 0.9.1 - 2026-11-22

Review of 0.9.0 (made with ChatGPT) and merge into the main folder.

- Edits work again without extra values: `operation_id` is generated when omitted and `document_id` is checked only
  when given (0.9.0 refused every edit without both, and needed a new id after the permission dialog).
  `excel_to_model` follows the same rule.
- Restored the error hints for a Revit that cannot be reached (MCP Status / select_revit) and for an edit that times
  out (permission dialog or running command).
- Config snippets regenerated for the main folder.
- Docs and smoke test updated; see docs/IMPLEMENTATION_LOG.md for the 0.9.0 changes.

## 0.9.0 - 2026-11-21

- Bind edits and multi-call workflows to the active document. Expose edit receipts for retry and status checks.
- Preserve parameter identities and workbook baselines. Report Excel conflicts and applied counts separately.
- Classify quantities before calculating concrete totals. Report direct overlap review pairs and resume scans at the pair boundary.
- Report deferred save, export, family, and view outcomes. Fail deployment when the installed add-in cannot be copied.
- Generate current-folder Claude and Codex configuration snippets. The server and add-in must both be updated for writes.

## 0.8.0 - 2026-11-20

Code restructure. Every action, edit operation and parameter keeps its name and behaviour; the live-tested results do not change.

- Add-in, shared helpers instead of copies:
  - Core/Units.cs: one place for mm / m / m2 / m3 conversions (no 304.8 or 0.3048 literals elsewhere).
  - Core/Materials.cs: an element's materials, "is steel", generic-material test, material by name
    (was repeated in QaRead, QuantityRead, MaterialOps, Geo).
  - Core/Find.cs: Scope (category / view / level filter used by take-off and QA), Families, FamilyNamed.
  - Core/Describe.cs: element labels and paging of long lists.
  - Core/Poly.cs: polygon area, inside test, centroid, tidy, self-crossing test, CurveLoop from points
    (was in CadPairs, CadRaster, CadMemberOps and EditContext).
  - Core/CadSections.cs: section size from a type (parameters or the first 'AxB' in its name), matching and
    creating sized types (was in Cad.cs, CadOps and CadMemberOps).
  - Cad.cs: the "already modelled here?" checks for columns, beams, walls and slabs in one region.
  - StructureOps: PlaceBeam / PlaceWall used by beam_create, wall_create, beams_from_cad and walls_from_cad.
  - FamilyOps: family_material_set and family_material_link share one open -> change -> load back -> report routine.
- Loops: sheets_from_views looks up placed views once (was one viewport scan per view); fix_materials counts the
  planned elements per type once (was a scan of the plan per group).
- Server: xlsx_util.py holds the workbook header style, path checks and cell values for qs_report and excel_io;
  the dispatcher is two tables (server actions that read, server actions that edit), checked against the catalogue by
  the smoke test; repeated parameter wording in catalog.py is shared.
- README rewritten: every feature, grouped, with how to use each workflow.

## 0.7.1 - 2026-11-20

- slabs_from_cad mode outer: after snapping, spikes and straight-through corners are removed and the outline is tested for
  self-crossing; if snapping made it cross itself the grid outline is used (snappedToCad false). First live run on the
  KTR15 First Floor DWG: 1 outline of 2445 m2 / 522 corners, rejected by Revit as self-crossing.
- preview: draw the outlines as model lines instead of making slabs. Slab results include the outline's box.

## 0.7.0 - 2026-11-20

- slabs_from_cad mode 'outer': the slab outline is the outer outline of everything on the given layers (slab edge, beams,
  columns together), found on a grid: curves drawn and thickened to close gaps, the outside flood-filled, the outline traced,
  pulled back, simplified and snapped to the nearest CAD vertex. Openings from 'opening_layer'. For drawings where no
  single layer holds a closed slab edge (KTR15 First Floor: edge broken at columns, dashed curved edge, beam faces as edge).

- family_material_set / family_material_link: report names were read from the replaced Family object after a reload
  (InvalidObjectException after the change had succeeded); names are now taken before the reload.
- Live 0.6.5: 9 KTR15 steel families set (built-up ST-52/3, rolled / SHS / UPN ST-44/2) in Revit 2024 and 2027;
  check_materials 0 problems of 917 in both.

## 0.6.5 - 2026-11-20

- family_material_set: set the solids of chosen families to one material directly (KTR15 steel families had their only
  extrusion on <By Category>, so Object Styles' RC C30 showed). family_material_link failed in Revit while linking the
  solid to the built-in Structural Material ('Document regeneration failed'); errors now include inner exceptions.

## 0.6.4 - 2026-11-20

- family_inspect (read): parameters, solids with their material and linked parameter, nested families of a loaded family.
- family_material_link: reports the step that failed and Revit's own failure messages from the family document
  (0.6.3 failed on the 9 KTR15 steel families with only 'Document regeneration failed').

## 0.6.3 - 2026-11-20

- family_material_link: open families whose solids carry a fixed material (KTR15 steel families had RC C30 inside),
  link the solids' material to Structural Material and load them back, keeping project type values.
- fix_materials: notEffective grouped by type.
- Live: Revit 2027 editing confirmed (fix_warnings + fix_materials, 304 elements verified); concrete materials fixed in 2024.

## 0.6.2 - 2026-11-20

From the live run of 0.6 / 0.6.1 on the KTR15 model (Revit 2024; Revit 2027 read checks passed):
- fix_materials: uses the element's own Structural Material when it is an instance parameter (M_Concrete-Rectangular Beam
  and most structural families; the type's built-in one is unused), and re-reads every element after the change:
  'notEffective' lists elements whose geometry still shows the old material.
- slabs_from_cad: edge pieces are bridged to their nearest free end (shortest gaps first), so dashed edges and edges
  broken at columns close into outlines; 'gap' sets the largest gap.
- sheets_from_views: exclude_contains.

## 0.6.1 - 2026-11-20

- fix_materials: material fixer by rule (steel members, Revit default materials, filters by category / family / type / level /
  current material). Type-level where every element of the type is fixed, else per element; slab / wall layer materials only
  when the whole type is fixed.
- Steel check: material class 'Steel' (Revit's S235 / S275 / S355) now counts as metal, not only 'Metal'.

## 0.6.0 - 2026-11-20

- beams_from_cad / walls_from_cad: members from pairs of parallel edge lines (or concentric arcs), on their centre line,
  typed by width (beams can create missing widths from a base type); already modelled members skipped.
- slabs_from_cad: slabs from closed outlines (polylines, circles, or lines / arcs chained end to end), inner outlines become openings.
- sheets_from_views: sheet sets, one per view, numbered from a start number, named from a template, view template / scale, view centred.
- CAD layers can be given as a list ('S-BEAM, S-BEAM-HDLN').
- 0.5.1 checked live: steel / concrete split, create_types (5 First Floor columns placed exactly on the DWG outlines).

## 0.5.1 - 2026-11-20

From the first live run of the CAD tools on the First Floor slab DWG:
- Column matching respects material: steel outlines (H / I) only match steel families, rectangles and circles only non-steel
  (three 650x1200 steel outlines had matched the concrete C3 650x1200).
- Sizes from type names use the first 'AxB' ('BU 2000x700x22x35' is 2000 x 700, not 22 x 35).
- columns_from_cad create_types / type_name: create the missing column sizes from a base type, then place them.

## 0.5.0 - 2026-11-20

- CAD to model: read action cad_columns (column outlines on a DWG layer, fitted with size and rotation, matched to column types)
  and edit operations columns_from_cad and grids_from_cad. Touching curves form one column, so steel H / I outlines drawn
  in pieces are found too; columns already standing at the base are skipped.
- Warning fixer: edit operation fix_warnings unjoins 'joined but do not intersect' pairs and lists the other warnings.
- set_parameters: element-id parameters (levels, materials, phases, types) accept a name as well as an id.

## 0.4.1 - 2026-11-20

Fixes from the first live run of 0.4:
- find_overlaps / join_overlaps crashed ("Collection was modified") whenever duplicates were found; fixed.
- opening_create: beams and braces now work: a sleeve through the web ('face' web, circle or width x height, at mid depth or a given z) or a hole through the top ('face' top).
- grid_create / grid_layout: new grids span every level, so they show in all plans (API grids used to stop below the upper levels).
- New edit operation grid_extents: stretch existing grids to every level.
- dimension_grids: when named grids exist but do not reach the view's level, the error says so and points to grid_extents.
- Excel: the Levels 'plan' column accepts TRUE / FALSE; params_from_excel batch results no longer list empty fields.

## 0.4.0 - joins that commit, openings, annotation, Excel

- join_overlaps (first live run: Revit refused the whole batch over 6 "Can't cut joined element" errors in join chains):
  now applies Revit's own resolution (unjoin that pair) and reports it in resolvedByRevit; pairs that only touch are
  skipped (they produced "joined but do not intersect" warnings).
- Duplicates: in each group of elements inside each other the LARGEST is kept (0.3.1 proposed deleting a column that
  contained the others); each candidate is marked identical or contained.
- opening_create (slab / roof / beam polygon or circle, rectangular wall openings) and shaft_create.
- tag_elements (category tag, skips tagged elements) and dimension_grids (chain dimensions per set of parallel grids).
- Server: params_to_excel / params_from_excel (only changed cells, dry run, 500 elements per Undo step),
  excel_template / excel_to_model (levels, grids, columns, beams, walls, slabs, footings in one batch).
- Any edit batch can let Revit resolve its own errors when an operation asks for it (FailureCollector.ResolveErrors).

## 0.3.1 - duplicates are not overlaps

- First live run of find_overlaps on the KTR15 model: 470 pairs, 70.5 m3 counted twice - of which 27.1 m3 were identical
  columns modelled twice. Same-category elements (almost) entirely inside each other are now reported as duplicates
  (keep / delete ids) and join_overlaps skips them; joining a duplicate would hide the error instead of fixing it.
- qs_report: duplicates get their own table on the QA sheet, a line in the summary and a warning.

## 0.3.0 - trustworthy quantities

- find_overlaps (auto-join check): element pairs whose geometry overlaps but that are not joined, with the volume counted
  twice, grouped by category pair; time-limited with resume_after for big models; steel pairs skipped by default.
- join_overlaps: joins them all in one batch; the cut order follows a category priority (default foundations, columns,
  walls, beams, floors - the earlier one keeps the shared volume).
- formwork_takeoff: side, soffit and top face areas per type / level; formwork = sides + soffits of beams and slabs.
- material_takeoff: group_by level or type, and exclude_steel.
- qs_report (server): one .xlsx with Summary, Concrete (grade x level), Elements, Steel, Rebar, Formwork, QA, Notes;
  totals are Excel formulas; overwrite protection. Needs the openpyxl package (setup_server.bat installs it).
- check_materials: level filter.
- Fix: a dry run no longer creates the export folder.

## 0.2.0 - small, high-priority features

- Curved grids, walls and beams: give a 'mid' point on the arc. Sloped beams: give a z per point. Arcs are read back
  with their mid point and radius.
- steel_takeoff: steel weight by section (count, length, volume, kg, kg/m), independent of the material assigned.
- check_materials: elements with no material, a Revit default material or a name outside your pattern, and steel
  members carrying a non-metal material.
- check_names: naming-convention check for views, sheets, types, levels, grids, materials, families, view templates.
- section_create (along a line, a straight grid or through an element) and elevation_create (marker + chosen directions).
- export_pdf / export_dwg: 'sheets' wildcard filter (e.g. S-1*) and 'name_template' for one file per sheet.
- types_create: many types from one base type (a whole column or beam schedule), with skip / update for existing ones.
- Several Revits at once: each writes its own connection file; list_revit_sessions and select_revit choose one.

## 0.1.2 - first live edit test (Revit 2024, detached KTR15 G26 model)

- Edit path tested: level with plan, grid, 4 columns, 4 beams, slab in one transaction; dry run, then committed.
- beam_create: sets the beam's justification itself (default: top of beam at the level, centred on the line), because
  families differ (the KTR15 family placed beams above the level and to one side). New parameters z_justification and
  y_justification; 'family' keeps the family's own setting.
- Edit permission: the first edit no longer waits for the dialog (Claude's connection gave up after 80 s). It returns at
  once, the dialog opens in Revit, and the edit is sent again after Yes.

## 0.1.1 - first live test (Revit 2024, KTR15 G26 model)

- All read actions answered against a real model (284 columns, 419 framing, 137 foundations, 14 CAD files).
- count_by_category: physical elements only; sketches, materials, sun path and base points left out; CAD files counted together.
- material_takeoff: elements without a material (e.g. steel sections) are no longer skipped; listed as "(no material assigned)".
- get_element: choice parameters (justification, structural usage ...) show Revit's text next to the number.

## 0.1.0 - first version

- Add-in for Revit 2027 (.NET 10) and Revit 2024 (.NET Framework 4.8) from one source, no third-party DLLs.
- One MCP tool `revit(action, params)`: 24 read actions and 43 edit operations inside the single `edit` action.
- Same safeguards as the Civil 3D bridge: localhost + per-session token, one-time edit permission per Revit session,
  one transaction (one Undo step) per edit batch, dry run, audit log, overwrite protection for exported files.
- Read: model, levels, grids, views, sheets, types, elements and parameters, selection, links, geometry of linked CAD
  files by layer, quantity / material / rebar take-off, schedules, Revit warnings.
- Edit: levels and grids, columns, beams, walls, floors / slabs, footings, wall foundations, any level-based family;
  parameters, types, move / copy / rotate / mirror / delete, joins; views, sheets, schedules, text and lines;
  link DWG / DXF and Revit models; export DWG, IFC, PDF, NWC, PNG, schedule CSV; save.
- Not yet: placing rebar, openings / shafts, dimensions and tags, curved walls / beams / grids, synchronising with
  central, the Family Editor.
