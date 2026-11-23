# Changelog

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
