# Actions and edit operations

Generated from `server/catalog.py` by `tools/gen_docs.py` - do not edit by hand.

Everything goes through one MCP tool: `revit(action, params)`. A parameter ending in `?` is optional.

UNITS: every length and coordinate you send or receive is in millimetres unless params.units says otherwise (mm | cm | m | ft | in; for an edit it applies to the whole batch). Take-off lengths are metres, areas m2, volumes m3, rebar weight kg, angles degrees. Coordinates are relative to the Revit internal origin; z defaults to the level.

## Read actions (39) - never change the model

### Model

| Name | Parameters | What it does |
|---|---|---|
| `ping` | - | Connection check with Revit version, active model and view, documentId for the edit target, and edit permission. |
| `get_project_info` | - | Active model: title, path, workshared / read-only, project information, phases, counts of structural elements, open models. |
| `list_levels` | - | Levels with project-relative elevation and Revit's displayElevation, sorted by project elevation. |
| `list_grids` | `limit?` default 500 | Grids with their end points (and mid point and radius for arc grids). |
| `list_views` | `type?` e.g. FloorPlan, EngineeringPlan, ThreeD, Section, Elevation, Schedule, DraftingView<br>`limit?` default 200 | Views (not templates, not sheets): name, type, level, scale. |
| `list_sheets` | `limit?` default 500 | Sheets: number, name, the views placed on them. |
| `count_by_category` | `view?` only elements visible in this view<br>`include_annotation?` default false | Number of model elements per category, largest first. |
| `list_types` | `category` alias (columns, beams, walls, floors, foundations, rebar, doors, windows, generic_models, ...), UI name ('Structural Columns') or OST_ name<br>`limit?` default 300 | Types of a category (family, type, id) with the number of instances of each. Use these names for the 'type' parameter of edit operations. |
| `list_materials` | `limit?` default 300 | Materials: name, class. |
| `list_warnings` | `limit?` default 100 | Revit's own warnings (overlaps, duplicates, joins ...) with the elements involved, and a count per message. |

### Elements

| Name | Parameters | What it does |
|---|---|---|
| `get_elements` | `category?` one category or a list (required without element references): alias (columns, beams, walls, floors, foundations, rebar, doors, windows, generic_models, ...), UI name ('Structural Columns') or OST_ name<br>`ids?` list of element ids<br>`unique_ids?` list of element UniqueIds instead of ids<br>`allow_missing?` default false; true reports missing references<br>`type?` 'Type name' or 'Family : Type name' (see list_types)<br>`level?` level name<br>`view?` only elements visible in this view<br>`params?` names or bip:/guid:/id: selectors (instance first, then type)<br>`parameter_details?` include each requested parameter's stable key, type, scope and writability<br>`location?` default true<br>`box?` bounding box, default false<br>`limit?` default 100<br>`after_id?` nextAfterId from the previous page, with the same filters | Elements of one or more categories (or by id): id, family, type, level, location (point or end points) and any parameters you name. |
| `get_element` | `id` element id<br>`include_type?` default true | Element geometry and parameter details, including same-name parameters and their stable keys. |
| `get_selection` | `params?` parameter names to include<br>`limit?` default 200 | The elements the user has selected in Revit. |
| `show_elements` | `ids` list of element ids<br>`select?` default true<br>`zoom?` default true | Select elements in Revit and zoom to them (changes only what the user sees, not the model). |

### Links and CAD

| Name | Parameters | What it does |
|---|---|---|
| `list_links` | - | Revit links (file, status) and linked / imported CAD files (file, linked or imported, view). |
| `get_cad_geometry` | `id` id of the CAD file from list_links<br>`layer?` only this layer<br>`layers_only?` default false: true returns just the layer names with entity counts<br>`limit?` default 2000 | Lines, arcs, circles and polylines inside a linked / imported DWG or DXF, by layer, in model coordinates. Call with layers_only first, then read one layer (e.g. the grid or column layer) to model from it. |
| `cad_columns` | `id` id of the CAD file from list_links<br>`layer` the column layer (see get_cad_geometry layers_only), e.g. S-COLS<br>`include?` concrete (default: plain rectangles and circles) \| steel (H / I and other outlines) \| all<br>`types?` {"650x1300": "Type name", ...}: type per outline size (width x depth as listed), checked before the automatic match<br>`tolerance?` size match tolerance; default 10 mm<br>`gap?` curves closer than this belong to one column; default 25 mm<br>`min_size?` default 150 mm<br>`max_size?` default 4000 mm<br>`skip_existing?` default true: skip outlines where a column already stands at the base<br>`level?` base level (for the existing-column check); default the drawing's elevation<br>`limit?` outlines listed; default 50 | Column outlines on a CAD layer, each fitted with a size and rotation and matched to a column type (size from the type's b/h, bf/d, Width/Depth or Diameter, else the first 'AxB' in its name); steel outlines (H / I) match only steel families, rectangles only non-steel; shows what columns_from_cad would place. Reads only. |

### Quantities

| Name | Parameters | What it does |
|---|---|---|
| `takeoff` | `category?` one category or a list; default: columns, beams, walls, floors, foundations<br>`group_by?` type (default) \| level \| type_level \| category<br>`view?` only elements visible in this view<br>`level?` only elements on this level | Quantity take-off from the model: count, length, area and volume as Revit computes them, grouped. |
| `material_takeoff` | `category?` one category or a list; default: columns, beams, walls, floors, foundations<br>`group_by?` category (default) \| level \| type<br>`exclude_steel?` default false: true leaves steel members out (e.g. steel wrongly carrying a concrete material)<br>`family_contains?` extra words that mark steel families, e.g. 'STL,SHS'<br>`view?` only elements visible in this view<br>`level?` only elements on this level | Volume and area by material and category, with concrete, non_concrete, and unclassified material kinds. |
| `formwork_takeoff` | `category?` one category or a list; default: columns, beams, walls, floors, foundations<br>`group_by?` type (default) \| level \| type_level \| category<br>`soffit_categories?` categories whose bottom face is formed; default beams and floors (remove floors for slabs on grade)<br>`family_contains?` extra words that mark steel families, e.g. 'STL,SHS'<br>`view?` only elements visible in this view<br>`level?` only elements on this level | Formwork areas from element faces: sides for all, soffits for beams and slabs, never top faces; steel skipped. |
| `steel_takeoff` | `category?` default: columns and beams<br>`group_by?` type (default) \| level \| type_level \| category<br>`density?` kg/m3, default 7850<br>`family_contains?` extra words that mark steel families, e.g. 'STL,SHS'<br>`view?` only elements visible in this view<br>`level?` only elements on this level | Steel weight by section: count, length, volume, kg and kg/m (Revit volume x density, independent of the material assigned). |
| `rebar_list` | `hosts?` only bars in these host element ids<br>`level?` only bars whose host is on this level<br>`view?` only bars visible in this view<br>`limit?` default 5000 | Every bar set (rebar and bars of area / path systems) for a bar bending schedule: member, mark, bar type, diameter, shape, number, length, dimensions A..J, total length, weight; totals by diameter. |
| `rebar_takeoff` | `group_by?` diameter (default) \| host_category \| type \| diameter_host_category<br>`host_category?` only bars hosted by these categories<br>`view?` only elements visible in this view<br>`level?` only elements on this level | Reinforcement: bars, total length (m) and weight (kg, 7850 kg/m3), grouped. |
| `list_schedules` | `limit?` default 300 | Schedules in the model: name, category, number of fields. |
| `get_schedule_data` | `schedule` schedule name or id<br>`limit?` default 500 rows | The rows of a schedule as displayed (text). |

### Model QA

| Name | Parameters | What it does |
|---|---|---|
| `check_materials` | `category?` one category or a list; default: columns, beams, walls, floors, foundations<br>`pattern?` regular expression for ACCEPTED material names, e.g. '^KTR15-'<br>`family_contains?` extra words that mark steel families, e.g. 'STL,SHS'<br>`view?` only elements visible in this view<br>`level?` only elements on this level | Elements with no material, a Revit default material (Concrete Cast-in-Place gray, Default ...) or a name outside your pattern, and steel members carrying a non-metal material; volume and sample ids per problem. |
| `find_overlaps` | `category?` default: foundations, columns, walls, beams, floors<br>`level?` only elements on this level<br>`view?` only elements visible in this view<br>`volumes?` default true: compute each overlap volume (slower)<br>`include_steel?` default false: pairs with a steel member are skipped<br>`family_contains?` extra words that mark steel families, e.g. 'STL,SHS'<br>`max_seconds?` time budget, default 40; then resume with resume_after<br>`resume_after?` resumeAfter cursor from the previous call<br>`max_pairs?` default 20000<br>`limit?` pairs listed, default 200 | Scan unjoined intersections and sum measured pairs. Reports coincident geometry, containment and near coincidence for review. |
| `check_names` | `kind` views \| sheets \| types \| levels \| grids \| materials \| families \| view_templates<br>`pattern` regular expression the names must match, e.g. '^KTR15-S-'<br>`field?` sheets only: name (default) \| number<br>`category?` types only: limit to one category<br>`limit?` default 300 | Names that do not match a regular expression (naming convention check). |

### Model management

| Name | Parameters | What it does |
|---|---|---|
| `model_health` | `purgeable?` default true: count purgeable elements (Revit 2024 and later) | Model health report: file size, warnings (top 10), in-place families, CAD linked / imported, unloaded links, views not on sheets, unused view templates and group types, purgeable elements, worksets, design options; plus an 'attention' list of what stands out. |
| `list_parameters` | `shared_file?` full path of a shared parameter file to list<br>`shared_file_contents?` default false: list the current shared parameter file | Project and shared parameters bound in the model: name, shared GUID, data type, palette group, instance / type, categories; optionally the groups and parameters of a shared parameter file. |
| `list_worksets` | `ids?` list of element ids<br>`counts?` default false: elements per workset (slower) | Worksets (open, editable, owner, active) and, for given elements, their workset, checkout status, owner, creator and last changed by. |
| `list_phases` | - | Phases in order, phase filters, and design option sets with their options (primary, element count). |

### Detailing and documentation

| Name | Parameters | What it does |
|---|---|---|
| `list_documentation` | `kind?` view_templates \| filters \| revisions \| scope_boxes \| title_blocks; default all | What documentation refers to by name: view templates (and how many views use each), filters, revisions, scope boxes, title blocks. |
| `list_detailing` | `kind?` line_styles \| region_types \| detail_components \| detail_groups \| repeating_details \| drafting_views \| legends; default all<br>`model?` title of another model open in this Revit (e.g. a detail library); default the active model<br>`limit?` per kind, default 300 | What drafting and detailing can use: line styles, filled region types, detail components (line-based or not), detail groups, repeating detail types, drafting views and legends; in this model or another open one (the source for views_copy_from). |

### Meta

| Name | Parameters | What it does |
|---|---|---|
| `family_inspect` | `family` family name | Look inside a loaded family (opened in memory, never saved): its parameters (instance / type, formula), solids with their material and the parameter it is linked to, nested families. |
| `describe_api` | `type_name` e.g. Autodesk.Revit.DB.Wall, or a short name<br>`contains?` member name filter<br>`limit?` default 300 | Developer aid: public members of a Revit API type as loaded in this Revit. |
| `get_edit_permission` | - | Whether editing is currently allowed in this Revit session, and how to allow it. |
| `get_operation_status` | `operation_id` the edit operation ID<br>`pid?` Revit process ID from the receipt or transport error | Look up an edit receipt without waiting for the Revit UI. Inspect result for the transaction outcome. |
| `list_actions` | - | What the installed add-in supports (names only). Use 'help' for descriptions. |

### Help (computed in the MCP server)

| Name | Parameters | What it does |
|---|---|---|
| `help` | `topic?` an action or edit operation name | This catalogue with every parameter. Pass topic to narrow it. |
| `qs_report` | `path` full path of the .xlsx to write<br>`overwrite?` default false<br>`level?` only this level<br>`category?` one category or a list; default: columns, beams, walls, floors, foundations<br>`check_overlaps?` default true (time-limited auto-join check)<br>`family_contains?` extra words that mark steel families, e.g. 'STL,SHS'<br>`density?` steel kg/m3, default 7850<br>`view?` view name or id; default: the active view<br>`document_id?` documentId to require<br>`concrete_materials?` exact additional names to classify as concrete | Write a QS workbook (.xlsx) on this computer: summary, concrete by grade and level, elements by type, steel tonnage, rebar, formwork, QA (material problems, unjoined overlaps), notes. Reads only; writes the file. |
| `bbs_report` | `path` full path of the .xlsx to write<br>`overwrite?` default false<br>`hosts?` only bars in these host ids<br>`level?` only bars whose host is on this level<br>`view?` only bars visible in this view | Write a bar bending schedule workbook (.xlsx) on this computer from the model's reinforcement: BBS sheet (member, mark, type, diameter, shape, number, length, A..J, total length, weight), summary by diameter and member. A complete selection of up to 50,000 sets is required. Narrow larger selections by hosts, level or view. |
| `params_to_excel` | `path` full path of the .xlsx<br>`params` list of parameter names<br>`category?` alias (columns, beams, walls, floors, foundations, rebar, doors, windows, generic_models, ...), UI name ('Structural Columns') or OST_ name<br>`ids?` list of element ids<br>`type?` 'Type name' or 'Family : Type name' (see list_types)<br>`level?` level name<br>`view?` only elements visible in this view<br>`limit?` default 5000<br>`overwrite?` default false<br>`after_id?` nextAfterId from previous export<br>`document_id?` documentId to require | Write element parameters to an .xlsx (one row per element, id first) to edit in Excel and send back with params_from_excel. |
| `params_from_excel` | `path` the edited .xlsx<br>`dry_run?` default false<br>`document_id?` required for legacy workbooks<br>`operation_id?` stable prefix for edit batch receipts | CHANGES THE MODEL: write back the cells that differ from the model in a params_to_excel workbook (one Undo step per 500 elements). |
| `excel_template` | `path` full path of the .xlsx<br>`overwrite?` default false | Write an empty input workbook for excel_to_model (sheets Levels, Grids, Columns, Beams, Walls, Slabs, Footings). |
| `excel_to_model` | `path` the filled .xlsx<br>`dry_run?` default false<br>`document_id?` documentId from ping: refuse if another model is active<br>`operation_id?` request ID (generated when omitted) | CHANGES THE MODEL: create levels, grids, columns, beams, walls, slabs and footings from an excel_template workbook, in ONE edit batch. |
| `list_revit_sessions` | - | Every running Revit with the add-in: pid, version, active model, whether it answers, which one is selected. |
| `select_revit` | `pid?` process id from list_revit_sessions, or 'auto' for the most recent Revit<br>`revit?` version, e.g. '2027', when only one of it runs | Send all following calls to one Revit (e.g. 2027 while 2024 is also open). |

## The `edit` action

Change the model or write files. The server captures the active document before editing. Pass document_id from an earlier ping to require that model. operation_id (generated when omitted) lets a retry after a lost reply return the first result instead of editing twice (reuse it only with the identical request), and get_operation_status reports it. Model edits run in one transaction. Dry runs list first-pass purge candidates and roll back simulated model changes. Saving and exporting run after commit and report their own outcomes in postCommit. Editing needs a ONE-TIME permission per Revit session: the first edit returns at once with permission 'not granted' and opens a Yes/No dialog in Revit (or the user presses Add-Ins > Claude MCP > Allow Editing). Ask the user to click Yes, confirm with get_edit_permission, then send the edit again as a new request.

## Edit operations (97) - items of `operations`

### Levels and grids

| Name | Parameters | What it does |
|---|---|---|
| `level_create` | `elevation`<br>`name?`<br>`plan?` none (default) \| structural \| floor \| ceiling<br>`if_exists?` 'skip' to accept an existing level of that name | Create a level, optionally with its plan view. |
| `grid_create` | `start` [x,y]<br>`end` [x,y]<br>`mid?` [x,y] point on the arc<br>`name?` | Create one grid line: straight, or an arc through start, mid and end. |
| `grid_layout` | `x` list of x positions<br>`y` list of y positions<br>`x_names?` one name per x<br>`y_names?` one name per y<br>`overhang?` how far the lines run past the outer grids; default 2000 mm | Create an orthogonal grid: one line at every x position (named 1, 2, 3 ...) and one at every y position (A, B, C ...). |
| `grid_extents` | `grids?` grid names; default all grids<br>`bottom?` bottom z; default 1 m below the lowest level<br>`top?` top z; default 1 m above the highest level | Stretch grids vertically so they show in the plans of every level (new grids already do); use it when a plan shows no grids. |

### Structure

| Name | Parameters | What it does |
|---|---|---|
| `column_create` | `type` 'Type name' or 'Family : Type name' (see list_types)<br>`level` level name<br>`points` list of [x,y]<br>`rotation?` degrees, counter-clockwise<br>`top_level?` top level name (or give height)<br>`height?` column height when no top_level<br>`base_offset?` from the base level<br>`top_offset?` from the top level<br>`structural?` default true; false = architectural column | Place columns at points, from a base level up to a top level or by height. |
| `beam_create` | `type` 'Type name' or 'Family : Type name' (see list_types)<br>`level` reference level<br>`beams` list of {start: [x,y] or [x,y,z], end: [x,y] or [x,y,z], mid?: [x,y] or [x,y,z] (a point on the arc: makes it curved)}<br>`z_offset?` added to the level elevation when a point has no z<br>`z_justification?` top (default: top of beam at the level) \| center \| origin \| bottom \| family<br>`y_justification?` center (default: centred on the line) \| left \| origin \| right \| family | Create beams (structural framing): straight, curved (mid) or sloped (a z per point). z defaults to the level elevation. |
| `wall_create` | `type` wall type name<br>`level` base level<br>`walls` list of {start: [x,y], end: [x,y], mid?: [x,y]}<br>`top_level?` (or give height)<br>`height?` unconnected height<br>`base_offset?` from the base level<br>`top_offset?` from the top level<br>`structural?` default true<br>`flip?` default false | Create walls on a level (straight, or curved with mid), up to a top level or by height. |
| `floor_create` | `type` floor / foundation-slab type name<br>`level` level name<br>`boundary` list of [x,y] corners, in order<br>`holes?` list of boundaries to cut out<br>`offset?` height offset from the level<br>`structural?` default true<br>`slope_arrow?` {start: [x,y], end: [x,y]}: from the low side towards the high side<br>`slope?` with slope_arrow: percent (2 = 2 %) | Create a floor, structural slab or foundation slab from a closed boundary (the type decides which). |
| `footing_create` | `type` 'Type name' or 'Family : Type name' (see list_types)<br>`level` level name<br>`points` list of [x,y]<br>`rotation?` degrees, counter-clockwise<br>`offset?` height offset from the level | Place isolated footings at points. |
| `wall_foundation_create` | `type` wall-foundation type name<br>`wall_ids` list of wall ids | Add a strip (wall) foundation under walls. |
| `family_place` | `category` alias (columns, beams, walls, floors, foundations, rebar, doors, windows, generic_models, ...), UI name ('Structural Columns') or OST_ name<br>`type` 'Type name' or 'Family : Type name' (see list_types)<br>`level` level name<br>`points` list of [x,y]<br>`rotation?` degrees, counter-clockwise<br>`offset?` height above the level | Place any level-based loadable family (generic model, equipment, furniture ...) at points. |

### Modify

| Name | Parameters | What it does |
|---|---|---|
| `set_parameters` | `ids?` list of element ids<br>`unique_ids?` element UniqueIds instead of ids<br>`values` {parameter selector: value, ...}; prefer bip:/guid:/id: keys from get_element<br>`target?` instance (default) \| type<br>`expected?` compare current values for these keys before writing | Set parameter values on elements (or on their types). Lengths in request units, areas m2, volumes m3, angles degrees, yes/no as true/false; text such as '300 mm' is parsed by Revit; levels, materials, phases and types by name or id. |
| `change_type` | `ids` list of element ids<br>`type` 'Type name' or 'Family : Type name' (see list_types)<br>`category?` default: the category of the first element | Give elements another type. |
| `move` | `ids` list of element ids<br>`dx?`<br>`dy?`<br>`dz?` | Displace elements. |
| `copy` | `ids` list of element ids<br>`dx?`<br>`dy?`<br>`dz?`<br>`count?` default 1 | Copy elements with a displacement; count > 1 makes a linear array. Returns the new ids. |
| `rotate` | `ids` list of element ids<br>`center` [x,y]<br>`angle` degrees, counter-clockwise | Rotate elements about a vertical axis through a point. |
| `mirror` | `ids` list of element ids<br>`axis_start` [x,y]<br>`axis_end` [x,y]<br>`copy?` default true (keep the originals) | Mirror elements about a vertical plane through two points. |
| `delete` | `ids` list of element ids | Delete elements (and whatever depends on them). |
| `pin` | `ids` list of element ids<br>`pinned?` default true | Pin or unpin elements. |
| `rename` | `id` element id<br>`name` | Rename a level, grid, view, sheet, type or other named element. |
| `join` | `pairs` list of [id, id] | Join geometry of element pairs (e.g. slab with column). |
| `unjoin` | `pairs` list of [id, id] | Unjoin geometry of element pairs. |
| `switch_join_order` | `pairs` list of [id, id] | Swap which element of a joined pair cuts the other (changes which one keeps the shared volume). |
| `types_create` | `base_type` 'Type name' or 'Family : Type name' (see list_types)<br>`types` list of {name, values?: {"Parameter name": value}}<br>`category?` alias (columns, beams, walls, floors, foundations, rebar, doors, windows, generic_models, ...), UI name ('Structural Columns') or OST_ name<br>`if_exists?` fail (default) \| skip \| update (sets the values on the existing type) | Create many types from one base type, e.g. a whole column or beam schedule, setting their parameters. |
| `join_overlaps` | `priority?` category order, default foundations, columns, walls, beams, floors<br>`category?` default: the same five<br>`level?` only elements on this level<br>`view?` only elements visible in this view<br>`include_steel?` default false<br>`family_contains?` extra words that mark steel families, e.g. 'STL,SHS'<br>`max_pairs?` per batch, default 1000<br>`max_seconds?` time budget, default 40<br>`resume_after?` resumeAfter cursor from the previous call | Join measured unjoined intersections in scope using the specified cut priority. Coincidence, containment, and unmeasured pairs are left for review. See resolvedByRevit for detach resolutions. |
| `type_duplicate` | `type` 'Type name' or 'Family : Type name' (see list_types)<br>`new_name`<br>`category?` alias (columns, beams, walls, floors, foundations, rebar, doors, windows, generic_models, ...), UI name ('Structural Columns') or OST_ name<br>`values?` {"Parameter name": value, ...} | Duplicate a type under a new name and optionally set its parameters (e.g. a 300x700 beam from a 300x600 one). |
| `family_load` | `path` full path of the .rfa | Load a family file (.rfa) into the model; returns its types. |

### Views, sheets and annotation

| Name | Parameters | What it does |
|---|---|---|
| `view_plan_create` | `level` level name<br>`kind?` structural (default) \| floor \| ceiling<br>`name?` | Create a plan view of a level. |
| `view_3d_create` | `name?` | Create an isometric 3D view. |
| `section_create` | `start?` [x,y]<br>`end?` [x,y]<br>`grid?` grid name (instead of start/end)<br>`element?` element id (instead of start/end)<br>`name?`<br>`flip?` look the other way<br>`depth?` far clip depth behind the cut line; default 1000 mm<br>`bottom?` z; default 1 m below the lowest level<br>`top?` z; default 1 m above the highest level<br>`extend?` extra width beyond each end; default 500 mm<br>`scale?` e.g. 50 | Create a section view along a line, along a straight grid, or through an element. It looks to the left of start -> end. |
| `elevation_create` | `point` [x,y] position of the marker<br>`plan?` plan view name; default: the active view<br>`directions?` list of north \| east \| south \| west; default all four<br>`name?` prefix; views are named '<prefix> - North' ...<br>`scale?` default 100 | Create elevation views from one marker placed in a plan view. |
| `tag_elements` | `category?` one or a list (or give ids)<br>`ids?` list of element ids<br>`view?` view name or id; default: the active view<br>`tag_type?` a specific tag type<br>`leader?` default false<br>`orientation?` horizontal (default) \| vertical<br>`skip_tagged?` default true | Tag elements in a plan, section or elevation with their category's loaded tag; already tagged ones are skipped. |
| `dimension_grids` | `view?` view name or id; default: the active view<br>`grids?` only these grid names<br>`offset?` distance in from the grid start ends, default 1500 mm | Chain dimensions across each set of parallel straight grids in a plan view. |
| `view_activate` | `view` view name or id | Make a view the active one (after the batch). |
| `sheet_create` | `number?`<br>`name?`<br>`title_block?` title-block type; default: the first one; 'none' for an empty sheet | Create a sheet. |
| `viewport_place` | `sheet` sheet number, name or id<br>`view` view name or id<br>`position?` [x,y] on the sheet; default: the centre | Place a view, legend or schedule on a sheet. |
| `schedule_create` | `category` alias (columns, beams, walls, floors, foundations, rebar, doors, windows, generic_models, ...), UI name ('Structural Columns') or OST_ name<br>`fields` list of field names as in Revit's schedule dialog (e.g. Type, Level, Volume, Count)<br>`name?` | Create a schedule of a category with the fields you name. |
| `text_note_create` | `position` [x,y]<br>`text`<br>`view?` view name or id; default: the active view<br>`type?` text type name | Place a text note in a view. |
| `model_lines_create` | `lines` list of {start: [x,y] or [x,y,z], end: [x,y] or [x,y,z], mid?: [x,y] or [x,y,z] (a point on the arc: makes it curved)} | Draw model lines in 3D. |

### Drafting and detailing

| Name | Parameters | What it does |
|---|---|---|
| `drafting_view_create` | `name?`<br>`scale?` e.g. 10; default 20 | Create a drafting view (for details drawn with lines, regions and detail components). |
| `detail_lines_create` | `lines` list of {start: [x,y], end: [x,y], mid?: [x,y] (makes an arc)} or {points: [[x,y], ...], closed?: bool} (a polyline); each may carry style: a line style name<br>`view?` view name or id; default: the active view<br>`line_style?` line style for all lines (see list_detailing kind line_styles) | Draw detail lines in a view: lines, arcs or polylines, with a line style. |
| `region_create` | `boundary?` list of [x,y] corners<br>`boundaries?` outline first, then holes: list of corner lists<br>`circle?` {center: [x,y], radius}<br>`type?` filled region type (see list_detailing); default the model's default<br>`masking?` default false: true makes a masking region<br>`line_style?` boundary line style<br>`view?` view name or id; default: the active view | Draw a filled region (hatch) or a masking region in a view. |
| `detail_component_place` | `type` 'Type name' or 'Family : Type name' (see list_types) of a detail component<br>`points?` list of [x,y] (point-based)<br>`along?` list of {start: [x,y], end: [x,y], spacing}: point-based, repeated along each line, turned to it, centred<br>`lines?` list of {start: [x,y], end: [x,y]} (line-based)<br>`rotation?` degrees, point-based only<br>`view?` view name or id; default: the active view | Place detail components (detail item families) in a view: at points, repeated along lines at a spacing (like a repeating detail), or stretched along lines for line-based ones. |
| `detail_group_place` | `group` detail group name (see list_detailing)<br>`points` list of [x,y]<br>`view?` view name or id; default: the active view (must be the active view) | Place a loaded detail group at points. Revit places detail groups in the active view only. |
| `callout_create` | `view` parent view name or id<br>`min` [x,y] or [x,y,z] one corner<br>`max` [x,y] or [x,y,z] the opposite corner<br>`name?`<br>`scale?`<br>`type?` view type name; default the parent's plan type, or a detail type for sections | Create a callout of a plan or section between two corner points. |
| `view_duplicate` | `view` view name or id<br>`mode?` duplicate (default) \| detailing \| dependent<br>`count?` default 1<br>`names?` one name per copy | Duplicate a view: plain, with its detailing, or as dependent views (legends too). |
| `view_set` | `view` view name or id<br>`name?`<br>`scale?`<br>`detail_level?` coarse \| medium \| fine<br>`discipline?` architectural \| structural \| mechanical \| electrical \| plumbing \| coordination<br>`scope_box?` scope box name, or 'none'<br>`crop?` {min: [x,y], max: [x,y]} in model coordinates; switches the crop on<br>`crop_active?` bool<br>`crop_visible?` bool<br>`view_range?` {top?, cut?, bottom?, depth?}: offsets from each plane's level | Change view settings: name, scale, detail level, discipline, crop region, view range (plans). |
| `legend_create` | `name`<br>`scale?`<br>`from?` legend to duplicate; default the first one | Create an empty legend view. Revit can only do this by duplicating an existing legend (copy one in with views_copy_from when the model has none). |
| `repeating_detail_place` | `lines` list of {start: [x,y], end: [x,y]}<br>`type?` repeating detail type of the sample to copy<br>`model?` another open model that holds the sample<br>`view?` view name or id; default: the active view | Place repeating details along lines. The API has no create method, so an existing placed repeating detail is copied (from this model, or another open model) and reshaped; detail_component_place with 'along' needs no sample. |
| `views_copy_from` | `model` title of the other open model<br>`views?` view names to copy<br>`name_contains?` or: copy views whose name contains this<br>`kind?` all (default) \| drafting \| legend<br>`skip_existing?` default true: skip views whose name exists here | Copy drafting views and legends (with their content) from another model open in this Revit, e.g. a standard detail library. Same-name types keep this model's definition. |

### Documentation

| Name | Parameters | What it does |
|---|---|---|
| `filter_create` | `name`<br>`categories` one category or a list (aliases such as columns, beams, floors, walls, foundations, or Revit names)<br>`rules?` list of {parameter, op?: equals \| not_equals \| contains \| not_contains \| begins_with \| ends_with \| greater \| greater_or_equal \| less \| less_or_equal \| has_value \| has_no_value, value}; numbers in request units / m2 / m3<br>`match?` all (default) \| any<br>`if_exists?` fail (default) \| skip \| update | Create a rule-based view filter (or update one). |
| `filter_apply` | `filter` filter name<br>`views?` view or view template names (default: 'view' or the active view)<br>`view?` view name or id; default: the active view<br>`visible?` default true; false hides what the filter selects<br>`remove?` true removes the filter from the views<br>`color?` "#RRGGBB", [r, g, b] or a name (red, blue ...)<br>`fill?` default false: solid fill in the colour<br>`line_weight?` 1-16<br>`halftone?` bool<br>`transparency?` 0-100 | Add a filter to views or view templates with graphic overrides, or hide what it selects. |
| `color_by_parameter` | `categories` one category or a list (aliases such as columns, beams, floors, walls, foundations, or Revit names)<br>`parameter` parameter name, e.g. Type Mark, Structural Material, Comments<br>`colors?` {value: colour}: fixed colours for some values<br>`prefix?` filter name prefix, default 'Claude - '<br>`view?` view name or id; default: the active view | Colour elements in a view by the values of a parameter: one filter per value with a solid colour fill; returns the colour legend. |
| `graphics_override` | `ids?` list of element ids<br>`categories?` one category or a list (aliases such as columns, beams, floors, walls, foundations, or Revit names)<br>`hide?` true hides, false unhides<br>`reset?` true clears overrides<br>`view?` view name or id; default: the active view<br>`color?` "#RRGGBB", [r, g, b] or a name (red, blue ...)<br>`fill?` default false: solid fill in the colour<br>`line_weight?` 1-16<br>`halftone?` bool<br>`transparency?` 0-100 | Override graphics for elements or categories in a view (colour, line weight, halftone, transparency, fill), hide / unhide them, or reset the overrides. |
| `view_template_create` | `view` view name or id<br>`name` template name | Create a view template from a view's settings. |
| `view_template_apply` | `views` view names<br>`template` view template name, or 'none' | Apply a view template to views ('none' removes it). |
| `dimension_elements` | `ids?` list of element ids<br>`categories?` one category or a list (aliases such as columns, beams, floors, walls, foundations, or Revit names) (elements shown in the view)<br>`direction?` x \| y \| both (default)<br>`offset?` distance of the dimension line beyond the elements; default 1500 mm<br>`include_grids?` default true<br>`view?` view name or id; default: the active view | Dimension elements in a plan: a chain through the outer faces of each element (and grids) along x, y or both, placed beyond the elements. |
| `spot_elevation` | `ids` list of element ids<br>`point?` [x,y] where to place it (default the face centre)<br>`leader?` default true<br>`view?` view name or id; default: the active view | Spot elevations on the top face of elements (slabs, beams, footings ...). |
| `revision_create` | `description`<br>`date?`<br>`issued_by?`<br>`issued_to?`<br>`issued?` bool<br>`sheets?` sheet numbers | Create a revision, optionally added to sheets. |
| `revision_cloud_create` | `revision` revision description, number or sequence<br>`boundary` list of [x,y] corners<br>`view?` view name or id; default: the active view | Draw a revision cloud in a view. |
| `sheet_layout` | `sheet` sheet number or name<br>`views` view, legend or schedule names, in reading order<br>`columns?` items per row; default about the square root of the count<br>`area?` {min: [x, y], max: [x, y]} paper mm from the sheet's lower-left corner (leave out the title strip)<br>`margin?` paper mm, default 20<br>`gap?` paper mm between items, default 15 | Place several views, legends and schedules on one sheet, arranged in rows inside the sheet area. |

### Reinforcement

| Name | Parameters | What it does |
|---|---|---|
| `rebar_column` | `ids` list of element ids<br>`bar` bar type name or diameter in mm (e.g. 20)<br>`tie` tie bar type or diameter<br>`bars_x?` bars on each face along the column's local x, corners included; default 3<br>`bars_y?` bars on each face along local y, corners included; default 3<br>`tie_spacing?` default 200 mm<br>`cover?` to the tie's outer face; default 40 mm<br>`end_zone?` length of the closer tie spacing at each end; default the larger of the long side, height / 6, 450 mm; 0 = none<br>`end_spacing?` tie spacing in the end zones; default the smaller of tie_spacing and 100 mm<br>`top?` auto (default: lap if a column is above, else bend) \| lap \| bend \| none<br>`bottom?` auto (default: footing if a foundation is below) \| footing \| none<br>`lap_d?` lap length in bar diameters; default 50<br>`footing_cover?` default 50 mm | Reinforce structural columns: longitudinal bars on all faces (corners included) and closed ties, closer at both ends. Bars continue above for the lap when a column is above, bend inward at the top otherwise, and stand on the footing mesh with feet when the column sits on a footing. |
| `rebar_beam` | `ids` list of element ids<br>`bottom_bar` bar type or diameter<br>`bottom_count?` default 3<br>`top_bar?` default the bottom bar<br>`top_count?` default 2<br>`stirrup` bar type or diameter<br>`stirrup_spacing?` default 150 mm<br>`cover?` to the stirrup's outer face; default 40 mm<br>`ends?` anchor (default) \| none (bars stop at cover inside the beam)<br>`anchorage_d?` anchorage length in bar diameters; default 40<br>`lap_d?` lap length in bar diameters; default 50<br>`end_zone?` length of closer stirrups at each end; default 2 x beam depth; 0 = none<br>`end_spacing?` stirrup spacing in the end zones; default the smaller of stirrup_spacing and 100 mm<br>`stock_length?` longest bar before a lap; default 12000 mm | Reinforce straight, level beams: bottom and top bars anchored into the supports (straight if the support is deep enough, else with a 90 degree bend; lapped into the next span in line), stirrups closer near the supports, and bars longer than the stock length split with laps. |
| `rebar_footing` | `ids` list of element ids<br>`bar` bar type or diameter<br>`spacing?` default 200 mm<br>`cover?` default 50 mm<br>`top?` default false<br>`top_bar?` default the bottom bar<br>`top_spacing?` default the bottom spacing<br>`end_bends?` bool; bottom bars turned up at the edges (U bars); default false<br>`bend_leg?` longest turned-up leg; default up to under the top mesh<br>`lap_d?` lap length in bar diameters; default 50<br>`stock_length?` default 12000 mm | Reinforce isolated footings (and pile caps): bottom mesh both ways, optional top mesh, optional bottom bars turned up at the edges. |
| `rebar_area` | `ids` slab ids<br>`bar` bar type or diameter<br>`spacing?` default 200 mm<br>`layers?` bottom \| top \| both (default)<br>`direction?` [x,y] major direction; default x<br>`boundary?` list of [x,y] corners; default the slab outline | Area reinforcement in slabs: one bar type at a spacing, bottom, top or both layers, both directions. |

### Modelling

| Name | Parameters | What it does |
|---|---|---|
| `slab_shape` | `id` slab id<br>`points?` list of {at: [x,y], offset: height change (+ up)}<br>`tolerance?` default 50 mm<br>`reset?` bool | Shape a slab by points: move an existing corner (within 'tolerance') or add a point, each by an offset from the slab top; 'reset' makes it flat again. Use floor_create with slope_arrow for a plane slope. |
| `beam_system_create` | `level` level name<br>`type` 'Type name' or 'Family : Type name' (see list_types) of framing<br>`boundary` list of [x,y] corners<br>`direction?` [x,y] beam direction; default x<br>`spacing?` default 1500 mm<br>`justification?` center (default) \| beginning \| end \| directionline | Create a beam system (joists at a fixed spacing) inside a boundary on a level. |
| `brace_create` | `type` 'Type name' or 'Family : Type name' (see list_types) of framing<br>`level` level name (reference level)<br>`braces` list of {start: [x,y] or [x,y,z], end: [x,y] or [x,y,z]}: points without z sit on the level | Create braces (structural framing set as brace) between 3D points. |

### From CAD

| Name | Parameters | What it does |
|---|---|---|
| `columns_from_cad` | `id` id of the CAD file from list_links<br>`layer` the column layer (see get_cad_geometry layers_only), e.g. S-COLS<br>`include?` concrete (default: plain rectangles and circles) \| steel (H / I and other outlines) \| all<br>`types?` {"650x1300": "Type name", ...}: type per outline size (width x depth as listed), checked before the automatic match<br>`tolerance?` size match tolerance; default 10 mm<br>`gap?` curves closer than this belong to one column; default 25 mm<br>`min_size?` default 150 mm<br>`max_size?` default 4000 mm<br>`skip_existing?` default true: skip outlines where a column already stands at the base<br>`level` level name (base)<br>`top_level?` top level<br>`height?` height if no top_level<br>`base_offset?`<br>`top_offset?`<br>`create_types?` base type (with b / h or Width / Depth type parameters): for every size with no type, duplicate it and set the size<br>`type_name?` name of created types with {b} and {h}; default the base name with its size replaced | Model columns from the outlines on a CAD column layer (as cad_columns lists them): each outline gets the type of its size, rotated to match; outlines with no type of their size are reported, not placed. |
| `grids_from_cad` | `id` id of the CAD file from list_links<br>`layer` the grid layer, e.g. S-GRID<br>`min_length?` default 3000 mm<br>`x_names?` names of the north-south lines, west to east<br>`y_names?` names of the east-west lines, south to north<br>`prefix?` put before automatic names, e.g. 'N-'<br>`skip_existing?` default true: skip lines on an existing grid | Model grids from the straight lines on a CAD grid layer: north-south lines numbered 1, 2, 3 ... west to east, east-west lines lettered A, B, C ... south to north (CAD text is not readable, so give names when they differ). |
| `beams_from_cad` | `id` id of the CAD file from list_links<br>`layer` beam edge layer(s), e.g. 'S-BEAM' or 'S-BEAM, S-BEAM-HDLN'<br>`level` level name<br>`z_offset?` top of beam above the level; default 0<br>`depth?` prefer types of this depth<br>`types?` {"300": "Type name", ...}: type per width (mm)<br>`include?` concrete (default) \| steel<br>`create_types?` base framing type: missing widths are made from it (same depth)<br>`type_name?` with {b} and {h}<br>`min_width?` default 150 mm<br>`max_width?` default 1500 mm<br>`min_length?` default 500 mm<br>`tolerance?` width match; default 10 mm<br>`skip_existing?` default true: skip what is already modelled there | Model beams from a CAD beam layer: two parallel edge lines a beam width apart (or two concentric arcs) give one beam on their centre line, top at the level, typed by width; beams already modelled there are skipped. |
| `walls_from_cad` | `id` id of the CAD file from list_links<br>`layer` wall layer(s)<br>`level` level name (base)<br>`top_level?`<br>`height?` if no top_level<br>`base_offset?`<br>`top_offset?`<br>`types?` {"250": "Wall type", ...}: type per thickness (mm)<br>`structural?` default true<br>`min_width?` default 100 mm<br>`max_width?` default 1500 mm<br>`min_length?` default 500 mm<br>`tolerance?` default 10 mm<br>`skip_existing?` default true: skip what is already modelled there | Model walls from a CAD wall layer: two parallel face lines (or concentric arcs) give a wall on their centre line, typed by thickness; walls already modelled there are skipped. |
| `slabs_from_cad` | `id` id of the CAD file from list_links<br>`layer` slab outline layer(s)<br>`level` level name<br>`type` floor / slab type<br>`mode?` closed (default): each closed outline is a slab \| outer: the outer outline of everything on 'layer' (give the slab edge, beam and column layers together) is one slab per separate area<br>`opening_layer?` mode outer: closed outlines on this layer inside the slab become openings (e.g. CE-VOID)<br>`cell?` mode outer: grid size; default 25 mm<br>`preview?` mode outer: draw the outlines as model lines on the level instead of making slabs (to check them first)<br>`offset?` height offset from the level<br>`min_area?` m2; default 1<br>`gap?` largest gap bridged between edge pieces (dashed edges, edges broken at columns); default 5 mm (closed) / 500 mm (outer)<br>`structural?` default true<br>`skip_existing?` default true: skip what is already modelled there | Model slabs from closed outlines on a CAD layer (polylines, circles, or lines / arcs meeting end to end); outlines inside a slab outline become its openings. Outlines where a slab of that level already stands are skipped. |

### Sheets

| Name | Parameters | What it does |
|---|---|---|
| `sheets_from_views` | `views?` view names<br>`view_type?` or: plan \| FloorPlan \| EngineeringPlan \| Section \| Elevation \| ThreeD ...<br>`name_contains?` with view_type: only views whose name contains this<br>`exclude_contains?` leave out views whose name contains any of these (e.g. ['Copy'])<br>`number?` first sheet number; default S-101<br>`name_template?` default '{view}'; also {level} and {number}<br>`title_block?` default: the one used most<br>`view_template?` apply this view template to each view<br>`scale?` e.g. 100 (views without a template)<br>`skip_placed?` default true | Make a sheet set: one sheet per view, numbered from 'number' upwards (S-101, S-102 ...; used numbers skipped), named from a template, the view centred on the title block. Views already on a sheet are skipped. |

### Materials

| Name | Parameters | What it does |
|---|---|---|
| `fix_materials` | `steel_material?` material for steel members carrying a non-metal material (e.g. 'KTR-S-ST-52/3')<br>`concrete_material?` material replacing Revit default materials (Concrete, Cast-in-Place gray, Default ...) on non-steel elements<br>`rules?` [{material, problem?: any \| steel \| default \| none, category?, family_contains?, type_contains?, level?, from_contains?: current material name contains}, ...]; checked in order, first match wins, after the two shortcuts<br>`category?` default the structural categories<br>`level?` only this level<br>`family_contains?` extra words that mark steel families, e.g. 'STL,SHS' | Material fixer: give elements the right material by rule (check_materials lists the problems). Loadable families (columns, beams, footings) get Structural Material on the type when the whole type is fixed, else per element; slab / wall types get the matching layer material changed, only when every element of the type is fixed. |
| `family_material_set` | `families?` family names<br>`family_contains?` or: families whose name contains this (e.g. 'BUILTUP')<br>`material` material name in the model<br>`category?` default structural columns, framing and foundations | Set the material of every solid in the chosen families to one material (no parameter link), e.g. all built-up steel families to KTR-S-ST-52/3; the families are loaded back. Use it when the grade is fixed per family. Runs after the commit. |
| `family_material_link` | `families?` family names<br>`family_contains?` or: families whose name contains this (e.g. 'S-STL')<br>`category?` default structural columns, framing and foundations | Fix families whose solids carry a fixed material: open each family, link its solids' material to Structural Material, load it back (project type values kept), so the element's material is what the geometry shows. Runs after the commit; a dry run lists the families. |

### Model management

| Name | Parameters | What it does |
|---|---|---|
| `parameter_add` | `name?` parameter name<br>`names?` several names (same settings)<br>`categories` one category or a list (aliases such as columns, beams, floors, walls, foundations, or Revit names)<br>`binding?` instance (default) \| type<br>`data_type?` text (default) \| multiline \| url \| number \| integer \| yes_no \| length \| area \| volume \| angle \| material<br>`group_under?` properties-palette group label, e.g. Identity Data, Constraints, Structural; default Data<br>`shared_file?` full path; default the model's shared parameter file, or the bridge's own file if none is set<br>`file_group?` group in the file for new definitions; default 'Revit MCP'<br>`create_if_missing?` default true | Add shared parameters to the model and bind them to categories. Takes each one from the shared parameter file (the model's, or 'shared_file'), adding it there first when missing; if one is already bound, its categories are extended. |
| `parameter_remove` | `name?` parameter name<br>`names?` several names | Remove project / shared parameters from the model (their values go with them). Use dry_run first. |
| `purge` | `categories?` one category or a list (aliases such as columns, beams, floors, walls, foundations, or Revit names); default everything<br>`keep_ids?` list of element ids to keep<br>`keep_names?` keep anything whose name contains one of these words<br>`passes?` default 3<br>`limit?` names listed, default 200 | Purge unused families, types, materials, patterns and styles, in several passes (Revit 2024 and later). Checks cascaded deletions against the keep lists. Counts all removed IDs. A purge dry run lists first-pass candidate IDs and names without deleting. Cascades and later passes are unknown. |
| `standards_copy` | `model` title of the other open model<br>`kind?` one kind<br>`kinds?` view_templates \| filters \| text_types \| dimension_types \| fill_patterns \| line_patterns \| materials \| wall_types \| floor_types \| title_blocks \| line_styles (list)<br>`names?` only these names<br>`name_contains?` only names containing this | Copy standards from another open model (template or library): view templates, filters, text / dimension types, fill / line patterns, materials, wall / floor types, title blocks, line styles (recreated by name with weight, colour and pattern). Names already in this model are kept, not overwritten. |
| `workset_create` | `name?` workset name<br>`names?` several names | Create worksets (workshared models only). |
| `workset_set` | `ids` list of element ids<br>`workset` workset name | Move elements to a workset (workshared models only; elements owned by another user are refused). |
| `phase_set` | `ids` list of element ids<br>`created?` phase name<br>`demolished?` phase name, or 'none' to clear | Set the phase created and / or demolished of elements. |

### Warnings

| Name | Parameters | What it does |
|---|---|---|
| `fix_warnings` | `kinds?` default all fixable kinds: joined_disjoint<br>`ids?` only warnings involving these elements | Fix Revit warnings that have one safe fix: 'joined but do not intersect' pairs are unjoined (no quantity change). Every other warning is listed under notFixed, by message, to review. |

### Openings

| Name | Parameters | What it does |
|---|---|---|
| `opening_create` | `host` id of the slab / roof / beam / wall<br>`boundary?` slabs, beam top: list of [x,y] corners<br>`center?` [x,y] circle centre; beam web: [x,y] or [x,y] or [x,y,z] (z defaults to mid depth), projected on the beam axis<br>`radius?` circle radius<br>`face?` beams: 'web' (default, horizontal sleeve) \| 'top' (vertical hole)<br>`width?` beam web rectangle: length along the beam<br>`height?` beam web rectangle: height<br>`start?` walls: [x,y] or [x,y,z] lower-left corner (or plan point with bottom/top)<br>`end?` walls: [x,y] or [x,y,z] upper-right corner<br>`bottom?` walls: bottom z<br>`top?` walls: top z<br>`perpendicular?` default true: cut square to the face | Cut an opening in a slab or roof (rectangle/polygon or circle), a sleeve through a beam web or a hole through its top, or a rectangular opening in a wall. |
| `shaft_create` | `bottom_level` level name<br>`top_level` level name<br>`boundary?` list of [x,y] corners<br>`center?` [x,y]<br>`radius?`<br>`base_offset?`<br>`top_offset?` | Create a shaft opening through every slab between two levels. |

### Links

| Name | Parameters | What it does |
|---|---|---|
| `cad_link` | `path` full path of the .dwg / .dxf<br>`mode?` link (default) \| import<br>`view?` view name or id; default: the active view<br>`this_view_only?` default false (visible in all views)<br>`placement?` origin (default: internal origin to internal origin) \| centered \| shared (shared coordinates)<br>`cad_units?` auto (default) \| mm \| cm \| m \| ft \| in - give them when the DWG is unitless<br>`colors?` preserve (default) \| black_white \| invert<br>`visible_layers_only?` default false<br>`pin?` default true | Link (or import) a DWG / DXF file. |
| `revit_link` | `path` full path of the .rvt<br>`placement?` origin (default: internal origin to internal origin) \| centered \| shared (shared coordinates)<br>`pin?` default true | Link another Revit model. |

### Export and save

| Name | Parameters | What it does |
|---|---|---|
| `export_dwg` | `folder` full path of the output folder (created if missing)<br>`overwrite?` default false: an existing file makes the batch fail<br>`views?` list of view names / sheet numbers / ids; default: the active view<br>`sheets?` sheets by number or name with * wildcards, e.g. 'S-1*', '*', or a list; overrides views<br>`name_template?` file name per view: {number} {name} {revision} {project_number} {model} {date}; default '{number} - {name}' for sheets, '{name}' for views | Export views / sheets to DWG (one file per view, named by name_template). |
| `export_ifc` | `folder` full path of the output folder (created if missing)<br>`name?` file name without extension; default: the model name<br>`overwrite?` default false: an existing file makes the batch fail<br>`version?` IFC2x3CV2 (default), IFC4, IFC2x3, IFC4RV, ...<br>`view?` export only what this view shows | Export the model to IFC. |
| `export_pdf` | `folder` full path of the output folder (created if missing)<br>`name?` file name without extension; default: the model name<br>`overwrite?` default false: an existing file makes the batch fail<br>`views?` list of view names / sheet numbers / ids; default: the active view<br>`sheets?` sheets by number or name with * wildcards, e.g. 'S-1*', '*', or a list; overrides views<br>`combine?` default true, or false when name_template is given<br>`name_template?` file name per view: {number} {name} {revision} {project_number} {model} {date}; default '{number} - {name}' for sheets, '{name}' for views | Export views / sheets to PDF: one combined file, or one file per sheet named by name_template. |
| `export_nwc` | `folder` full path of the output folder (created if missing)<br>`name?` file name without extension; default: the model name<br>`overwrite?` default false: an existing file makes the batch fail<br>`view?` a 3D view; default: the whole model | Export to Navisworks (.nwc); needs the Navisworks exporter installed. |
| `export_image` | `folder` full path of the output folder (created if missing)<br>`name?` file name without extension; default: the model name<br>`overwrite?` default false: an existing file makes the batch fail<br>`views?` list of view names / sheet numbers / ids; default: the active view<br>`width_px?` default 2000 | Export views to PNG images. |
| `export_schedule` | `schedule` schedule name or id<br>`folder` full path of the output folder (created if missing)<br>`name?` default: the schedule name<br>`overwrite?` default false: an existing file makes the batch fail | Export a schedule to CSV. |
| `save` | - | Save the model. |
| `save_as` | `path` full path ending in .rvt<br>`overwrite?` default false: an existing file makes the batch fail | Save the model under a new path (not for workshared models). |
