# API alignment audit (0.18.1, 25 Nov 2026)

Question: do the built features (0.1 to 0.18.0) use the Revit API's types and methods as the API intends, in Revit 2024
and 2027? Three layers were checked. Reference data: the API metadata inventory of RevitAPI / RevitAPIUI 2024 and 2027
(2,442 types) behind the capability map https://claude.ai/artifact/1TPLW8RrsshSPtNaz7fxiJ.

## 1. Every API member the add-in references

Read from the compiled add-ins' metadata (System.Reflection.Metadata, every TypeRef / MemberRef into RevitAPI*), not
from the source, so nothing reached through `var` or extension calls is missed.

| | Revit 2024 add-in | Revit 2027 add-in |
| --- | --- | --- |
| API types referenced | 362 | 359 |
| API members referenced | 986 | 984 |
| Missing from that year's API | 0 | 0 |
| Marked obsolete in that year's API | 0 | 0 |

Unresolved in the inventory: only nested classes (`SpecTypeId.Boolean`, `SpecTypeId.String` ...,
`BuiltInFailures.JoinElementsFailures`) and `==` operators, which the inventory does not list separately. The build
does not suppress CS0618, so the 0-warning builds already rule out any `[Obsolete]` member. APIs obsolete or replaced in
2027 (EnergyDataSettings.GetFromDocument, EnergyAnalysisDetailModel.Create(doc, options), Rebar.CreateFromCurves with
hook types) were already behind `#if` guards.

## 2. How the API is used (patterns)

| Check | Result |
| --- | --- |
| Transactions | Edits: one Transaction per batch in EditEngine, failure preprocessor on it. Reads: none start a transaction or call Regenerate (IFC / DWF export use a rolled-back transaction, as the API needs a modifiable document). Family edits: EditFamily after the commit, own transaction in the family document, LoadFamily with an IFamilyLoadOptions. Link reload / unload: after the commit. |
| Failures | IFailuresPreprocessor deletes warnings (reported), rolls back on errors (reported), resolves only the join failure it created. Forced modal handling, clear after rollback. |
| **Dry runs** | **Fixed.** Revit checks failures only when a transaction commits, never on Regenerate, so a dry run could report success for a batch Revit would roll back, and never showed commit warnings. Now, on a model that is not workshared, the dry run commits inside a TransactionGroup that is rolled back (the API's own pattern for trial commits). Workshared models keep the plain roll-back, as a commit there may borrow elements from the central. |
| **Units of parameter values** | **Fixed.** Values were classified by `SpecTypeId.Length` / `Area` / `Volume` / `Angle` only. Revit has many other specs with a length dimension (bar diameter, rebar cover / spacing / length, section dimension, displacement ...); they came back in the project display unit whatever `units` was, and set_parameters, filters, schedule filters and global parameters read numbers for them the same way. Now one classifier (U.KindOf) asks the API: a spec whose valid units include millimetres and whose internal value converts at 304.8 mm per unit is a length (same test for m2, m3, degrees; slope, a ratio, is not an angle). Used in all five places; parameterDetails now name the unit of every measured value. |
| Geometry | Options: fine detail, no hidden objects; references only where dimensions need them; View and DetailLevel never set together. **Fixed:** overlap volume tried all solid pairs in one try block, so one pair Revit refuses to intersect blanked the whole result; now per pair (clash already was). |
| Parameters | StorageType and IsReadOnly checked before Set; built-in parameters before names (LookupParameter only as a fallback or for shared rebar shape letters A..K). |
| Rebar | RebarHostData.IsValidHost before every creation; layout rules through RebarShapeDrivenAccessor with the documented arguments; 2024 / 2027 creation overloads guarded. |
| Element ids | ElementId.Value (Int64) throughout; no IntegerValue. |

## 3. Built actions against the API's options

Members new in the 2027 API on the types the add-in already uses were listed (Wall attachments and wrapping, Rebar
splices / cranks / custom spacing / Mass, RebarBarType mass and lap lengths, SlabShapeEditor.AddPoint, PDF export in
background, Viewport anchors, SiteLocation.SetLatitudeAndLongitude ...). One was a correctness point and is used now:

- **rebar_list weight (fixed):** on Revit 2027 the weight is Revit's own `Rebar.Mass` / `RebarInSystem.Mass` (from the
  bar type's mass per unit length) with `weightSource: "model"`; Revit 2024 has no mass in the API, so d^2/162.2 kg/m
  stays, labelled `"d2/162"`.

The others are new capabilities, not misuse; they belong to the roadmap (phase 7 for rebar splices and cranks).

## Live check (Revit 2024, Task 04, tests/live_audit.py: reads and dry runs only)

ALL CHECKS AS EXPECTED: bar diameter 10 mm -> 0.01 m with units m, spacing 175 -> 0.175, shape lengths and bar lengths
follow units; reinforcement volume in m3; every measured parameter names its unit. Two identical walls in a dry run:
trialCommit Committed, group RolledBack, Revit's "Highlighted walls overlap" warning reported. rebar_list weightSource
d2/162. find_overlaps: 18 unjoined pairs, 0.95 m3. Task 04 afterwards: no unsaved changes, same counts. Not run here:
Revit 2027 (rebar mass from the model).
