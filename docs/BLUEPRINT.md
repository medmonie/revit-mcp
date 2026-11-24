# Revit MCP - developer blueprint and handover

Version 0.17.0 · 24 Nov 2026 · Mohamed Ahmed Hamed. Live copy: https://claude.ai/artifact/259SEYX5NoeuYK1zCEqWkp

## Start here

Revit MCP is Mohamed's local bridge between AI clients (Claude Desktop, ChatGPT desktop, Codex) and Autodesk Revit 2024
and 2027. It reads, checks, quantifies, models and documents the open model. It never designs. The current version is
**0.17.0**.

| Item | Value |
| --- | --- |
| Source on the laptop | `C:\Users\mohamed.hamed\Desktop\Revit MCP\revit-mcp` |
| GitHub | [medmonie/revit-mcp](https://github.com/medmonie/revit-mcp) (public, PolyForm Noncommercial 1.0.0) |
| Last pushed commit | 0.17.0 (25 Nov 2026; hash in `publish_result.txt`) |
| Last release | [v0.17.0](https://github.com/medmonie/revit-mcp/releases/tag/v0.17.0) with `RevitMcp.zip` and `RevitMcp-source.zip` |
| Installed on the laptop | 0.17.0, add-in for 2024 and 2027, server exe in `%LOCALAPPDATA%\RevitMcpBridge\` |
| Tested live | 0.17.0 on Revit 2024: phases 8 / 9 by tests/live_p89.py (Task 04 dry runs, scratch-project real runs); 0.16 link reads. Revit 2027 not yet run on a model |
| Not yet done | Revit 2027 live run; DGN / gbXML export; install check on the domain controller |
| Related docs | Coverage, audit and roadmap: https://claude.ai/artifact/8CA4XSykMoVujLZVs6DJep, `ROADMAP.md`, `CHANGELOG.md`, `docs/ACTIONS.md`, `docs/IMPLEMENTATION_LOG.md` |

**Paste this into a new chat to continue:**

```
Continue developing my Revit MCP. Read the blueprint first: docs/BLUEPRINT.md in
C:\Users\mohamed.hamed\Desktop\Revit MCP\revit-mcp (stage it from my computer), then ROADMAP.md and the top of
CHANGELOG.md. Follow its rules exactly: build the package and test every change yourself on my laptop,
dry runs only on Task 04, never edit or sync the KTR15 central, no design logic in the MCP.
Next task: <what you want, e.g. Phase 6 - read elements inside linked Revit models>.
```

## Rules that never change

These come from Mohamed and override any convenience. Read them before the first tool call of a new chat.

1. **No design.** The MCP models, reads, checks and documents. It never sizes members or reinforcement and applies no
   code (ECP 203, ACI, EC2). Every engineering value is required input with no default. A missing value stops the
   operation with a list of what to give (the `Need` helper in `RebarOps.cs`). When geometry cannot take a value, the
   result says so instead of changing it silently. Measurement conventions (steel 7850 kg/m3, formwork rules, join cut
   order, CAD tolerances) are allowed, but each must be overridable and reported.
2. **KTR15 central** (Autodesk Docs, workshared) is never edited: reads only, no dry runs, no sync. Edit tests on KTR15
   run only on a detached copy.
3. **Task 04** (`Desktop\New folder\Revit Task 04_Mohamed A.Hamed_66088960.rvt`) is Mohamed's real file. Edits there are
   dry runs only. A real run is allowed only for rebar, and it is deleted straight after. Never save it.
4. **Build and test every change yourself** on the laptop: compile check, package build, install, then a live test in
   Revit. Report results, not plans. Fix what fails, rebuild and retest before reporting.
5. **Revit's edit permission dialog** ("Claude - allow editing in Revit?") is Mohamed's approval. Never click it. Trigger
   it, ask him to click Yes, then confirm with `get_edit_permission`.
6. **Unsigned add-in prompt** after each rebuild: choose **Load Once** (no stored setting changes) unless Mohamed says
   otherwise.
7. **Git:** push only when asked, with `publish_to_github.bat`. Commit messages end with the session's attribution
   lines. Never commit `tools/commit_message.txt`, `refs/`, `dist/` or logs (all gitignored).
8. **Synchronise with central**, when built (phase 12), needs its own confirmation every time and never runs inside an
   edit batch.

## Architecture

The bridge is two programs on one PC: a Python MCP server the AI client starts, and a C# add-in inside each running
Revit. Nothing leaves the machine except the results returned to the client.

```
AI client (Claude Desktop, ChatGPT desktop, Codex)
   |  stdio: MCP tool revit (or revit_read + revit_edit with REVIT_MCP_SPLIT=1)
   v
MCP server  revit-mcp-server.exe  (server/*.py)
   catalog.py        every action, parameter and description Claude sees; BRIDGE_VERSION; NO_DESIGN_NOTE
   server.py         dispatch; answers help, Excel round trips, QS and BBS workbooks itself
   bridge_client.py  finds running Revits, selects one, adds the document_id to edits
   |  TCP 127.0.0.1, random port + per-session token (bridge.json)
   v
Add-in  RevitMcpBridge.dll, one per Revit year (net48 for 2024, net10 for 2027)
   BridgeServer      socket listener and token check
   MainThread        queues each request for Revit's main thread (ExternalEvent)
   Registry          read actions (never change the model)
   EditEngine        one transaction per batch; dry run rolls back; after-commit work
   WriteGate         one Yes per Revit session;  EditReceipts: results by operation_id;  Audit: audit.log
   Data folder       %LOCALAPPDATA%\RevitMcpBridge (bridge.json, audit.log, the server exe)
   |  Revit API
   v
Revit 2024 / 2027 and the open model (several Revits can run at once; each writes its own port and token)
```

A read goes straight through. An edit first passes the one-time permission in Revit, then runs as one transaction
(rolled back for a dry run). Its result is stored as a receipt under its `operation_id`, so a lost reply can be
recovered with `get_operation_status`. The server's own actions (help, Excel, QS and BBS workbooks, session selection)
reach Revit only for the data they read.

## Repository map

| Path | What it holds |
| --- | --- |
| `plugin/App.cs` | Add-in entry point and the ribbon buttons (MCP Status, Allow Editing, Stop Editing) |
| `plugin/BridgeInfo.cs` | `Version` (must equal `BRIDGE_VERSION` in `catalog.py`), protocol, per-user data folder |
| `plugin/RevitMcpBridge.csproj` | net48 for 2024, net10.0-windows for 2027 (net8 for 2025/26); `REVIT<year>` define; `Nullable` disabled; `LangVersion` latest |
| `plugin/Bridge/` | `BridgeServer.cs` (127.0.0.1 socket, random port, token), `MainThread.cs` (ExternalEvent), `EditReceipts.cs` (operation_id receipts, last 128) |
| `plugin/Core/Args.cs` | Request parameters: `Str`, `NeedStr`, `Dbl`, `NeedDbl`, `Int`, `Flag`, `Choice`, `NeedRef`, `Has` |
| `plugin/Core/Units.cs` | All unit conversion: `U.In`, `U.Out`, `U.Len`, `U.LenMm`, `U.Mm`, `U.NeedPt`. Revit works in feet |
| `plugin/Core/Registry.cs` | Read action registration: one `Read("name", ...)` line per action; `Rc` gives `rc.Doc` / `rc.UiDoc` |
| `plugin/Core/WriteGate.cs`, `Audit.cs` | Edit permission dialog and `audit.log` |
| `plugin/Core/Find.cs`, `Describe.cs`, `Materials.cs` | Shared lookups (levels, types, views, built-in parameters by name), element output, materials |
| `plugin/Core/Geo.cs`, `Poly.cs`, `Cad*.cs`, `BarStock.cs` | Solids and overlaps, 2D polygons, CAD readers, bar stock splitting |
| `plugin/Core/DocumentTarget.cs` | Which document an edit targets (`document_id`) |
| `plugin/Core/LinkScope.cs` | Read inside a Revit link: resolves `link` (instance id, type id, name, file), `coordinates` host / link, the link transform and the `link` block of results. Reuse it for clash and other link features |
| `plugin/Read/*Read.cs` | Read actions, one file per group (Model, Element, Quantity, Qa, Overlap, Rebar, Link, Family, Detail, Manage, Api) |
| `plugin/Edit/EditEngine.cs` | One transaction per batch, dry run rollback, purge-only preview, after-commit actions, failure handling |
| `plugin/Edit/EditContext.cs` | Per-batch context: `c.Doc`, `c.Elements(p, max)`, `Loop`, `AfterCommit`, detach rule for joins |
| `plugin/Edit/*Ops.cs` | Edit operations by group (Structure, Model, Modify, Rebar, Detail, Doc, View, Sheet, Opening, Link, Export, Manage, Material, Family, Fix, Cad, CadMember, Datum, Annotation) |
| `server/server.py` | MCP tool(s), dispatch, `LOCAL_READ` / `LOCAL_EDIT` server actions |
| `server/catalog.py` | All wording Claude sees: `READ_GROUPS`, `EDIT_GROUPS`, `LOCAL_ACTIONS`, `UNITS_NOTE`, `NO_DESIGN_NOTE`, `BRIDGE_VERSION` |
| `server/bridge_client.py` | Connection, token, session selection: the only listening Revit, else the default for reads; edits need `select_revit` when several are open |
| `server/qs_report.py`, `bbs_report.py`, `excel_io.py`, `xlsx_util.py` | Workbooks: QS, BBS, Excel round trips |
| `tests/smoke_test.py` | No Revit needed: fake bridge, catalogue vs source names and parameter names, versions, README counts |
| `tests/live_read.py` | Read checks against the open model |
| `tests/live_two_revits.py` | Several Revit sessions: default, session files, automatic target, edit refusal; `run_two_revits_check.bat` appends to `tests\results\two_revits.txt` (`diag_sessions.bat` lists the raw files) |
| `tests/live_p89.py` | Phase 8 / 9 live test straight to the add-in: `run_live_p89_task04.bat` (dry runs, refuses other models), `run_live_p89_scratch.bat` (real runs, only in a never-saved project); output `tests\results\` |
| `tools/gen_docs.py` | Regenerates `docs/ACTIONS.md` from the catalogue |
| `tools/publish_release.ps1`, `release_notes.md` | GitHub release with assets |
| `package/` | `install.bat`, `uninstall.bat`, `configure_clients.ps1`, `check_user.ps1` (stops an install under another account than the desktop user), `INSTALL.txt` (go into `RevitMcp.zip`) |
| `build.bat` | Builds and deploys the add-in to `%APPDATA%\Autodesk\Revit\Addins\<year>\`; copies `RevitAPI.dll` / `RevitAPIUI.dll` to `refs\<year>\` |
| `build_package.bat` | Builds both years without deploying, PyInstaller server exe, `dist\RevitMcp\` + `dist\RevitMcp.zip` + `dist\RevitMcp-source.zip`; log `package_build.txt` |
| `publish_to_github.bat` | fetch, reset --mixed to origin/main, add, commit with `tools\commit_message.txt`, push; log `publish_result.txt` |
| `publish_release.bat` | Release tagged from `BRIDGE_VERSION`, uploads both zips; log `release_result.txt` |
| `setup_server.bat` | Development server venv and client snippets (option B install) |

## How to add a feature

Every feature is three edits in three files plus regenerated docs. The smoke test fails if any of them disagree.

**Read action**

1. Add a method to `plugin/Read/<Group>Read.cs`: `public static object Name(Rc rc, Obj p)`.
2. Register it in `plugin/Core/Registry.cs`: `Read("name", GroupRead.Name);`.
3. Add the entry to `READ_GROUPS` in `server/catalog.py`: `"name": ("summary", {"param": "desc", "optional?": "desc"})`.

**Edit operation**

1. Add a method to `plugin/Edit/<Group>Ops.cs`: `private static object Name(EditContext c, Obj p)`.
2. Register it in that file's `Register`: `ops["name"] = Name;`. A new Ops file also needs `NewOps.Register(Ops);` in
   `EditEngine.cs`.
3. Add the entry to `EDIT_GROUPS` in `catalog.py`.
4. Work that cannot run inside the batch transaction (export, family edit, save) goes to `c.AfterCommit`, which a dry
   run skips.

**Server action** (answered by Python): add a function in `server.py`, put it in `LOCAL_READ` or `LOCAL_EDIT`, add the
entry to `catalog.LOCAL_ACTIONS`.

**Then, every time:**

1. Bump the version if behaviour changed (release section).
2. `python tools/gen_docs.py` regenerates `docs/ACTIONS.md`.
3. Update the README: section 4 workflow table, section 5 reference, and the action counts in section 5 (the smoke
   test checks the counts).
4. Add a `CHANGELOG.md` entry, and an `IMPLEMENTATION_LOG.md` entry for live test results.
5. Update the feature's status in `ROADMAP.md`.

**Conventions**

- Lengths cross the bridge only through `U.In` / `U.Out` / `U.Len` / `U.LenMm`. No unit literals elsewhere. Request
  default unit is mm.
- Built-in parameters are looked up by name at run time (`Find.Builtin`), so a name missing in one Revit year cannot
  break the other build.
- Version-specific API goes behind `#if REVIT2024` / `#else`. Use `ElementId.Value` (present from 2024), and avoid APIs
  added after 2024 unless guarded.
- `Nullable` is disabled: no `string?` / `object?` annotations (CS8632 warnings). The build must have 0 warnings.
- Design values: use `Need` (in `RebarOps.cs`), or a required parameter (`NeedDbl`, `NeedRef`). Never a numeric default
  for an engineering value. Defaults are allowed only for display, tolerances and measurement conventions, and each
  must be reported in the result.
- Fit, don't change: when geometry limits a value, clip it and say so in the result ("bend_leg 250 limited by the
  column width inside the ties").
- Errors are `ArgumentException` with a message telling the user exactly what to give or fix.
- Results are plain objects. Lists over their limit set `truncated: true`.
- Keep files' line endings. The `.bat`, `.cs` and `.md` files are CRLF, so Python rewrites must preserve `\r\n`.

## Build, install and test loop

One change takes about ten minutes end to end. The laptop is reached through the device tools: staging and committing
files, File Explorer by clicks, and the Revit MCP tool. The cloud workspace starts empty in every new chat.

**1. Set up the workspace (once per chat)**

1. Stage the sources from the laptop with `device_stage_files` (at most 50 files per call; list folders with
   `device_list_dir`). They land in `/mnt/user-data/uploads/Revit MCP/revit-mcp/`. Copy them to a working folder such
   as `/home/claude/revit-mcp`.
2. Stage `refs\2024\RevitAPI.dll`, `RevitAPIUI.dll` and the same for `refs\2027` (written by `build.bat`).
3. Compile check: two small projects, one per year, that include `plugin/**/*.cs` and reference those DLLs, with the
   same settings as `RevitMcpBridge.csproj` (`RevitYear`, net48 or net10.0, `Nullable` disabled, `LangVersion` latest,
   `REVIT<year>` define). Run `dotnet build -c Release` for both. nuget.org is blocked in the cloud workspace, so:
   `apt-get install dotnet-sdk-10.0 mono-devel`; the 2027 project targets plain `net10.0`; the 2024 project targets
   `net48` with `FrameworkPathOverride=/usr/lib/mono/4.8-api/` and `AutomaticallyUseReferenceAssemblyPackages=false`
   (no WPF assemblies are needed). Worked on 24 Nov 2026. `build_package.bat` on the laptop stays the final check.
4. Smoke test: `pip install mcp openpyxl --break-system-packages`, then `python tests/smoke_test.py` ends with `ALL OK`.

**2. Make the change** in the working copy, keeping CRLF. Run the compile check for both years (0 errors, 0 warnings),
`python tools/gen_docs.py` and the smoke test.

**3. Write the files back to the laptop**

1. Copy each changed file to `/mnt/user-data/outputs/<batch>/<same relative path>`.
2. `device_commit_files` with `force: true` and the full device path under
   `C:\Users\mohamed.hamed\Desktop\Revit MCP\revit-mcp\`.
3. Re-stage one or two of the files and `cmp` them against the working copy (the file store lagged once).

**4. Grant computer use** (once per chat): `computer_resolve_access` then `computer_request_access` for File Explorer
(click tier only: no typing), Revit 2024 and Revit 2027.

**5. Build the package** (Revit can stay open)

1. Open File Explorer, click Desktop > Revit MCP > revit-mcp (screenshot each step; positions move).
2. Double-click `build_package.bat` and wait about 100 s.
3. Stage `package_build.txt`: `Build succeeded` twice, `0 Warning(s)` and `0 Error(s)` twice, `RESULT=OK` at the end.

**6. Install**

1. `get_project_info` must show `hasUnsavedChanges: false`. Otherwise stop and ask.
2. Close Revit: click its taskbar icon, then the window's X. Do not use `open_application` to bring Revit forward: it
   starts a second Revit.
3. In File Explorer open `dist\RevitMcp` and double-click `install.bat` (it refuses to run while Revit runs).
4. Stage `dist\RevitMcp\install_result.txt`: `INSTALLED 2024`, `INSTALLED 2027`, `SERVER ...revit-mcp-server.exe`,
   `CLIENTS configure_clients.ps1 exit 0`, `RESULT=OK`. The console window stays open; ignore it.

**7. Reopen and test**

1. `open_application` Revit 2024, wait about 45 s, and click **Load Once** in the unsigned add-in dialog.
2. Open Task 04 from **File > Recent Documents** (first entry). Revit's Open dialog and Home page block computer-use
   input.
3. `ping` must report the new `bridgeVersion`.
4. Send the first dry-run edit: it returns "not granted" and opens the permission dialog. Ask Mohamed to click **Yes**,
   then check `get_edit_permission`.
5. Run the test set, then confirm `hasUnsavedChanges: false` and the unchanged rebar count.

Add-in changes need only a Revit restart. Catalogue wording reaches Claude only after Claude Desktop restarts, because
it runs the installed server exe.

## Release loop

A release is a version bump, a push and a GitHub release, all run from the laptop by double-clicking. Chrome uploads
are no longer used: they were slow.

**Version bump checklist** (the smoke test checks the first three):

- [ ] `plugin/BridgeInfo.cs` `Version = "X.Y.Z"`
- [ ] `server/catalog.py` `BRIDGE_VERSION = "X.Y.Z"`
- [ ] `README.md`: line 3 `Version X.Y` and install step 5 `(X.Y.Z)`
- [ ] `CHANGELOG.md`: a new `## X.Y.Z - date` heading at the top
- [ ] `ROADMAP.md`: `Status as of version X.Y.Z`, plus feature statuses
- [ ] `tools/release_notes.md`: the "What is new" section
- [ ] `docs/IMPLEMENTATION_LOG.md`: live test results

**Steps**

1. Write `tools/commit_message.txt`: subject, blank line, body, then the session's attribution lines (gitignored).
2. Double-click `publish_to_github.bat`; `publish_result.txt` ends with `RESULT=OK` and shows the new commit hash. Git
   Credential Manager already holds the GitHub sign-in.
3. Run `build_package.bat` if the package is older than the last change.
4. Double-click `publish_release.bat`: creates or updates release `v<BRIDGE_VERSION>` and replaces same-name assets.
   `release_result.txt` lists both uploads with sizes and ends with `RESULT=OK`.
5. Check `https://github.com/medmonie/revit-mcp/releases/expanded_assets/v<version>` with Chrome `get_page_text`:
   `RevitMcp.zip` (about 26 MB) and `RevitMcp-source.zip`.

## Test models and the standard test set

Task 04 baseline (the check that nothing changed): 27 columns, 64 framing, 21 walls, 7 floors, 33 foundations,
273 rebar, 8 grids, 4 levels, no unsaved changes.

| Model | Use | Rule |
| --- | --- | --- |
| Revit Task 04 (`Desktop\New folder`, not workshared, Revit 2024) | All read and edit tests | Dry runs only; a rebar real run is deleted after; never saved |
| KTR15 central (Autodesk Docs, workshared) | Read tests only | No edits, no dry runs, no sync |
| KTR15 detached copy | Workshared edit tests (join_overlaps, worksets) | Real runs allowed; open it detached |
| Second model with drafting views | views_copy_from, standards_copy | Not set up yet |

| Task 04 element | Id | Notes |
| --- | --- | --- |
| Ground column 300x600 on footing F3 | 2187231 | Column above: 1713154 |
| Upper column 300x600 | 1713154 | Sits on 2187231; no column above |
| Footing F3 (under 2187231) | 2158901 | |
| Footing F4 | 2158895 | Shallow: U-bar legs limited to 218 mm |
| Beam 200x600, FF, between columns | 1723276 | Clear span 5800; supports 2187193 and 2187231 |
| Beam 200x1000, FF, one free end | 1722045 | Clear span 800 |
| Curved beam | 2225279 | For curved-member tests (not reinforceable yet) |
| Slab 140 FF | 1739650 | For rebar_area |
| Levels | FOUNDATION PLAN, GROUND FLOOR, FIRST FLOOR FRAMING PLAN, ROOF FRAMING PLAN | |

Ids can change if Mohamed edits the file. Confirm them with `get_elements` first.

**Standard test set for each change**

1. `ping` returns the expected `bridgeVersion`.
2. Every new or changed action with full values, as a dry run; check each number in the result against the inputs.
3. The same action with values missing: the error lists exactly what is missing, and nothing that depends on a choice
   not yet made.
4. One case where geometry limits a value: the result says so.
5. Read-only regression: `get_project_info`, `takeoff` on columns, `rebar_list` on one host, `model_health`.
6. At the end, `get_project_info` shows `hasUnsavedChanges: false` and the baseline counts.
7. Record the results in `docs/IMPLEMENTATION_LOG.md`.

## Known pitfalls

| Pitfall | Fix |
| --- | --- |
| Mass delete followed by a dry-run rollback crashed Revit (ContentRec.cpp assert, seen in the journal) | Purge-only dry runs open no transaction (`ManageOps.PreviewPurge`); a real purge deletes per element in a SubTransaction. Never roll back a mass delete |
| Rebar sized from joined, cut geometry | `GetOriginalGeometry` + transform for the section; a beam's length from the joined solid |
| A beam's far end at an L-corner read as a free end | Corner probe inside the end, as well as the probe past the end face |
| `dynamic` fails on net48 | Tuples or small classes |
| `string?` annotations give CS8632 warnings | No nullable annotations |
| Python rewrites lost CRLF in `.bat` files | Read and write with `newline=''` and keep `\r\n` |
| `findstr` version parsing in batch files | Read the version in PowerShell |
| `RevitDir` quoted with a trailing backslash | Double the backslash: `"-p:RevitDir=%API%\\"` |
| PowerShell `-File` with an array parameter | Pass a comma-separated string |
| `open_application` on a running Revit starts a second Revit | Bring Revit forward from its taskbar icon |
| Revit's Open dialog blocks computer-use clicks; the Home page web view shows blank | Open models from File > Recent Documents |
| File Explorer is click-only (no typing) | Navigate by double-clicking folders; screenshot each step |
| Chrome `file_upload` only takes device-staged files, max 3.8 MB; GitHub web upload is slow | Use `publish_to_github.bat` and `publish_release.bat` |
| GitHub API asset upload printed empty names | Fixed in `publish_release.ps1`; verify on the expanded_assets page |
| Access to Claude's own config folders is refused | `configure_clients.ps1` writes it; check by restarting Claude Desktop and pinging Revit |
| The device file store lagged once after a commit | Re-stage and `cmp` after every commit |
| Strict detach rule in `EditContext` (ChatGPT 0.13.3): only CannotCutJoinedGeometry on the exact registered pair | Untested on KTR15; test join_overlaps on a detached copy before relying on it |
| `rebar_column` assumes the footing mesh is two layers of the column bar's diameter | Open item: decide on a `footing_mesh_depth` input |
| Task 04 has no Revit links | Test link features in a new unsaved project (Home > Models > New, OK), link Task 04 with `revit_link` (pin false), move / rotate the instance there, close without saving. Task 04 itself stays untouched |
| Link elements are not visible in a host plan whose view range misses their elevations (Task 04 sits at about 98.5-106.9 m) | Use a host 3D view for `view` tests with link |
| Source files are LF, not CRLF (only BLUEPRINT.md is CRLF) | Keep each file's own line endings |
| New actions are refused by the running Claude Desktop (its server exe was started before the install) | Restart Claude Desktop, or test straight to the add-in with a script like `tests/live_p89.py` started by a .bat |
| Revit 2024 refuses `PropertySetElement.SetStructuralAsset` / `SetThermalAsset` on an existing asset ("internal error") | `material_asset_set` always creates a new asset (`PropertySetElement.Create`) and moves the materials to it |
| `AssemblyInstance.AssemblyTypeName` cannot be set before the creating transaction commits | Set it in `c.Defer` (own transaction); assembly views need an assembly from an earlier edit |
| DWF export needs a modifiable document | Export inside a transaction that is rolled back (as IFC) |
| A permission dialog left open makes reads time out after 30 s | Ask Mohamed to click Yes before polling; the test script retries |
| A second Revit took over all calls; closing it left "not reachable" (fixed 0.17.0) | bridge.json = first Revit (or MCP Status), handed over on close; edits need select_revit while several are open |
| `open_application` only brings the running Revit forward | Start a second Revit from Explorer: C:\Program Files\Autodesk\Revit 2024, sort by Type, double-click Revit.exe |
| install.bat run elevated under another account installs into that account's profile | install.bat stops unless the account is the desktop user (`/this_user` overrides) |

## Roadmap and open checks

Phases 8 and 9 are built (0.17.0) and tested live in Revit 2024. Phase 6 is in progress: reading elements inside
linked Revit models is built (0.16.0, `link` parameter on six read actions, `LinkScope`). Next in phase 6: link management (reload, unload, path, pin; size S, also lets the not-loaded
error be tested), base / survey point and shared coordinates read (S), annotation values (S), then clash against a
link (L, uses `LinkScope` and `Geo`). Full list with priority and effort: `ROADMAP.md`.

| Phase | Content | Priority | Effort |
| --- | --- | --- | --- |
| 6 Coordination and links | Linked-model elements, clash against a link, link reload / unload / path / pin, base and survey points, annotation values | High | 3 S, 1 M, 1 L |
| 7 Reinforcement completion | Path reinforcement, fabric, hooks from hook types, rebar tags and views, couplers, sloped and curved members | High / Medium | 1 S, 5 M, 1 L |
| 8 Modify and documentation depth | Flip, end joins, face-based families, groups and assemblies, schedule filters, angular dimensions, more warning fixes | Medium | 3 S, 4 M, 2 L |
| 9 Standards and data | Global parameters, material assets, keynotes, DWF / FBX / gbXML export | Medium / Low | 3 S, 1 M |
| 10 Analytical model and loads | Read analytical members and loads; place loads from given values | Medium / Low | 2 M, 1 L |
| 11 Architectural and site | Pile caps, roofs, stairs, curtain walls, rooms, topography, DirectShape | Low | 1 S, 5 M, 2 L |
| 12 Worksharing | Synchronise with central, reload latest, relinquish; own confirmation each time | Medium | 2 S, 1 M |
| 13 MEP | Read systems and sizes, MEP quantities, crossings feeding opening_create, then modelling | Medium / Low | 4 M, 2 L |
| 14 Steel connections | Read; place connections of a chosen type; plates, bolts, cuts from given values | Low | 1 S, 2 L |

Out of scope by rule: any design (code anchorage and laps, MEP sizing, connection design), point clouds, extensible
storage, creating family geometry, fabrication parts. The Revit API does not allow moving elements into a design
option or creating non-shared project parameters.

**Open checks**

- [ ] Phases 8 / 9 in Revit 2027 (dimension_radial is 2027 only); export_dgn with a setup / seed; export_gbxml with an energy model
- [ ] install.bat user check on the domain controller (elevated under another account must stop)
- [ ] Run 0.16.0 in Revit 2027 on a model (only 2024 is tested), including a link read
- [ ] Link reads with a not-loaded, tilted or mirrored link
- [ ] `join_overlaps` real run on a KTR15 detached copy (strict detach rule)
- [ ] Workset operations on a detached workshared copy
- [ ] `views_copy_from` and `standards_copy` with a second open model that has drafting views
- [ ] Restart Claude Desktop and confirm the installed exe serves the 0.16.0 catalogue (`link` parameters)
- [ ] Decide on `footing_mesh_depth` for `rebar_column`
