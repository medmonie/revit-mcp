"""The catalogue of everything Claude can ask Revit to do.

This file is the single place for the WORDING Claude sees (action names, what they do, their parameters).
The behaviour lives in the add-in; `help` and the tests compare the two so they cannot drift apart silently.
docs/ACTIONS.md is generated from this file (tools/gen_docs.py).

Each entry: name -> (summary, {parameter: description}).  A parameter ending in '?' is optional.
The groups mirror the add-in sources: plugin/Read/<Group>Read.cs and plugin/Edit/<Group>Ops.cs.
"""
from __future__ import annotations

import os

BRIDGE_VERSION = "0.30.0"   # keep plugin/BridgeInfo.cs in step; the tests check it

Entry = tuple[str, dict[str, str]]
Group = dict[str, Entry]

# Parameter descriptions that repeat.
XY = "[x,y]"
XYZ = "[x,y] or [x,y,z]"
IDS = "list of element ids"
CATEGORY = "alias (columns, beams, walls, floors, foundations, rebar, doors, windows, generic_models, ...), UI name ('Structural Columns') or OST_ name"
CATEGORIES = "one category or a list; default: columns, beams, walls, floors, foundations"
TYPE = "'Type name' or 'Family : Type name' (see list_types)"
LEVEL = "level name"
CATEGORY_LIST = "one category or a list (aliases such as columns, beams, floors, walls, foundations, or Revit names)"
VIEW = "view name or id; default: the active view"
VIEWS = "list of view names / sheet numbers / ids; default: the active view"
FOLDER = "full path of the output folder (created if missing)"
OVERWRITE = "default false: an existing file makes the batch fail"
SCOPE = {"view?": "only elements visible in this view", "level?": "only elements on this level"}
CAD_ID = "id of the CAD file from list_links"
LINK = {"link?": "read inside this loaded Revit link: instance id, link type id or file name from list_links",
        "coordinates?": "with link: host (default, the link transform applied) | link (the linked model's own coordinates)"}
OVERRIDES = {"color?": "\"#RRGGBB\", [r, g, b] or a name (red, blue ...)", "fill?": "default false: solid fill in the colour",
             "line_weight?": "1-16", "halftone?": "bool", "transparency?": "0-100"}
COLUMN_LAYER = "the column layer (see get_cad_geometry layers_only), e.g. S-COLS"
STEEL_WORDS = "extra words that mark steel families, e.g. 'STL,SHS'"
SKIP_EXISTING = "default true: skip what is already modelled there"

# --------------------------------------------------------------------------- read actions (never change the model)

READ_GROUPS: dict[str, Group] = {
    "Model": {
        "ping": ("Connection check with Revit version, active model and view, documentId for the edit target, and edit permission.", {}),
        "revit_addins": ("What this Revit has loaded: add-in applications (name, assembly, version, file), optional exporters (Navisworks, IFC, "
                         "DGN, DWG, DXF), the loaded structural analysis and steel connection add-ins by name, and the analytical members, "
                         "panels, load cases, supports and steel connection types in the active model.", {}),
        "get_project_info": ("Active model: title, path, workshared / read-only, project information, phases, "
                             "counts of structural elements, open models.", {}),
        "list_levels": ("Levels with project-relative elevation and Revit's displayElevation, sorted by project elevation; "
                        "with link, the linked model's levels (elevation in the reported coordinates and linkElevation).", {**LINK}),
        "list_grids": ("Grids with their end points (and mid point and radius for arc grids); with link, the linked model's grids.",
                       {"limit?": "default 500", **LINK}),
        "list_views": ("Views (not templates, not sheets): name, type, level, scale.",
                       {"type?": "e.g. FloorPlan, EngineeringPlan, ThreeD, Section, Elevation, Schedule, DraftingView", "limit?": "default 200"}),
        "list_sheets": ("Sheets: number, name, the views placed on them.", {"limit?": "default 500"}),
        "count_by_category": ("Number of model elements per category, largest first.",
                              {"view?": "only elements visible in this view (a host view, also with link)", "include_annotation?": "default false",
                               "link?": LINK["link?"]}),
        "list_types": ("Types of a category (family, type, id) with the number of instances of each. "
                       "Use these names for the 'type' parameter of edit operations.",
                       {"category": CATEGORY, "limit?": "default 300", "link?": LINK["link?"]}),
        "list_materials": ("Materials: name, class.", {"limit?": "default 300"}),
        "list_warnings": ("Revit's own warnings (overlaps, duplicates, joins ...) with the elements involved, and a count per message.",
                          {"limit?": "default 100"}),
    },
    "Elements": {
        "get_elements": ("Elements of one or more categories (or by id): id, family, type, level, location (point or end points) "
                         "and any parameters you name. With link: the elements of a linked Revit model (their own ids, types and levels), "
                         "located in host coordinates through the link transform.",
                         {"category?": "one category or a list (required without element references): " + CATEGORY, "ids?": IDS,
                          "unique_ids?": "list of element UniqueIds instead of ids", "allow_missing?": "default false; true reports missing references",
                          "type?": TYPE, "level?": LEVEL, "view?": "only elements visible in this view (a host view, also with link)",
                          "params?": "names or bip:/guid:/id: selectors (instance first, then type)",
                          "parameter_details?": "include each requested parameter's stable key, type, scope and writability",
                          "location?": "default true", "box?": "bounding box, default false", "limit?": "default 100",
                          "after_id?": "nextAfterId from the previous page, with the same filters", **LINK}),
        "get_element": ("Element geometry and parameter details, including same-name parameters and their stable keys.",
                        {"id": "element id (in the link when link is given)", "include_type?": "default true", **LINK}),
        "get_selection": ("The elements the user has selected in Revit.", {"params?": "parameter names to include", "limit?": "default 200"}),
        "show_elements": ("Select elements in Revit and zoom to them (changes only what the user sees, not the model).",
                          {"ids": IDS, "select?": "default true", "zoom?": "default true"}),
    },
    "Links and CAD": {
        "list_links": ("Revit links (file, status, path, loaded document, fromIfc, transform: origin and plan rotation) and linked / imported "
                       "CAD files (file, linked or imported, view). Read inside a loaded link with the link parameter of get_elements, "
                       "get_element, count_by_category, list_types, list_levels and list_grids.", {}),
        "get_cad_geometry": ("Lines, arcs, circles and polylines inside a linked / imported DWG or DXF, by layer, in model coordinates. "
                             "Call with layers_only first, then read one layer (e.g. the grid or column layer) to model from it.",
                             {"id": CAD_ID, "layer?": "only this layer",
                              "layers_only?": "default false: true returns just the layer names with entity counts", "limit?": "default 2000"}),
        "cad_columns": ("Column outlines on a CAD layer, each fitted with a size and rotation and matched to a column type (size from the type's "
                        "b/h, bf/d, Width/Depth or Diameter, else the first 'AxB' in its name); steel outlines (H / I) match only steel families, rectangles only non-steel; shows what columns_from_cad would place. Reads only.",
                        {"id": CAD_ID, "layer": COLUMN_LAYER,
                          "include?": "concrete (default: plain rectangles and circles) | steel (H / I and other outlines) | all",
                          "types?": "{\"650x1300\": \"Type name\", ...}: type per outline size (width x depth as listed), checked before the automatic match",
                          "tolerance?": "size match tolerance; default 10 mm", "gap?": "curves closer than this belong to one column; default 25 mm",
                          "min_size?": "default 150 mm", "max_size?": "default 4000 mm",
                          "skip_existing?": "default true: skip outlines where a column already stands at the base", "level?": "base level (for the existing-column check); default the drawing's elevation",
                          "limit?": "outlines listed; default 50"}),
        "get_coordinates": ("Project base point, survey point and internal origin (internal and shared coordinates), angle to true north, "
                            "the active and other named locations, and the site (place, latitude / longitude). With link=: the same for the "
                            "linked model, and whether its shared coordinates agree with the host's (offset and rotation if not).",
                            {"link?": "a loaded Revit link (instance id, link type id or file name from list_links)",
                             "coordinates?": "with link: host (default, base points shown in host coordinates) | link"}),
        "link_clashes": ("Hard clashes between this model's elements and a loaded Revit link (an IFC link too): pairs whose solids intersect, "
                         "with volume and a point inside each clash, grouped by category pair. Reads only.",
                         {"link": "the Revit or IFC link (instance id, link type id or file name from list_links)",
                          "category?": "host categories; default: foundations, columns, walls, beams, floors",
                          "link_category?": "categories in the link; default every model category except rooms, areas, spaces, masses",
                          "level?": "only host elements on this level", "view?": "only host elements visible in this view",
                          "min_volume?": "smallest clash counted, m3; default about 0.03 cm3 (touching faces do not count)",
                          "max_seconds?": "time budget, default 40; then resume with resume_after", "resume_after?": "resumeAfter from the previous call",
                          "limit?": "clashes listed, default 200"}),
        "list_annotations": ("What the drawings say, for QA: dimension values and the text they show (per segment), text overrides, prefixes / "
                             "suffixes, references; spot dimensions; tag texts and the elements tagged; text notes. Flags text_override, "
                             "zero_value, no_references, orphaned, empty_text.",
                             {"view?": "one view", "sheet?": "a sheet (number, name or id): the sheet and every view on it",
                              "kind?": "any of dimensions, spots, tags, text; default all", "only_issues?": "default false: list only flagged items",
                              "limit?": "items listed, default 500"}),
    },
    "Quantities": {
        "takeoff": ("Quantity take-off from the model: count, length, area and volume as Revit computes them, grouped.",
                    {"category?": CATEGORIES, "group_by?": "type (default) | level | type_level | category", **SCOPE}),
        "material_takeoff": ("Volume and area by material and category, with concrete, non_concrete, and unclassified material kinds.",
                             {"category?": CATEGORIES, "group_by?": "category (default) | level | type",
                              "exclude_steel?": "default false: true leaves steel members out (e.g. steel wrongly carrying a concrete material)",
                              "family_contains?": STEEL_WORDS, **SCOPE}),
        "formwork_takeoff": ("Formwork areas from element faces: sides for all, soffits for beams and slabs, never top faces; steel skipped.",
                             {"category?": CATEGORIES, "group_by?": "type (default) | level | type_level | category",
                              "soffit_categories?": "categories whose bottom face is formed; default beams and floors (remove floors for slabs on grade)",
                              "family_contains?": STEEL_WORDS, **SCOPE}),
        "steel_takeoff": ("Steel weight by section: count, length, volume, kg and kg/m (Revit volume x density, independent of the material assigned).",
                          {"category?": "default: columns and beams", "group_by?": "type (default) | level | type_level | category",
                           "density?": "kg/m3, default 7850", "family_contains?": STEEL_WORDS,
                           **SCOPE}),
        "rebar_list": ("Every bar set (rebar and bars of area / path systems, with systemKind) for a bar bending schedule: member, mark, "
                       "bar type, diameter, shape, number, length, dimensions A..J, total length, weight (weightSource); totals by "
                       "diameter; fabric sheets (cut size, kg) and couplers.",
                       {"hosts?": "only bars in these host element ids", "level?": "only bars whose host is on this level",
                        "view?": "only bars visible in this view", "limit?": "default 5000"}),
        "list_reinforcement_types": ("Reinforcement types in the model, by the names the reinforcement edits take: bar types (diameter, "
                                     "hooks allowed), hook types (angle), path / area / fabric area types, fabric sheet types (size, "
                                     "spacing, wires, laps, kg), fabric wire types, coupler types, rebar annotation types; Revit 2027 splice and crank types.",
                                     {"limit?": "default 300 per list"}),
        "list_numbering": ("Revit's numbering of rebar, fabric and couplers: each schema's partitions with the numbers in use (ranges), "
                           "their count and the gaps.", {}),
        "rebar_takeoff": ("Reinforcement: bars, total length (m) and weight (kg, 7850 kg/m3), grouped.",
                          {"group_by?": "diameter (default) | host_category | type | diameter_host_category",
                           "host_category?": "only bars hosted by these categories", **SCOPE}),
        "list_schedules": ("Schedules in the model: name, category, number of fields.", {"limit?": "default 300"}),
        "get_schedule_data": ("The rows of a schedule as displayed (text).", {"schedule": "schedule name or id", "limit?": "default 500 rows"}),
    },
    "Model QA": {
        "check_materials": ("Elements with no material, a Revit default material (Concrete Cast-in-Place gray, Default ...) or a name outside your "
                            "pattern, and steel members carrying a non-metal material; volume and sample ids per problem.",
                            {"category?": CATEGORIES, "pattern?": "regular expression for ACCEPTED material names, e.g. '^KTR15-'",
                             "family_contains?": STEEL_WORDS, "view?": "only elements visible in this view",
                             "level?": "only elements on this level"}),
        "find_overlaps": ("Scan unjoined intersections and sum measured pairs. Reports coincident geometry, containment and near coincidence for review.",
                          {"category?": "default: foundations, columns, walls, beams, floors", "level?": "only elements on this level",
                           "view?": "only elements visible in this view", "volumes?": "default true: compute each overlap volume (slower)",
                           "include_steel?": "default false: pairs with a steel member are skipped", "family_contains?": STEEL_WORDS,
                           "max_seconds?": "time budget, default 40; then resume with resume_after", "resume_after?": "resumeAfter cursor from the previous call",
                           "max_pairs?": "default 20000", "limit?": "pairs listed, default 200"}),
        "check_names": ("Names that do not match a regular expression (naming convention check).",
                        {"kind": "views | sheets | types | levels | grids | materials | families | view_templates",
                         "pattern": "regular expression the names must match, e.g. '^KTR15-S-'",
                         "field?": "sheets only: name (default) | number", "category?": "types only: limit to one category",
                         "limit?": "default 300"}),
    },
    "Model management": {
        "model_health": ("Model health report: file size, warnings (top 10), in-place families, CAD linked / imported, unloaded links, views "
                         "not on sheets, unused view templates and group types, purgeable elements, worksets, design options; plus an "
                         "'attention' list of what stands out.",
                         {"purgeable?": "default true: count purgeable elements (Revit 2024 and later)"}),
        "list_parameters": ("Project and shared parameters bound in the model: name, shared GUID, data type, palette group, instance / type, "
                            "categories; optionally the groups and parameters of a shared parameter file.",
                            {"shared_file?": "full path of a shared parameter file to list", "shared_file_contents?": "default false: list the current shared parameter file"}),
        "list_worksets": ("Worksets (open, editable, owner, active) and, for given elements, their workset, checkout status, owner, "
                          "creator and last changed by.",
                          {"ids?": IDS, "counts?": "default false: elements per workset (slower)"}),
        "list_phases": ("Phases in order, phase filters, and design option sets with their options (primary, element count).", {}),
    },
    "Detailing and documentation": {
        "list_documentation": ("What documentation refers to by name: view templates (and how many views use each), filters, revisions, "
                               "scope boxes, title blocks.",
                               {"kind?": "view_templates | filters | revisions | scope_boxes | title_blocks; default all"}),
        "list_detailing": ("What drafting and detailing can use: line styles, filled region types, detail components (line-based or not), "
                           "detail groups, repeating detail types, drafting views and legends; in this model or another open one "
                           "(the source for views_copy_from).",
                           {"kind?": "line_styles | region_types | detail_components | detail_groups | repeating_details | drafting_views | legends; default all",
                            "model?": "title of another model open in this Revit (e.g. a detail library); default the active model",
                            "limit?": "per kind, default 300"}),
    },
    "Groups, assemblies and geometry": {
        "list_groups": ("Model and detail group types: instances, members and their categories, instance ids.",
                        {"kind?": "all (default) | model | detail", "limit?": "default 200"}),
        "list_assemblies": ("Assemblies: name, naming category, members by category, centre, and the assembly views made for each.",
                            {"limit?": "default 200"}),
        "get_geometry": ("Raw geometry of one element: each solid (volume, surface area, centroid, box, faces with kind, area, normal / axis), "
                         "free curves, meshes; as modelled (after joins and cuts) or the family's original shape.",
                         {"id": "element id (in the link when link is given)", "detail?": "coarse | medium | fine (default)",
                          "original?": "family instances: default false; true = geometry before joins and cuts",
                          "faces?": "default true: list the faces", "edges?": "default false: total edge length per solid",
                          "include_hidden?": "default false", "limit?": "faces listed, default 200", **LINK}),
    },
    "Standards and data": {
        "list_global_parameters": ("Global parameters: name, spec, value (lengths in request units, m2, m3, degrees), formula, reporting, "
                                   "driven by a dimension, number of elements it drives.", {"limit?": "default 300"}),
        "get_material": ("One material in full: identity, graphics (shading colour, patterns), structural asset (density kg/m3, E, G in MPa, "
                         "Poisson, thermal expansion 1/C, concrete f'c or steel yield / tensile in MPa), thermal asset (W/(m K), J/(kg C)), "
                         "appearance asset and its colour, and the other materials sharing each asset.",
                         {"material": "material name or id"}),
        "list_keynotes": ("Keynote file (path and entries) and assembly code table, and the keynote / assembly code of each type in use, "
                          "with the keynote text and the counts of types missing them.",
                          {"category?": CATEGORIES, "used_only?": "default true: only types with instances",
                           "entries_limit?": "keynote entries listed, default 100", "limit?": "types listed, default 300"}),
    },
    "Clearances and crossings": {
        "ray_cast": ("What lies above, below or beside a point or an element (in this model or a link): cast rays through a 3D view, "
                     "into loaded links too; hits nearest first with distance and point.",
                     {"origin?": XYZ + " start point (or give id)", "id?": "start from this element's box (bottom for down, top for up)",
                      "link?": "the element of id is in this link", "direction?": "up | down (default) | east | west | north | south | [x,y,z], or a list",
                      "category?": "only hit these categories", "links?": "default true: search inside links", "max_distance?": "default 50 m",
                      "limit?": "hits per ray, default 5", "view3d?": "3D view to cast in; default {3D}"}),
        "clearances": ("Clear distance from each element of a category (in this model or a link) to the first element below (or above): "
                       "headroom under ducts and beams, gaps to slabs. Smallest first.",
                       {"category": CATEGORY, **LINK, "direction?": "down (default) | up", "target_category?": "only these count as obstacles",
                        "less_than?": "report only clearances below this", "links?": "default true", "max_distance?": "default 50 m",
                        "max_seconds?": "default 40; then resume_after", "resume_after?": "", "limit?": "default 200", "view3d?": "", **SCOPE}),
        "mep_crossings": ("Where ducts, pipes, cable trays and conduits pass through beams, walls, slabs, columns and foundations, each model "
                          "in this model or a link (MEP and structure links): point, length inside, size, system, structural element.",
                          {"mep_link?": "link holding the MEP runs (default this model)", "structure_link?": "link holding the structure (default this model)",
                           "mep_category?": "default ducts, pipes, cable trays, conduits", "structure_category?": "default beams, walls, floors, columns, foundations",
                           "min_size?": "only runs at least this big", "max_seconds?": "default 40; then resume_after", "resume_after?": "", "limit?": "default 300"}),
    },
    "MEP": {
        "mep_systems": ("MEP systems (duct, pipe, electrical): name, type, classification, terminals and equipment, network size, base equipment, well connected.",
                        {**LINK, "kind?": "all (default) | duct | pipe | electrical", "limit?": "default 300"}),
        "mep_quantities": ("MEP run lengths (ducts, pipes, trays, conduits, flex) by size, type, system, level or category (duct surface area too), and counts of fittings, "
                           "accessories, terminals, equipment and fixtures by type.",
                           {**LINK, "category?": "default all runs", "group_by?": "size (default) | type | system | level | category",
                            "fittings?": "default true: count fittings and equipment", **SCOPE}),
    },
    "MEP and HVAC checks": {
        "mep_flow": ("Revit's computed flow (L/s), velocity (m/s), friction (Pa/m) and pressure drop (Pa) per duct or pipe, totals per system; "
                     "with max_velocity / max_friction only the runs over those limits (the engineer's limits) are listed.",
                     {"kind?": "duct (default) | pipe", "system?": "system name contains", "max_velocity?": "m/s", "max_friction?": "Pa/m",
                      **LINK, "limit?": "default 200"}),
        "mep_connectivity": ("Open duct / pipe ends (connected to nothing) and terminals, fittings and equipment whose connectors are in no system, "
                             "by category with locations.", {"category?": "one category; default all duct and pipe categories", **LINK, "limit?": "default 100"}),
        "air_terminals": ("Air terminals with flow (L/s), system classification and the MEP space they are in; per space the supply / return / "
                          "exhaust totals against the space's specified supply airflow.", {"level?": LEVEL, **LINK, "limit?": "default 200"}),
        "mep_connectors": ("Every connector of the elements given (or of a category): domain, shape, size, classification, flow "
                           "direction, flow, system, what it is connected to, where it is and which way it points. Use it to pick "
                           "the connector for mep_route 'from' or mep_connect 'classification'.",
                           {"ids?": IDS, "category?": CATEGORY, "open_only?": "default false", "limit?": "default 100 elements", **LINK}),
        "mep_insulation": ("Insulation and lining of ducts, pipes, fittings and accessories: type and thickness per element, totals by type "
                           "and thickness (with run length), and the elements with neither, for QA against the engineer's specification.",
                           {"ids?": IDS, "category?": CATEGORY + "; default ducts, pipes, their fittings, accessories and flex",
                            "system?": "system name contains", "missing_only?": "list only elements without insulation; default false",
                            "limit?": "default 200", **LINK}),
        "electrical_circuits": ("Panels (name, distribution system, mains, connected and demand load and current, circuits) and circuits "
                                "(number, panel, kind, load name, poles, voltage, apparent / true load, current, rating, wire, length, loads) as "
                                "Revit computes and shows them, and the electrical loads on no circuit.",
                                {"panel?": "panel name or id", "limit?": "default 200", **LINK}),
    },
    "Documents and batch reads": {
        "list_documents": ("Models open in this Revit: title, documentId, active, path, workshared, cloud, unsaved changes, and whether Claude opened "
                           "it. Pass a model's documentId as document_id to any read to read that model without activating it.", {}),
        "model_files": ("Revit files in a folder (or given paths) WITHOUT opening them: Revit version saved in, workshared, central / local, "
                        "central path, saved by, size, date. Backups (.0001.rvt) are skipped.",
                        {"folder?": "full folder path", "paths?": "full .rvt paths", "pattern?": "default *.rvt", "recursive?": "default false",
                         "limit?": "default 200"}),
        "model_open": ("Open a model in the background by default. New opens of workshared files detach from central and preserve worksets "
                       "by default. An already open model keeps its current worksharing state. Check alreadyOpen, detached and active. "
                       "Read it with document_id or request activate: true.",
                       {"path": "full .rvt path (cloud models: open them in Revit)", "activate?": "default false",
                        "detach?": "preserve (default: keep worksets) | discard", "worksets?": "all (default) | none (open closed)",
                        "audit?": "default false"}),
        "model_close": ("Close a model that Claude opened (model_open, model_new, batch_read), without saving. Not the active model "
                        "(Revit does not allow it) and never a model the user opened. A model with unsaved changes is closed only with "
                        "discard_changes: true and edit permission (the changes are lost).",
                        {"document_id": "from list_documents", "discard_changes?": "default false; true closes a modified model and drops its changes (needs edit permission)"}),
        "model_new": ("Create a new project from a template. activate: true (default) saves it once as a scratch file under "
                      "%TEMP%\\RevitMcpScratch so Revit can make it active (edits need the active model); it is never saved again.",
                      {"template?": "full .rte path; default Revit's default project template", "activate?": "default true"}),
        "batch_read": ("Run one read action over several model files: each is opened in the background (workshared files detached), read, "
                       "and closed without saving. Per file: ok, seconds, the action's result or the error. Stops at max_seconds; continue "
                       "with resume_after.",
                       {"action?": "a read action, e.g. count_by_category, takeoff, model_health, list_warnings, check_names",
                        "params?": "that action's parameters",
                        "actions?": "instead of action: list of {action, params} run on each file while it is open (at most 20)", "folder?": "full folder path", "paths?": "full .rvt paths",
                        "pattern?": "default *.rvt", "recursive?": "default false", "max_files?": "default 20, at most 200",
                        "max_seconds?": "default 600", "resume_after?": "resumeAfter from the previous call"}),
    },
    "Worksharing": {
        "worksharing_status": ("Workshared model: central path (server or Autodesk Docs), user, elements and worksets you hold; for given ids their "
                               "checkout status, owner, creator, last changed by, workset and whether they are current with central.",
                               {"ids?": IDS}),
    },
    "Spaces, analysis and connections": {
        "list_spaces": ("Rooms, areas or MEP spaces: number, name, level, area, perimeter, volume; unplaced and not-enclosed ones; totals by level.",
                        {**LINK, "kind?": "rooms (default) | areas | spaces", "level?": "", "limit?": "default 500"}),
        "analytical_model": ("Analytical members and panels: role, section, material, end points, releases, associated physical element; "
                             "boundary conditions by type.", {**LINK, "limit?": "default 500"}),
        "list_loads": ("Load cases, load combinations (factors) and point / line / area loads with their values (kN, kN/m, kN/m2), as entered.",
                       {**LINK, "limit?": "default 500"}),
        "steel_connections": ("Structural (steel) connections: type, connected members, approval, code checking status.",
                              {**LINK, "limit?": "default 500"}),
    },
    "Model QA and session": {
        "performance_adviser": ("Run Revit's Performance Adviser rules (Manage > Performance Adviser) on the model: per rule its description and "
                                "findings with the elements involved.", {"rule_contains?": "only rules whose name contains this", "run?": "default true; false lists the rules",
                                                                         "limit?": "elements per rule, default 50", "max_seconds?": "default 60"}),
        "pick": ("Ask the user to pick in Revit while you wait: one element, several (they press Finish) or a point (in a view with a work plane). "
                 "Esc cancels (picked: false). Active model only; nothing is changed.",
                 {"kind?": "element (default) | elements | point", "prompt?": "text shown in Revit's status bar", "category?": "only these categories can be picked"}),
        "watch_start": ("Start listing what changes in this model (added, modified, deleted elements, by anyone) until watch_stop.",
                        {"category?": "only these categories; default all"}),
        "watch_changes": ("What changed since watch_start (or since the last read): ids, kind of change, category, counts.",
                          {"clear?": "default true: start a new list after reading", "limit?": "default 500"}),
        "watch_stop": ("Stop watching this model.", {}),
        "preview_show": ("Draw temporary lines and boxes in this model's plan, section and 3D views (DirectContext3D) to show where something would go "
                         "before an edit. Not part of the model; gone with preview_clear or when Revit closes.",
                         {"lines?": "list of [" + XYZ + ", " + XYZ + "]", "boxes?": "list of [min " + XYZ + ", max " + XYZ + "]", "color?": "default #FF6600"}),
        "preview_clear": ("Remove previews.", {"preview?": "one preview id; default all"}),
    },
    "Meta": {
        "family_inspect": ("Look inside a loaded family (opened in memory, never saved): its parameters (instance / type, formula), solids with "
                           "their material and the parameter it is linked to, nested families.", {"family": "family name"}),
        "describe_api": ("Developer aid: public members of a Revit API type as loaded in this Revit.",
                         {"type_name": "e.g. Autodesk.Revit.DB.Wall, or a short name", "contains?": "member name filter", "limit?": "default 300"}),
        "get_edit_permission": ("Whether editing is currently allowed in this Revit session, and how to allow it.", {}),
        "get_operation_status": ("Look up an edit receipt without waiting for the Revit UI. Inspect result for the transaction outcome. "
                                 "Without operation_id: the writes this MCP server dispatched, newest first, with their outcome. "
                                 "Use that after an edit call timed out or was cancelled, before sending the edit again.",
                                 {"operation_id?": "the edit operation ID; omit to list recent writes",
                                  "pid?": "Revit process ID from the receipt or transport error"}),
        "list_actions": ("What the installed add-in supports (names only). Use 'help' for descriptions.", {}),
    },
}

# computed in the MCP server; they never reach the add-in under these names
LOCAL_ACTIONS: Group = {
    "help": ("This catalogue with every parameter. Pass topic to narrow it.",
             {"topic?": "an action or edit operation name, a list of names, or a group title such as 'Quantities' or 'edit'"}),
    "qs_report": ("Write a QS workbook (.xlsx) on this computer: summary, concrete by grade and level, elements by type, steel tonnage, "
                  "rebar, formwork, QA (material problems, unjoined overlaps), notes. Reads only; writes the file.",
                  {"path": "full path of the .xlsx to write", "overwrite?": "default false", "level?": "only this level",
                   "category?": CATEGORIES, "check_overlaps?": "default true (time-limited auto-join check)",
                   "family_contains?": STEEL_WORDS, "density?": "steel kg/m3, default 7850",
                   "view?": VIEW, "document_id?": "documentId to require", "concrete_materials?": "exact additional names to classify as concrete"}),
    "mep_report": ("Write an MEP quantities workbook (.xlsx) on this computer: run lengths (ducts, pipes, trays, conduits, flex) by system, "
                   "size, type and level with duct surface area, fittings / accessories / terminals / equipment / fixtures by type, insulation "
                   "totals and the elements without, panels and circuits, notes; totals are Excel formulas. Reads only; writes the file.",
                   {"path": "full path of the .xlsx to write", "overwrite?": "default false", "level?": "only this level", "view?": VIEW,
                    "category?": "one MEP category or a list; default all runs", "link?": "read a loaded link instead (from list_links)", "insulation?": "default true",
                    "electrical?": "default true", "document_id?": "documentId to require"}),
    "bbs_report": ("Write a bar bending schedule workbook (.xlsx) on this computer from the model's reinforcement: BBS sheet (member, "
                   "mark, type, diameter, shape, number, length, A..J, total length, weight), summary by diameter and member. "
                   "A complete selection of up to 50,000 sets is required. Narrow larger selections by hosts, level or view.",
                   {"path": "full path of the .xlsx to write", "overwrite?": "default false", "hosts?": "only bars in these host ids",
                    "level?": "only bars whose host is on this level", "view?": "only bars visible in this view"}),
    "params_to_excel": ("Write element parameters to an .xlsx (one row per element, id first) to edit in Excel and send back with params_from_excel.",
                        {"path": "full path of the .xlsx", "params": "list of parameter names", "category?": CATEGORY, "ids?": IDS,
                         "type?": TYPE, "level?": LEVEL, "view?": "only elements visible in this view", "limit?": "default 5000",
                         "overwrite?": "default false", "after_id?": "nextAfterId from previous export", "document_id?": "documentId to require"}),
    "params_from_excel": ("CHANGES THE MODEL: write back the cells that differ from the model in a params_to_excel workbook "
                          "(one Undo step per 500 elements).", {"path": "the edited .xlsx", "dry_run?": "default false",
                          "document_id?": "required for legacy workbooks", "operation_id?": "stable prefix for edit batch receipts"}),
    "excel_template": ("Write an empty input workbook for excel_to_model (sheets Levels, Grids, Columns, Beams, Walls, Slabs, Footings).",
                       {"path": "full path of the .xlsx", "overwrite?": "default false"}),
    "excel_to_model": ("CHANGES THE MODEL: create levels, grids, columns, beams, walls, slabs and footings from an excel_template "
                       "workbook, in ONE edit batch.", {"path": "the filled .xlsx", "dry_run?": "default false",
                       "document_id?": "documentId from ping: refuse if another model is active",
                       "operation_id?": "request ID (generated when omitted)"}),
    "list_revit_sessions": ("Every running Revit with the add-in: pid, version, active model, whether it answers, which one is selected and which is the default. With several open, edits need select_revit first.", {}),
    "select_revit": ("Send all following calls to one Revit (e.g. 2027 while 2024 is also open).",
                     {"pid?": "process id from list_revit_sessions, or 'auto' (the only open Revit; with several, the default one for reads, and edits are refused)", "revit?": "version, e.g. '2027', when only one of it runs"}),
}

# --------------------------------------------------------------------------- edit operations (inside action 'edit')

EDIT_ACTION = (
    "Change the model or write files. The server captures the active document before editing. Pass document_id from an earlier ping to require that model. "
    "operation_id (generated when omitted) lets a retry after a lost reply return the first result instead of editing twice "
    "(reuse it only with the identical request), and get_operation_status reports it. "
    "If an edit call times out or is cancelled, the edit may still have been applied: call get_operation_status "
    "(no operation_id lists recent writes) before sending it again. "
    "If an edit call times out or is cancelled, the edit may still have been applied: call get_operation_status "
    "(no operation_id lists recent writes) before sending it again. "
    "Model edits run in one transaction. Dry runs list first-pass purge candidates and roll back simulated model changes. Saving and exporting run after commit "
    "and report their own outcomes in postCommit. "
    "Editing needs a ONE-TIME permission per Revit session: the first edit returns at once with permission 'not granted' "
    "and opens a Yes/No dialog in Revit (or the user presses Add-Ins > Claude MCP > Allow Editing). Ask the user to click "
    "Yes, confirm with get_edit_permission, then send the edit again as a new request."
)

OFFSETS = {"base_offset?": "from the base level", "top_offset?": "from the top level"}
PLACED = {"type": TYPE, "level": LEVEL, "points": "list of " + XY, "rotation?": "degrees, counter-clockwise"}
SEGMENT = "{start: " + XYZ + ", end: " + XYZ + ", mid?: " + XYZ + " (a point on the arc: makes it curved)}"
DISPLACEMENT = {"dx?": "", "dy?": "", "dz?": ""}
EXPORT = {"folder": FOLDER, "name?": "file name without extension; default: the model name", "overwrite?": OVERWRITE}
SHEETS = "sheets by number or name with * wildcards, e.g. 'S-1*', '*', or a list; overrides views"
NAME_TEMPLATE = ("file name per view: {number} {name} {revision} {project_number} {model} {date}; "
                 "default '{number} - {name}' for sheets, '{name}' for views")
PLACEMENT = "origin (default: internal origin to internal origin) | centered | shared (shared coordinates)"

EDIT_GROUPS: dict[str, Group] = {
    "Levels and grids": {
        "level_create": ("Create a level, optionally with its plan view.",
                         {"elevation": "", "name?": "", "plan?": "none (default) | structural | floor | ceiling",
                          "if_exists?": "'skip' to accept an existing level of that name"}),
        "grid_create": ("Create one grid line: straight, or an arc through start, mid and end.", {"start": XY, "end": XY, "mid?": XY + " point on the arc", "name?": ""}),
        "grid_layout": ("Create an orthogonal grid: one line at every x position (named 1, 2, 3 ...) and one at every y position (A, B, C ...).",
                        {"x": "list of x positions", "y": "list of y positions", "x_names?": "one name per x", "y_names?": "one name per y",
                         "overhang?": "how far the lines run past the outer grids; default 2000 mm"}),
        "grid_extents": ("Stretch grids vertically so they show in the plans of every level (new grids already do); use it when a plan shows no grids.",
                         {"grids?": "grid names; default all grids", "bottom?": "bottom z; default 1 m below the lowest level",
                          "top?": "top z; default 1 m above the highest level"}),
    },
    "Structure": {
        "column_create": ("Place columns at points, from a base level up to a top level or by height.",
                          {**PLACED, "top_level?": "top level name (or give height)", "height?": "column height when no top_level",
                           **OFFSETS, "structural?": "default true; false = architectural column"}),
        "beam_create": ("Create beams (structural framing): straight, curved (mid) or sloped (a z per point). z defaults to the level elevation.",
                        {"type": TYPE, "level": "reference level", "beams": "list of " + SEGMENT, "z_offset?": "added to the level elevation when a point has no z",
                         "z_justification?": "top (default: top of beam at the level) | center | origin | bottom | family",
                         "y_justification?": "center (default: centred on the line) | left | origin | right | family"}),
        "wall_create": ("Create walls on a level (straight, or curved with mid), up to a top level or by height.",
                        {"type": "wall type name", "level": "base level", "walls": "list of {start: [x,y], end: [x,y], mid?: [x,y]}",
                         "top_level?": "(or give height)", "height?": "unconnected height", **OFFSETS,
                         "structural?": "default true", "flip?": "default false"}),
        "floor_create": ("Create a floor, structural slab or foundation slab from a closed boundary (the type decides which).",
                         {"type": "floor / foundation-slab type name", "level": LEVEL, "boundary": "list of " + XY + " corners, in order",
                          "holes?": "list of boundaries to cut out", "offset?": "height offset from the level", "structural?": "default true",
                          "slope_arrow?": "{start: " + XY + ", end: " + XY + "}: from the low side towards the high side",
                          "slope?": "with slope_arrow: percent (2 = 2 %)"}),
        "footing_create": ("Place isolated footings at points.", {**PLACED, "offset?": "height offset from the level"}),
        "wall_foundation_create": ("Add a strip (wall) foundation under walls.", {"type": "wall-foundation type name", "wall_ids": "list of wall ids"}),
        "family_place": ("Place any level-based loadable family (generic model, equipment, furniture ...) at points.",
                         {"category": CATEGORY, **PLACED, "offset?": "height above the level"}),
    },
    "Modify": {
        "set_parameters": ("Set parameter values on elements (or on their types). Lengths in request units, areas m2, volumes m3, "
                           "angles degrees, yes/no as true/false; text such as '300 mm' is parsed by Revit; levels, materials, phases and types by name or id.",
                           {"ids?": IDS, "unique_ids?": "element UniqueIds instead of ids",
                            "values": "{parameter selector: value, ...}; prefer bip:/guid:/id: keys from get_element",
                            "target?": "instance (default) | type", "expected?": "compare current values for these keys before writing"}),
        "change_type": ("Give elements another type.", {"ids": IDS, "type": TYPE, "category?": "default: the category of the first element"}),
        "move": ("Displace elements.", {"ids": IDS, **DISPLACEMENT}),
        "copy": ("Copy elements with a displacement; count > 1 makes a linear array. Returns the new ids.",
                 {"ids": IDS, **DISPLACEMENT, "count?": "default 1"}),
        "rotate": ("Rotate elements about a vertical axis through a point.", {"ids": IDS, "center": XY, "angle": "degrees, counter-clockwise"}),
        "mirror": ("Mirror elements about a vertical plane through two points.",
                   {"ids": IDS, "axis_start": XY, "axis_end": XY, "copy?": "default true (keep the originals)"}),
        "delete": ("Delete elements (and whatever depends on them).", {"ids": IDS}),
        "pin": ("Pin or unpin elements.", {"ids": IDS, "pinned?": "default true"}),
        "rename": ("Rename a level, grid, view, sheet, type or other named element.", {"id": "element id", "name": ""}),
        "join": ("Join geometry of element pairs (e.g. slab with column).", {"pairs": "list of [id, id]"}),
        "unjoin": ("Unjoin geometry of element pairs.", {"pairs": "list of [id, id]"}),
        "switch_join_order": ("Swap which element of a joined pair cuts the other (changes which one keeps the shared volume).",
                              {"pairs": "list of [id, id]"}),
        "types_create": ("Create many types from one base type, e.g. a whole column or beam schedule, setting their parameters.",
                         {"base_type": TYPE, "types": "list of {name, values?: {\"Parameter name\": value}}", "category?": CATEGORY,
                          "if_exists?": "fail (default) | skip | update (sets the values on the existing type)"}),
        "join_overlaps": ("Join measured unjoined intersections in scope using the specified cut priority. Coincidence, "
                          "containment, and unmeasured pairs are left for review. See resolvedByRevit for detach resolutions. When Revit refuses a join at commit "
                          "(can't cut joined element), the batch is run again without those elements and they are listed in skippedCutConflict.",
                          {"priority?": "category order, default foundations, columns, walls, beams, floors", "category?": "default: the same five",
                           "level?": "only elements on this level", "view?": "only elements visible in this view",
                           "include_steel?": "default false", "family_contains?": STEEL_WORDS,
                           "max_pairs?": "per batch, default 1000", "max_seconds?": "time budget, default 40",
                           "resume_after?": "resumeAfter cursor from the previous call"}),
        "type_duplicate": ("Duplicate a type under a new name and optionally set its parameters (e.g. a 300x700 beam from a 300x600 one).",
                           {"type": TYPE, "new_name": "", "category?": CATEGORY, "values?": "{\"Parameter name\": value, ...}"}),
        "family_load": ("Load a family file (.rfa) into the model; returns its types.", {"path": "full path of the .rfa"}),
        "flip": ("Flip family instances (facing, hand or work plane) or walls (orientation), as the family allows; others are listed as skipped.",
                 {"ids": IDS, "what": "facing | hand | work_plane | wall"}),
        "end_join": ("Allow or disallow joins at the start / end of walls and structural framing; framing can also get an end extension "
                     "(the length you give, beyond that end).",
                     {"ids": IDS, "end": "start | end | both", "allow?": "true / false", "extension?": "framing: length beyond the end (request units)"}),
        "family_place_hosted": ("Place face-based or work-plane-based families (other kinds are refused: use family_place): on a face of a host element (the face nearest each point, "
                                "or the top / bottom / side faces) or on a level / named reference plane.",
                                {"type": TYPE, "category": CATEGORY, "points": "list of " + XYZ + " (projected onto the face / plane)",
                                 "host?": "element id whose face carries them", "face?": "nearest (default) | top | bottom | side",
                                 "work_plane?": "level name or named reference plane (instead of host)",
                                 "direction?": XYZ + " the family's reference direction; default x"}),
    },
    "Groups and assemblies": {
        "group_create": ("Make a model or detail group from elements.", {"ids": IDS, "name?": "group type name; default Revit's"}),
        "group_place": ("Place instances of a model group type at points.", {"group": "group type name or id (list_groups)", "points": "list of " + XYZ}),
        "ungroup": ("Ungroup group instances (the members stay; the group type stays until purged).", {"ids": "group instance ids"}),
        "assembly_create": ("Make an assembly from elements. The name is applied right after the commit (Revit creates the assembly type then), "
                            "so a dry run does not show it; make its views with assembly_views in a later edit.",
                            {"ids": IDS, "name?": "assembly type name; default Revit's", "naming_category?": "default the first element's category"}),
        "assembly_views": ("Create assembly views of an existing assembly: 3D, elevations / detail sections, parts list, material take-off, "
                           "and optionally an assembly sheet. The assembly must exist before the batch (create it in an earlier edit).",
                           {"assembly": "assembly name or instance id (list_assemblies)",
                            "views": "list of 3d | top | bottom | left | right | front | back | DetailSectionA | DetailSectionB | "
                                     "HorizontalDetail | parts_list | material_takeoff",
                            "sheet_title_block?": "title-block type: also create an assembly sheet"}),
    },
    "Views, sheets and annotation": {
        "view_plan_create": ("Create a plan view of a level.", {"level": LEVEL, "kind?": "structural (default) | floor | ceiling", "name?": ""}),
        "view_3d_create": ("Create an isometric 3D view.", {"name?": ""}),
        "section_create": ("Create a section view along a line, along a straight grid, or through an element. It looks to the left of start -> end.",
                           {"start?": XY, "end?": XY, "grid?": "grid name (instead of start/end)", "element?": "element id (instead of start/end)",
                            "name?": "", "flip?": "look the other way", "depth?": "far clip depth behind the cut line; default 1000 mm",
                            "bottom?": "z; default 1 m below the lowest level", "top?": "z; default 1 m above the highest level",
                            "extend?": "extra width beyond each end; default 500 mm", "scale?": "e.g. 50"}),
        "elevation_create": ("Create elevation views from one marker placed in a plan view.",
                             {"point": XY + " position of the marker", "plan?": "plan view name; default: the active view",
                              "directions?": "list of north | east | south | west; default all four", "name?": "prefix; views are named '<prefix> - North' ...",
                              "scale?": "default 100"}),
        "tag_elements": ("Tag elements in a plan, section or elevation with their category's loaded tag; already tagged ones are skipped.",
                         {"category?": "one or a list (or give ids)", "ids?": IDS, "view?": VIEW, "tag_type?": "a specific tag type",
                          "leader?": "default false", "orientation?": "horizontal (default) | vertical", "skip_tagged?": "default true"}),
        "dimension_grids": ("Chain dimensions across each set of parallel straight grids in a plan view.",
                            {"view?": VIEW, "grids?": "only these grid names", "offset?": "distance in from the grid start ends, default 1500 mm"}),
        "view_activate": ("Make a view the active one (after the batch).", {"view": "view name or id"}),
        "sheet_create": ("Create a sheet.", {"number?": "", "name?": "", "title_block?": "title-block type; default: the first one; 'none' for an empty sheet"}),
        "viewport_place": ("Place a view, legend or schedule on a sheet.", {"sheet": "sheet number, name or id", "view": "view name or id",
                                                        "position?": XY + " on the sheet; default: the centre"}),
        "schedule_create": ("Create a schedule of a category with the fields you name.",
                            {"category": CATEGORY, "fields": "list of field names as in Revit's schedule dialog (e.g. Type, Level, Volume, Count)", "name?": ""}),
        "text_note_create": ("Place a text note in a view.", {"position": XY, "text": "", "view?": VIEW, "type?": "text type name"}),
        "model_lines_create": ("Draw model lines in 3D.", {"lines": "list of " + SEGMENT}),
    },
    "Drafting and detailing": {
        "drafting_view_create": ("Create a drafting view (for details drawn with lines, regions and detail components).",
                                 {"name?": "", "scale?": "e.g. 10; default 20"}),
        "detail_lines_create": ("Draw detail lines in a view: lines, arcs or polylines, with a line style.",
                                {"lines": "list of {start: " + XY + ", end: " + XY + ", mid?: " + XY + " (makes an arc)} or {points: [" + XY +
                                          ", ...], closed?: bool} (a polyline); each may carry style: a line style name",
                                 "view?": VIEW, "line_style?": "line style for all lines (see list_detailing kind line_styles)"}),
        "region_create": ("Draw a filled region (hatch) or a masking region in a view.",
                          {"boundary?": "list of " + XY + " corners", "boundaries?": "outline first, then holes: list of corner lists",
                           "circle?": "{center: " + XY + ", radius}", "type?": "filled region type (see list_detailing); default the model's default",
                           "masking?": "default false: true makes a masking region", "line_style?": "boundary line style", "view?": VIEW}),
        "detail_component_place": ("Place detail components (detail item families) in a view: at points, repeated along lines at a spacing "
                                   "(like a repeating detail), or stretched along lines for line-based ones.",
                                   {"type": TYPE + " of a detail component", "points?": "list of " + XY + " (point-based)",
                                    "along?": "list of {start: " + XY + ", end: " + XY + ", spacing}: point-based, repeated along each line, turned to it, centred",
                                    "lines?": "list of {start: " + XY + ", end: " + XY + "} (line-based)", "rotation?": "degrees, point-based only",
                                    "view?": VIEW}),
        "detail_group_place": ("Place a loaded detail group at points. Revit places detail groups in the active view only.",
                               {"group": "detail group name (see list_detailing)", "points": "list of " + XY, "view?": VIEW + " (must be the active view)"}),
        "callout_create": ("Create a callout of a plan or section between two corner points.",
                           {"view": "parent view name or id", "min": XYZ + " one corner", "max": XYZ + " the opposite corner", "name?": "",
                            "scale?": "", "type?": "view type name; default the parent's plan type, or a detail type for sections"}),
        "view_duplicate": ("Duplicate a view: plain, with its detailing, or as dependent views (legends too).",
                           {"view": "view name or id", "mode?": "duplicate (default) | detailing | dependent", "count?": "default 1",
                            "names?": "one name per copy"}),
        "view_set": ("Change view settings: name, scale, detail level, discipline, crop region, view range (plans).",
                     {"view": "view name or id", "name?": "", "scale?": "", "detail_level?": "coarse | medium | fine",
                      "discipline?": "architectural | structural | mechanical | electrical | plumbing | coordination",
                      "scope_box?": "scope box name, or 'none'",
                      "crop?": "{min: " + XY + ", max: " + XY + "} in model coordinates; switches the crop on",
                      "crop_active?": "bool", "crop_visible?": "bool",
                      "view_range?": "{top?, cut?, bottom?, depth?}: offsets from each plane's level"}),
        "legend_create": ("Create an empty legend view. Revit can only do this by duplicating an existing legend (copy one in with views_copy_from "
                          "when the model has none).", {"name": "", "scale?": "", "from?": "legend to duplicate; default the first one"}),
        "repeating_detail_place": ("Place repeating details along lines. The API has no create method, so an existing placed repeating detail is "
                                   "copied (from this model, or another open model) and reshaped; detail_component_place with 'along' needs no sample.",
                                   {"lines": "list of {start: " + XY + ", end: " + XY + "}", "type?": "repeating detail type of the sample to copy",
                                    "model?": "another open model that holds the sample", "view?": VIEW}),
        "views_copy_from": ("Copy drafting views and legends (with their content) from another model open in this Revit, e.g. a standard "
                            "detail library. Same-name types keep this model's definition.",
                            {"model": "title of the other open model", "views?": "view names to copy", "name_contains?": "or: copy views whose name contains this",
                             "kind?": "all (default) | drafting | legend", "skip_existing?": "default true: skip views whose name exists here"}),
    },
    "Documentation": {
        "filter_create": ("Create a rule-based view filter (or update one).",
                          {"name": "", "categories": CATEGORY_LIST, "rules?": "list of {parameter, op?: equals | not_equals | contains | not_contains | "
                           "begins_with | ends_with | greater | greater_or_equal | less | less_or_equal | has_value | has_no_value, value}; "
                           "numbers in request units / m2 / m3", "match?": "all (default) | any", "if_exists?": "fail (default) | skip | update"}),
        "filter_apply": ("Add a filter to views or view templates with graphic overrides, or hide what it selects.",
                         {"filter": "filter name", "views?": "view or view template names (default: 'view' or the active view)", "view?": VIEW,
                          "visible?": "default true; false hides what the filter selects", "remove?": "true removes the filter from the views", **OVERRIDES}),
        "color_by_parameter": ("Colour elements in a view by the values of a parameter: one filter per value with a solid colour fill; returns "
                               "the colour legend.",
                               {"categories": CATEGORY_LIST, "parameter": "parameter name, e.g. Type Mark, Structural Material, Comments",
                                "colors?": "{value: colour}: fixed colours for some values", "prefix?": "filter name prefix, default 'Claude - '",
                                "view?": VIEW}),
        "graphics_override": ("Override graphics for elements or categories in a view (colour, line weight, halftone, transparency, fill), "
                              "hide / unhide them, or reset the overrides.",
                              {"ids?": IDS, "categories?": CATEGORY_LIST, "hide?": "true hides, false unhides", "reset?": "true clears overrides",
                               "view?": VIEW, **OVERRIDES}),
        "view_template_create": ("Create a view template from a view's settings.", {"view": "view name or id", "name": "template name"}),
        "view_template_apply": ("Apply a view template to views ('none' removes it).",
                                {"views": "view names", "template": "view template name, or 'none'"}),
        "dimension_elements": ("Dimension elements in a plan: a chain through the outer faces of each element (and grids) along x, y or both, "
                               "placed beyond the elements.",
                               {"ids?": IDS, "categories?": CATEGORY_LIST + " (elements shown in the view)", "direction?": "x | y | both (default)",
                                "offset?": "distance of the dimension line beyond the elements; default 1500 mm",
                                "include_grids?": "default true", "view?": VIEW, "type?": "linear dimension type name (an Ordinate type gives ordinate dimensions); default Revit's"}),
        "spot_elevation": ("Spot elevations on the top face of elements (slabs, beams, footings ...).",
                           {"ids": IDS, "point?": XY + " where to place it (default the face centre)", "leader?": "default true", "view?": VIEW}),
        "revision_create": ("Create a revision, optionally added to sheets.",
                            {"description": "", "date?": "", "issued_by?": "", "issued_to?": "", "issued?": "bool", "sheets?": "sheet numbers"}),
        "revision_cloud_create": ("Draw a revision cloud in a view.",
                                  {"revision": "revision description, number or sequence", "boundary": "list of " + XY + " corners", "view?": VIEW}),
        "schedule_set": ("Change a schedule: filters, sorting / grouping (headers, footers, blank lines), itemize, grand total, title / headers, "
                         "and per field the heading, hidden, alignment, totals and number format. Filters and sort replace the existing ones.",
                         {"schedule": "schedule name or id",
                          "filters?": "list (max 8) of {field, op?: equals (default) | not_equals | greater | greater_or_equal | less | less_or_equal | "
                                      "contains | not_contains | begins_with | not_begins_with | ends_with | not_ends_with | has_value | has_no_value, "
                                      "value: number (lengths in request units, m2, m3, degrees) or text}; [] clears",
                          "sort?": "list of {field, order?: ascending | descending, header?, footer?, footer_count? (needs footer), blank_line?}; [] clears",
                          "itemize?": "bool", "grand_total?": "bool", "show_title?": "bool", "show_headers?": "bool",
                          "fields?": "{field name: {heading?, hidden?, align?: left | center | right, total?: bool, unit?: mm | cm | m | m2 | m3 | "
                                     "kg | deg ..., decimals?, style?: {bold?, italic?, underline?, color?: '#RRGGBB', background?: '#RRGGBB', "
                                     "font?, size?: points}}}",
                          "percentage?": "list of {of: field, by?: a sort / group field, heading?}: calculated percentage fields",
                          "split?": "number of parts (2 to 20), or list of part heights on the sheet; replaces any split",
                          "unsplit?": "true: join a split schedule back into one",
                          "calculated?": "not possible: Revit's API cannot set a calculated field's formula (refused with the reason)"}),
        "schedule_key_create": ("Create a key schedule of a category with the fields and keys (rows) given: each key's name and values. "
                                "Elements then take a key with schedule_key_assign.",
                                {"category": CATEGORY, "name?": "", "key_parameter?": "name of the key parameter elements get (default Revit's)",
                                 "fields?": "fields of the key schedule (e.g. Comments, a shared parameter)",
                                 "keys?": "list of {key: name, values?: {field: value}}: lengths in request units, areas m2, volumes m3"}),
        "schedule_key_assign": ("Give elements a key of a key schedule (their key parameter), or clear it with key: null.",
                                {"schedule": "key schedule name or id", "key": "key name, or null to clear", "ids": IDS}),
        "dimension_rotated": ("A linear dimension chain along any plan direction: through the outer faces of each element square to it, "
                              "and the grids across it, placed beyond the elements.",
                              {"ids": IDS, "angle": "direction measured along, degrees from x", "view?": VIEW,
                               "offset?": "distance of the dimension line beyond the elements; default 1500 mm", "include_grids?": "default true",
                               "type?": "linear dimension type name (an Ordinate type gives ordinate dimensions); default Revit's"}),
        "dimension_angular": ("An angular dimension between two straight grids, reference planes or model / detail lines, centred on their intersection.",
                              {"between": "two grid names, reference plane names or line ids", "view?": VIEW + " (plan or drafting)",
                               "radius?": "arc radius; default 1000 mm", "type?": "angular dimension type; default the first one"}),
        "dimension_radial": ("Radius or diameter dimensions on arc grids, arc model / detail lines, or the curved face of walls / beams. Revit 2027 "
                             "only (the 2024 API cannot create them).",
                             {"ids": IDS, "diameter?": "default false (radius)", "view?": VIEW}),
        "spot_coordinate": ("Spot coordinates (N / E) on the top face of elements; the values follow the spot type's base (project or survey point). "
                            "Spot slopes cannot be created through the Revit API.",
                            {"ids": IDS, "point?": XY + " where to place it (default the face centre)", "leader?": "default true", "view?": VIEW}),
        "sheet_layout": ("Place several views, legends and schedules on one sheet, arranged in rows inside the sheet area.",
                         {"sheet": "sheet number or name", "views": "view, legend or schedule names, in reading order",
                          "columns?": "items per row; default about the square root of the count",
                          "area?": "{min: [x, y], max: [x, y]} paper mm from the sheet's lower-left corner (leave out the title strip)",
                          "margin?": "paper mm, default 20", "gap?": "paper mm between items, default 15"}),
    },
    "Reinforcement": {
        "rebar_column": ("Reinforce structural columns with the engineer's values: longitudinal bars on all faces (corners included) and closed "
                         "ties. No design and no defaults: counts, spacings, cover, lap and bend lengths are required; the operation only fits "
                         "them to the column's geometry and reports what it had to shorten.",
                         {"ids": IDS, "bar": "bar type name or diameter in mm (e.g. 20)", "tie": "tie bar type or diameter",
                          "bars_x": "bars on each face along the column's local x, corners included",
                          "bars_y": "bars on each face along local y, corners included",
                          "tie_spacing": "tie spacing", "cover": "to the tie's outer face",
                          "top": "lap (bars continue above) | bend (bent inward) | none (stop at cover) | auto (lap where a column is above, else bend)",
                          "bottom": "footing (stand on the footing mesh with feet) | none | auto (footing where a foundation is below)",
                          "lap_d": "lap length in bar diameters; required for top lap / auto",
                          "bend_leg": "length of the inward top bend; required for top bend / auto",
                          "footing_cover": "footing bottom cover; required for bottom footing / auto",
                          "foot_length": "length of the outward feet on the footing mesh; required for bottom footing / auto",
                          "end_zone?": "length of closer tie spacing at each end; leave out for uniform ties",
                          "end_spacing?": "tie spacing in the end zones; required with end_zone",
                          "starter_tie_spacing?": "ties inside the footing at this spacing; leave out for none"}),
        "rebar_beam": ("Reinforce straight, level beams with the engineer's values: bottom and top bars, ends anchored into the supports "
                       "(straight when the anchorage fits, else to the far face with the given 90 degree bend; lapped into the next span in "
                       "line), closed stirrups. No design and no defaults; anchorage that does not fit straight is reported for checking.",
                       {"ids": IDS, "bottom_bar": "bar type or diameter", "bottom_count": "number of bottom bars", "top_bar": "bar type or diameter",
                        "top_count": "number of top bars", "stirrup": "bar type or diameter", "stirrup_spacing": "stirrup spacing",
                        "cover": "to the stirrup's outer face",
                        "ends": "anchor (into the supports) | none (bars stop at cover inside the beam)",
                        "anchorage_d": "anchorage length in bar diameters; required for ends anchor",
                        "bend_leg": "90 degree bend length where the anchorage does not fit straight, and at corners; required for ends anchor",
                        "lap_d": "lap length in bar diameters; required for ends anchor or with stock_length",
                        "end_zone?": "length of closer stirrups at each end; leave out for uniform stirrups",
                        "end_spacing?": "stirrup spacing in the end zones; required with end_zone",
                        "stock_length?": "longest bar before a lap; leave out to model each bar line in one piece"}),
        "rebar_footing": ("Reinforce isolated footings (and pile caps) with the engineer's values: bottom mesh both ways, optional top mesh, "
                          "optional bottom bars turned up at the edges. No design and no defaults.",
                          {"ids": IDS, "bar": "bar type or diameter", "spacing": "bottom mesh spacing", "cover": "cover to the mesh",
                           "top?": "bool; add a top mesh", "top_bar?": "required with top", "top_spacing?": "required with top",
                           "end_bends?": "bool; bottom bars turned up at the edges (U bars)",
                           "bend_leg?": "turned-up leg length; leave out to turn up to under the top mesh or top cover",
                           "stock_length?": "longest bar before a lap; leave out to model each bar in one piece",
                           "lap_d?": "lap length in bar diameters; required with stock_length"}),
        "rebar_area": ("Area reinforcement in slabs with the engineer's values: one bar type at a spacing, both directions, in the given layers.",
                       {"ids": "slab ids", "bar": "bar type or diameter", "spacing": "bar spacing", "layers": "bottom | top | both",
                        "direction?": XY + " major direction; default x", "boundary?": "list of " + XY + " corners; default the slab outline"}),
    },
    "Reinforcement systems": {
        "path_reinforcement": ("Path reinforcement in a wall or slab with the engineer's values: bars perpendicular to a sketched path at a "
                               "spacing, of a length, optional alternating bars and hooks.",
                               {"host": "wall or slab id", "path": "open polyline: list of " + XY + " points (slab: at its top; wall: "
                                "projected on its plane, z default the wall base)", "bar": "bar type or diameter", "spacing": "bar spacing",
                                "length": "primary bar length", "face?": "slab: top | bottom; wall: exterior | interior",
                                "flip?": "bool: bars to the other side of the path", "start_hook?": "hook type or 'none'",
                                "end_hook?": "hook type or 'none'", "additional_offset?": "extra cover offset",
                                "alternating?": "{bar?, length, offset?}: alternating bars", "type?": "path reinforcement type"}),
        "fabric_area": ("Fabric (welded mesh) area over a slab or wall with a given sheet type: the whole host, or a boundary in a slab.",
                        {"host": "slab or wall id", "sheet_type": "fabric sheet type ('default' creates Revit's default type)",
                         "boundary?": "slab only: list of " + XY + " corners", "direction?": XY + " major direction",
                         "origin?": XY + " sheet layout origin (with boundary)", "location?": "top | bottom (slab), exterior | interior (wall)",
                         "cover_offset?": "additional cover", "lap_major?": "lap in the major direction (default: the sheet type's)",
                         "lap_minor?": "lap in the minor direction", "type?": "fabric area type"}),
        "fabric_sheet_place": ("Place one fabric sheet in a slab or wall at a point, rotated in the host plane.",
                               {"host": "slab or wall id", "sheet_type": "fabric sheet type", "point": XY + " sheet origin",
                                "rotation?": "degrees in the host plane", "location?": "top | bottom | exterior | interior",
                                "cover_offset?": "additional cover"}),
        "rebar_hooks": ("Hooks of the model's hook types on existing bar sets, with orientation and rotation. Checks the bar type and "
                        "shape allow the hook.",
                        {"ids": "rebar ids", "start?": "hook type or 'none'", "end?": "hook type or 'none'",
                         "start_orientation?": "left | right", "end_orientation?": "left | right",
                         "start_rotation?": "degrees", "end_rotation?": "degrees"}),
        "coupler_place": ("A coupler of a given type joining two bar ends end to end (or one bar end, as an end treatment).",
                          {"type": "coupler family type", "first": "{rebar: id, end: start | end}",
                           "second?": "{rebar: id, end: start | end}; leave out for a single bar end"}),
        "rebar_annotate": ("Multi-reference annotation (one dimension across the bars plus a tag) for bar sets in a section, plan or "
                           "elevation.",
                           {"ids": "rebar ids", "origin": XYZ + " a point on the dimension line ([x, y] takes z from the first bar)",
                            "view?": VIEW, "direction?": XYZ + " dimension line direction, default the view's right",
                            "tag_head?": XYZ + " tag position, default the origin", "leader?": "default true",
                            "type?": "annotation type (list_reinforcement_types)"}),
    },
    "Reinforcement numbering and splices": {
        "rebar_numbering": ("Renumber a partition of a numbering schema: remove gaps, shift its numbers to start at a number, or change "
                            "one number. Move bars to another partition with set_parameters (bip:NUMBER_PARTITION_PARAM).",
                            {"action": "remove_gaps | shift | change", "schema?": "rebar (default) | fabric | coupler, or the schema name",
                             "partition?": "partition name; required when the schema has several", "first?": "with shift: the new first number",
                             "from?": "with change", "to?": "with change"}),
        "splice_type_create": ("Revit 2027: a rebar splice type with the engineer's lap (and stagger) in bar diameters.",
                               {"name": "", "lap_d": "lap length in bar diameters", "stagger_d?": "stagger length in bar diameters",
                                "shift?": "bar_plane | none"}),
        "rebar_splice": ("Revit 2027: splice bar sets with a splice type, by rules (no bar longer than max_length) or at a line in a view.",
                         {"ids": "rebar ids", "type": "splice type (list_reinforcement_types)", "position?": "end1 | middle (default) | end2",
                          "rules?": "{max_length, min_length?, run_out?: start | end}", "at?": "{start: " + XYZ + ", end: " + XYZ + ", view?}"}),
    },
    "Modelling": {
        "slab_shape": ("Shape a slab by points: move an existing corner (within 'tolerance') or add a point, each by an offset from the slab top; "
                       "'reset' makes it flat again. Use floor_create with slope_arrow for a plane slope.",
                       {"id": "slab id", "points?": "list of {at: " + XY + ", offset: height change (+ up)}",
                        "tolerance?": "default 50 mm", "reset?": "bool"}),
        "beam_system_create": ("Create a beam system (joists at a fixed spacing) inside a boundary on a level.",
                               {"level": LEVEL, "type": TYPE + " of framing", "boundary": "list of " + XY + " corners",
                                "direction?": XY + " beam direction; default x", "spacing": "joist spacing (design input, no default)",
                                "justification?": "center (default) | beginning | end | directionline"}),
        "brace_create": ("Create braces (structural framing set as brace) between 3D points.",
                         {"type": TYPE + " of framing", "level": LEVEL + " (reference level)",
                          "braces": "list of {start: " + XYZ + ", end: " + XYZ + "}: points without z sit on the level"}),
    },
    "Parts, arrays and alignment": {
        "parts_create": ("Make parts of walls, floors, roofs and other layered elements (one part per layer).", {"ids": IDS}),
        "parts_divide": ("Divide parts (or the parts of elements given) along lines on a level (pour breaks, construction joints) and / or by "
                         "grids, levels and reference planes that cross them.",
                         {"ids": "parts, or elements made into parts", "lines?": "list of [" + XY + ", " + XY + "] dividing lines",
                          "level?": "level the lines are drawn on (with lines)", "datums?": "grid, level or reference plane ids"}),
        "parts_merge": ("Merge touching parts into one.", {"ids": "part ids (at least two)"}),
        "array_create": ("Array elements: linear (by a vector between neighbours, or to the last member) or radial (about a centre through an "
                         "angle), as a Revit array group (default) or as plain copies.",
                         {"ids": IDS, "kind": "linear | radial", "count": "number of members incl. the original (2 to 200)",
                          "vector?": "linear: " + XYZ + " offset to the second member (anchor second) or to the last (anchor last)",
                          "center?": "radial: " + XY + " centre", "angle?": "radial: degrees to the second or last member",
                          "anchor?": "second (default) | last", "group?": "default true (Revit array); false: separate copies", "view?": VIEW}),
        "reference_plane_create": ("Create a reference plane through a line (vertical by default).",
                                   {"start": XYZ, "end": XYZ, "cut?": XYZ + " a direction in the plane, default up (vertical plane)",
                                    "name?": "", "view?": VIEW}),
        "align_lock": ("Lock element faces to a grid or reference plane (Revit's Align padlock). The element's face parallel to the datum must lie "
                       "on it, or move: true moves the element onto it first.",
                       {"datum": "grid or reference plane id", "ids": IDS, "move?": "default false", "tolerance?": "default 1 mm", "view?": VIEW + " (plan or section)"}),
    },
    "From CAD": {
        "columns_from_cad": ("Model columns from the outlines on a CAD column layer (as cad_columns lists them): each outline gets the type of its size, "
                             "rotated to match; outlines with no type of their size are reported, not placed.",
                             {"id": CAD_ID, "layer": COLUMN_LAYER,
                          "include?": "concrete (default: plain rectangles and circles) | steel (H / I and other outlines) | all",
                          "types?": "{\"650x1300\": \"Type name\", ...}: type per outline size (width x depth as listed), checked before the automatic match",
                          "tolerance?": "size match tolerance; default 10 mm", "gap?": "curves closer than this belong to one column; default 25 mm",
                          "min_size?": "default 150 mm", "max_size?": "default 4000 mm",
                          "skip_existing?": "default true: skip outlines where a column already stands at the base", "level": LEVEL + " (base)", "top_level?": "top level", "height?": "height if no top_level",
                              "base_offset?": "", "top_offset?": "",
                              "create_types?": "base type (with b / h or Width / Depth type parameters): for every size with no type, duplicate it and set the size",
                              "type_name?": "name of created types with {b} and {h}; default the base name with its size replaced"}),
        "grids_from_cad": ("Model grids from the straight lines on a CAD grid layer: north-south lines numbered 1, 2, 3 ... west to east, "
                           "east-west lines lettered A, B, C ... south to north (CAD text is not readable, so give names when they differ).",
                           {"id": CAD_ID, "layer": "the grid layer, e.g. S-GRID", "min_length?": "default 3000 mm",
                            "x_names?": "names of the north-south lines, west to east", "y_names?": "names of the east-west lines, south to north",
                            "prefix?": "put before automatic names, e.g. 'N-'", "skip_existing?": "default true: skip lines on an existing grid"}),
        "beams_from_cad": ("Model beams from a CAD beam layer: two parallel edge lines a beam width apart (or two concentric arcs) give one beam "
                           "on their centre line, top at the level, typed by width; beams already modelled there are skipped.",
                           {"id": CAD_ID, "layer": "beam edge layer(s), e.g. 'S-BEAM' or 'S-BEAM, S-BEAM-HDLN'", "level": LEVEL,
                            "z_offset?": "top of beam above the level; default 0", "depth?": "prefer types of this depth",
                            "types?": "{\"300\": \"Type name\", ...}: type per width (mm)", "include?": "concrete (default) | steel",
                            "create_types?": "base framing type: missing widths are made from it (same depth)", "type_name?": "with {b} and {h}",
                            "min_width?": "default 150 mm", "max_width?": "default 1500 mm", "min_length?": "default 500 mm",
                            "tolerance?": "width match; default 10 mm", "skip_existing?": SKIP_EXISTING}),
        "walls_from_cad": ("Model walls from a CAD wall layer: two parallel face lines (or concentric arcs) give a wall on their centre line, "
                           "typed by thickness; walls already modelled there are skipped.",
                           {"id": CAD_ID, "layer": "wall layer(s)", "level": LEVEL + " (base)", "top_level?": "", "height?": "if no top_level",
                            "base_offset?": "", "top_offset?": "", "types?": "{\"250\": \"Wall type\", ...}: type per thickness (mm)",
                            "structural?": "default true", "min_width?": "default 100 mm", "max_width?": "default 1500 mm",
                            "min_length?": "default 500 mm", "tolerance?": "default 10 mm", "skip_existing?": SKIP_EXISTING}),
        "slabs_from_cad": ("Model slabs from closed outlines on a CAD layer (polylines, circles, or lines / arcs meeting end to end); outlines inside "
                           "a slab outline become its openings. Outlines where a slab of that level already stands are skipped.",
                           {"id": CAD_ID, "layer": "slab outline layer(s)", "level": LEVEL, "type": "floor / slab type",
                            "mode?": "closed (default): each closed outline is a slab | outer: the outer outline of everything on 'layer' "
                                     "(give the slab edge, beam and column layers together) is one slab per separate area",
                            "opening_layer?": "mode outer: closed outlines on this layer inside the slab become openings (e.g. CE-VOID)",
                            "cell?": "mode outer: grid size; default 25 mm",
                            "preview?": "mode outer: draw the outlines as model lines on the level instead of making slabs (to check them first)",
                            "offset?": "height offset from the level", "min_area?": "m2; default 1",
                            "gap?": "largest gap bridged between edge pieces (dashed edges, edges broken at columns); default 5 mm (closed) / 500 mm (outer)",
                            "structural?": "default true", "skip_existing?": SKIP_EXISTING}),
    },
    "Sheets": {
        "sheet_collection_set": ("Revit 2027: put sheets into a sheet collection (made when missing).",
                                 {"name": "collection name", "sheets?": "sheet numbers or names"}),
        "color_fill_scheme_set": ("A colour fill scheme for a category (rooms, areas, spaces, ducts, pipes ...): use one by name or copy one ('from') "
                                  "under that name; colour by a parameter; set each value's colour and caption; apply it to views, optionally "
                                  "with a legend. Colours are the user's.",
                                  {"category": CATEGORY, "scheme": "scheme name", "from?": "scheme to copy when 'scheme' does not exist ('*': the first one)",
                                   "title?": "", "parameter?": "parameter to colour by (e.g. Department, Name)",
                                   "entries?": "list of {value, color?: '#RRGGBB', caption?, visible?}; new values are added for text parameters",
                                   "views?": "plans or sections to show it in", "legend_at?": XY + " place a colour fill legend there in each view"}),
        "sheets_from_views": ("Make a sheet set: one sheet per view, numbered from 'number' upwards (S-101, S-102 ...; used numbers skipped), "
                              "named from a template, the view centred on the title block. Views already on a sheet are skipped.",
                              {"views?": "view names", "view_type?": "or: plan | FloorPlan | EngineeringPlan | Section | Elevation | ThreeD ...",
                               "name_contains?": "with view_type: only views whose name contains this",
                               "exclude_contains?": "leave out views whose name contains any of these (e.g. ['Copy'])", "number?": "first sheet number; default S-101",
                               "name_template?": "default '{view}'; also {level} and {number}", "title_block?": "default: the one used most",
                               "view_template?": "apply this view template to each view", "scale?": "e.g. 100 (views without a template)",
                               "skip_placed?": "default true"}),
    },
    "Materials": {
        "fix_materials": ("Material fixer: give elements the right material by rule (check_materials lists the problems). Loadable families "
                          "(columns, beams, footings) get Structural Material on the type when the whole type is fixed, else per element; "
                          "slab / wall types get the matching layer material changed, only when every element of the type is fixed.",
                          {"steel_material?": "material for steel members carrying a non-metal material (e.g. 'KTR-S-ST-52/3')",
                           "concrete_material?": "material replacing Revit default materials (Concrete, Cast-in-Place gray, Default ...) on non-steel elements",
                           "rules?": "[{material, problem?: any | steel | default | none, category?, family_contains?, type_contains?, level?, "
                                     "from_contains?: current material name contains}, ...]; checked in order, first match wins, after the two shortcuts",
                           "category?": "default the structural categories", "level?": "only this level",
                           "family_contains?": STEEL_WORDS}),
        "family_material_set": ("Set the material of every solid in the chosen families to one material (no parameter link), e.g. all built-up steel "
                                "families to KTR-S-ST-52/3; the families are loaded back. Use it when the grade is fixed per family. Runs after the commit.",
                                {"families?": "family names", "family_contains?": "or: families whose name contains this (e.g. 'BUILTUP')",
                                 "material": "material name in the model", "category?": "default structural columns, framing and foundations"}),
        "family_material_link": ("Fix families whose solids carry a fixed material: open each family, link its solids' material to Structural "
                                 "Material, load it back (project type values kept), so the element's material is what the geometry shows. "
                                 "Runs after the commit; a dry run lists the families.",
                                 {"families?": "family names", "family_contains?": "or: families whose name contains this (e.g. 'S-STL')",
                                  "category?": "default structural columns, framing and foundations"}),
        "family_parameters": ("Inside loadable families (any category), as given: add parameters, rename them, add types, set formulas, set "
                              "type values and remove parameters, then load the families back with these values. No geometry is changed. "
                              "Runs after the commit; a dry run lists the families.",
                              {"families?": "family names", "family_contains?": "or: families whose name contains this", "category?": "only these categories",
                               "add?": "list of {name, type: length | area | volume | angle | number | integer | text | yes_no | material, "
                                       "instance?: default false (type parameter), group?: dimensions | construction | data | identity_data | "
                                       "materials | structural | text | graphics | other (default), if_exists?: 'skip'}",
                               "rename?": "{old name: new name}",
                               "new_types?": "list of {name, from?: type to copy (default the current type), if_exists?: 'skip'}",
                               "formulas?": "{parameter: formula as typed in Revit, e.g. 'Width * 2'; null clears it}",
                               "values?": "{type name or '*' (all types): {parameter: value}}: lengths in request units, areas m2, volumes m3, "
                                          "angles degrees, yes_no true / false, material by name",
                               "remove?": "parameter names to delete"}),
    },
    "Model management": {
        "parameter_add": ("Add shared parameters to the model and bind them to categories. Takes each one from the shared parameter file "
                          "(the model's, or 'shared_file'), adding it there first when missing; if one is already bound, its categories are extended.",
                          {"name?": "parameter name", "names?": "several names (same settings)", "categories": CATEGORY_LIST,
                           "binding?": "instance (default) | type", "data_type?": "text (default) | multiline | url | number | integer | yes_no | length | area | volume | angle | material",
                           "group_under?": "properties-palette group label, e.g. Identity Data, Constraints, Structural; default Data",
                           "shared_file?": "full path; default the model's shared parameter file, or the bridge's own file if none is set",
                           "file_group?": "group in the file for new definitions; default 'Revit MCP'",
                           "create_if_missing?": "default true"}),
        "parameter_remove": ("Remove project / shared parameters from the model (their values go with them). Use dry_run first.",
                             {"name?": "parameter name", "names?": "several names"}),
        "purge": ("Purge unused families, types, materials, patterns and styles, in several passes (Revit 2024 and later). "
                  "Checks cascaded deletions against the keep lists. Counts all removed IDs. A purge dry run lists first-pass candidate IDs and names without deleting. Cascades and later passes are unknown.",
                  {"categories?": CATEGORY_LIST + "; default everything", "keep_ids?": IDS + " to keep",
                   "keep_names?": "keep anything whose name contains one of these words", "passes?": "default 3", "limit?": "names listed, default 200"}),
        "standards_copy": ("Copy standards from another open model (template or library): view templates, filters, text / dimension types, "
                           "fill / line patterns, materials, wall / floor types, title blocks, line styles (recreated by name with weight, colour and pattern). Names already in this model are kept, not overwritten.",
                           {"model": "title of the other open model", "kind?": "one kind", "kinds?": "view_templates | filters | text_types | dimension_types | fill_patterns | "
                            "line_patterns | materials | wall_types | floor_types | title_blocks | line_styles (list)",
                            "names?": "only these names", "name_contains?": "only names containing this"}),
        "workset_create": ("Create worksets (workshared models only).", {"name?": "workset name", "names?": "several names"}),
        "workset_set": ("Move elements to a workset (workshared models only; elements owned by another user are refused).",
                        {"ids": IDS, "workset": "workset name"}),
        "phase_set": ("Set the phase created and / or demolished of elements.",
                      {"ids": IDS, "created?": "phase name", "demolished?": "phase name, or 'none' to clear"}),
    },
    "Warnings": {
        "fix_warnings": ("Fix Revit warnings that have one safe fix: 'joined but do not intersect' pairs are unjoined (no quantity change; the default). "
                         "When named in kinds: identical instances in the same place (copies deleted, lowest id kept) and overlapping lines "
                         "(a line lying within another is deleted). Every other warning is listed under notFixed, by message, to review.",
                         {"kinds?": "joined_disjoint (default) | duplicate_instances | overlapping_lines (list)", "ids?": "only warnings involving these elements"}),
    },
    "Standards and data": {
        "acc_parameter_add": ("Add a parameter from the Autodesk parameters service (ACC) to the model by its id, bound to categories. Revit must be "
                              "signed in with access to it.",
                              {"id": "parameter id from the service, e.g. parameters.<collection>:<name>-1.0.0", "categories": CATEGORY_LIST,
                               "instance?": "default the service's", "visible?": "", "region?": "hub region (US, EMEA, AUS ...); Revit 2027 default US"}),
        "global_parameter_create": ("Create a global parameter, with the value or formula you give.",
                                    {"name": "", "spec": "length | area | volume | angle | number | integer | text | yes_no | material",
                                     "value?": "lengths in request units, m2, m3, degrees, true / false, material name, text",
                                     "formula?": "a Revit formula instead of a value", "reporting?": "bool",
                                     "if_exists?": "fail (default) | update"}),
        "global_parameter_set": ("Set a global parameter's value, formula or reporting state.",
                                 {"name": "", "value?": "as for global_parameter_create", "formula?": "", "reporting?": "bool"}),
        "global_parameter_associate": ("Drive an element (or type) parameter by a global parameter, or clear the association.",
                                       {"name?": "global parameter (not needed with clear)", "ids": IDS, "parameter": "parameter name on the elements",
                                        "target?": "instance (default) | type", "clear?": "true removes the association"}),
        "material_asset_set": ("Set a material's structural / thermal asset values and appearance colour from the values you give. An asset "
                               "shared with other materials is copied into a new asset for this one unless change_shared is true.",
                               {"material": "material name or id",
                                "structural?": "{density kg/m3, young_modulus MPa, shear_modulus MPa, poisson_ratio, thermal_expansion 1/C, "
                                               "concrete_compression MPa, yield_stress MPa, tensile_strength MPa, lightweight}",
                                "thermal?": "{conductivity W/(m K), specific_heat J/(kg C), density kg/m3, emissivity, porosity, reflectivity}",
                                "appearance?": "{color (render colour, Generic assets), shading_color, transparency 0-100}",
                                "change_shared?": "default false"}),
    },
    "Openings": {
        "opening_create": ("Cut an opening in a slab or roof (rectangle/polygon or circle), a sleeve through a beam web or a hole through its top, or a rectangular opening in a wall.",
                           {"host": "id of the slab / roof / beam / wall", "boundary?": "slabs, beam top: list of " + XY + " corners",
                            "center?": XY + " circle centre; beam web: " + XY + " or " + XYZ + " (z defaults to mid depth), projected on the beam axis",
                            "radius?": "circle radius", "face?": "beams: 'web' (default, horizontal sleeve) | 'top' (vertical hole)",
                            "width?": "beam web rectangle: length along the beam", "height?": "beam web rectangle: height",
                            "start?": "walls: " + XYZ + " lower-left corner (or plan point with bottom/top)", "end?": "walls: " + XYZ + " upper-right corner",
                            "bottom?": "walls: bottom z", "top?": "walls: top z", "perpendicular?": "default true: cut square to the face"}),
        "shaft_create": ("Create a shaft opening through every slab between two levels.",
                         {"bottom_level": LEVEL, "top_level": LEVEL, "boundary?": "list of " + XY + " corners", "center?": XY, "radius?": "",
                          "base_offset?": "", "top_offset?": ""}),
        "crossing_opening": ("Cut an opening in a host wall, slab, roof, ceiling, beam or brace where a duct, pipe, tray or conduit (host or link) "
                             "passes through it (see mep_crossings): the run's size plus the clearance you give on each side, square to the face. "
                             "Walls get rectangular openings; slabs and beams round or rectangular like the run. Refused when the run is more "
                             "than 15 degrees off square, or no longer passes through (an opening already clears it). Structure in a link cannot be cut.",
                             {"structure": "id of the host element to cut", "mep": "id of the run", "mep_link?": "the link holding the run (from list_links)",
                              "clearance": "gap on each side of the run, e.g. 50 (the engineer's value)",
                              "shape?": "auto (default: like the run) | round | rectangular", "at?": XYZ + " picks the crossing nearest this point when the run passes through the element more than once"}),
    },
    "MEP": {
        "mep_route": ("One CONNECTED duct / pipe / tray / conduit run along a path of points, with the elbows the type's routing "
                      "preferences give (straight segments with the given type, system and size); it can start on an equipment, terminal "
                      "or panel connector (from) and end on a main (to_main); a sanitary / drainage run can fall at the engineer's slope.",
                      {"kind": "duct | pipe | cable_tray | conduit", "level": LEVEL, "path": "list of " + XYZ + " (2D points: at the level plus z)",
                       "z?": "centreline height above the level for 2D points", "type?": "type name or id; default the first",
                       "slope?": "percent, the engineer's: the run falls by it along the path from the first point (negative rises); 2D path points only",
                       "system?": "ducts and pipes: system type name", "diameter?": "not with from (the run takes the connector's size)", "width?": "", "height?": "",
                       "from?": "{element: equipment / terminal id, classification?: e.g. SupplyAir, ReturnAir (mep_connectors), stub?: straight "
                                "length out of the connector when the first path point is not ahead of it, default 300}: the run starts on that connector",
                       "to_main?": "id of a duct / pipe / tray / conduit the last segment points at: joined with a tap or tee (trays and conduits: tee)"}),
        "mep_connect": ("Connect an element's open connector (air terminal, fixture, sprinkler, equipment, the open end of a run) to a main duct, "
                        "pipe, cable tray or conduit: a straight branch along the connector's direction, then the tap / tee from the main's "
                        "routing preferences (trays and conduits: a tee, the main split there). The connector must point at the main.",
                                              {"element": "id of the terminal / fixture / equipment / run", "main": "id of the main duct, pipe, tray or conduit",
                                               "type?": "branch type; default the main's", "diameter?": "branch size", "width?": "", "height?": "",
                                               "level?": LEVEL + " (only if the main has none)",
                                               "classification?": "which open connector, e.g. SupplyAir or ReturnAir (mep_connectors lists them)"}),
        "mep_fixture_place": ("Plumbing fixtures, sprinklers, electrical / lighting / fire alarm / data / security / communication devices "
                              "and plumbing or mechanical equipment of the engineer's type at the engineer's points: level-based types on the level "
                              "at height; face-based types on a plane at height facing down (ceiling sprinklers, downlights) or up (floor drains), or, with walls, "
                              "on the nearer side face of a wall (receptacles, switches; walls of a link with wall_link). Optional rotation, "
                              "parameter values and a connection of each to a main pipe or duct (sprinkler drops, fixture branches).",
                              {"type": TYPE, "level": LEVEL, "points": "list of " + XYZ, "height?": "mounting height above the level, default 0",
                               "rotation?": "degrees about the vertical: one, or one per point (not on walls)",
                               "face?": "face-based types without walls: down (default, a ceiling plane: sprinklers, downlights) | up (a floor plane: floor drains, floor boxes)",
                               "walls?": "wall id for every point, or a list of one id (or null) per point: face-based types sit on its nearer side face",
                               "wall_link?": "the link holding the walls (from list_links)", "set?": "{parameter: value} for every placed element",
                               "main?": "pipe or duct to connect each one to (its connector must point at it; a tee splits the main, later points use the piece they point at)",
                               "classification?": "which connector, e.g. DomesticColdWater, Sanitary, FireProtectWet", "branch_type?": "", "branch_diameter?": "",
                               "branch_width?": "", "branch_height?": ""}),
        "sleeve_place": ("Sleeves of the family type the engineer gives where runs cross walls / slabs / beams (host or link): centred on the "
                         "crossing (wall centre plane, slab mid-depth), turned so the family's own axis lies along the run, with the parameter "
                         "values given; a value can be a length measured at the crossing: crossing_length, run_outside_diameter, run_diameter, "
                         "run_width or run_height, optionally + or - the engineer's clearance (e.g. \"run_outside_diameter + 50\"). The run is "
                         "not cut (crossing_opening cuts openings; mep_crossings finds the crossings).",
                         {"type": TYPE + " (generic model, pipe / duct accessory or fitting, specialty or mechanical equipment; level- or work-plane-based)",
                          "items": "list of {run: duct / pipe / tray / conduit id, crossing: wall / floor / beam id}",
                          "crossing_link?": "the link holding the crossed elements", "axis?": "x | y | z (default): the family's own axis along its length",
                          "set?": "{parameter: value or measured length}, e.g. {\"Diameter\": \"run_outside_diameter + 50\", \"Length\": \"crossing_length\"}"}),
        "electrical_circuit_create": ("Electrical circuits from the engineer's list: each circuit's loads (devices, fixtures, equipment), and "
                                      "optionally its panel, kind, load name and breaker rating. Revit computes the load, voltage and current.",
                                      {"circuits": "list of {elements: ids, panel?: panel name or id, kind?: power (default) | data | telephone | "
                                                   "security | fire_alarm | nurse_call | controls | communication, load_name?, rating?: amperes}"}),
        "electrical_circuit_edit": ("Change a circuit: add or remove loads, put it on a panel or take it off, load name, rating.",
                                    {"circuit": "circuit id (electrical_circuits)", "add?": IDS, "remove?": IDS, "panel?": "panel name or id",
                                     "disconnect_panel?": "default false", "load_name?": "", "rating?": "amperes"}),
        "electrical_panel_set": ("A panel's distribution system (from Revit's Electrical Settings) and / or panel name.",
                                 {"panel": "panel name or id", "distribution_system?": "name or id", "name?": "new panel name"}),
        "mep_system_create": ("Create a duct or piping system of a given system type from the free connectors of given terminals / equipment "
                              "(only the connectors of the type's classification; others are listed); optional base equipment (its connector is the source, so the system also needs at least one other element).", {"kind": "duct | pipe", "system_type?": "e.g. Supply Air, Chilled Water Supply; default the type matching the connectors' classification",
                                                           "elements": IDS, "name?": "system name", "base_equipment?": "equipment id"}),
        "air_terminal_place": ("Place air terminals of a given type at points and a height above a level, with the airflow the engineer gives, "
                               "optionally each connected to a main duct above it (branch + tap / tee, as mep_connect; a main split for a tee is "
                               "followed to the next terminal). Face-based types are hosted on a reference plane facing down at that height.",
                               {"type": TYPE + " (air terminals)", "level": LEVEL, "points": "list of " + XY, "height": "terminal face height above the level",
                                "flows?": "L/s: one value, or one per point (the engineer's values)", "main?": "id of a duct above the points to connect to",
                                "branch_type?": "branch duct type; default the main's", "branch_diameter?": "branch size (round)",
                                "branch_width?": "", "branch_height?": ""}),
        "mep_system_add": ("Add elements to an existing duct or piping system (their free connectors of the system's classification; others are listed).",
                           {"system": "system id (mep_systems)", "elements": IDS}),
        "mep_system_remove": ("Remove elements from a duct or piping system (not its base equipment).", {"system": "system id (mep_systems)", "elements": IDS}),
        "mep_accessory_place": ("Insert duct accessories (dampers, fire dampers) or pipe accessories (valves) of the type the engineer gives into "
                                "existing straight ducts / pipes, at a point on the run or where the run crosses a given wall / slab (its centre "
                                "plane or mid-depth, so an opening already cut there does not matter; host or link). Revit splits the run, gives "
                                "the accessory the run's size and connects it; several items on one run follow its pieces. Nothing is chosen here: "
                                "type, fire rating and positions are the engineer's (mep_crossings finds the crossings).",
                                {"type": TYPE + " (duct or pipe accessories)",
                                 "items": "list of {run: duct / pipe id, point?: " + XYZ + " on the run ([x, y] is matched in plan), "
                                          "crossing?: id of the wall / floor / other element it passes through}: give point or crossing",
                                 "crossing_link?": "the link holding the crossing elements (from list_links)"}),
        "mep_insulation_add": ("Add duct / pipe insulation or duct lining of the type and thickness the engineer gives to ducts, pipes, their "
                               "fittings and accessories, or to a whole system's network. Elements that already have it are listed, or changed with replace.",
                               {"kind": "insulation | lining (ducts only)", "type": "insulation / lining type name or id (mep_insulation and list_types show them)",
                                "thickness": "the engineer's thickness", "elements?": IDS, "system?": "duct or piping system id (mep_systems): its ducts / pipes, fittings, accessories",
                                "replace?": "change the type and thickness of existing insulation; default false"}),
        "mep_insulation_remove": ("Remove the insulation or lining of the elements given, or of a system's network.",
                                  {"kind": "insulation | lining", "elements?": IDS, "system?": "duct or piping system id"}),
        "mep_run_create": ("Create straight ducts, pipes, cable trays or conduits between the points given, with the type, system and size given "
                           "(no fittings are added between runs).",
                           {"kind": "duct | pipe | cable_tray | conduit", "level": LEVEL, "runs": "list of {start: " + XYZ + ", end: " + XYZ + "} (z defaults to the level)",
                            "type?": "type name or id; default the first", "system?": "ducts and pipes: system type name; default the first",
                            "diameter?": "round ducts, pipes, conduits", "width?": "rectangular ducts, trays", "height?": "rectangular ducts, trays"}),
    },
    "Rooms and spaces": {
        "room_create": ("Create rooms or MEP spaces at the points given, with the names and numbers given, or (all_regions) a room in every enclosed region "
                        "of a level that has none. Reports each one's area and warns about any that are not enclosed.",
                        {"kind?": "rooms (default) | spaces", "level": LEVEL, "points?": "list of " + XY, "names?": "one per point",
                         "numbers?": "one per point", "all_regions?": "default false: rooms in every enclosed region instead of points",
                         "phase?": "phase name (spaces at points, or rooms with all_regions)"}),
        "spaces_from_rooms": ("Create an MEP space for every placed room of a linked architectural model, on the host level at the same elevation, "
                              "with the room's name, number and height; the link is made room-bounding; regions that hold a space are skipped. Reports "
                              "spaces that are not enclosed.", {"link": "link instance id (list_links)", "levels?": "only these host levels",
                                                                 "room_bounding?": "default true", "level_tolerance?": "default 50", "phase?": "phase name", "max?": "default 2000"}),
        "hvac_zone_create": ("Create an HVAC zone holding the given spaces (all on one level). Revit 2024: an HVAC zone. Revit 2027: a "
                             "space-based system zone of a system zone type (type; create_type: true makes the type, its set points "
                             "left at Revit's defaults for the engineer to set).",
                             {"spaces": "list of space ids", "name?": "zone name", "type?": "Revit 2027: system zone type name (needed when there are several)",
                              "create_type?": "Revit 2027: default false; true creates 'type' when missing"}),
        "space_airflow_set": ("Set spaces' design supply / return / exhaust airflow to the engineer's values (L/s); air_terminals compares them with "
                              "the terminals.", {"spaces": "list of space ids", "supply?": "L/s: one value or one per space", "return?": "", "exhaust?": ""}),
        "room_separation": ("Draw room (or space) separation lines on a level's plan.",
                            {"kind?": "rooms (default) | spaces", "level": LEVEL, "lines": "list of " + SEGMENT, "view?": "plan of that level; default the first floor plan"}),
    },
    "Analytical model and loads": {
        "analytical_member_create": ("Create an analytical member (Revit 2023+ analytical model) along a line or arc, optionally tied to its physical element.",
                                     {"start": XYZ, "end": XYZ, "mid?": XYZ + " a point on the arc", "role?": "beam (default) | column | girder | member | unset",
                                      "section?": TYPE + " (framing or column type)", "material?": "material name", "physical?": "id of the physical element to associate"}),
        "analytical_panel_create": ("Create an analytical panel from a flat outline (floor or wall), optionally tied to its physical element.",
                                    {"points": "list of " + XYZ + " corners in one plane", "role?": "floor | wall (default from the outline) | panel | unset",
                                     "thickness?": "", "physical?": "id of the physical element to associate"}),
        "load_case_create": ("Create a load case.",
                             {"name": "", "category": "dead | live | wind | snow | roof_live | accidental | temperature | seismic",
                              "nature?": "load nature name; default the category's (Dead, Live ...)", "create_nature?": "default false: add the nature when missing",
                              "number?": "case number", "if_exists?": "'skip' to accept an existing case of that name"}),
        "load_combination_create": ("Create a load combination from the cases and factors you give (no factors are chosen for you).",
                                    {"name": "", "components": "list of {case: load case or combination name, factor}",
                                     "type?": "combination (default) | envelope", "state?": "ultimate (default) | serviceability"}),
        "load_place": ("Place a point, line or area load with the values you give on an analytical member or panel (or a physical element with "
                       "an associated analytical one). Forces in global axes: kN (point), kN/m (line), kN/m2 (area); moments kN m (point), kN m/m (line).",
                       {"kind": "point | line | area", "load_case": "load case name", "host": "analytical member / panel id, or its physical element id",
                        "force": "[fx, fy, fz]", "moment?": "[mx, my, mz]", "point?": "point loads: " + XYZ + " on the member; default at_end",
                        "at_end?": "point loads: start (default) | end", "start?": "line loads: " + XYZ + " start of a part of the member; default all of it",
                        "end?": "line loads: " + XYZ, "points?": "area loads: list of " + XYZ + " corners of a region of the panel; default all of it"}),
        "analytical_release_set": ("Set the end releases of analytical members as the engineer gives them: fixed, pinned, bending_moment, "
                                   "or user with the released directions.",
                                   {"members": "analytical member ids", "end?": "start | end | both (default)",
                                    "release": "fixed | pinned | bending_moment | user",
                                    "fx?": "release 'user' only: true = released, false or missing = fixed", "fy?": "as fx",
                                    "fz?": "as fx", "mx?": "as fx", "my?": "as fx", "mz?": "as fx"}),
        "boundary_condition_create": ("Place a support on the analytical model as given: a point at an analytical member's end, a line along a "
                                      "member, or an area on an analytical panel. Presets fixed, pinned (translations fixed, rotations free), "
                                      "roller (only Z fixed); 'user' takes each direction. No springs.",
                                      {"kind": "point | line | area", "host": "analytical member id (point, line) or panel id (area)",
                                       "end?": "point: start | end", "condition": "fixed | pinned | roller | user",
                                       "x?": "condition 'user': fixed (default) | released", "y?": "as x", "z?": "as x",
                                       "rx?": "as x (point, line)", "ry?": "as x (point only)", "rz?": "as x (point only)"}),
    },
    "Links": {
        "cad_link": ("Link (or import) a DWG / DXF file.",
                     {"path": "full path of the .dwg / .dxf", "mode?": "link (default) | import", "view?": VIEW,
                      "this_view_only?": "default false (visible in all views)", "placement?": PLACEMENT,
                      "cad_units?": "auto (default) | mm | cm | m | ft | in - give them when the DWG is unitless",
                      "colors?": "preserve (default) | black_white | invert", "visible_layers_only?": "default false", "pin?": "default true"}),
        "revit_link": ("Link another Revit model.", {"path": "full path of the .rvt", "placement?": PLACEMENT, "pin?": "default true"}),
        "link_reload": ("Reload a Revit link or a linked CAD file, or reload it from another file (change its path). Runs after the commit "
                        "(Revit reloads in its own transaction): a dry run only checks.",
                        {"link": "Revit link (instance id, type id or file name) or linked CAD file id, from list_links",
                         "path?": "reload from this file instead (.rvt for a Revit link, .dwg / .dxf for CAD)"}),
        "link_unload": ("Unload a Revit link (moved shared positions are not saved back to it). Runs after the commit; a dry run only checks. "
                        "Revit's API cannot unload CAD links.",
                        {"link": "Revit link (instance id, type id or file name)",
                         "for?": "everyone (default) | me: unload only for you in a workshared model"}),
        "link_set": ("Change a link: pin / unpin it, overlay or attachment, relative or absolute path.",
                     {"link": "Revit link (instance id, type id or file name) or linked CAD file id",
                      "pinned?": "true / false", "attachment?": "overlay | attachment (Revit links)",
                      "path_type?": "relative | absolute (Revit links)"}),
    },
    "Export and save": {
        "export_dwg": ("Export views / sheets to DWG (one file per view, named by name_template).",
                       {"folder": FOLDER, "overwrite?": OVERWRITE, "views?": VIEWS, "sheets?": SHEETS, "name_template?": NAME_TEMPLATE}),
        "export_ifc": ("Export the model to IFC.", {**EXPORT, "version?": "IFC2x3CV2 (default), IFC4, IFC2x3, IFC4RV, ...", "view?": "export only what this view shows"}),
        "export_pdf": ("Export views / sheets to PDF: one combined file, or one file per sheet named by name_template.",
                       {**EXPORT, "views?": VIEWS, "sheets?": SHEETS, "combine?": "default true, or false when name_template is given",
                        "name_template?": NAME_TEMPLATE}),
        "export_nwc": ("Export to Navisworks (.nwc); needs the Navisworks exporter installed.", {**EXPORT, "view?": "a 3D view; default: the whole model"}),
        "export_image": ("Export views to PNG images.", {**EXPORT, "views?": VIEWS, "width_px?": "default 2000"}),
        "export_dwf": ("Export views / sheets to DWF: one merged file, or one file per view named by name_template.",
                       {**EXPORT, "views?": VIEWS, "sheets?": SHEETS, "merged?": "default true", "name_template?": NAME_TEMPLATE}),
        "export_fbx": ("Export a 3D view to FBX.", {**EXPORT, "view?": "a 3D view; default the active view"}),
        "export_gbxml": ("Export the model to gbXML from its building elements with the model's Energy Settings; an energy analysis model it needs "
                         "is made for the export and rolled back (the model is not changed).", {**EXPORT}),
        "export_dgn": ("Export views / sheets to DGN, one file per view.",
                       {"folder": FOLDER, "overwrite?": OVERWRITE, "views?": VIEWS, "sheets?": SHEETS, "name_template?": NAME_TEMPLATE,
                        "setup?": "a DGN export setup saved in the model; default: Revit's defaults, then the model's setups (the one used is reported)",
                        "seed?": "full path of a .dgn seed file (needed when the model has no DGN setup and Revit's defaults fail)"}),
        "export_step": ("Revit 2027: export what a 3D view shows to STEP (.stp).", {"folder": FOLDER, "name?": "default: the model name",
                                                                                   "view?": "3D view; default the active 3D view, else {3D}",
                                                                          "protocol?": "STEP application protocol, default Revit's", "overwrite?": OVERWRITE}),
        "export_schedule": ("Export a schedule to CSV.", {"schedule": "schedule name or id", "folder": FOLDER, "name?": "default: the schedule name", "overwrite?": OVERWRITE}),
        "save": ("Save the model.", {}),
        "save_as": ("Save the model under a new path (not for workshared models).", {"path": "full path ending in .rvt", "overwrite?": OVERWRITE}),
    },
}


def _flatten(groups: dict[str, Group]) -> Group:
    flat: Group = {}
    for group in groups.values():
        for name, entry in group.items():
            assert name not in flat, f"{name} is listed twice"
            flat[name] = entry
    return flat


READ_ACTIONS: Group = _flatten(READ_GROUPS)
EDIT_OPS: Group = _flatten(EDIT_GROUPS)

WORKSHARE_NOTE = (
    "These talk to the central model, cannot be undone and have no dry run, so each is its own action (never inside edit). "
    "They need the same one-time edit permission. sync_with_central and relinquish also need confirm: true: ask the user "
    "first, every time. Revit then shows its own Yes/No dialog for that one action: the first call returns permission "
    "'not granted'; after the user clicks Yes, send the same request again within 5 minutes. "
    "Pass document_id from ping to make sure the right model is synced."
)
WORKSHARE_GROUPS: dict[str, Group] = {
    "Worksharing": {
        "sync_with_central": ("Synchronize the active workshared model with its central (server or Autodesk Docs): send your changes, "
                              "receive others', relinquish by choice. Reports what you held before and after.",
                              {"confirm": "true, after the user agreed to this sync", "comment?": "sync comment",
                               "relinquish?": "all (default) | borrowed (elements only, keep worksets) | none",
                               "compact?": "default false", "save_local?": "default true: save the local file before and after (always on for cloud models, Revit requires it)",
                               "document_id?": "documentId from ping", "operation_id?": "request ID"}),
        "reload_latest": ("Bring other users' synced changes into this model (no changes are sent).",
                          {"document_id?": "documentId from ping", "operation_id?": "request ID"}),
        "relinquish": ("Give back what you hold in the central model without synchronising (only what is not modified locally).",
                       {"confirm": "true, after the user agreed", "what?": "all (default) | borrowed (elements) | worksets",
                        "document_id?": "documentId from ping", "operation_id?": "request ID"}),
        "checkout": ("Take ownership of elements and / or worksets in the central model before editing them.",
                     {"ids?": IDS, "worksets?": "workset names", "document_id?": "documentId from ping", "operation_id?": "request ID"}),
    },
}
WORKSHARE_ACTIONS: Group = _flatten(WORKSHARE_GROUPS)

# --------------------------------------------------------------------------- rendering

EDIT_PARAMS = {"operations": "list of edit operations", "document_id?": "documentId from ping: refuse if another model is active",
               "operation_id?": "request ID (generated when omitted); reuse only for an identical retry", "dry_run?": "bool",
               "units?": "mm (default) | cm | m | ft | in"}

UNITS_NOTE = (
    "UNITS: every length and coordinate you send or receive is in millimetres unless params.units says otherwise "
    "(mm | cm | m | ft | in; for an edit it applies to the whole batch). Take-off lengths are metres, areas m2, volumes m3, "
    "rebar weight kg, angles degrees. Coordinates are relative to the Revit internal origin; z defaults to the level."
)

NO_DESIGN_NOTE = (
    "NO DESIGN: this tool models, reads and documents; it never designs. Engineering values (bar counts, spacings, cover, "
    "anchorage, laps, bend lengths, end zones, joist spacing, member sizes) come from the user or their design; never invent "
    "them or apply a design code. If one is missing, ask the user for it."
)


def _signatures(groups: dict[str, Group], summaries: bool = True) -> list[str]:
    """One heading per group, one line per entry: '- name(param, param?): summary' (or without the summary)."""
    out: list[str] = []
    for title, group in groups.items():
        out.append(f"[{title}]")
        for name, (summary, params) in group.items():
            line = f"- {name}({', '.join(params)})" if params else f"- {name}"
            out.append(f"{line}: {summary}" if summaries else line)
    return out


def description_detail() -> str:
    """How much the tool description spells out: 'full' (default: every action and edit operation with its summary
    and parameters) or, with REVIT_MCP_DESCRIPTION=compact, names and parameter names only (about 40% of the text;
    what each does comes from action='help')."""
    return "compact" if os.environ.get("REVIT_MCP_DESCRIPTION", "").strip().lower() == "compact" else "full"


COMPACT_NOTE = (
    "The lists below give names and parameter names only. Before the first use of an action or edit operation in a "
    "conversation, call action='help' with topic=<name>, a list of names or a [group] title: it returns what it does "
    "and what every parameter means. Do not guess a parameter's format."
)


def tool_description(mode: str = "all") -> str:
    """Text Claude sees for the MCP tool. mode: 'all', 'read' or 'edit'."""
    full = description_detail() == "full"
    out: list[str] = []
    if mode in ("all", "read"):
        out.append("Work with the model open in Autodesk Revit on this computer. Call with action=<name> and params={...}. "
                   "Export actions write files. Session and selection actions change application state. "
                   "Parameters ending in ? are optional. action='help' returns every "
                   "parameter description.")
        out.append(UNITS_NOTE)
        out.append(NO_DESIGN_NOTE)
        if not full:
            out.append(COMPACT_NOTE)
        out.append("\nREAD ACTIONS")
        out += _signatures(READ_GROUPS, full)
        out += _signatures({"Help": {k: v for k, v in LOCAL_ACTIONS.items()
                                      if mode == "all" or k not in ("params_from_excel", "excel_to_model")}})
    if mode in ("all", "edit"):
        if mode == "edit":
            out.append(UNITS_NOTE)
            out.append(NO_DESIGN_NOTE)
            if not full:
                out.append(COMPACT_NOTE.replace("call action='help'", "call the read tool's action='help'"))
        out.append("\nEDIT ACTION\n- edit(" + ", ".join(EDIT_PARAMS) + "): " + EDIT_ACTION)
        if mode == "edit":
            out += _signatures({"Excel edits (pass name as excel_action)": {
                name: LOCAL_ACTIONS[name] for name in ("params_from_excel", "excel_to_model")}})
        out.append("\nWORKSHARING ACTIONS (own actions, not edit operations): " + WORKSHARE_NOTE)
        if mode == "edit":
            out.append("Pass the name as worksharing_action and its arguments as worksharing_params.")
        out += _signatures(WORKSHARE_GROUPS)
        out.append("\nEDIT OPERATIONS (each is {op: name, ...})")
        out += _signatures(EDIT_GROUPS, full)
        out.append("\nElements are addressed by the 'id' the read actions return; levels, views, sheets and types by name. "
                   "Types must exist in the model (list_types; family_load / type_duplicate add more). Before a large edit, "
                   "read the relevant lists, then send ONE batch; later operations of a batch can use levels and types made "
                   "by earlier ones.")
    return "\n".join(out)


def help_data(topic: str | list[str] | None = None) -> dict:
    def full(entries: Group) -> dict:
        return {name: {"summary": summary, "params": params} for name, (summary, params) in entries.items()}

    edit = {"summary": EDIT_ACTION, "params": EDIT_PARAMS}
    if isinstance(topic, list):
        if not topic or len(topic) > 40 or any(not isinstance(t, str) or not t.strip() for t in topic):
            raise ValueError("topic must be a name or a list of 1 to 40 names.")
        merged: dict = {}
        for one in topic:
            merged.update(help_data(one))
        return merged
    if topic is not None and not isinstance(topic, str):
        raise ValueError("topic must be a name or a list of names.")
    if topic is not None:
        topic = topic.strip().strip("[]")
        # a group title as the tool description prints it, e.g. 'Quantities' or 'Model QA'
        for groups, kind in ((READ_GROUPS, "read actions"), (EDIT_GROUPS, "edit operations (use inside action 'edit')"),
                             (WORKSHARE_GROUPS, "worksharing actions")):
            for title, group in groups.items():
                if title.casefold() == topic.casefold() and topic not in READ_ACTIONS and topic not in EDIT_OPS:
                    return {title: {"kind": kind, "entries": full(group)}}
    if topic is None:
        return {"units": UNITS_NOTE, "noDesign": NO_DESIGN_NOTE, "readActions": full(READ_ACTIONS), "localActions": full(LOCAL_ACTIONS),
                "edit": edit, "editOperations": full(EDIT_OPS), "worksharing": {"note": WORKSHARE_NOTE, "actions": full(WORKSHARE_ACTIONS)}}
    if topic == "edit":
        return {"edit": {**edit, "operations": full(EDIT_OPS)}}
    for entries, kind in ((READ_ACTIONS, "action"), (LOCAL_ACTIONS, "action"), (WORKSHARE_ACTIONS, "worksharing action (changes the central model)"),
                          (EDIT_OPS, "edit operation (use inside action 'edit')")):
        if topic in entries:
            summary, params = entries[topic]
            return {topic: {"summary": summary, "params": params, "kind": kind}}
    raise ValueError(f"Unknown help topic '{topic}'.")
