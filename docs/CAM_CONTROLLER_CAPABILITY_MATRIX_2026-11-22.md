# AI CutPro: controller capability and plasma safety matrix
Date: 2026-11-22. Scope: documented planning contract, no machine commands.

## What is actually established from sources

- OpenBuilds CONTROL is a host that sends controller-compatible G-code and manages machine functions; its source repo is [OpenBuilds/OpenBuilds-CONTROL](https://github.com/OpenBuilds/OpenBuilds-CONTROL). Do **not** assume it certifies any plasma source, torch, THC or Arc OK.
- Standard Grbl 1.1 documents G4 dwell and M3/M4/M5 spindle-state commands. Their presence in the G-code dialect alone does **not** prove a plasma torch is safely wired, an arc has formed, or that Arc OK/THC is available: [Grbl commands](https://github.com/gnea/grbl/blob/master/doc/markdown/commands.md), [Grbl interface](https://github.com/gnea/grbl/blob/master/doc/markdown/interface.md).
- grblHAL may expose additional commands and hardware options; treat it as a different dialect/capability set: [grblHAL reference](https://github.com/grblHAL/grblhal_docs/blob/main/content/markdown/04-Reference/02-complete-gcode-and-mcode-reference.md).
- The OpenBuilds plasma postprocessor documents probing and a pierce delay, but does not establish a universal hardware Arc OK or THC implementation: [README-plasma](https://github.com/OpenBuilds/OpenBuilds-Fusion360-Postprocessor/blob/master/README-plasma.md).
- OpenBuilds CAM is [AGPL-3.0](https://github.com/OpenBuilds/OpenBuilds-CAM); CONTROL's repo advertises GPL-3.0 and its package.json declares AGPL-3.0. Treat this as a **license discrepancy requiring review** before any code reuse; this project uses its own implementation.

## Controller capability record required before any postprocessor
| Capability | Required evidence | Fail-closed behavior |
| --- | --- | --- |
| Controller identity + firmware version | Readable, documented model and firmware | Unknown → no G-code generation |
| G-code dialect + G4 dwell units/limits | Controller/firmware documentation and simulator test | Unknown → no plasma sequencing |
| Torch enable/disable output (e.g. mapped M3/M5) | Machine wiring and controller IO test with torch isolated | Unverified → no torch output |
| Z axis, homing, IHS and touch-off | Axis/probe documentation and test | Missing → cannot perform profile using different pierce/cut heights |
| Arc OK input | Power source & controller IO mapping, polarity, timeout | Required but absent → stop planning |
| THC enable/disable/status | Manufacturer THC manual and operator-approved integration | Requested but unsupported → stop planning |
| Emergency stop + interlock | Independent physical E-stop architecture and safety review | Unverified → no machine operation |
| Communication/queue state | Defined disconnect, alarm, hold and reset semantics | Unknown state → no automatic resume |
| Feed and kerf data | Verified cut chart for exact consumables/material/thickness | Unknown → profile rejected |
| Repeat pierce, small holes | Manufacturer procedure and shop-floor test | No automatic retry strategy |

## Proposed state progression for a **future simulator only**
1. `Unconfigured`: forbid all motion/tool outputs.
2. `ProfileReviewed`: parameters and cut-chart reference validated, machine capabilities known.
3. `GeometryValidated`: verified CAD, waste-side lead and torch clearance available.
4. `SimulationReady`: controller-specific steps planned, operator consent for simulation only.
5. `PiercePositioned`: imaginary tool at verified pierce location and height.
6. `ArcRequested`: simulated torch-request event.
7. `ArcConfirmed`: only if the chosen design requires Arc OK; otherwise explicitly classify as open-loop mode requiring separate approval.
8. `PierceDelayCompleted`: simulated time threshold met and torch condition still valid.
9. `Cutting`: simulated move at established cut height and rate.
10. `TorchOff` → `Retracted` → `Completed`.

**Any** fault (Arc OK missing, timeout, alarm, limit, lost connection, operator stop) transitions to `Fault`; future controller implementation must de-energize outputs via certified safety channels and forbid automatic continuation. In a simulator, no real outputs exist.

## Open validation / blockers
Exact controller, firmware, power supply, torch, THC, IHS/probe, operator handbook, physical wiring and safety circuit are **not known**; therefore **NO machine-specific compatibility claim, G-code template, cut value or production acceptance** is made.

This document closes the **generic controller capability and hazard research deliverable**, not verification of any actual controller, hardware-in-the-loop test or certification. Those remain separate `[ ]` tasks.
