# Revit MCP implementation log

## 2026-11-24 · Version 0.17.0 (phases 8 and 9: modify / documentation depth, standards and data)

New files: `plugin/Edit/DepthOps.cs` (flip, end_join, family_place_hosted, group_create / group_place / ungroup,
assembly_create / assembly_views, schedule_set, dimension_rotated / angular / radial, spot_coordinate),
`plugin/Read/DepthRead.cs` (list_groups, list_assemblies, get_geometry), `plugin/Edit/DataOps.cs` (global parameters,
material_asset_set), `plugin/Read/DataRead.cs` (list_global_parameters, get_material, list_keynotes),
`tests/live_p89.py` + `run_live_p89_task04.bat` / `run_live_p89_scratch.bat`, `package/check_user.ps1`.
Changed: ExportOps (DWF, FBX, gbXML, DGN), FixOps (duplicate_instances, overlapping_lines), DocOps (shared
PointOnFace for spot_elevation / spot_coordinate), EditEngine, Registry, catalogue (45 reads, 118 edit operations).

API checked first in both RevitAPI.dll files (metadata dump): RadialDimension.Create exists only in 2027; no API
creates spot slopes; UNIFORMAT_CODE is missing in 2027 (looked up by name); EnergyAnalysisDetailModelOptions and
EnergyDataSettings.GetFromDocument are obsolete in 2027 (guarded). Both years 0 warnings, 0 errors.

**Live test, Revit 2024.** The running Claude Desktop still served the 0.16 server exe (it refuses names it does not
know), so the tests call the add-in directly through `server/bridge_client.py` from a script started by double-click.
Task 04: reads and dry runs only (refused unless the active model is Task 04). Scratch: real runs in a new, never
saved project (refused otherwise); closed without saving. Each run ended with Task 04 unchanged (baseline counts,
no unsaved changes, 1090 purgeable, 273 rebar sets / 5335.9 kg).

| Area | Result |
| --- | --- |
| get_geometry | Column 2187231 original 0.765 m3 = 0.3 x 0.6 x 4.25, edges 20600 mm; as modelled 0.7108 m3 (slab cut). Scratch wall (6000 - 127) x 0.254 x 3.0 = 4.4752 m3 |
| list_groups / list_assemblies / list_keynotes / get_material / list_global_parameters | Task 04: 11 group types; keynote file RevitKeynotes_Metric.txt, 3840 entries; _CONCRETE-RC has only an appearance asset |
| flip | Wall flipped; column refused by its family; missing 'what' refused |
| end_join | Beam joins disallowed both ends + 150 start extension; wall end; extension on a wall refused |
| family_place_hosted | Refuses non-work-plane families with their kind (Task 04 has none to place) |
| groups | Create + place 2 copies; ungroup (real); list_groups after: 2 instances x 2 walls |
| assemblies | Create (name applied after commit: Revit makes the type at commit), views 3D / front / parts list (real) |
| schedule_set | Filter Length > 4500, sort with header, m 2 decimals, totals: three 6.00 m rows, total 18.00 |
| dimensions | Rotated 0 / 90 deg; 30 deg refused (no square faces); angular A / 2 = 90 deg at (2500, 0); radial refused on 2024 |
| spot_coordinate / spot_elevation | On the L-shaped slab 1739650 after the PointOnFace fix (box centre fell outside the face) |
| fix_warnings | All kinds dry run: 48 warnings, nothing fixable in Task 04; unknown kind refused |
| global parameters | Create length / yes_no / text, formula 1000 mm + 500 mm = 1500; drives column Top Offset (dry) and wall Base Offset 200 -> 350 (real). Wall Unconnected Height refused by Revit |
| material_asset_set | Revit 2024 refuses SetStructuralAsset on existing assets ("internal error"): edited assets are always new PropertySetElements; shared asset copied for the material (density 2500, E 27000, conductivity 1.6 read back), in place with change_shared (old asset replaced, name kept, all four users moved); render colour on a Generic asset #3366CC read back; non-Generic refused |
| exports | DWF (105 KB) and FBX (93 KB) written; DWF needs a modifiable document (rolled-back transaction). DGN failed without a DGN setup in the model (seed parameter added, untested). gbXML failed: no compatible energy analysis model, even with the analysis mode and model made inside the rolled-back transaction |

Several Revit sessions (from Mohamed's report, "two Revits mess it up"). Faults in 0.17.0 and before: every Revit
overwrote bridge.json at startup, so a second Revit silently took all calls and edits; closing it deleted bridge.json
and left "Revit is not reachable" while the first was still open; crashed Revits left session files (three on the
laptop, from 20 and 23 Nov). Fix: the add-in writes bridge.json only when no running Revit owns it, hands it to another
running Revit when the default closes, and removes session files whose Revit is not running; the server uses the
only listening Revit whatever bridge.json says, sends reads to the default when several listen (ping lists the others)
and refuses edits until select_revit. Smoke test: two fake add-ins (both open, crash of each). Live, Revit 2024 started
twice from Explorer (tests/live_two_revits.py via run_two_revits_check.bat):

| Stage | Result |
| --- | --- |
| No Revit, before | 3 stale session files, no bridge.json, "not reachable" |
| A started, Task 04 open | Stale files removed; default = A; one session |
| B started, new Project1 | Default still A (Task 04); ping lists B; edit refused "2 Revit sessions are open" |
| A closed | bridge.json handed to B; automatic calls reach Project1 (also through the old server in Claude Desktop) |
| B closed | No files left |

Installer (from Mohamed's report): on a domain controller install.bat installed into C:\Users\mohamed.hamed. The
scripts use %APPDATA% / %LOCALAPPDATA% / %USERPROFILE% of the account that runs them; an elevated window under
another account installs there. install.bat / uninstall.bat now print the account and stop when it is not the
owner of the desktop session (check_user.ps1, explorer.exe owner); /this_user overrides. Tested only for the normal
case (same user) here; the domain controller case needs a run there.

## 2026-11-24 · Version 0.16.0 (phase 6: read elements inside linked Revit models)

| File | Change |
| --- | --- |
| `plugin/Core/LinkScope.cs` (new) | Resolves `link` (instance id, link type id placed once, instance name or file name with or without .rvt), needs a loaded link, `coordinates` host / link, transform output (origin, plan rotation, tilted, mirrored) and the `link` block every result carries |
| `plugin/Core/Describe.cs` | `Location` / `Box` take an optional transform; host output unchanged (no transform = identity path) |
| `plugin/Read/ElementRead.cs` | get_elements, get_element read the link's document; `view` with link uses Revit's host-view link collector (`FilteredElementCollector(host, viewId, linkId)`, present in the 2024 API) |
| `plugin/Read/ModelRead.cs` | count_by_category, list_types, list_levels (elevation + linkElevation), list_grids with link |
| `plugin/Read/LinkRead.cs` | list_links: path, attachment, nested, loaded, document, workshared, transform |
| `server/catalog.py`, `README.md` (4.1a and section 5), `docs/ACTIONS.md`, `CHANGELOG.md`, `ROADMAP.md`, version 0.16.0 | Wording, workflow, status |

Compile check in the cloud workspace this time: .NET 10 SDK and Mono 4.8 reference assemblies from the Ubuntu
archive (nuget.org is blocked there), two small projects against `refs\2024` and `refs\2027`: 0 warnings, 0 errors
both years. Laptop `build_package.bat`: both years 0 / 0, `RESULT=OK`; `install.bat` `RESULT=OK`.

**Live test, Revit 2024 (24.3.60.12), bridge 0.16.0.** Task 04 has no Revit links, so the host was a new unsaved
scratch project (Project1, Imperial Multi-discipline template) with Task 04 linked into it. Task 04 itself was not
opened or edited during the link tests. Real edits only in Project1: revit_link (origin to origin, unpinned), move
(10000, 5000), rotate 30 deg about (10000, 5000), view_3d_create LinkTest3D. Project1 was closed without saving.

| Check | Result |
| --- | --- |
| list_links | path, Loaded, Overlay, document title, transform origin [0,0,0] rotation 0; after the move / rotate [10000,5000,0], 30 |
| count_by_category link=<instance id> | 27 columns, 64 framing, 21 walls, 7 floors, 33 foundations, 273 rebar = Task 04 baseline |
| get_elements link=<file name without .rvt>, identity | Column 2187231 (8600, -6050), rot 90; beam 1723276 (8400,0)-(8400,-5950), Mark B6: same as Task 04 itself |
| get_elements link=<type id>, moved + rotated | Column (20472.819, 4060.546), rot 120; beam (17274.613, 9200)-(20249.613, 4047.149), length 5950: equal to the hand calculation R30 * p + (10000, 5000) |
| coordinates=link | Original link coordinates returned |
| list_levels / list_grids link | 4 link levels (elevation and linkElevation, no vertical offset); grid 2 rotated 30 deg, length 21347.629 unchanged |
| level + type filters with link | Link level / type names (FOUNDATION PLAN, COL-RC-RECT 300X600); the host still answers with its own levels (L1, L2) |
| get_element link | Full parameters of beam B6 from the link, location / box transformed |
| view with link | Plan L1: 0 (link elements at about 98.5-106.9 m, outside L1's view range: correct). 3D view: 428 physical elements, the sheets / legend components of the unfiltered count drop out |
| Errors | Unknown link lists the links with ids; coordinates=shared refused with the allowed values |
| Fix from the test | list_types with link now carries the `link` block |
| Rebuild + reinstall with the fix, then regression on Task 04 (reads only, opened from Recent) | ping 0.16.0; get_elements 2187231 / 1723276 without link: same location and box as before the change; takeoff columns 27 / 18.614 m3; rebar_takeoff 273 sets, 5335.9 kg; model_health 60594 elements, 1090 purgeable; list_links note null with no Revit links; link on a model without links: "This model has no Revit links."; get_project_info baseline counts, hasUnsavedChanges false |

Not covered live: a link that is not loaded (needs link unload, a later phase-6 item), a tilted or mirrored link,
Revit 2027.


## 2026-11-23 · Version 0.14.0 (installer package)

- Modelled on civil3d-mcp's build_package.bat / package\install.bat. Added: automatic client configuration
  (configure_clients.ps1: Claude Desktop JSON merge, Codex TOML table replace, backups) and Unblock-File on the unzipped
  package (Revit 2024 refuses DLLs carrying the Zone.Identifier mark).
- build_package.bat passes RevitDir with a doubled trailing backslash so the quoted argument is not escaped.
- The server exe bundles the lazily imported modules (excel_io, qs_report, bbs_report, xlsx_util) as hidden imports.

## 2026-11-23 · Live purge, Undo and rollback checks on 0.13.3

Updated this log to record follow-up runtime coverage. The tests used disposable local copies. No source code changes were needed.

| Revit build and model | Operation | Verified result |
| --- | --- | --- |
| 2024, 24.3.60.12, Task 04 | Mixed preview, receipt `397b87eb3c7f443d8350eeb1e14b5dca` | Wrote a temporary Comments value to wall 2121504 and enumerated 1,090 purge candidates. Revit reported rollback. The original blank value, candidate count and unmodified document flag were verified afterward. |
| 2024, same copy | Single-pass materials purge, receipt `1d4d866dd7ae43d8bdbca81bf38a7592` | Preview listed 182 unused materials. Protected the other 181 by ID and committed deletion of 1222129 (`_Crystaline Waterproofing`). The response reported one deletion and no failures. Reading the IDs showed 1222129 missing and protected ID 1222130 present. Revit Undo restored the original ID and name. Model health again reported 1,090 candidates, 60,594 non-type elements and no unsaved changes. |
| 2027, 27.4.0.31, Pacific Continental Residence sample | Standalone preview, receipt `829150684778433d98af33f29f2e1ae8` | Returned 938 candidates, zero deletions and transaction status `NotStarted`. The sample remained unmodified. |
| 2027, same sample | Mixed preview, receipt `5c1ee269e33146dab29aeaaa5c3cf025` | Changed one wall's Comments parameter (ID 1321999) and enumerated 938 candidates. After rollback the Comments readback and candidate count matched the baseline, the sample remained unmodified, and Revit answered a final ping. |

The first 2027 launch encountered an incorrect-schema message while opening the Task 04 copy (journal.0027.txt). The 2027 results above cover the installed Autodesk sample. Task 04 compatibility with 2027 remains unverified. The matching bridge was deployed to the Revit 2027 user Addins directory for these checks.

## 2026-11-23 · Read-only purge preview, 0.13.3

| File | Change and reason |
| --- | --- |
| `plugin/Edit/ManageOps.cs` | Replaced simulated deletion during purge previews with first-pass candidate enumeration. Reuses category, ID, name and limit options. Reports zero deletions and distinguishes candidates from the unknown effects of cascading deletions and later passes. |
| `plugin/Edit/EditEngine.cs` | Handles purge-only previews without an edit prompt, transaction, regeneration or rollback. Mixed previews use the same read-only purge implementation inside the batch. Updated result text to describe temporary IDs accurately. |
| `plugin/BridgeInfo.cs`, `server/catalog.py` | Set bridge release to 0.13.3 and updated the client-facing purge and dry-run descriptions. |
| `plugin/Bridge/BridgeServer.cs`, `server/bridge_client.py` | Advertise and require protocol 4 for editing so the updated client cannot send a purge preview to the crash-prone protocol-3 implementation. |
| `tests/fake_bridge.py`, `tests/smoke_test.py` | Updated the simulated bridge to protocol 4 and added a regression case for refusing edits on protocol 3. |
| `README.md`, `CHANGELOG.md`, `docs/ACTIONS.md` | Updated deployment and preview guidance, recorded the fix, and regenerated the action reference. |
| `docs/IMPLEMENTATION_LOG.md` | Recorded the changes, live receipts and verification scope for this release. |

Both Revit targets built with zero warnings and errors. The MCP smoke suite passed including rejection of edits on protocols 2 and 3. Deployed the 2024 DLL and manifest to the user Addins directory. Created a fresh temporary copy of Task 04 and opened it in Revit 2024 build 24.3.60.12, PID 22028. Confirmed bridge 0.13.3 and protocol 4 before testing.

Default purge preview receipt `89971002e43249fdbd9feee9c693e23b` returned 1,090 candidates and zero deletions. Receipt `dcdbe07c5e564ffc97e0081a4c86981e` protected one candidate ID and returned 1,089, with the output limited to one ID. Both reported transaction status `NotStarted`. A subsequent health read returned the original 1,090 candidates, 60,594 non-type elements and identical warning results. The same document reported no unsaved changes and responded to a final ping. Live coverage was limited to standalone previews in Revit 2024. No purge was committed.

## 2026-11-23 · Revit 2024 live purge regression, 0.13.2

Deployed the workspace add-in to the Revit 2024 user Addins folder. The existing process retained its loaded protocol-2 DLL. A second Revit 2024 process (PID 35336) loaded protocol 3 and opened a temporary non-workshared copy of the saved Task 04 model. The model-health read returned 1,090 first-pass purge candidates.

With editing granted, one `purge` operation using defaults and `dry_run: true` reproduced the native crash. Receipt `8a8a20365abd42948b951eacdf38e707` reached `preview_regenerate` and then `preview_rollback` with no regeneration error. At 15:16:59 Cairo time the journal recorded the assertion in `ContentRec.cpp` line 924 while finishing the purge transaction. Revit wrote `journal.0141.0001.dmp` and the socket closed before a result. Journal lines 2975–2997 also record family/group modification restrictions and removal of the last copies of three detail groups. The exact offending element is unknown.

The test establishes that a standalone purge preview suffices on this model. The original Revit window remained open. Follow-up work should replace the destructive simulation for purge previews with read-only candidate enumeration, clearly marking cascading and later-pass impacts as unknown.

## 2026-11-23 · Version 0.13.2

Changes in the **Revit MCP - ChatGPT** workspace, based on 0.13.1. Four files were added. The remaining entries describe edits. No source files were removed.

| File | Change and reason |
|---|---|
| `plugin/Edit/ManageOps.cs` | Isolated each purge candidate and phase update in a subtransaction. Purge checks the actual deletion set against protected IDs and names, includes cascades in its count, and returns deleted IDs up to the requested limit. Phase writes validate both fields, check setter outcomes and count only committed changes. Each parameter name gets a fresh category set, preserving all categories already bound to that definition. Ambiguous definitions, mismatched shared GUIDs and refused rebindings report an error. A preview with no configured shared file works in a temporary file. |
| `plugin/Edit/EditContext.cs` | Centralized transaction startup so Revit installs the preprocessor after starting. Removed automatic default resolution for failures that share one element with a batch join. Detach requires the specific cut failure, the exact pair registered by the operation, and a permitted detach resolution. Other errors reach the rollback report. |
| `plugin/Edit/EditEngine.cs` | Applied corrected transaction startup to project edits. Added audit checkpoints before each operation, final regeneration and final rollback during management previews, to help locate a native Revit exit. |
| `plugin/Edit/FamilyOps.cs` | Applied the same transaction startup to family edits so their warnings and errors reach the family report. |
| `plugin/Core/Find.cs` | Replaced value-based guessing for duplicate parameter names with explicit column family/schedule level and offset pairs and the sheet/viewer sheet-number pair. Other ambiguous display names return the available stable selectors. |
| `plugin/Core/Geo.cs` | Requires both elements of an overlap to pass the view, level and category scope. Failed measurements go to the review list. A measured zero and a deliberately omitted measurement retain their existing meanings. |
| `plugin/Edit/ModifyOps.cs` | Updated the auto-join loop comment to reflect its shared review classification. Specific pairs can still be joined using the explicit `join` operation. |
| `plugin/Read/OverlapRead.cs`, `server/qs_report.py` | Updated overlap explanations, QA headings and warnings so that failed measurements appear alongside coincidence and containment cases. |
| `plugin/Core/BarStock.cs` **(new)** | Calculates straight extents for stock pieces, reserves both bend legs, and distributes laps within each piece’s available length. Rejects impossible inputs and requests exceeding 2,000 pieces per bar line. This replaces the fallback that returned a single overlength bar. |
| `plugin/Edit/RebarOps.cs` | Uses the existing deduplicated element resolver for all four placement tools. Both ends of each beam layer use that layer’s diameter for side cover. Beam and footing bars use the stock calculation above. |
| `server/bbs_report.py` | Refuses to save a bar bending schedule if the read was truncated or the returned row count disagrees with the selection count. This keeps workbook detail and summary totals on the same scope and preserves an existing destination on error. |
| `server/bridge_client.py`, `plugin/Bridge/BridgeServer.cs` | Direct edits without an explicit target capture the active document on the pinned connection. The add-in requires the target at execution, and the client includes it in the receipt or uncertain-delivery message. Changed the editing protocol to 3 so an older add-in cannot silently accept incompatible writes. |
| `plugin/RevitMcpBridge.csproj`, `tools/deploy_addin.ps1` **(new script)** | Replaced the move-before-copy build target with a serialized installer that stages the DLL and manifest, restores prior files on a replacement error, and cleans released backups after successful deployment. |
| `tests/fake_bridge.py`, `tests/smoke_test.py` | Updated the simulated bridge protocol and added regressions for a document switch between capture and execution, refusal of protocol 2, and preservation of an existing workbook on incomplete reinforcement reads. Updated the quantity-report assertion. |
| `tests/bar_stock_test.ps1` **(new)** | Exercises the production C# calculation with straight and bent bars, exact stock fit, long laps and impossible inputs. Checks piece lengths, overlap and full run coverage. |
| `tests/deploy_test.ps1` **(new)** | Uses temporary Windows files to test first install, a manifest locked after DLL replacement, restoration of the previous DLL and manifest, and successful retry while the original DLL is held open. |
| `plugin/BridgeInfo.cs`, `server/catalog.py`, `docs/ACTIONS.md` | Bumped the release to 0.13.2 and updated tool help for targeting, protected purge and complete reinforcement schedules. Regenerated the action reference. |
| `README.md`, `CHANGELOG.md` | Updated installation, active-document targeting, retry and overlap instructions and recorded the release changes. |
| `claude_config_snippet.json`, `codex_config_snippet.toml` | Regenerated the examples for this workspace. The previous examples launched the separate Desktop checkout. Both examples were parsed and their launch paths verified. |
| `docs/IMPLEMENTATION_LOG.md` | Recorded the affected files, reasons, verification, and remaining live-model checks. |

**Verification.** Both Release targets built against the installed Revit 2024 and 2027 APIs with zero warnings and errors. Each build deployed to its own temporary directory and the installed DLL and manifest hashes matched their sources. The stock calculation, Windows deployment recovery, and simulated single-tool and split-tool MCP tests passed. The 6 m straight run with 2 m stock and a 1 m lap produced five pieces.

**Live validation.** Revit was closed during verification. Actual model transactions, native failure processing, and reinforcement geometry need a detached-model run. The earlier Revit 2024 journal recorded a native assertion while finishing the combined management preview. It could not identify the responsible operation. The checkpoints support running `parameter_add`, `phase_set`, `standards_copy` and `purge` individually before repeating the combined preview. This repair was tested using temporary add-in locations. The installed add-ins and active client configuration were not updated.

## 2026-11-23 · Version 0.13.1 (fixes from the first 0.13.0 live test)

- Live on Task 04 (Revit 2024, pid 18852): reinforcement real run verified except the beam's far end (L-corner with
  beam 1722620, whose box y 1100-1300 overlaps the beam end; the probe 50 mm past the end face found nothing).
- Revit closed during the dry-run batch [parameter_add, phase_set, standards_copy (view_templates, filters,
  line_styles, text_types) from the detached model, purge]. Audit log / journal not readable from here. Line styles
  were copied as GraphicsStyle elements across documents: replaced by recreating sub-categories. Next test runs
  each operation as its own dry run so a failure points at one operation.
- Reads verified: model_health (45 MB, 48 warnings, 255 imported CAD, 1090 purgeable), list_parameters (123, 113
  shared; shared file KTR15-G26-S.txt), list_phases, list_worksets (not workshared message).

## 2026-11-23 · Version 0.13.0 (roadmap phase 5: model management)

- New files: Read/ManageRead.cs (model_health, list_parameters, list_worksets, list_phases), Edit/ManageOps.cs
  (parameter_add, parameter_remove, purge, standards_copy, workset_create, workset_set, phase_set).
- API checked in both versions: Document.GetUnusedElements, BindingMap.Insert/ReInsert(def, binding, ForgeTypeId),
  ExternalDefinitionCreationOptions(name, ForgeTypeId), WorksharingUtils.GetCheckoutStatus(out owner), Workset.Create.
- DetailOps.UseDestinationTypes made internal and shared with standards_copy.
- Not tested live yet; built together with 0.12.0 reinforcement so both are tested in one session.

## 2026-11-23 · Version 0.12.0 (reinforcement detailing)

- 0.11.4 live: beam 1722045 bars 720 = cut length 800 - 2 x 40, top bars at the true top, stirrups 120 x 920. Verified.
- Added anchorage / bends / laps / stock splitting / end zones (see CHANGELOG). Supports found with a bounding-box
  point probe 50 mm past each end face at mid-depth; support depth from LocalBox of the support in the beam frame.
- Column feet point outward and are capped to stay inside the footing (min 4 x diameter, else no foot).
- Tie / stirrup zones: three sets (end, middle, end); a member shorter than 2 zones + 2 spacings gets one set at the end spacing.

## 2026-11-23 · Version 0.11.4 (reinforcement retest)

- 0.11.3 live: column 1712923 bars 103482-106818 (full 3400 height, 40 cover) - fixed. Footing still correct.
- Beam 1722045: vertical position right (top bars 54 mm to centre from top) but bars 495 mm and offset: framing
  original geometry is at the family default length. LocalBox now takes local x (along the beam) from the joined solid.
- Revit warned "placed completely outside of its host" for the top bars: the slab cuts the beam top, so the top
  bars are in the slab zone. Expected; reported as a note.

## 2026-11-23 · Version 0.11.3 (reinforcement real-run test)

- Real run on Revit Task 04 (column 1712923 300x600, beam 1722045 200x1000, footing 2158885 F3), then deleted, not saved.
- Footing meshes correct: 50 mm cover, bottom T16@200 both ways, top T12@250 both ways.
- Column and beam bars used the joined (cut) solid: column bars ended 232 mm below the top, beam top bars 263 mm
  below the top, beam bars 720 mm long instead of the full span. Fix: LocalBox reads FamilyInstance.GetOriginalGeometry
  (family coordinates, mapped with GetTransform) and checks it lies on the element before using it.
- Not done by design: laps/anchorage into supports, hooks. Bars run face to face of the member's own concrete.

## 2026-11-23 · Version 0.11.1 (live test of 0.11.0)

| File | Change | Reason |
|---|---|---|
| `plugin/Edit/DocOps.cs`, `plugin/Read/DetailRead.cs` | DocOps.NumberOf: revision number or null; spot elevations fall back to model geometry. | Per-sheet revision numbering threw; beam top faces under slabs are hidden in plan geometry. |
| `plugin/Core/Find.cs` | Same-name parameters: the single editable one wins when values differ. | Sheets carry Sheet Number twice (SHEET_NUMBER, VIEWER_SHEET_NUMBER). |
| `plugin/Edit/ModelOps.cs` | Regenerate after SlabShapeEditor.Enable; clear error with corners when AddPoints is refused. | Points refused on two Task 04 slabs; worked on the roof slab. |
| `plugin/Edit/DetailOps.cs` | view_range error lists the current planes. | "Plan view range is not valid" gave no help. |

## 2026-11-23 · Version 0.11.0 (roadmap phases 2, 3, 4)

| File | Change | Reason |
|---|---|---|
| `plugin/Edit/DocOps.cs` (new) | Filters, colour by parameter, overrides, view templates, face dimensions, spot elevations, revisions, clouds, sheet layout. | Phase 2. |
| `plugin/Read/DetailRead.cs`, `plugin/Core/Registry.cs` | list_documentation. | Names for templates, filters, revisions, scope boxes, title blocks. |
| `plugin/Edit/ViewOps.cs`, `plugin/Edit/DetailOps.cs` | viewport_place places schedules; view_set scope_box. | Phase 2. |
| `plugin/Edit/RebarOps.cs` (new), `plugin/Read/RebarRead.cs` (new), `server/bbs_report.py` (new) | Column, beam, footing, slab reinforcement; rebar_list; BBS workbook. | Phase 3. Rebar.CreateFromCurves differs: 2024 hook arguments, 2027 BarTerminationsData (checked against both APIs). |
| `plugin/Edit/ModelOps.cs` (new), `plugin/Edit/StructureOps.cs` | slab_shape, beam_system_create, brace_create; floor_create slope arrow. | Phase 4. SlabShapeEditor.AddPoints / ModifySubElement exist in both versions. |
| `plugin/Core/Describe.cs` | ToInternal(parameter, value) shared by set_parameters and filter rules. | No duplicate unit conversion. |

## 2026-11-23 · Version 0.10.1

| File | Change | Reason |
|---|---|---|
| `plugin/Edit/EditContext.cs` | Errors on elements joined in the batch get Revit's default resolution, reported in resolvedByRevit with its type. | The detach-only rule rolled back join_overlaps on the KTR15 model (4 "Can't cut joined element" errors). |
| `plugin/Edit/DetailOps.cs` | legend_create, repeating_detail_place, detail_component_place 'along'. | Workarounds for the two items the API cannot create. |

## 2026-11-23 · Version 0.10.0 (roadmap phase 1: drafting and detailing)

| File | Change | Reason |
|---|---|---|
| `plugin/Edit/DetailOps.cs` (new) | drafting_view_create, detail_lines_create (arcs, polylines, line styles), region_create, detail_component_place, detail_group_place, callout_create, view_duplicate, view_set, views_copy_from. | Roadmap phase 1. |
| `plugin/Read/DetailRead.cs` (new), `plugin/Core/Registry.cs` | list_detailing read action. | Names to use for styles, types, components, groups; source views for copies. |
| `plugin/Edit/ViewOps.cs` | detail_lines_create moved to DetailOps; model_lines_create draws arcs on their own plane. | The documented 'mid' was ignored (a straight line was drawn). |
| `server/catalog.py`, `README.md`, `docs/ACTIONS.md` | New group "Drafting and detailing", workflow 4.7, counts 33 read / 70 edit. | Documentation. |
| Not built | Repeating details; new legends. | The Revit API has no create method for them (checked against the 2024 and 2027 API). |

## 2026-11-22 · Version 0.9.2 (live test of 0.9.1)

| File | Change | Reason |
|---|---|---|
| `plugin/Core/Find.cs` | Same-name parameters with equal values resolve to the editable one; refused only when the values differ. | Columns carry Base Level / Base Offset twice (schedule and family parameter); 0.9.1 refused every read of them. |
| `plugin/Edit/EditContext.cs` | The detach resolution applies when "Can't cut joined element" names any element joined in this batch. | Revit names one element; 0.9.1 required two and rolled back the batch (tested on the detached model). |
| `plugin/Edit/ModifyOps.cs` | join_overlaps joins pairs with an unmeasured overlap volume again. | They intersect; 0.8.0 joined them. |
| `plugin/RevitMcpBridge.csproj`, `build.bat` | After a successful compile the installed DLL is renamed aside before the copy. | Builds work with Revit open (0.9.0 required closing Revit); the installed add-in is still untouched when compiling fails. |

## 2026-11-22 · Version 0.9.1 (review of 0.9.0)

| File | Change | Reason |
|---|---|---|
| `server/server.py`, `server/excel_io.py`, `plugin/Bridge/BridgeServer.cs` | `document_id` and `operation_id` are optional: an id is generated when omitted, and the model is checked when `document_id` is given. | 0.9.0 refused every edit without both values, which broke plain requests. The safety stays available. |
| `plugin/Core/WriteGate.cs`, `server/catalog.py`, `README.md` | Permission and edit wording matches the optional values. | Clear instructions after the permission dialog. |
| `server/bridge_client.py` | Restored the hints to press MCP Status / use select_revit, and to check for a permission dialog. | They were dropped in 0.9.0. |
| `claude_config_snippet.json`, `codex_config_snippet.toml` | Paths point at the main folder. | 0.9.0 pointed them at the review copy. |
| `tests/smoke_test.py`, `tests/fake_bridge.py` | An edit without the ids is accepted. | Covers the change above. |

## 2026-11-21 · Version 0.9.0

This entry covers the changes made after reviewing the Revit 2024 and 2027 bridge. It records workspace source, configuration, test, and documentation changes. Build and test outputs were written to temporary directories.

### Bridge, targeting, and request handling

| File | Change | Reason |
|---|---|---|
| `plugin/Bridge/EditReceipts.cs` | Created the in-process edit receipt registry. It records queued, running, and terminal outcomes, compares retry payloads, and keeps older IDs reserved after their results expire. | A lost reply previously allowed a retry to execute a write twice. |
| `plugin/Bridge/BridgeServer.cs` | Added protocol version 2 to discovery, published discovery files by replacement, routed receipt status outside Revit’s UI thread, and checked document targets on the UI thread. | Readers need complete connection metadata and a write needs to verify its target immediately before execution. Receipt lookup should work while the UI is busy. |
| `plugin/Bridge/MainThread.cs` | Handled Revit’s external event response, cancelled work that was rejected before execution, and replaced the per-request wait handle with a completion task. | Rejected events should return an error promptly. A running edit must still finish and record its outcome. |
| `plugin/Core/DocumentTarget.cs` | Created model and session identity helpers using the document’s creation GUID and its file location or cloud IDs. | The active RVT can change while an edit is queued. Workbooks also need a source identity across sessions. |
| `plugin/Core/Registry.cs` | Registered `get_operation_status` as a read action that does not use Revit’s API context. Made duplicate read registrations fail. | Status should stay accessible while an edit is in progress. A duplicate registration previously replaced the first handler. |
| `plugin/Core/WriteGate.cs` | Cleared the pending-dialog flag if the event could not be scheduled. Updated permission messages to request a new operation ID after approval. | A failed event must allow another permission request. The original operation ID already records the permission-only outcome. |
| `plugin/Core/Args.cs` | Required text for choice values and rejected fractional or inexact numeric element IDs. Kept support for signed 64-bit IDs supplied as text. | Bad input could fall back to a default operation or be rounded to a different element ID. |
| `plugin/Core/Json.cs` | Rejected duplicate object properties when parsing bridge requests. | The last duplicate value previously replaced an earlier one silently. |
| `plugin/BridgeInfo.cs` | Set the add-in version to `0.9.0`. | The protocol and write requirements changed. |
| `server/bridge_client.py` | Pinned port, token, and PID for multi-call work. Checked the protocol before writing and verified response IDs. Included the operation ID and PID when a write reply was lost. | Each workflow needs one Revit connection. An older add-in can ignore the newer target fields, and a transport failure can leave the outcome unknown. |
| `server/server.py` | Required `document_id` and `operation_id` for direct edits. Bound QS and Excel workflows to one session and model. Added optional PID routing for receipt lookups and passed target fields through the split tool. | Callers need to identify the intended model and distinguish an identical retry from a new edit. |

### Parameters, element queries, and Excel

| File | Change | Reason |
|---|---|---|
| `plugin/Core/Find.cs` | Added built-in, shared GUID, and document parameter selectors. Rejected ambiguous display names and out-of-scope numeric type IDs. Ordered levels by project elevation. | Names can collide, type references must honor the requested category or class, and mixed elevation bases can change sort order. |
| `plugin/Core/Describe.cs` | Added element `UniqueId`, stable parameter metadata, and a normalized value for conditional writes. | An Excel edit needs to carry the parameter identity, scope, and value it read. |
| `plugin/Read/ElementRead.cs` | Preserved same-name parameters in detailed results. Added UniqueId queries, explicit reporting for missing references, optional parameter details, and ID-ordered pagination. | Detailed reads previously hid duplicate names. Large queries and deleted workbook rows needed a way to continue. |
| `plugin/Edit/ModifyOps.cs` | Resolved parameter selectors before writing, rejected multiple selectors for one parameter, supported UniqueIds and expected values, and removed repeated unordered join pairs. Isolated bulk join attempts in subtransactions and registered only their pairs for detach handling. | Preserve edits to the intended parameter, detect intervening changes, and avoid a failed join or repeated toggle leaving the wrong state. |
| `plugin/Read/ModelRead.cs` | Returned model and document identities through `ping` and project info. Reported project and display elevations separately. | Reads and writes need a shared model identity. Revit’s display elevation can use a different datum. |
| `plugin/Edit/DatumOps.cs` | Returned both elevations for newly created and existing levels. | Level creation and level queries need a consistent coordinate contract. |
| `server/excel_io.py` | Added hidden source, element, parameter, and baseline metadata to exported workbooks. Validated headers, formulas, IDs, and editable cells. Compared values by Revit storage type and sent expected values to Revit. Reported planned changes, applied changes, missing rows, conflicts, and per-batch results separately. Kept a legacy import path with an explicit document target. Split large Excel-to-model groups within a single transaction and validated numeric inputs. | Prevent writes to the wrong RVT or parameter, preserve text such as `001`, detect stale values, and report partial imports accurately. |
| `server/xlsx_util.py` | Added literal text cells, strict boolean flags, and staged workbook saves. Removed the old lossy parameter-to-cell conversion. | Model strings can resemble formulas. A failed save or overlapping write should leave an existing workbook intact. |

### Quantity reports, geometry, and transaction results

| File | Change | Reason |
|---|---|---|
| `plugin/Core/Materials.cs` | Added material classification based on structural assets and material class. Ignored extra whitespace-only steel family patterns. | Concrete totals require material evidence. A blank family pattern could classify everything as steel. |
| `plugin/Read/QuantityRead.cs` | Returned material classification and measurement failure counts, cached classification per material, grouped material quantities by structured keys, and calculated steel weight before rounding volume. Added rebar host category and level filtering. | Prevent misleading totals and repeated material lookups. Reinforcement scope should follow its host where available. |
| `server/qs_report.py` | Separated concrete from other and unclassified materials, added exact-name overrides, and applied explicit category and view scope across relevant checks. Used literal text for model labels. Displayed coincidence and containment as review items, and labelled incomplete or unmeasured overlap results. | The workbook could count brick as concrete, mix scopes, or recommend deletion from geometry alone. |
| `plugin/Core/Geo.cs` | Added bounding-box candidate filtering, cached steel checks, assigned each intersection pair one source owner, and added pair-level continuation. Replaced transitive deletion candidates with direct geometric comparisons. | Keep scans resumable, avoid repeated intersections, and avoid inferring equivalence between elements that were never directly compared. |
| `plugin/Core/CadPairs.cs` | Sorted concentric arc candidates by center X and stopped scanning once they exceeded the existing tolerance. | Dense CAD layers were comparing far-apart arc centers that could never match. |
| `plugin/Read/OverlapRead.cs` | Returned direct review pairs with type, phase, design option, and dependent element details and indicated skipped or unmeasured geometry. | A valid deletion decision needs model context beyond overlap volume. Pairwise volumes may also share the same space. |
| `plugin/Edit/EditContext.cs` | Deduplicated element references, checked ownership, reserved output names, deferred folder creation, and restricted error resolution to the specific detach resolution for a registered join pair. | Avoid duplicate targets, pre-commit file side effects, and unintended resolutions of unrelated transaction errors. |
| `plugin/Edit/EditEngine.cs` | Checked transaction start and rollback states, marked failed previews, associated deferred work with operation indexes, and logged operation IDs and post-commit failures. | A committed model transaction and a failed export or save are different outcomes. |
| `plugin/Edit/ExportOps.cs` | Deferred filesystem creation and exporter execution, validated fully qualified paths, and reserved output names across the batch. | A dry run or an earlier model failure should leave the output location untouched. |
| `plugin/Edit/FamilyOps.cs` | Connected deferred family changes to the post-commit outcome and surfaced per-family failures. | A partial family reload should appear in the batch result. |
| `plugin/Edit/ViewOps.cs` | Registered view activation through the indexed deferred operation API. | A failure activating a view should identify the corresponding operation. |

### Deployment, configuration, documentation, and tests

| File | Change | Reason |
|---|---|---|
| `build.bat` | Removed pre-build DLL renames and backup deletion. Propagated build and deployment failures. | A failed compile previously risked leaving no current DLL while the script still returned success. |
| `plugin/RevitMcpBridge.csproj` | Copied the manifest after the DLL and made deployment copy errors fail the build. | A success message must reflect the installation outcome. |
| `setup_server.bat` | Replaced inline config generation with the shared generator and handled generator errors. | Generate both clients’ settings from the same checkout. |
| `tools/write_client_configs.py` | Created the Claude and Codex snippet generator. | Resolve the current checkout’s path and escape it for JSON and TOML. |
| `claude_config_snippet.json` | Regenerated the command and script paths. | The previous snippet pointed to a different existing checkout. |
| `codex_config_snippet.toml` | Created a local Codex config snippet with split read and edit tools. | Provide a working stdio launch configuration for this checkout. |
| `server/catalog.py` | Updated the protocol version, action and parameter descriptions, and split-tool descriptions. | Clients need the new tool signatures, coordinate conventions, and retry semantics. |
| `docs/ACTIONS.md` | Regenerated the action reference from the catalogue. | The human-facing reference must match the server. |
| `README.md` | Documented the matching-version requirement, Codex and private ChatGPT web setup, document targeting, receipt handling, and revised Excel and QA outcomes. | Users need installation and migration instructions for the changes. |
| `CHANGELOG.md` | Added the 0.9.0 release entry. | Record the release-level changes alongside earlier versions. |
| `tests/fake_bridge.py` | Modelled the new wire protocol, target identity, parameter metadata, paging, material classification, overlap review, and repeated operation receipts. | Exercise the updated Python integration without a live RVT. |
| `tests/smoke_test.py` | Added checks for wrong-model rejection, retries, old add-in rejection, paging, material classification, workbook origin, typed comparison, and partial Excel updates. | Protect against the regressions found in the review. |
| `docs/IMPLEMENTATION_LOG.md` | Created this file. | Record what changed and why. |

### Removed behavior

- Removed overlap deletion recommendations and the `deleteCandidates` result. Direct pair evidence is returned as `reviewPairs`.
- Removed automatic replacement of ambiguous same-name parameters and duplicate JSON or Excel fields.
- Removed generic Revit error resolution from bulk join transactions.
- Removed export-directory creation during previews and pre-build removal of the installed add-in DLL.
- No workspace files were deleted during this repair.

### Verification and scope

- Revit 2024 (`net48`) and Revit 2027 (`net10.0-windows`) Release builds completed with zero warnings and errors.
- The MCP smoke suite passed in single-tool and split-tool modes using the local fake bridge. The simulated partial import reported 501 planned edits and 500 applied edits.
- Verified that an intentionally blocked deployment destination returns a nonzero MSBuild result after compilation. Parsed the generated Claude and Codex configs and confirmed they point to this checkout.
- Revit was closed during verification. The installed add-ins and live models were untouched. Geometry, exporters, worksharing, and native transaction behavior still need verification in a running Revit session. Receipts are held for the life of the running Revit process. The multi-call report verifies the model across steps and can reflect changes made within that model during the report.
