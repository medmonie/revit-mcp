# Roadmap

Planned and finished work for the Revit MCP bridge, by phase. S = one build batch, M = one or two batches with a live
test, L = several batches. Status as of version 0.17.0 (November 2026).

| Status | Meaning |
|---|---|
| Done | Built for Revit 2024 and 2027 and tested live |
| Built | Built and compiled for both releases; live test pending |
| Planned | Not started |
| Not possible | The Revit API does not allow it |
| Out of scope | Left out by rule: the bridge never designs |

Priority: High, Medium, Low. The bridge models, reads, checks and documents; it never designs, so every planned
feature places or reads what the engineer gives.

## Phase 1 - Drafting and detailing (done)

| Feature | Effort | Status |
|---|---|---|
| Drafting views | S | Done |
| Detail lines: arcs, polylines, line styles | S | Done |
| Filled and masking regions | M | Done |
| Detail components (points, along a line, line-based) and detail groups | M | Done |
| Callouts, duplicate views, view settings (scale, crop, view range, scope box) | S | Done |
| Copy drafting views / details from another open model (`views_copy_from`) | M | Built |
| Repeating details | - | Not possible (API cannot create them; existing ones are listed) |
| Legends | - | Not possible to create from nothing; an existing legend can be duplicated |

## Phase 2 - Documentation (done)

| Feature | Effort | Status |
|---|---|---|
| Dimensions to element faces | L | Done |
| Spot elevations | M | Done |
| View filters, colour by parameter, graphic overrides | M | Done |
| View templates: create from a view, apply | M | Done |
| Revisions and revision clouds | M | Done |
| Several views and schedules per sheet, auto-arranged | M | Done |

## Phase 3 - Reinforcement (done)

| Feature | Effort | Status |
|---|---|---|
| Column bars and ties; laps into the column above, bends at the roof, feet on the footing, closer ties at the ends, starter ties in the footing | L | Done |
| Beam bars anchored into supports (straight or bent), corner supports, laps into the next span, stock-length splitting, closer stirrups at the ends | L | Done |
| Footing meshes, top mesh, U bars | M | Done |
| Slab area reinforcement | M | Done |
| Bar list (`rebar_list`) and bar bending schedule to Excel | M | Done |
| No defaults: every engineering value from the engineer (0.15.0) | M | Done |

## Phase 4 - Modelling gaps (done)

| Feature | Effort | Status |
|---|---|---|
| Sloped slabs (slope arrow, slab shape points) | M | Done |
| Beam systems | M | Done |
| Braces | S | Done |

## Phase 5 - Model management (built)

| Feature | Effort | Status |
|---|---|---|
| Model health report (`model_health`) | S | Done |
| Project and shared parameters: list, add from a shared parameter file, remove | M | Done (add / remove tested as dry runs) |
| Purge unused (preview, then per-element purge) | S | Done |
| Copy standards from another open model (templates, filters, types, patterns, materials, title blocks, line styles) | M | Built |
| Worksets: list, ownership, create, move elements | S | Built (needs a workshared test model) |
| Phases and design options: read; phase created / demolished | S | Done |
| Create non-shared project parameters; move elements into a design option | - | Not possible (no API) |

## Phase 6 - Coordination and links (in progress)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Read elements inside linked Revit models (with the link transform) | Read | High | M | Built (0.16.0) |
| Clash check against a linked Revit or IFC model | Read | High | L | Planned |
| Link management: reload, unload, change path, pin | Edit | High | S | Planned |
| Project base point, survey point, shared coordinates: read | Read | High | S | Planned |
| Annotation values: dimension values, tag text, for drawing QA | Read | High | S | Planned |

## Phase 7 - Reinforcement completion (planned)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Path reinforcement for walls and slabs (given values) | Edit | High | M | Planned |
| Fabric sheets and fabric areas | Edit | High | M | Planned |
| Rebar hooks from the model's hook types (given by the engineer) | Edit | High | M | Planned |
| Rebar tags and rebar views (sections through members) | Edit | High | M | Planned |
| Couplers | Edit | Medium | M | Planned |
| Rebar in sloped, curved and irregular members | Edit | Medium | L | Planned |
| Path reinforcement, couplers, rebar containers in rebar_list | Read | Medium | S | Planned |

## Phase 8 - Modify and documentation depth (built)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Flip facing and hand | Edit | Medium | S | Built (0.17.0) |
| Wall and beam end joins (allow / disallow, extensions) | Edit | Medium | M | Built (0.17.0) |
| Face-based and work-plane-based families | Edit | Medium | M | Built (0.17.0) |
| Groups and assemblies: read, create, assembly views | Read + Edit | Medium | L | Built (0.17.0) |
| Schedules: filters, sorting, grouping, formatting | Edit | Medium | M | Built (0.17.0) |
| Dimensions: rotated, angular, radial | Edit | Medium | L | Built (0.17.0); radial needs Revit 2027 |
| Spot coordinates | Edit | Low | S | Built (0.17.0) |
| Spot slopes | Edit | Low | - | Not possible (no API) |
| fix_warnings: more warning types with one safe fix | Edit | Medium | M | Built (0.17.0) |
| Raw element geometry (faces, edges, solids) read | Read | Low | S | Built (0.17.0) |

## Phase 9 - Standards and data (built)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Global parameters: read, create, set | Read + Edit | Medium | S | Built (0.17.0) |
| Material assets (structural, thermal, appearance): read, set | Read + Edit | Medium | M | Built (0.17.0) |
| Keynotes and assembly codes: read | Read | Low | S | Built (0.17.0) |
| Export DWF, FBX, gbXML, DGN | Edit | Low | S | Built (0.17.0): DWF and FBX tested live; DGN needs a DGN setup or seed file; gbXML not working live yet |

## Phase 10 - Analytical model and loads (planned)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Analytical members and panels: read, with releases and supports | Read | Medium | M | Planned |
| Load cases, combinations and loads: read | Read | Medium | M | Planned |
| Place loads and load cases from given values (no load derivation) | Edit | Low | L | Planned |

## Phase 11 - Architectural and site modelling (planned)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Pile caps and piles from a layout or Excel | Edit | Medium | S | Planned |
| Roofs and ceilings | Edit | Low | M | Planned |
| Slab edges and wall sweeps | Edit | Low | M | Planned |
| Stairs, railings, ramps | Edit | Low | L | Planned |
| Curtain walls and grids | Edit | Low | L | Planned |
| Rooms, areas, spaces: read, create | Read + Edit | Low | M | Planned |
| Topography and site (toposolid) | Edit | Low | M | Planned |
| Imported or free-form geometry (DirectShape) | Edit | Low | M | Planned |

## Phase 12 - Worksharing (planned)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Synchronise with central (comment, relinquish options); reload latest | Edit | Medium | M | Planned |
| Relinquish all or chosen elements; check out elements before an edit | Edit | Medium | S | Planned |
| Separate confirmation for every sync: no dry run exists, so it never runs inside an edit batch | Edit | Medium | S | Planned |

## Phase 13 - MEP (planned)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Read MEP systems, connectors, sizes, flow direction | Read | Medium | M | Planned |
| MEP quantities: duct, pipe, tray, conduit lengths by size; fittings and equipment counts | Read | Medium | M | Planned |
| MEP crossings through beams, walls and slabs, feeding opening_create (sleeves, openings) | Read + Edit | Medium | M | Planned |
| Ducts, pipes, cable trays, conduits between points with given types and sizes | Edit | Low | L | Planned |
| Place MEP equipment and fixtures and connect them to systems | Edit | Low | M | Planned |
| Create systems and assign elements | Edit | Low | L | Planned |

## Phase 14 - Steel connections (planned)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Read steel connections: type, members joined, plates, bolts | Read | Low | S | Planned |
| Place connections of a type the engineer chooses between given members | Edit | Low | L | Planned |
| Steel plates, bolts, cuts and welds (Steel namespace) from given values | Edit | Low | L | Planned |

## Out of scope

| Feature | Status |
|---|---|
| Any design: code-calculated anchorage or laps, MEP sizing and calculations, connection design | Out of scope |
| Point clouds, extensible storage, creating family geometry, fabrication parts | Out of scope |
| Move elements into a design option; non-shared project parameters | Not possible (no API) |
| Create spot slopes; radial dimensions in Revit 2024 | Not possible (no API) |

## Open checks

- Phases 8 and 9 tested live in Revit 2024 only (Task 04 dry runs, real runs in a scratch project); run them in 2027,
  including dimension_radial (2027 only).
- export_dgn with a DGN setup or seed file; export_gbxml with an energy analysis model set up in Revit.

- `join_overlaps` real run on a detached copy of a workshared model (the failure handler now detaches only the exact
  pair it joined).
- `views_copy_from` and `standards_copy` with a second open model that has drafting views.
- Workset operations on a workshared, detached copy.
