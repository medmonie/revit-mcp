# Roadmap

Planned and finished work for the Revit MCP bridge, by phase. S = one build batch, M = one or two batches with a live
test, L = several batches. Status as of version 0.14.0 (November 2026).

| Status | Meaning |
|---|---|
| Done | Built for Revit 2024 and 2027 and tested live |
| Built | Built and compiled for both releases; live test pending |
| Planned | Not started |
| Not possible | The Revit API does not allow it |

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
| Code-calculated anchorage and lap lengths (ECP 203 / ACI 318 / EC2) | M | Planned (today a multiple of the bar diameter you set) |
| Hooks from Revit hook types, cranked laps | M | Planned |
| Path reinforcement for walls and slabs; rebar tags and rebar views | M | Planned |

## Phase 4 - Modelling gaps (done)

| Feature | Effort | Status |
|---|---|---|
| Sloped slabs (slope arrow, slab shape points) | M | Done |
| Beam systems | M | Done |
| Braces | S | Done |
| Pile caps and piles from a layout or Excel | S | Planned |
| Slab edges, wall sweeps, stairs, ramps, roofs | L | Planned |

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

## Phase 6 - Coordination (planned)

| Feature | Effort | Status |
|---|---|---|
| Clash check against a linked Revit or IFC model | L | Planned |
| Analytical model, loads and load cases | L | Planned |
| Link management: reload, unload, path, pin | S | Planned |
| Synchronise with central, relinquish | S | Left out on purpose (do it in Revit) |

## Open checks

- `join_overlaps` real run on a detached copy of a workshared model (the failure handler now detaches only the exact
  pair it joined).
- `views_copy_from` and `standards_copy` with a second open model that has drafting views.
- Workset operations on a workshared, detached copy.
