"""The catalogue of everything Claude can ask Revit to do.

This file is the single place for the WORDING Claude sees (action names, what they do, their parameters).
The behaviour lives in the add-in; `help` and the tests compare the two so they cannot drift apart silently.
docs/ACTIONS.md is generated from this file (tools/gen_docs.py).

Each entry: name -> (summary, {parameter: description}).  A parameter ending in '?' is optional.
The groups mirror the add-in sources: plugin/Read/<Group>Read.cs and plugin/Edit/<Group>Ops.cs.
"""
from __future__ import annotations

BRIDGE_VERSION = "0.14.0"   # keep plugin/BridgeInfo.cs in step; the tests check it

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
OVERRIDES = {"color?": "\"#RRGGBB\", [r, g, b] or a name (red, blue ...)", "fill?": "default false: solid fill in the colour",
             "line_weight?": "1-16", "halftone?": "bool", "transparency?": "0-100"}
COLUMN_LAYER = "the column layer (see get_cad_geometry layers_only), e.g. S-COLS"
STEEL_WORDS = "extra words that mark steel families, e.g. 'STL,SHS'"
SKIP_EXISTING = "default true: skip what is already modelled there"

# --------------------------------------------------------------------------- read actions (never change the model)

READ_GROUPS: dict[str, Group] = {
    "Model": {
        "ping": ("Connection check with Revit version, active model and view, documentId for the edit target, and edit permission.", {}),
        "get_project_info": ("Active model: title, path, workshared / read-only, project information, phases, "
                             "counts of structural elements, open models.", {}),
        "list_levels": ("Levels with project-relative elevation and Revit's displayElevation, sorted by project elevation.", {}),
        "list_grids": ("Grids with their end points (and mid point and radius for arc grids).", {"limit?": "default 500"}),
        "list_views": ("Views (not templates, not sheets): name, type, level, scale.",
                       {"type?": "e.g. FloorPlan, EngineeringPlan, ThreeD, Section, Elevation, Schedule, DraftingView", "limit?": "default 200"}),
        "list_sheets": ("Sheets: number, name, the views placed on them.", {"limit?": "default 500"}),
        "count_by_category": ("Number of model elements per category, largest first.",
                              {"view?": "only elements visible in this view", "include_annotation?": "default false"}),
        "list_types": ("Types of a category (family, type, id) with the number of instances of each. "
                       "Use these names for the 'type' parameter of edit operations.", {"category": CATEGORY, "limit?": "default 300"}),
        "list_materials": ("Materials: name, class.", {"limit?": "default 300"}),
        "list_warnings": ("Revit's own warnings (overlaps, duplicates, joins ...) with the elements involved, and a count per message.",
                          {"limit?": "default 100"}),
    },
    "Elements": {
        "get_elements": ("Elements of one or more categories (or by id): id, family, type, level, location (point or end points) "
                         "and any parameters you name.",
                         {"category?": "one category or a list (required without element references): " + CATEGORY, "ids?": IDS,
                          "unique_ids?": "list of element UniqueIds instead of ids", "allow_missing?": "default false; true reports missing references",
                          "type?": TYPE, "level?": LEVEL, "view?": "only elements visible in this view",
                          "params?": "names or bip:/guid:/id: selectors (instance first, then type)",
                          "parameter_details?": "include each requested parameter's stable key, type, scope and writability",
                          "location?": "default true", "box?": "bounding box, default false", "limit?": "default 100",
                          "after_id?": "nextAfterId from the previous page, with the same filters"}),
        "get_element": ("Element geometry and parameter details, including same-name parameters and their stable keys.",
                        {"id": "element id", "include_type?": "default true"}),
        "get_selection": ("The elements the user has selected in Revit.", {"params?": "parameter names to include", "limit?": "default 200"}),
        "show_elements": ("Select elements in Revit and zoom to them (changes only what the user sees, not the model).",
                          {"ids": IDS, "select?": "default true", "zoom?": "default true"}),
    },
    "Links and CAD": {
        "list_links": ("Revit links (file, status) and linked / imported CAD files (file, linked or imported, view).", {}),
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
        "rebar_list": ("Every bar set (rebar and bars of area / path systems) for a bar bending schedule: member, mark, bar type, "
                       "diameter, shape, number, length, dimensions A..J, total length, weight; totals by diameter.",
                       {"hosts?": "only bars in these host element ids", "level?": "only bars whose host is on this level",
                        "view?": "only bars visible in this view", "limit?": "default 5000"}),
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
    "Meta": {
        "family_inspect": ("Look inside a loaded family (opened in memory, never saved): its parameters (instance / type, formula), solids with "
                           "their material and the parameter it is linked to, nested families.", {"family": "family name"}),
        "describe_api": ("Developer aid: public members of a Revit API type as loaded in this Revit.",
                         {"type_name": "e.g. Autodesk.Revit.DB.Wall, or a short name", "contains?": "member name filter", "limit?": "default 300"}),
        "get_edit_permission": ("Whether editing is currently allowed in this Revit session, and how to allow it.", {}),
        "get_operation_status": ("Look up an edit receipt without waiting for the Revit UI. Inspect result for the transaction outcome.",
                                 {"operation_id": "the edit operation ID", "pid?": "Revit process ID from the receipt or transport error"}),
        "list_actions": ("What the installed add-in supports (names only). Use 'help' for descriptions.", {}),
    },
}

# computed in the MCP server; they never reach the add-in under these names
LOCAL_ACTIONS: Group = {
    "help": ("This catalogue with every parameter. Pass topic to narrow it.", {"topic?": "an action or edit operation name"}),
    "qs_report": ("Write a QS workbook (.xlsx) on this computer: summary, concrete by grade and level, elements by type, steel tonnage, "
                  "rebar, formwork, QA (material problems, unjoined overlaps), notes. Reads only; writes the file.",
                  {"path": "full path of the .xlsx to write", "overwrite?": "default false", "level?": "only this level",
                   "category?": CATEGORIES, "check_overlaps?": "default true (time-limited auto-join check)",
                   "family_contains?": STEEL_WORDS, "density?": "steel kg/m3, default 7850",
                   "view?": VIEW, "document_id?": "documentId to require", "concrete_materials?": "exact additional names to classify as concrete"}),
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
    "list_revit_sessions": ("Every running Revit with the add-in: pid, version, active model, whether it answers, which one is selected.", {}),
    "select_revit": ("Send all following calls to one Revit (e.g. 2027 while 2024 is also open).",
                     {"pid?": "process id from list_revit_sessions, or 'auto' for the most recent Revit", "revit?": "version, e.g. '2027', when only one of it runs"}),
}

# --------------------------------------------------------------------------- edit operations (inside action 'edit')

EDIT_ACTION = (
    "Change the model or write files. The server captures the active document before editing. Pass document_id from an earlier ping to require that model. "
    "operation_id (generated when omitted) lets a retry after a lost reply return the first result instead of editing twice "
    "(reuse it only with the identical request), and get_operation_status reports it. "
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
                          "containment, and unmeasured pairs are left for review. See resolvedByRevit for detach resolutions.",
                          {"priority?": "category order, default foundations, columns, walls, beams, floors", "category?": "default: the same five",
                           "level?": "only elements on this level", "view?": "only elements visible in this view",
                           "include_steel?": "default false", "family_contains?": STEEL_WORDS,
                           "max_pairs?": "per batch, default 1000", "max_seconds?": "time budget, default 40",
                           "resume_after?": "resumeAfter cursor from the previous call"}),
        "type_duplicate": ("Duplicate a type under a new name and optionally set its parameters (e.g. a 300x700 beam from a 300x600 one).",
                           {"type": TYPE, "new_name": "", "category?": CATEGORY, "values?": "{\"Parameter name\": value, ...}"}),
        "family_load": ("Load a family file (.rfa) into the model; returns its types.", {"path": "full path of the .rfa"}),
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
                                "include_grids?": "default true", "view?": VIEW}),
        "spot_elevation": ("Spot elevations on the top face of elements (slabs, beams, footings ...).",
                           {"ids": IDS, "point?": XY + " where to place it (default the face centre)", "leader?": "default true", "view?": VIEW}),
        "revision_create": ("Create a revision, optionally added to sheets.",
                            {"description": "", "date?": "", "issued_by?": "", "issued_to?": "", "issued?": "bool", "sheets?": "sheet numbers"}),
        "revision_cloud_create": ("Draw a revision cloud in a view.",
                                  {"revision": "revision description, number or sequence", "boundary": "list of " + XY + " corners", "view?": VIEW}),
        "sheet_layout": ("Place several views, legends and schedules on one sheet, arranged in rows inside the sheet area.",
                         {"sheet": "sheet number or name", "views": "view, legend or schedule names, in reading order",
                          "columns?": "items per row; default about the square root of the count",
                          "area?": "{min: [x, y], max: [x, y]} paper mm from the sheet's lower-left corner (leave out the title strip)",
                          "margin?": "paper mm, default 20", "gap?": "paper mm between items, default 15"}),
    },
    "Reinforcement": {
        "rebar_column": ("Reinforce structural columns: longitudinal bars on all faces (corners included) and closed ties, closer at both ends. "
                         "Bars continue above for the lap when a column is above, bend inward at the top otherwise, and stand on the footing mesh with "
                         "feet when the column sits on a footing.",
                         {"ids": IDS, "bar": "bar type name or diameter in mm (e.g. 20)", "tie": "tie bar type or diameter",
                          "bars_x?": "bars on each face along the column's local x, corners included; default 3",
                          "bars_y?": "bars on each face along local y, corners included; default 3",
                          "tie_spacing?": "default 200 mm", "cover?": "to the tie's outer face; default 40 mm",
                          "end_zone?": "length of the closer tie spacing at each end; default the larger of the long side, height / 6, 450 mm; 0 = none",
                          "end_spacing?": "tie spacing in the end zones; default the smaller of tie_spacing and 100 mm",
                          "top?": "auto (default: lap if a column is above, else bend) | lap | bend | none",
                          "bottom?": "auto (default: footing if a foundation is below) | footing | none",
                          "lap_d?": "lap length in bar diameters; default 50", "footing_cover?": "default 50 mm"}),
        "rebar_beam": ("Reinforce straight, level beams: bottom and top bars anchored into the supports (straight if the support is deep "
                       "enough, else with a 90 degree bend; lapped into the next span in line), stirrups closer near the supports, and bars "
                       "longer than the stock length split with laps.",
                       {"ids": IDS, "bottom_bar": "bar type or diameter", "bottom_count?": "default 3", "top_bar?": "default the bottom bar",
                        "top_count?": "default 2", "stirrup": "bar type or diameter", "stirrup_spacing?": "default 150 mm",
                        "cover?": "to the stirrup's outer face; default 40 mm",
                        "ends?": "anchor (default) | none (bars stop at cover inside the beam)",
                        "anchorage_d?": "anchorage length in bar diameters; default 40", "lap_d?": "lap length in bar diameters; default 50",
                        "end_zone?": "length of closer stirrups at each end; default 2 x beam depth; 0 = none",
                        "end_spacing?": "stirrup spacing in the end zones; default the smaller of stirrup_spacing and 100 mm",
                        "stock_length?": "longest bar before a lap; default 12000 mm"}),
        "rebar_footing": ("Reinforce isolated footings (and pile caps): bottom mesh both ways, optional top mesh, optional bottom bars "
                          "turned up at the edges.",
                          {"ids": IDS, "bar": "bar type or diameter", "spacing?": "default 200 mm", "cover?": "default 50 mm",
                           "top?": "default false", "top_bar?": "default the bottom bar", "top_spacing?": "default the bottom spacing",
                           "end_bends?": "bool; bottom bars turned up at the edges (U bars); default false",
                           "bend_leg?": "longest turned-up leg; default up to under the top mesh",
                           "lap_d?": "lap length in bar diameters; default 50", "stock_length?": "default 12000 mm"}),
        "rebar_area": ("Area reinforcement in slabs: one bar type at a spacing, bottom, top or both layers, both directions.",
                       {"ids": "slab ids", "bar": "bar type or diameter", "spacing?": "default 200 mm", "layers?": "bottom | top | both (default)",
                        "direction?": XY + " major direction; default x", "boundary?": "list of " + XY + " corners; default the slab outline"}),
    },
    "Modelling": {
        "slab_shape": ("Shape a slab by points: move an existing corner (within 'tolerance') or add a point, each by an offset from the slab top; "
                       "'reset' makes it flat again. Use floor_create with slope_arrow for a plane slope.",
                       {"id": "slab id", "points?": "list of {at: " + XY + ", offset: height change (+ up)}",
                        "tolerance?": "default 50 mm", "reset?": "bool"}),
        "beam_system_create": ("Create a beam system (joists at a fixed spacing) inside a boundary on a level.",
                               {"level": LEVEL, "type": TYPE + " of framing", "boundary": "list of " + XY + " corners",
                                "direction?": XY + " beam direction; default x", "spacing?": "default 1500 mm",
                                "justification?": "center (default) | beginning | end | directionline"}),
        "brace_create": ("Create braces (structural framing set as brace) between 3D points.",
                         {"type": TYPE + " of framing", "level": LEVEL + " (reference level)",
                          "braces": "list of {start: " + XYZ + ", end: " + XYZ + "}: points without z sit on the level"}),
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
        "fix_warnings": ("Fix Revit warnings that have one safe fix: 'joined but do not intersect' pairs are unjoined (no quantity change). "
                         "Every other warning is listed under notFixed, by message, to review.",
                         {"kinds?": "default all fixable kinds: joined_disjoint", "ids?": "only warnings involving these elements"}),
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
    },
    "Links": {
        "cad_link": ("Link (or import) a DWG / DXF file.",
                     {"path": "full path of the .dwg / .dxf", "mode?": "link (default) | import", "view?": VIEW,
                      "this_view_only?": "default false (visible in all views)", "placement?": PLACEMENT,
                      "cad_units?": "auto (default) | mm | cm | m | ft | in - give them when the DWG is unitless",
                      "colors?": "preserve (default) | black_white | invert", "visible_layers_only?": "default false", "pin?": "default true"}),
        "revit_link": ("Link another Revit model.", {"path": "full path of the .rvt", "placement?": PLACEMENT, "pin?": "default true"}),
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

# --------------------------------------------------------------------------- rendering

EDIT_PARAMS = {"operations": "list of edit operations", "document_id?": "documentId from ping: refuse if another model is active",
               "operation_id?": "request ID (generated when omitted); reuse only for an identical retry", "dry_run?": "bool",
               "units?": "mm (default) | cm | m | ft | in"}

UNITS_NOTE = (
    "UNITS: every length and coordinate you send or receive is in millimetres unless params.units says otherwise "
    "(mm | cm | m | ft | in; for an edit it applies to the whole batch). Take-off lengths are metres, areas m2, volumes m3, "
    "rebar weight kg, angles degrees. Coordinates are relative to the Revit internal origin; z defaults to the level."
)


def _signatures(groups: dict[str, Group]) -> list[str]:
    """One heading per group, one line per entry: '- name(param, param?): summary'."""
    out: list[str] = []
    for title, group in groups.items():
        out.append(f"[{title}]")
        out += [f"- {name}({', '.join(params)}): {summary}" if params else f"- {name}: {summary}"
                for name, (summary, params) in group.items()]
    return out


def tool_description(mode: str = "all") -> str:
    """Text Claude sees for the MCP tool. mode: 'all', 'read' or 'edit'."""
    out: list[str] = []
    if mode in ("all", "read"):
        out.append("Work with the model open in Autodesk Revit on this computer. Call with action=<name> and params={...}. "
                   "Reading never changes anything. Parameters ending in ? are optional. action='help' returns every "
                   "parameter description.")
        out.append(UNITS_NOTE)
        out.append("\nREAD ACTIONS")
        out += _signatures(READ_GROUPS)
        out += _signatures({"Help": {k: v for k, v in LOCAL_ACTIONS.items()
                                      if mode == "all" or k not in ("params_from_excel", "excel_to_model")}})
    if mode in ("all", "edit"):
        if mode == "edit":
            out.append(UNITS_NOTE)
        out.append("\nEDIT ACTION\n- edit(" + ", ".join(EDIT_PARAMS) + "): " + EDIT_ACTION)
        if mode == "edit":
            out += _signatures({"Excel edits (pass name as excel_action)": {
                name: LOCAL_ACTIONS[name] for name in ("params_from_excel", "excel_to_model")}})
        out.append("\nEDIT OPERATIONS (each is {op: name, ...})")
        out += _signatures(EDIT_GROUPS)
        out.append("\nElements are addressed by the 'id' the read actions return; levels, views, sheets and types by name. "
                   "Types must exist in the model (list_types; family_load / type_duplicate add more). Before a large edit, "
                   "read the relevant lists, then send ONE batch; later operations of a batch can use levels and types made "
                   "by earlier ones.")
    return "\n".join(out)


def help_data(topic: str | None = None) -> dict:
    def full(entries: Group) -> dict:
        return {name: {"summary": summary, "params": params} for name, (summary, params) in entries.items()}

    edit = {"summary": EDIT_ACTION, "params": EDIT_PARAMS}
    if topic is None:
        return {"units": UNITS_NOTE, "readActions": full(READ_ACTIONS), "localActions": full(LOCAL_ACTIONS),
                "edit": edit, "editOperations": full(EDIT_OPS)}
    if topic == "edit":
        return {"edit": {**edit, "operations": full(EDIT_OPS)}}
    for entries, kind in ((READ_ACTIONS, "action"), (LOCAL_ACTIONS, "action"), (EDIT_OPS, "edit operation (use inside action 'edit')")):
        if topic in entries:
            summary, params = entries[topic]
            return {topic: {"summary": summary, "params": params, "kind": kind}}
    raise ValueError(f"Unknown help topic '{topic}'.")
