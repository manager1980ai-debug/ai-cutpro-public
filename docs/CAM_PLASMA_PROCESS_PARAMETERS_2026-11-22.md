# Plasma process parameters: AI CutPro specification (research task 2/3)
Date: 2026-11-22. Scope: profile data and planning validation ONLY. **No controller output.**

## Data contract (implemented in `PlasmaProcessProfile`)
All physical values are provided explicitly, not derived from a guessed default:

| Parameter | Unit | Requirement |
| --- | --- | --- |
| Material | description | Required, for operator identification |
| ThicknessMm | mm | Finite positive |
| PowerSourceModel / ControllerModel | identification | Both mandatory; generic GRBL is not an equipment model |
| CutChartReference | vendor manual/chart citation | Mandatory provenance; a nonempty field does not automatically certify that the chart is authentic |
| CurrentAmps | A | Finite positive, source-specific |
| FeedRateMmPerMinute | mm/min | Finite positive, material-specific |
| KerfWidthMm | mm | Finite positive; not a substitute for full contour compensation |
| PierceDelaySeconds | seconds | Finite, >= 0; 0 is structurally allowed but not an assurance of safe cutting |
| PierceHeightMm | mm | Finite positive, greater than CutHeight for supported process profile |
| CutHeightMm | mm | Finite positive |
| OperatorConfirmed | boolean | Required for planning checks; not physical permission |
| RequiresArcOk / ArcOkSupported | boolean | If Arc OK is required but unavailable, reject |
| ThcRequested / ThcSupported | boolean | If THC requested but unavailable, reject |
| ZMotionSupported | boolean | Mandatory for separate Z pierce/cut heights |

## OpenBuilds mapping
The official OpenBuilds Fusion 360 plasma postprocessor documentation specifies *CutHeight*, *PierceHeight*, *PierceDelay*, kerf width, workpiece thickness, Z touch-off, probe offset and a postprocessor override for spindle on/off/delay. These are **not universal values**. In particular, its example heights are examples, not values for our machine.

- [OpenBuilds Fusion360 plasma setup](https://github.com/OpenBuilds/OpenBuilds-Fusion360-Postprocessor/blob/master/README-plasma.md).
- [OpenBuilds CAM](https://github.com/OpenBuilds/OpenBuilds-CAM).
- [Avid CNC preflight cut-chart checklist](https://www.avidcnc.com/cdata/cad/2018Q1/Plasma_CNC_User_Operations_Checklists_v0001.pdf).

## Sequence specification (not implemented)
1. Verify CAD and material/profile provenance and explicit operator selection.
2. Determine hole-first order, waste-side pierce points and safe lead-in/out.
3. Reconcile capabilities of selected controller: IHS/touch-off, Z motion, torch output, Arc OK, THC and pause/stop.
4. In an isolated simulation only: approach; set pierce height; request torch; wait for confirmed arc if required; apply source-specific pierce delay; transition to cut height; cut; turn off torch; retract and check completion.
5. On missing arc, conflicting sensor status, lost controller connection, unavailable THC or timeout: stop simulation and report specific reason. Never blindly continue.
6. Resume and repeated pierce need an independent operator-approved state recovery design. No automatic resume after an unknown torch state.

**Excluded from this delivery:** presets for any real power source, G-code, arc-wiring assumptions, actual THC loop, hardware control, lead-in generation, nozzle wear adjustment, bevel and small-hole process selection, shop-floor acceptance.

## Validation evidence
`PlasmaProcessProfile.ValidateForPlanning()` checks structurally inconsistent parameters, unapproved provenance, unavailable Arc OK/THC/Z capability; the record's `CanStartMachine` is always false. Synthetic core regression tests include negative and nonfinite delays, mismatched heights, unsupported signals, missing cut-chart reference, and unconfirmed operator. No numerical settings are represented as safe for a real machine.


## Virtual pierce-delay rehearsal (implemented, not hardware)
`PlasmaPierceSimulator.Rehearse(profile, inputs)` is a deterministic, synthetic-clock demonstration of the arc confirmation and delay stage. It requires a structurally accepted profile, required/supported Arc OK input, nonfaulted synthetic controller signals, and an Arc OK event within the supplied simulated timeout. The virtual cut phase is reached only if Arc OK stays present through the entire configured delay. Lost arc, timeout, E-stop, alarm, disconnection and invalid numeric inputs give a `Fault` result without a simulated cut phase.

The result exposes `CanStartMachine=false` and `ProducesGCode=false` unconditionally. There is no real time, control signal, output adapter, torch enable, feed, lead-in, hardware stop or actual cutting phase. Inputs are test fixtures, not guarantees of hardware feedback. Acceptance evidence is tracked in `TASKS.md`.


## Bounded multiple-contour rehearsal
`PlasmaPierceBatchSimulator.Rehearse(profile, attempts)` accepts **1–100 uniquely named virtual pierce attempts**, retains their specified order, and runs the previously tested isolated `PlasmaPierceSimulator` for each. At the first arc timeout, lost arc, emergency stop, alarm or invalid profile, it returns a failure mentioning the affected contour, **does not start any subsequent attempt**, and does not automatically retry. Each simulated attempt has its own clock beginning at zero. A completed sequence does not assert that an actual cut, move, pierce, torch-off, retraction, or safe physical cycle occurred.

The batch `Report` always has `CanStartMachine=false`, `ProducesGCode=false`, and `AllowsAutomaticResume=false`. This is not a CAM path generator and it does not derive an actual inside-first order: the supplied labels and ordering are synthetic test inputs. Tests cover full three-contour completion, failure on the second hole before outer contour, lost arc, duplicates, empty/oversized batches and unconfirmed profiles.
