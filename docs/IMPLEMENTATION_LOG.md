# Revit MCP implementation log

## 2026-11-28 · Version 0.30.1 (phases 5, 6, 8, 9 and 13 closed)

| Item | Change | Test |
|---|---|---|
| Opening families | sleeve_place: wall-hosted families in the crossed wall; work-plane-based (face-based) families with axis z on the wall's face; void cut measured from the wall volume (cut, wallVolumeRemoved); refused for a wall in a link when hosted | live_v030d on 2024 and 2027 with Wall Opening.rfa (face-based, cut with voids when loaded): placed on the face, 0.061 m3 removed |
| family_inspect | placement and hosting (host, work-plane-based, always vertical, cut with voids when loaded) | live_v030d |
| Phase 5 | shared parameters added and removed for real | live_v030d on both releases |
| Phases 6, 8, 9 | live_p6 / live_p89 scratch guard accepts a model_new scratch; DGN and gbXML left to live_v030b; dimension_radial expected to work on 2027 | live_close_chain on both releases: all parts passed |
| ACC parameters | closed without a live add (no ACC parameter available) | refusals from 0.30.0 |

## 2026-11-28 · Version 0.30.0 (phases 15, 16 and 18 closed; 0.26.3 audit items fixed)

Mohamed asked to finish phases 15, 16 and 18 with the items listed, create analytical items (no model had any) and make
sure analytical works on Revit 2024. Live tests in scratch projects made by model_new (closed without saving); Task 04
opened read-only and closed unchanged (on 2027: upgraded in memory, never saved).

New files: Edit/ModelingOps.cs (parts, arrays, reference planes, align_lock), Edit/DocExtraOps.cs (colour fill, dimension
type, sheet collections, STEP, ACC parameters), Read/SessionRead.cs (performance_adviser, pick, watch_*, preview_*),
tools/probe_exe.py, tests/live_v030.py, live_v030b.py, live_v030c.py, live_v030_pair.py, live_2027_chain.py.
80 reads, 171 edit operations. Both years 0 / 0, smoke ALL OK (workbook budget checks added).

| Run | Result |
| --- | --- |
| Build 1 | probe_exe caught the frozen exe's 'I/O operation on closed file' at end of input and refused to promote; fixed in server.py (closed std streams pointed at devnull). Promotion later failed once because paused install windows held dist\RevitMcp; the previous build was restored as designed; install.bat /no_pause added for the test installer |
| live_v030, 2027 | all but: test category name 'MEP Spaces' (alias spaces added); support 'preset' parameter is the support kind (dropped; direction states read instead) |
| live_v030, 2024 / 2027 | analytical members, panel, releases (pinned, user), supports (FFFFFF / FFFRRR / RFFR / FFF), load cases, combination, loads; HVAC zone (2024) and system zone (2027); key schedules; family_parameters (new type 450 -> formula 900); staged exports; Task 04 reopened gets id ':2', the old id refused; close guard; audit health; 40 concurrent pings. Percentage fields: refused by Revit's API in both years (now reported as not possible). Final runs ALL CHECKS AS EXPECTED on both years |
| live_v030b, 2024 / 2027 | parts create / divide / merge, linear and radial arrays, reference plane + align_lock (moved 347.6 then locked), Performance Adviser (17 rules), watch (added / modified / deleted), preview (13 lines), pick (Mohamed clicked a wall), colour fill scheme with legend, dimension type, sheet collection (2027), ACC refusals, DWG export, CAD link reload, gbXML. Fixed on the way: CAD reload needs its own transaction; radial plain copies listed the original; count < 2 refused before the arrayable check; STEP needs a 3D view; gbXML ground plane / export category. Final: ALL CHECKS AS EXPECTED on 2024; on 2027 STEP and gbXML confirmed by live_v030c. DGN skipped with the reason: no DGN setup in the template and no seed file installed with Revit on this PC |
| live_p10 on 2027 | scratch: ALL CHECKS AS EXPECTED (0.22.0: rooms, spaces, crossings and openings, analytical associated with physical, loads, combinations). task04 (dry runs): ALL CHECKS AS EXPECTED, Task 04 unchanged |
| revit_addins | 2024: ResultsExplorer, ResultsBuilder, MemberForces, SectionProperties, StructuralRibbon, SteelConnections. 2027: AnalyticalAutomationTools, HCLoadsAnalysis, SteelConnections |

## 2026-11-28 · Central-model question and ownership scan tested live (0.29.0, no code change in the add-in)

The audit features added in 0.26.5 / 0.26.6 had not run on a central model. tests/live_ws.py gained: a timed
worksharing_status with elementsScanned / scanComplete checks; a relinquish answered **No** in Revit (nothing changes, the
resend is refused with "declined in Revit a moment ago"), a 35 s cool-down, then the Yes flow; and a check that every
relinquish and sync was asked in Revit first (an approval is used once). Model: the cloud TEST model
KTR15-G26-Z2-X-S-RC-RFT-ZZ-20100-00 (Mohamed answered every dialog).

| Run | Result |
| --- | --- |
| 1 | Yes was clicked on the dialog meant for No, so the Yes path ran: asked first, nothing changed, applied after Yes. Test bug: while the question is open, Revit is not idle and a resend returns "did not become available within 30 s" after 30 s; the script treated that as a failure, so one relinquish was counted as failed when the answer took longer than 30 s. Fixed with a poll() helper that keeps waiting on that reply (up to 3 minutes) |
| 2 | ALL CHECKS AS EXPECTED. No: nothing applied, resend refused. Yes: checkout, relinquish all, real Comments edit, sync relinquish all, reload_latest, value restored, sync relinquish borrowed; the model ends with nothing held and nothing unsaved. Each of the three central actions was asked in Revit first. The ownership scan covered all 8424 elements in under 0.1 s, so the 5 s cut-off did not trigger on this model (it remains untested on a larger central) |

## 2026-11-28 · Version 0.29.0 (phase 18 completed: sleeves, plumbing / fire fighting, electrical, trays, report)

New: Edit/FixtureOps.cs (mep_fixture_place), Edit/ElectricalOps.cs (electrical_circuit_create / edit, electrical_panel_set),
Read/ElectricalRead.cs (electrical_circuits), AccessoryOps.SleevePlace (sleeve_place), MepOps: route slope, routes from tray /
conduit connectors, tray / conduit tees (SplitRun by copy), DownPlane facing up; server/mep_report.py (mep_report, added to
the PyInstaller hidden imports). 72 reads, 156 edit operations. Both years 0 / 0, smoke test ALL OK (new mep_report checks
against the fake bridge), build_package RESULT=OK three times, add-ins installed for 2024 and 2027 (server exe locked by
Claude Desktop: known limitation). Mohamed declined folder access to C:\ProgramData\Autodesk\RVT 2024, so the tests use the
families in the detached KTR15 copies; found with model_files, model_open in the background, count_by_category, list_types,
family_inspect. Test: tests/live_mep5.py (`run_live_mep5.bat`): FF, PL and E copies opened active one after the other and
closed without saving; workbooks tests\results\mep_report_live_PL.xlsx and _E.xlsx.

| Run | Result |
| --- | --- |
| 1 | Most passed. Bugs: mep_insulation called GetLiningIds on pipes (Revit: "not a duct, fitting or accessory"), which broke the PL workbook; circuit membership was read from connectors' MEPSystem, but electrical circuits sit on MEPModel.GetElectricalSystems(), so "already on a circuit" fell through to Revit's own error and the test loads were listed as on no circuit. Test: the sprinkler main used a pipe type without a takeoff or 50 x 15 tee; the OD + 50 expectation was off by one (OD = nominal here); the linked-wall socket needed the wall's own height |
| 2 | Fixed (lining only on the duct side, wrapped; CircuitsOf via GetElectricalSystems; the panel is resolved before the circuit is made). All checks passed except the sprinkler joint: KTR15-M-PITY-Steel Black, Galvanized Steel and Chilled Water have no takeoff and no 50 x 15 tee in their routing preferences (reported, rolled back) |
| 3 | ALL CHECKS AS EXPECTED. FF: two pendent sprinklers on a downward plane at 2.8 m, each teed into a 50 mm main (pipe type Standard); refusals (not under the main, wrong category, walls that are not walls). PL: floor drains on an upward plane (work-plane-based, rotated, comment set); a 100 mm run at 2 % falling exactly 100 / 60 mm with its elbow; sleeves (coupling as stand-in) at two test walls (centre plane, 90 degree turn, 460 mm crossing length, measured diameter, OD + 50) and at a slab (mid-depth; axis x on the vertical pipe refused: would tilt); refusals. E: 10 panels, 58 circuits read; sockets on a test wall's face and on a wall of the linked architecture model; ceiling lights; circuits on DB-B-GP with 20 A / 10 A and load names; edit (remove, add, rename, 16 A, off and back on the panel); refusals (no connector, already on a circuit, unknown panel, empty circuit, nothing to change, unknown distribution system); panel renamed; tray and conduit runs with elbows and teed branches; workbooks with runs, counts, insulation, panels, circuits |
| Regression | live_hvac4 and live_hvac3 on 0.29.0: ALL CHECKS AS EXPECTED. Task 04: no unsaved changes, 27 / 64 / 21 / 7 / 33 / 273 / 8 / 4 |

## 2026-11-28 · Version 0.28.0 (phase 18 fourth part: accessories, insulation and lining)

New: Edit/AccessoryOps.cs (mep_accessory_place, mep_insulation_add, mep_insulation_remove), HvacRead.MepInsulation
(mep_insulation). 71 reads, 151 edit operations. Before any change: both years rebuilt in the cloud (0 / 0) and smoke test
ALL OK on 0.27.0. After: compiled 0 / 0 for 2024 and 2027, smoke test ALL OK, build_package RESULT=OK twice, add-ins
installed for 2024 and 2027 (the server exe copy failed: Claude Desktop had it open, the known limitation; the tests talk
straight to the add-in). Test: tests/live_hvac4.py (`run_live_hvac4.bat`) in the detached HV copy, closed without saving.

| Run | Result |
| --- | --- |
| 1 | Revit's host-based NewFamilyInstance on a duct places the damper but does not break or connect the run, so the split / place / trim / connect path ran: dampers connected on both sides, but they kept the family's 300x300 on a 600x300 duct. A second call on the split run found only the original piece ("beyond the ends" instead of "off the centreline"). System insulation was refused because the network holds the AHU. The arch link only places origin to origin here and crossed no duct |
| 2 | Fixes: the accessory is sized through MEPFamilyConnectorInfo.GetAssociateFamilyParameterId (instance parameters only; a type-driven size is reported), rectangular ones are turned about the run axis to the run's width; a run id follows its in-line chain through earlier accessories; a system's network is filtered to ducts, pipes, fittings and accessories; the test draws its own duct through a linked wall. ALL CHECKS AS EXPECTED, nothing skipped: two volume dampers 600x300 on one test duct (pieces followed), refusals (off the centreline, beyond the ends of the run and its 2 pieces, neither point nor crossing, no type, a pipe accessory type on a duct, a wall the duct does not cross), fire damper at a host wall's centre plane (600x300, connected), fire damper where a test duct crosses a wall of the linked architecture model (0.0 mm from the wall's location line; mep_crossings finds the same crossing), balancing valve on a 100 mm test pipe (connected; its DN 125 type keeps its size: "Connection Radius" is a type parameter, reported), insulation 25 then replace 50 then remove, refusals (no thickness, elements and system, unknown type, already insulated, none to remove, lining on a pipe), the test duct's whole system insulated (5 created, AHU and terminals left out), mep_insulation totals and missing_only, duct lining 25, pipe insulation 25 |
| Regression | live_hvac3 on 0.28.0 in the same Revit session: ALL CHECKS AS EXPECTED. Task 04 after the runs: no unsaved changes, 27 / 64 / 21 / 7 / 33 / 273 / 8 / 4 |

## 2026-11-28 · Version 0.27.0 (phase 18 third part: connectors, runs from equipment, quantities, schedules)

New: HvacRead.MepConnectors (mep_connectors); MepOps mep_route from / to_main, elbow retry with swapped width / height,
mep_connect classification; MepRead.MepQuantities duct surface area. 70 reads, 148 edit operations; compiled 0 / 0 for both
years; smoke test ALL OK. Test: tests/live_hvac3.py (`run_live_hvac3.bat`) in the detached HV copy, closed without saving.
(A compact tool description was tried and dropped at Mohamed's request: the full catalogue stays.)

Client setup (Mohamed: match the ETABS installer). The ETABS and SAP2000 install.bat and server exe are identical; their
install log writes the same three Claude Desktop files as ours and Codex. OpenAI's MCP page (learn.chatgpt.com/docs/
extend/mcp): the ChatGPT desktop app, Codex CLI and IDE extension share ~/.codex/config.toml; STDIO options command,
args, env, env_vars, cwd; startup_timeout_sec default 10, tool_timeout_sec default 60. So the Codex entry now also serves
ChatGPT and sets 30 / 3600 s; Claude entries keep user settings; backups unique. Smoke test extended: ALL OK.

Merged the 0.26.5 code-audit fixes made on 27 Nov in the "MCP implementation code audit" chat (H1-H2, M1-M6, L1-L10:
operation journal and progress heartbeat, central-model Yes / No per sync and relinquish, accept-loop retry, 4 MB request
guard, batch_read budget, PathPolicy / allowed_roots.txt, ignoredParams, ownership scan limit, stricter whole numbers,
receipt eviction, dead-session skip, pinned requirements). That chat wrote 27 files at 21:26; this session's 0.26.5
install fix (22:46) then rewrote catalog.py, client_config.py, smoke_test.py, README.md, CHANGELOG.md, BridgeInfo.cs and
ACTIONS.md from an older base, dropping its edits there. Restored from that chat's transcript: help by group title or list
and the compact-description switch (catalog.py), the Codex writer that recognises [mcp_servers."revit"] and writes
atomically (merged with the TOML check of 0.26.5), the smoke checks (recent writes, 4 MB guard, ignoredParams, grouped
help), the README sections (central dialog, allowed folders, ignored options, recent writes) and its CHANGELOG entry. The
other 20 files were never overwritten. One choice differs from that chat: the full tool description stays the default
(Mohamed: do not shorten); REVIT_MCP_DESCRIPTION=compact gives the short one. Both years compile 0 / 0; smoke test ALL
OK; the live runs above already ran the add-in with that chat's changes.

| Run | Result |
| --- | --- |
| 1 | mep_connectors: 50 of 50 mechanical equipment with connectors, 7 with a free duct connector (the smallest a 2350x627 return, facing down). Route from it: "No elbow at path point 2" |
| 2 | Same, with the turn along the connector's height axis: still no elbow |
| 3 | Terminal scenario added: route, elbow and tap made, but Revit rolled the batch back at commit ("modified to be in the opposite direction"): the 600 mm first segment was shorter than the elbow. AHU: the test had picked an oval duct type |
| 4 | Lengths 1.5 m (terminal) and 3 m (AHU), types matched to the connector shape: terminal scenario all OK. AHU still no elbow; a free-standing 2350x627 bend with the same type was made, so the routing preferences were fine |
| 5 | Cause found: the first path point was a hair off the connector's axis (rounded coordinates), so a 300 mm stub was inserted and Revit was asked for an elbow between two nearly straight segments. Fixed: within 2 degrees counts as straight ahead and is snapped onto the axis |
| 6 | AHU route made; the test's tee of a 2350 mm branch into a 2650 mm test main failed (not a real-world joint) |
| 7 | ALL CHECKS AS EXPECTED: terminal: from + elbow + tap into a main, terminal connected; AHU: from the 2350x627 return + elbow, unit connected; refusals (wrong classification, size with from, connect with no free connector of that classification); duct surface area; air terminal and duct schedules with rows; HV closed without saving |

## 2026-11-27 · Version 0.26.5 (install for Claude Desktop 6.x)

Mohamed: the installation snippet no longer worked; the SAP2000 MCP package (Desktop\CSI MCP\dist\Sap2000Mcp) does.
The last Revit install log (26 Nov) showed RESULT=OK and "already configured" for %APPDATA%\Claude and the Store copy.
The SAP2000 server's client_config module (read from its exe) also writes %LOCALAPPDATA%\Claude-Data\claude_*\
claude_desktop_config.json, the file Claude Desktop 6.x reads (this laptop runs 6.28899). Ported that, plus CODEX_HOME and
a TOML check before replacing the Codex file. Smoke test extended (6.x file with other settings kept, Codex parsed, a rerun
changes nothing): ALL OK.

## 2026-11-26 · Version 0.26.4 (ChatGPT audit fixes merged and tested live)

Reviewed every change in `Desktop\Revit MCP - ChatGPT\revit-mcp` (12 files and the audit note) against the 0.26.3
source: all built on 0.26.3, none conflicting; merged as they are. Also fixed the audit's open item on spaces_from_rooms
(max applied before the level filter). Compiled 0 / 0 for both years; smoke test ALL OK.

| Run | Result |
| --- | --- |
| install (run_install_claude_open.bat) | Add-ins for 2024 and 2027 installed. The server exe copy failed: the file was in use (the ChatGPT desktop app was running) |
| tests/live_docs.py (Task 02 opened from Recent, reads only) | ALL CHECKS AS EXPECTED: background open / reuse / close, user's Task 02 close refused, workshared file opened detached, batch_read with max_files and resume_after, model_new in memory and active (scratch_<guid>), Task 04 activated, scratch closed |
| tests/live_hvac2.py | ALL CHECKS AS EXPECTED, including the edits pinned to the opened HV copy and the switch back to Task 04 (already open: activated by the new path). Spaces now taken in level / number order |

## 2026-11-26 · Recheck of the updated 0.26.3 source

Fixed document ownership and activation, batch read cleanup, mixed transaction retries, phase-sensitive space matching, split-mode worksharing, and live test targeting. Both Release targets compiled with zero warnings and errors. Simulated MCP checks passed. The existing frozen server answered the stdio probes and emitted a shutdown traceback on stderr. The patched add-ins were built without deployment. The [0.26.3 audit and file-by-file change record](0.26.3-AUDIT-CHANGES.md) gives the evidence, outstanding risks and reasons for each edit.

## 2026-11-26 · Version 0.26.3 (phase 18 second part: spaces, zones, airflow, terminals, system membership)

New edit operations: spaces_from_rooms, space_airflow_set, hvac_zone_create (SpaceOps); air_terminal_place,
mep_system_add, mep_system_remove (MepOps). 69 reads, 148 edit operations; compiled 0 / 0 for both years. Test:
tests/live_hvac2.py (`run_live_hvac2.bat`): HV copy opened active, X-A-GN linked into it (shared coordinates refused:
"do not share the same coordinate system"; origin to origin used), then the steps below; HV closed with changes
discarded, Task 04 left active.

| Run | Result |
| --- | --- |
| 0.26.0 | Spaces: 23 of 23 rooms, all enclosed. Lists of values ("supply": [200, 150], "flows": [100, 120]) refused: the per-point helper read the value as one number first. A single flow (80 L/s) did not take: the built-in flow parameter of M_Supply Diffuser is not what drives its connector (it kept the family default, 235 L/s = 500 CFM). Removing a system's only terminal: Revit's "should be less than elements contained". A second spaces_from_rooms run made one duplicate ("POOL OFFICE": no space found at the room point) |
| 0.26.1 | All of the above fixed (lists; flow written to the parameter that drives the connector and checked by the connector flow; empty-system refusal; second probe heights and a number + name match). Connected terminals landed in systems other than the main's |
| 0.26.2 | Terminals join the main's system before connecting. This model has two systems on the same supply network (KSAD 1 and KSAD 17); Revit settles the membership at commit, so the test reads it after commit |
| 0.26.3 | ALL CHECKS AS EXPECTED: 23 spaces from the linked rooms, all enclosed, a second run skips all 23; design airflow 200 / 150 supply and 120 return read back; wrong count refused; zone "MCP Test Zone" of 2 spaces, 82.2 m2; zone across levels refused; M_Supply Diffuser (level-based, from the model) x2 under a supply main at 100 and 120 L/s, both connected with tees and in the network's system KSAD 1 (no new default system); a free terminal at 80 L/s added to KSAD 1 (3 members) and removed (2); removing it again refused ("Not in system"); air_terminals lists 3 |

## 2026-11-26 · Version 0.25.4 (phase 18 first part: MEP / HVAC reads, routes, branches, systems)

Mohamed supplied detached, never-workshared copies of the office's Block B models in
`Desktop\New folder\detached\detached` (architecture, electrical, HVAC, plumbing, fire fighting, structure, federated)
and agreed to real test edits there that are never saved. Survey (model_files, batch_read): all Revit 2024; the HVAC
model has 840 ducts, 767 duct fittings, 50 mechanical equipment and 57 duct systems; no model has MEP spaces.

New: Read/HvacRead.cs (mep_flow, mep_connectivity, air_terminals); MepOps mep_route, mep_connect, mep_system_create;
batch_read `actions`. 69 reads, 142 edit operations; compiled 0 / 0 for both years. Test: tests/live_hvac.py
(`run_live_hvac.bat`): batch_read over HV and PL, then the HV copy opened active, edited, closed with changes discarded;
Task 04 left active.

| Run | Result |
| --- | --- |
| 0.25.0 / 0.25.1 | mep_connect: Revit refused the takeoff with the branch end inside the main. The branch now ends on the main's centerline; the HVAC duct types route with tees, not taps, so a tee fallback was added (BreakCurve + NewTeeFitting) |
| 0.25.2 | All passed except mep_system_create: "connectors can't match system with domain, system type or direction" (the indoor unit has supply, return and sanitary pipe connectors) |
| 0.25.3 | Connectors filtered by the system type's classification, others listed. Then "The base connector has been used": the base equipment's only matching connector had also been added as a member |
| 0.25.4 | ALL CHECKS AS EXPECTED: batch_read 3 actions x 2 files in 7.7 s; mep_flow 840 ducts / 28 systems; air_terminals with the no-spaces note; a stub under a main connected with a tee (branch 8x8); a branch that misses the main by 37 m refused; L-shaped route = 2 ducts + 1 elbow; collinear path refused; duct system from a unit with only pipe connectors refused; system of only its base equipment refused; Hydronic Return system of two indoor units, the first as base (type found by classification, supply and sanitary connectors listed as left out); system read back by mep_systems; HV copy closed, changes discarded |

## 2026-11-26 · Version 0.24.1 (phase 16: open, close, new, batch reads)

New: Read/DocumentActions.cs (list_documents, model_files, model_open, model_close, model_new, batch_read); Rc.Target
(a non-active document for reads); DocumentTarget.Verify finds any open model by document_id for reads (edits: the
active model only). The server gives model_open, model_new and batch_read the long timeout. 66 reads, 139 edit
operations, 4 worksharing actions; compiled 0 / 0 for both years.

| Run | Result |
| --- | --- |
| tests/live_docs.py, 0.24.0 (Task 02 open by the user) | All checks passed except two refusal messages: a document_id naming the ACTIVE model did not set the target, so model_close said "give document_id" instead of "not opened by Claude" / "active model" (both still refused) |
| 0.24.1 | ALL CHECKS AS EXPECTED (19): model_files of Desktop\New folder (versions, backups skipped); Task 04 opened in the background in 2.3 s, read by document_id (get_project_info, count_by_category) with the active model unchanged; second open reused it; get_selection on it and an edit with its id refused; closing the user's Task 02 refused; Task 04 closed; the workshared KTR15 Block E file opened detached in 7.3 s with worksets kept (opened closed) and closed; batch_read count_by_category over Task 04 + Task 02 (Task 04 opened and closed again, Task 02 left open), an edit refused, max_files / resume_after; model_new in memory, read and closed; model_new active (scratch_..., %TEMP%\RevitMcpScratch), closing it while active refused, Task 04 opened active, scratch closed; the user's model unchanged |

Left over: Task 04 stays open and active (opened by Claude; Revit cannot close the active model from an add-in) and two
scratch files remain in %TEMP%\RevitMcpScratch (they also show in Revit's Recent list).

## 2026-11-26 · Version 0.23.1 (phase 12 worksharing; copy from another model)

Mohamed opened KTR15-G26-Z2-X-S-RC-RFT-ZZ-20100-00, a workshared CLOUD model in Autodesk Docs (project KT-G26-Z2), and
confirmed it is a throwaway test model only he uses, where syncs and test edits are allowed. Never assume this for any
other KTR15 model. He also opened Revit Task 02 (drafting views, a legend) as a copy source.

| Run | Result |
| --- | --- |
| tests/live_ws.py, 0.23.0 | status, checkout (Workset1 + a column), relinquish all: as expected. Sync refused by Revit: "saving local before ... and after ... is mandatory" (cloud). Refusals were reported as "outcome unknown". The model was closed with "Do not save" + "Relinquish all": the test value never reached the cloud |
| tests/live_ws.py, 0.23.1 | ALL CHECKS AS EXPECTED: checkout, relinquish, edit Comments, sync (11 s, relinquish all: column current with central, not held, nothing unsaved), reload_latest, value restored and synced again (26 s, relinquish borrowed). Two syncs in the cloud model, comments "MCP 0.23.0 sync test" and "MCP test: Comments restored" |
| tests/live_copy.py (Task 02 open, new scratch project) | ALL CHECKS AS EXPECTED (after a test fix: views_copy_from needs view names): 5 drafting views and Legend 1 copied and read back; a second copy skipped all 6; standards_copy of line styles, fill patterns and text types; unknown model refused. Scratch discarded, Task 02 unchanged |

Also: GetModelUpdatesStatus says CurrentWithCentral for an existing element edited locally (NotYetInCentral is for new
elements); the checkout status shows the pending edit. While opening Task 02 from File > Recent, a local KTR15 Block B
file was opened by mistake and closed unchanged.

## 2026-11-26 · Version 0.22.2 (join_overlaps on a real model; worksets; steel connections read live)

Mohamed opened a detached copy of the Block B structure model (KTR15-G26-Z2-B-S-RC-RFT-XX-20001-00, detached with
worksets preserved, never saved) and Revit Task 02 (steel, with connections). He agreed to real edits in the unsaved
detached copy, closed without saving afterwards.

| Run | Result |
| --- | --- |
| Worksets (detached copy, real) | workset_create made "MCP Test A" and "MCP Test B"; a second create reported alreadyExist; workset_set moved a column and a beam, list_worksets read them back in "MCP Test A", owned by the current user; an unknown workset was refused with the list. The open check is closed |
| join_overlaps (detached copy), 0.22.0 | 169 measured unjoined pairs (35.8 m3 counted twice). Dry run passed (workshared: no trial commit); the real run joined all 169, then Revit rolled the batch back: "Can't cut joined element" on 3 grade beams GB1 250x600 at -2.10 and 3 slabs 280 mm (one element per failure, so the exact-pair detach never applied) |
| 0.22.1 | Retry without the failing elements, matched on JoinElementsFailures.CannotCutJoinedGeometry: did not trigger live (Revit uses another definition) |
| 0.22.2, real run | Retried once without the 6 elements: 151 pairs joined, 18 listed in skippedCutConflict, warning names the elements. find_overlaps after: 20 unjoined pairs, 4.4 m3 (from 169, 35.8 m3). Copy discarded |
| Task 02, tests/live_reads_task02.py (reads only) | steel_connections: 80 connections (22 base plates, 25 single and 4 double tube brace gussets, 14 bolted knees with haunch, 7 apex haunches, 8 beam seats), each with type and its columns / beams; analytical_model and list_loads answer (no analytical model; 8 default load cases); Task 02 unchanged |

Also seen: the Claude app's Revit MCP server was started before the 0.21 install, so it refused the new read actions;
the test scripts call the add-in directly. Restarting the Claude app loads the new catalogue.

## 2026-11-26 · Version 0.22.0 (edits: MEP runs, openings at crossings, rooms, analytical model, loads)

Mohamed gave one more edit approval (two in the end: a fix needed a rebuild and a Revit restart). New: Edit/MepOps.cs
(mep_run_create), crossing_opening in Edit/OpeningOps.cs (intersects the run's centreline with the host's solids, sizes
from the run's cross-section and connector axes), Edit/SpaceOps.cs (room_create, room_separation), Edit/AnalysisOps.cs
(analytical members and panels, load cases, combinations, loads). Revit 2023+ loads must be hosted, so load_place takes
an analytical element (or a physical one with an association); free-standing loads are not offered. 59 reads, 139 edit
operations; compiled 0 / 0 for both years. Test: tests/live_p10.py (task04: dry runs; scratch: real runs).

Live, Revit 2024:
| Run | Result |
| --- | --- |
| live_p10 task04 (dry runs), twice | ALL CHECKS AS EXPECTED; Task 04 unchanged |
| live_p10 scratch, first run | 5 checks failed, all in rooms: optional names / numbers were read as empty lists (one name per point refused); all_regions (NewRooms2) also filled regions that already had a room (rooms of zero area); list_spaces reported "Name Number" as the name. Everything else passed |
| Fixes | names / numbers only checked when given; all_regions places rooms only in plan circuits with no room (PlanTopology, IsRoomLocated); names from ROOM_NAME |
| live_p10 scratch, after the fixes (new Revit session, second approval) | ALL CHECKS AS EXPECTED, nothing skipped: box of walls, slab, beam; rooms 74.6 m2, split by a separation line into 37.3 + 37.3 m2; no region left; a space; pipe through the slab, pipe through the beam web, 16x12 in duct through a wall found by mep_crossings; openings 150 mm round (2 in pipes + 50 each side) and 500 x 400 in the wall; no crossings left after; analytical member (8000, beam role) and panel (200) associated with the beam and slab; load cases, combination 1.4 / 1.6; six loads read back (point -10 kN, area -3 kN/m2 over 80 m2); a load given on the physical beam lands on its analytical member; refusals as expected |
| Regression, Task 04 dry runs | live_p6: ALL CHECKS AS EXPECTED |

## 2026-11-25 · Version 0.21.0 (reads: ray casting, clearances, MEP, spaces, analysis, connections)

Mohamed opened the KTR15 federated model (a detached, non-workshared local copy in Desktop\New folder\detached\detached,
9 links: architecture x3, HVAC, plumbing, fire fighting, electrical, structure x2) and asked for several phases built
and tested at once before he slept, giving one edit approval for the night. New: Read/QueryRead.cs (ray_cast,
clearances: ReferenceIntersector, FindReferencesInRevitLinks), Read/MepRead.cs (mep_systems, mep_quantities,
mep_crossings: Solid.IntersectWithCurve across two link transforms), Read/AnalysisRead.cs (list_spaces,
analytical_model, list_loads, steel_connections). 59 reads, 130 edit operations. Compiled 0 / 0 for both years first time.

Live, Revit 2024 (one session, no restart; edit approval once at 23:35):
| Run | Result |
| --- | --- |
| Federated model, tests/live_federated.py (reads only) | ALL CHECKS AS EXPECTED, nothing skipped, model unchanged. HVAC 93 systems (57 duct, 36 pipe), 958 runs 2203 m; plumbing 50 systems, 623 runs 286 m; fire 10 systems, 1084 runs 2405 m; electrical 58 systems, no trays / conduits. Rooms: 23 in X-A-GN (2424 m2), 1 unplaced in B-A-GN. Analytical model, loads and connections: none in the structure links. ray_cast from a linked beam up / down: hit distances match the points, the beam itself is not hit. clearances ducts down: 840 measured, smallest 5.8 mm to ceiling tiles (architecture link), 6 mm to a chilled water pipe; ducts up to slabs under 300 mm: 613, smallest 16 mm. mep_crossings: HVAC 108, plumbing 94, fire 89 (complete, under 1 s each); one crossing per discipline confirmed by an independent ray_cast hitting the same structural element |
| Missing-choice message (0.20.0 fix) | Confirmed: check_names without kind and rebar_numbering without action are refused with "'... ' is required" |
| Regression on 0.21.0, Task 04 dry runs | live_p6, live_p7, live_p7b, live_audit, live_p89: ALL CHECKS AS EXPECTED; Task 04 unchanged (p89 is the first phase 8 / 9 run since dry runs commit on trial) |
| Regression on 0.21.0, scratch projects (real, discarded) | live_p6, live_p7, live_p7b: ALL CHECKS AS EXPECTED; live_p89: all except the known export_dgn (no DGN setup) and export_gbxml (no energy model), as in 0.17.0 |

Mistake on the way: computer_open_application on a running Revit started a second Revit (known pitfall); it was closed
at its Home page, no model was opened in it.

## 2026-11-25 · Version 0.20.0 (phase 7, second part: numbering, containers, Revit 2027 splices)

New: plugin/Core/Numbering.cs (list_numbering), rebar_numbering / splice_type_create / rebar_splice in ReinforcementOps,
container rows in rebar_list, 2027 splice / crank types in list_reinforcement_types. 50 reads, 130 edit operations.
API: NumberingSchema in 2027 retires GetNumberingSchema(doc, type), SchemaType, Assign / Move / Merge sequences and
minimum digits; only GetSchemasInDocument, GetNumberingSequences, GetNumbers, RemoveGaps, ShiftNumbers and ChangeNumber
are used (both versions). Splices: RebarSpliceTypeUtils, RebarSpliceRules, RebarSpliceUtils (2027 only, behind #if).

Live (tests/live_p7b.py, and live_p7.py rerun in 2027):
| Run | Result |
| --- | --- |
| Revit 2024, Task 04 (dry runs) | ALL CHECKS AS EXPECTED after the fix below. Task 04's rebar numbering has real gaps (e.g. RFT_Footing 3-6, 10-14, ... missing 7-9, 15, 21-23, 26-35, 40-41); remove_gaps / shift / change preview; unknown partition refused with the list; splices refused (2027 only); Task 04 unchanged |
| Revit 2024, scratch (real, discarded) | ALL CHECKS AS EXPECTED, nothing skipped: footing bars, partition P7B by set_parameters, shift to 101, change 101 -> 150 (gap 103-149), remove gaps; splice ops refused |
| Revit 2027, scratch (real, discarded) - first live 2027 runs | live_p7b ALL CHECKS AS EXPECTED, nothing skipped: numbering as in 2024; splice type "Lap 40d" (lap 40); Concrete-Rectangular Beam loaded from Revit's US Imperial library, 12 m beam, rebar_beam; rebar_splice max 6000 split the 11 924 mm set into 6000 + 6000 + 1194 (= 11 924 + 2 x 635 lap). live_p7 (phase 7) ALL CHECKS AS EXPECTED in 2027 too (hooks through SetTermination*, fabric laps, path, coupler, annotation) |

Found and fixed on the way: Revit 2024 names every numbering schema "NumberingSchema" (kind now from the numbered
parameter); rebar_splice read the spliced bar's id after Revit replaced it (InvalidObjectException; id kept before);
the 2027 template has no concrete beams (test loads Revit's library family); test used a 32 mm stirrup in a 300 mm
beam (Revit: "Can't solve Rebar Shape", reported cleanly). Pending live check: the missing-'action' / missing-choice
message (Args.Choice), built after the last live run.

Revit 2027 notes: its Home page takes about 2 minutes to load; until then the window is blank and the bridge reports
busy. Close button at the window's top right (1361, 13 at full scale).

## 2026-11-25 · Version 0.19.0 (phase 7, first part: path / fabric reinforcement, hooks, couplers, rebar annotation)

New: plugin/Edit/ReinforcementOps.cs (6 edit operations), plugin/Read/ReinforcementRead.cs (list_reinforcement_types,
fabric / coupler rows for rebar_list). 49 reads, 127 edit operations. API checked first against the 2024 / 2027
inventories: PathReinforcement.Create, FabricArea.Create, FabricSheet.Create + PlaceInHost, RebarCoupler.Create with
RebarReinforcementData, MultiReferenceAnnotation.Create are the same in both; hook orientation / rotation is
SetHookOrientation / SetHookRotationAngle in 2024 and SetTerminationOrientation / SetTerminationRotationAngle in 2027.

Live, Revit 2024 (tests/live_p7.py), found and fixed on the way:
- Fabric area laps were silently ignored (set before Revit lays out the sheets, which then takes the sheet type's 609.6):
  now set after the first regeneration and read back; an unkept value is an error.
- Task 04 has no fabric wire types: Revit's default sheet type then has no wires, fabric_area "succeeded" with 30 sheets
  of 0 kg and single sheets could not be placed ("out of the host"). Wireless sheet types are now refused.
- rebar_annotate defaulted to the view's right direction; Revit needs the bar set's distribution direction (bar plane
  normal) projected into the view. A set laid out along the view is now refused with a clear message.
- The template's rebar annotation type has no tag type: only the dimension is made; this is now reported, not hidden.
- Path bars at a slab edge with flip false lay outside the slab (y -1208 to 308 for a slab from y 0): flagged.

| Run | Result |
| --- | --- |
| Task 04 (dry runs, trial commit) | ALL CHECKS AS EXPECTED. Path reinforcement and fabric area on a 235 m2 slab preview; hooks start (Rebar Hook 90, right) and none + rotation preview; missing length, unknown hook, empty request refused; fabric with no wire types refused; annotation previews in Cross Sec. 01a for a set seen across its bars, sets along the view refused clearly; Task 04 unchanged. Skipped (not in the model): coupler families, fabric types |
| New scratch project, Imperial template (real runs, discarded) | ALL CHECKS AS EXPECTED, nothing skipped. Slab path 28 bars (flagged beyond host), flipped 28 inside; wall path 39 bars with alternating; fabric area 4 sheets 144 kg, laps 400 / 300 as given; sheet 12x12-W15xW15 placed at 30 deg (43 kg); hooks set and read back, then removed; coupler FS#5 on a bar end (10), wrong type refused (IncorrectEndTreatmentCoupler, explained); annotation dimension made, missing tag type reported; rebar_list shows path bars and 5 sheets |

Not run: Revit 2027 (termination API path), coupler joining two bars end to end, fabric on walls.

## 2026-11-25 · Version 0.18.1 (API alignment audit of the built features)

Mohamed asked, before phase 7, to make sure everything built is aligned with the API's types and methods. Full report:
docs/API_ALIGNMENT_AUDIT.md. Method: (1) a small metadata reader (System.Reflection.Metadata) listed every TypeRef and
MemberRef into RevitAPI / RevitAPIUI in the compiled 2024 and 2027 add-ins and matched them to the API inventory of
each year: none missing, none obsolete; (2) usage patterns reviewed (transactions, failures, units, geometry options,
parameters, rebar); (3) members new in 2027 on the types already used were listed.

Fixed: unit classification by spec dimension (U.KindOf, five call sites), trial-commit dry runs in a rolled-back
TransactionGroup (not on workshared models), Revit 2027 rebar mass in rebar_list, overlap volume per solid pair.

Tests: smoke ALL OK; 2024 and 2027 builds 0 / 0 (cloud and laptop); install RESULT=OK; ping 0.18.1. Live, Revit 2024,
tests/live_audit.py on Task 04 (reads, dry runs): ALL CHECKS AS EXPECTED (bar diameter 10 mm = 0.01 m, spacing 175 =
0.175, reinforcement volume m3; overlap warning of two identical walls reported by the dry run, group rolled back; Task
04 left without unsaved changes, same counts). The first run flagged integer parameters (Quantity) for having no unit:
a test error, corrected. Not run: Revit 2027.

## 2026-11-25 · Version 0.18.0 (phase 6: coordinates, annotation values, link management, clash with a link)

Plan source: the Revit API capability map (https://claude.ai/artifact/1TPLW8RrsshSPtNaz7fxiJ), build order in
ROADMAP.md. New: plugin/Read/CoordRead.cs, AnnotationRead.cs, ClashRead.cs; link_reload / link_unload / link_set in
plugin/Edit/LinkOps.cs (reload and unload are deferred after the commit, as the API cannot run them in a transaction;
unload uses a callback that never saves shared positions back to the link). 48 reads, 121 edit operations.

Shared coordinates: the API's ActiveProjectLocation transform direction differs between sources, so get_coordinates
picks the direction that reproduces the base point and survey point SharedPosition and reports `checkError` (0 on
Task 04). Clash: link elements prefiltered by the host element box moved into link coordinates, host solids moved into
the link, BooleanOperationsUtils intersection; clearance clashes are not checked.

Tests: smoke ALL OK; 2024 and 2027 builds 0 / 0 (cloud and laptop). Laptop, Revit 2024, tests/live_p6.py:

| Run | Result |
| --- | --- |
| Task 04 (reads and refused dry runs) | ALL CHECKS AS EXPECTED. Shared coordinates check 0; internal origin at shared elevation 100000 mm; location Internal; lat 42.387, long -71.242. Annotations: 21342 dimensions, 27 spots, 596 tags, 1967 text notes; 14382 dimensions show 0 (typical details) and 2 empty texts; sheet 0000 empty |
| Scratch project (real runs, never saved) | ALL CHECKS AS EXPECTED. Task 04 linked origin to origin: get_coordinates link= reports offset 0, 0, -100000 mm (Task 04's shared elevation), agree false. link_set pin / overlay / relative path. Wall placed through a linked column: link_clashes found it (0.285 m3), 4 clashes in all, also with link_category columns only. link_reload (same file and from path), missing file refused, link_unload, clash on the unloaded link refused, reload again, for=me refused (not workshared). Project discarded |

Not run: CAD link reload, Revit 2027 on a model, a workshared model for unload for=me.

## 2026-11-25 · Version 0.17.1 (review of the ChatGPT audit, merged and tested)

Source: `Desktop\Revit MCP - ChatGPT\revit-mcp` (a copy of 0.17.0 plus the audit below, with
`docs/0.17.0-AUDIT-CHANGES.md` and `docs/PROTOCOL_AUDIT.md`). An earlier draft of the same patch had also been written
straight into this folder at 00:45 on 25 Nov (7 files, after the 0.17.0 package was built at 00:32, so the release was
not affected); every line of it is in the final audit version, which replaced it.

Review, file by file against the GitHub release (de44491): the claims in the change log match the code. Kept all of it.
Two corrections:
- EditReceipts: the audit marked a receipt "running" before DocumentTarget / EditEngine checks, so a refused request
  (Revit busy with a command, family document, read-only model, bad units) would have come back as "edit outcome
  unknown" and locked its operation_id. Now `Rc.Started` is called by EditEngine just before the transaction.
- Committed edit whose save / export failed afterwards: the audit made it a tool error; the error now starts with "The
  model changes WERE COMMITTED ... Do not send this edit again" (smoke test with a fake post-commit failure).
- BridgeServer.Start: a 5 s timeout on the new default-session lock no longer stops the bridge.

Tests: smoke suite ALL OK (cloud); 2024 and 2027 builds 0 warnings / 0 errors (cloud and laptop). Laptop:

| Check | Result |
| --- | --- |
| build_package.bat | Both years built here, mcp 1.30.0 / jsonschema in the exe, RESULT=OK |
| install.bat | Add-in 2024 and 2027 installed; server exe NOT replaced: in use by Claude Desktop / ChatGPT desktop (close them first) |
| tests/exe_check.py on the new dist exe | MCP handshake (protocol 2025-11-25), ping 0.17.1 on Task 04, unknown argument refused, `dryRun` refused |
| same on the installed 0.17.0 exe | Unknown argument silently accepted; `dryRun` reached Revit and is now refused by the 0.17.1 add-in |
| tests/live_p89.py task04 (dry runs) | ALL CHECKS AS EXPECTED; Task 04 unchanged (27/64/21/7/33, 273 rebar, no unsaved changes) |
| Default session | bridge.json written at start-up through the new lock |

Installer, MCP client settings (Mohamed: on some PCs "revit" was missing from Claude Desktop's config). The settings
were written by configure_clients.ps1: blocked where Group Policy forbids PowerShell scripts, skipped when Claude
Desktop had never been started (no folder), exposed to Claude Desktop writing its settings back while running, and
its messages never reached install_result.txt. Now `revit-mcp-server.exe --configure-clients <exe>` (server/
client_config.py) does it; install.bat logs every line and stops while claude.exe runs (/claude_open overrides);
uninstall.bat uses `--remove-clients`. Smoke test covers merge, Store copy, Codex, removal, broken JSON. Live, the new
dist exe on this laptop: Store copy (LocalCache\Roaming\Claude) "already configured"; the normal
%APPDATA%\Claude file did not have the same entry and was updated (backup next to it); Codex rewritten (backup).
So on the Microsoft Store install the two copies can differ: both are written and read back now.

The exit traceback "I/O operation on closed file" printed by the frozen exe when its client closes is the same in 0.17.0.
Open from the audit (not done): export publication race, main-thread queue lifetime, per-process audit log, workbook
size budgets, verifying prebuilt DLLs. Two Revits at once and Revit 2027 not re-run on 0.17.1.

## 2026-11-25 · Systems and protocol audit of the 0.17.0 source

| Files | Changes and reason |
| --- | --- |
| `server/server.py` | Validated and closed tool envelopes, separated unknown-tool protocol errors from execution errors, checked edit modes and options, added a per-process invocation limit, corrected selection and annotations, and propagated cancellation to worker checkpoints. |
| `server/bridge_client.py` | Validated discovery records, retained the identity of an explicitly selected process, recovered from an invalid default when another session is available, bounded reply read time, validated envelopes and carried indeterminate edit receipts back to clients. |
| `server/request_lifetime.py` (created), `server/xlsx_util.py` | Added a cancellation signal transferable to worker threads. Check before dispatch or workbook publication so an abandoned worker can stop before its next side effect. |
| `server/catalog.py`, `server/requirements.txt` | Corrected client-facing claims about exports and selection. Pinned the audited MCP SDK and declared direct dependencies. |
| `server/excel_io.py` | Rejected duplicate normalized input headers before row dictionaries can discard values. |
| `plugin/App.cs`, `plugin/Bridge/BridgeServer.cs` | Clean up failed startup, serialize default ownership decisions across Revit processes, bound network writes and discard unterminated request frames. |
| `plugin/Bridge/EditReceipts.cs` | Reserved uncertain execution outcomes against retry and bounded the number of session receipt IDs while preserving deduplication. |
| `plugin/Core/Json.cs`, `plugin/Core/Args.cs` | Tightened numeric and string parsing and added a checked whole-number conversion for Revit integer values. |
| `plugin/Edit/EditEngine.cs`, `plugin/Edit/ManageOps.cs` | Refuse unknown batch keys, misplaced or null preview flags and unknown or null purge options before model changes. |
| `plugin/Edit/DataOps.cs`, `plugin/Edit/DepthOps.cs` | Use checked integer conversion for global integer parameter values and integer schedule filters. |
| `plugin/Core/Describe.cs`, `plugin/Read/DepthRead.cs` | Share the eight-corner box transformation, accumulate unrounded solid volume, bound geometry traversal and replace repeated group and view scans with lookups. |
| `tests/live_p89.py` | Pin the checked document and process, isolate export paths, and fail the run on failed checks or post-commit work. |
| `tests/fake_bridge.py`, `tests/smoke_test.py` | Added rollback, argument and ambiguous-mode regressions. Run fake processes directly so their Windows PIDs match the session files. |
| `build_package.bat` | Propagate compiler and copy failures during per-year staging to the overall build. |
| `docs/PROTOCOL_AUDIT.md` (created), `docs/IMPLEMENTATION_LOG.md` | Recorded severity, source locations, corrections, open risks, specification links, test coverage and the purpose of every changed component. |

No release was published. Native live tests and validation of a newly packaged installer remain pending.

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
