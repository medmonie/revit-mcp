# Roadmap

Planned and finished work for the Revit MCP bridge, by phase. S = one build batch, M = one or two batches with a live
test, L = several batches. Status as of version 0.29.0 (28 November 2026).

| Status | Meaning |
|---|---|
| Done | Built for Revit 2024 and 2027 and tested live |
| Built | Built and compiled for both releases; live test pending |
| Planned | Not started |
| Not possible | The Revit API does not allow it |
| Out of scope | Left out by rule: the bridge never designs |

Priority: High, Medium, Low. The bridge models, reads, checks and documents; it never designs, so every planned
feature places or reads what the engineer gives.

The whole Revit API (2024 and 2027: 2,713 public types) is mapped against the bridge in the
[Revit API capability map](https://claude.ai/artifact/1TPLW8RrsshSPtNaz7fxiJ) (Sheet, 25 Nov 2026): 90 capabilities,
36 built, 20 partly built, 22 planned or not built. Its Build plan tab gives each gap a phase and a priority
(P1 first). Agreed build order:

1. Phase 6 (P1): coordinates read, link management, annotation values, clash check against a linked Revit / IFC model. Built in 0.18.0.
2. Phase 7 (P1 / P2): path and fabric reinforcement, then couplers, hooks, rebar tags, numbering, 2027 splices.
3. Phase 7 (rest): sloped / curved members, 2027 cranks. Phase 15 (P1): ray casting (what is above / below, clearances); Phase 16 (P1): open / close models, batch reads.
4. Phases 10, 11, 12, 13, 14 by their P2 items, then the P3 items.

## Phase 1 - Drafting and detailing (done)

| Feature | Effort | Status |
|---|---|---|
| Drafting views | S | Done |
| Detail lines: arcs, polylines, line styles | S | Done |
| Filled and masking regions | M | Done |
| Detail components (points, along a line, line-based) and detail groups | M | Done |
| Callouts, duplicate views, view settings (scale, crop, view range, scope box) | S | Done |
| Copy drafting views / details from another open model (`views_copy_from`) | M | Done (tested 0.23.1 from Task 02) |
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
| Copy standards from another open model (templates, filters, types, patterns, materials, title blocks, line styles) | M | Done (tested 0.23.1 from Task 02) |
| Worksets: list, ownership, create, move elements | S | Done (tested 0.22.2 on a detached workshared copy) |
| Phases and design options: read; phase created / demolished | S | Done |
| Create non-shared project parameters; move elements into a design option | - | Not possible (no API) |

## Phase 6 - Coordination and links (built)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Read elements inside linked Revit models (with the link transform) | Read | High | M | Built (0.16.0) |
| Clash check against a linked Revit or IFC model | Read | High | L | Built (0.18.0, hard clashes; IFC links are linked by Revit Insert > Link IFC) |
| Link management: reload, unload, change path, pin | Edit | High | S | Built (0.18.0) |
| Project base point, survey point, shared coordinates: read | Read | High | S | Built (0.18.0) |
| Annotation values: dimension values, tag text, for drawing QA | Read | High | S | Built (0.18.0) |

## Phase 7 - Reinforcement completion (in progress)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Path reinforcement for walls and slabs (given values) | Edit | High | M | Built (0.19.0) |
| Fabric sheets and fabric areas | Edit | High | M | Built (0.19.0) |
| Rebar hooks from the model's hook types (given by the engineer) | Edit | High | M | Built (0.19.0, on existing bar sets) |
| Rebar tags and rebar views (sections through members) | Edit | High | M | Built (0.19.0 rebar_annotate; sections by section_create; tags by tag_elements) |
| Couplers | Edit | Medium | M | Built (0.19.0) |
| Rebar in sloped, curved and irregular members | Edit | Medium | L | Planned |
| Path reinforcement, couplers, rebar containers in rebar_list | Read | Medium | S | Built (0.19.0 / 0.20.0) |
| Revit 2027: splices by rules, cranks, numbering schemas | Edit | Medium | M | Splices built (0.20.0); numbering read and renumber built for both versions; cranks and 2027 numbering-schema creation planned |

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

## Phase 10 - Analytical model and loads (built)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Analytical members and panels: read, with releases and supports | Read | Medium | M | Built (0.21.0; live model had none) |
| Load cases, combinations and loads: read | Read | Medium | M | Built (0.21.0; live model had none) |
| Place loads and load cases from given values (no load derivation) | Edit | Low | L | Done (0.22.0: load cases, combinations, hosted point / line / area loads; analytical members and panels) |

## Phase 11 - Architectural and site modelling (partly done; the rest moves to phase 17)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Pile caps and piles from a layout or Excel | Edit | Medium | S | Planned |
| Roofs and ceilings | Edit | Low | M | Planned |
| Slab edges and wall sweeps | Edit | Low | M | Planned |
| Stairs, railings, ramps | Edit | Low | L | Planned |
| Curtain walls and grids | Edit | Low | L | Planned |
| Rooms, areas, spaces: read, create | Read + Edit | Low | M | Done (0.21.0 read; 0.22.0 rooms and spaces at points or every region, separation lines; areas not yet) |
| Topography and site (toposolid) | Edit | Low | M | Planned |
| Imported or free-form geometry (DirectShape) | Edit | Low | M | Planned |

## Phase 12 - Worksharing (done)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Synchronise with central (comment, relinquish options); reload latest | Edit | Medium | M | Done (0.23.1, tested on a cloud model) |
| Relinquish all or chosen elements; check out elements before an edit | Edit | Medium | S | Done (0.23.1; relinquish by kind, not by element) |
| Separate confirmation for every sync: no dry run exists, so it never runs inside an edit batch | Edit | Medium | S | Done (own actions; sync and relinquish need confirm: true) |

## Phase 13 - MEP (partly done; the rest moves to phase 18)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Read MEP systems, connectors, sizes, flow direction | Read | Medium | M | Done (0.21.0, mep_systems; connectors / flow not yet) |
| MEP quantities: duct, pipe, tray, conduit lengths by size; fittings and equipment counts | Read | Medium | M | Done (0.21.0, mep_quantities) |
| MEP crossings through beams, walls and slabs, feeding opening_create (sleeves, openings) | Read + Edit | Medium | M | Done (0.21.0 mep_crossings; 0.22.0 crossing_opening in host structure; sleeve families not yet) |
| Ducts, pipes, cable trays, conduits between points with given types and sizes | Edit | Low | L | Built (0.22.0, straight runs, no fittings) |
| Place MEP equipment and fixtures and connect them to systems | Edit | Low | M | Planned |
| Create systems and assign elements | Edit | Low | L | Planned |

## Phase 14 - Steel connections (planned)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Read steel connections: type, members joined, plates, bolts | Read | Low | S | Built (0.21.0: type, members, approval; tested on Task 02, 80 connections; plates / bolts not yet) |
| Place connections of a type the engineer chooses between given members | Edit | Low | L | Planned |
| Steel plates, bolts, cuts and welds (Steel namespace) from given values | Edit | Low | L | Planned |

## Phase 15 - Model QA and query depth (proposed)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Ray casting: what is above / below / beside a point or element, clearances (ReferenceIntersector) | Read | P1 | M | Done (0.21.0: ray_cast, clearances; tested on the KTR15 federated model) |
| Parts: create from layers, divide (pour breaks, construction joints), merge | Edit | P2 | M | Planned |
| More model_health checks from Revit's PerformanceAdviser rules | Read | P2 | S | Planned |
| Linear / radial arrays; alignment locks; reference planes | Edit | P3 | S | Planned |
| Interactive picking (the user clicks elements or points while Claude waits) | Read | P3 | M | Planned |
| Change watching (dynamic model updaters) and preview graphics before an edit (DirectContext3D) | Read | P3 | L | Planned |

## Phase 16 - Documents and batch automation (first part done)

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Open (detached, read-only), close, new from template; reads over a folder of models without saving | Read + Edit | P1 | M | Done (0.24.1: list_documents, model_files, model_open / close / new, batch_read; reads by document_id of any open model) |
| Schedule depth: calculated fields, key schedules, cell styles, split schedules | Edit | P2 | M | Planned |
| Family type parameters and formulas in loaded families (no geometry) | Edit | P3 | M | Planned |
| Colour fill schemes; arc-length and ordinate dimensions; sheet collections and STEP export (2027) | Edit | P3 | M | Planned |
| Autodesk (ACC) parameters service | Edit | P3 | M | Planned |

## Phase 17 - Architecture (proposed; for the architecture department; takes over the rest of phase 11)

Values (sizes, ratings, finishes, heights) come from the architect; nothing is designed or code-checked.

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| Doors and windows hosted in walls at given positions (type, sill / head height, flip) | Edit | P1 | M | Planned |
| Wall layers, finishes and wall types from given layers | Edit | P1 | M | Planned |
| Room data: finishes, occupancy, names / numbers from Excel; room-data and finish sheets to Excel | Read + Edit | P1 | M | Planned |
| Door, window, room and finish schedules | Edit | P1 | S | Planned |
| Architectural QA: untagged / unplaced / not enclosed rooms, door and window marks, naming | Read | P1 | S | Planned |
| Area plans and area schemes (gross, rentable), colour fill schemes | Edit | P2 | M | Planned |
| Ceilings and roofs (footprint, slope from given values) | Edit | P2 | M | Planned |
| Curtain walls: grids, mullions, panels | Edit | P2 | L | Planned |
| Stairs, railings, ramps from given risers / treads / widths | Edit | P3 | L | Planned |
| Slab edges, wall sweeps and reveals; toposolid and site | Edit | P3 | M | Planned |

## Phase 18 - MEP and HVAC (done: 0.25.4, 0.26.3, 0.27.0, 0.28.0 and 0.29.0; for the MEP department; takes over the rest of phase 13)

Revit's own calculated values (flow, velocity, pressure drop) are read and reported; design values (airflow per space,
equipment capacity, sizes, flow rates, lighting levels, circuit loads) come from the engineer. Nothing is sized here.

| Feature | Read / edit | Priority | Effort | Status |
|---|---|---|---|---|
| **HVAC:** connected duct runs with fittings (elbows, tees, transitions) from the routing preferences of the duct type | Edit | P1 | L | Done 0.25 (mep_route; mep_connect for branches: tap or tee) |
| **HVAC:** air terminals (supply, return, exhaust) placed in ceilings / spaces with the given airflow, connected to a duct | Edit | P1 | M | Done 0.26 (air_terminal_place: flows, connected to a main) |
| **HVAC:** mechanical equipment (AHU, FCU, VRF indoor / outdoor units, fans) placed from given families and connected | Edit | P1 | M | Done 0.27 (family_place; mep_route from an equipment connector, to_main; mep_connect classification) |
| **HVAC:** duct systems (supply / return / exhaust) and hydronic piping systems (chilled / hot water, condensate): create, assign | Edit | P1 | M | Done 0.25 / 0.26 (mep_system_create; mep_system_add / remove) |
| **HVAC:** spaces and HVAC zones with the engineer's design airflow; read space airflow vs. sum of its terminals | Read + Edit | P1 | M | Done 0.26 (spaces_from_rooms, space_airflow_set, hvac_zone_create (2024); read: air_terminals) |
| **HVAC:** read Revit's computed flow, velocity and pressure drop per duct / pipe and per system; flag values over limits the engineer gives | Read | P1 | M | Done 0.25 (mep_flow) |
| **HVAC:** duct accessories (dampers, fire dampers at fire-rated walls / slabs found by crossings), insulation and lining | Read + Edit | P2 | M | Done 0.28 (mep_accessory_place at points or crossings, host or link; mep_insulation_add / remove; read mep_insulation). Pipe accessories (valves) too |
| **HVAC:** air terminal, equipment, duct and pipe schedules; quantities to Excel | Read + Edit | P1 | S | Done 0.27 / 0.29 (schedule_create on MEP categories; mep_quantities with duct surface area; mep_report workbook) |
| Plumbing and fire fighting: fixtures, sprinklers at given points, connected pipe runs with fittings, systems | Edit | P2 | L | Done 0.29 (mep_fixture_place: level, ceiling / floor plane, wall face; connected to a main; mep_route slope; systems with mep_system_create) |
| Electrical: read panels, circuits and connected loads; place devices and circuit them from a given list | Read + Edit | P2 | L | Done 0.29 (electrical_circuits; mep_fixture_place on walls / ceilings, host or link; electrical_circuit_create / edit; electrical_panel_set) |
| Cable trays and conduits with fittings | Edit | P3 | M | Done 0.29 (mep_route elbows; tees with to_main / mep_connect; routes from tray / conduit connectors) |
| MEP QA: open ends, disconnected elements, elements not in a system, terminals outside spaces | Read | P1 | M | Open ends and unsystemed connectors done 0.25 (mep_connectivity); terminals outside spaces via air_terminals |
| Sleeves (sleeve families) at crossings found by mep_crossings, beside the existing crossing_opening | Edit | P1 | S | Done 0.29 (sleeve_place, host or link, measured lengths plus clearance) |
| Connectors and flow direction read (the rest of phase 13's first row) | Read | P2 | S | Done 0.27 (mep_connectors) |

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

