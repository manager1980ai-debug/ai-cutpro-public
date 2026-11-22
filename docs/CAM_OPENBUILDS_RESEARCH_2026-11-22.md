# AI CutPro: OpenBuilds plasma CAM / CONTROL research
Date: 2026-11-22. Status: source review and non-executable requirements, **not** machine integration.

## 1. Verified division of responsibilities
- **OpenBuilds CAM:** receives DXF/SVG/images, configures plasma cutting toolpaths and outputs G-code. This is a CAM layer; geometry-only preview is insufficient for a valid manufacturing program.
- **OpenBuilds CONTROL:** connects to a GRBL-compatible controller, loads/runs G-code, exposes machine controls. It must not be confused with the CAM toolpath generator.
- **OpenBuilds Fusion 360 postprocessor** is a third, distinct codebase. Its plasma instructions explicitly list kerf, PierceHeight, CutHeight, PierceDelay, workpiece thickness, Z touch-off probe offset and the pierce delay override. Do not assume all postprocessor features are universally supported by OpenBuilds CAM, CONTROL or every GRBL variant.
- An OpenBuilds support discussion describes a pierce-delay problem with CAM-generated G-code run in CONTROL. This is evidence that timing needs end-to-end validation, not that any delay is guaranteed by CONTROL itself.

## 2. Comparison: OpenBuilds example → AI CutPro requirement → implementation → qualification
| OpenBuilds / source feature | AI CutPro requirement | Planned own implementation | Qualification |
| --- | --- | --- | --- |
| DXF to G-code (CAM) | Separate verified CAD geometry from CAM | New isolated CAM stage after geometry preflight | No CAM approval for current DXF preview |
| CONTROL executes GRBL-compatible G-code | Never send G-code without hardware profile and explicit authorization | Separate controller adapter, disabled by default | Exact controller/firmware not identified |
| Plasma PierceDelay | Nonnegative duration in seconds from verified process profile | Validated data model, later controller-specific postprocessor | No universal delay or safe default |
| PierceHeight / CutHeight | Separate heights and Z-probe/touch-off strategy | Dedicated values and IHS mode, later simulator | Cannot assume Z axis or probe exists |
| Kerf / feedrate | Thickness/material-specific process configuration | Explicit profile with provenance and operator approval | Values must come from source's cut chart |
| Torch enable/disable, pierce pause | Ordered machine-state plan, not a blind timer | Future non-executable state-machine simulation | M3/M5/G4 semantics depend on dialect |
| Arc OK and THC | Feature-gated machine IO and height control | Capability negotiation in controller profile | OpenBuilds docs reviewed do not establish universal availability |
| Lead-in/out and inside-first | Pierce off the finished edge, prioritize voids | Topology-aware path planning after industrial CAD validation | Not authorized for current synthetic contours |

## 3. Verified source register
1. [OpenBuilds Software](https://software.openbuilds.com/) — high-level CAM/CONTROL roles.
2. [Official OpenBuilds CAM documentation](https://docs.openbuilds.com/doku.php?id=docs%3Asoftware%3Aopenbuilds-cam) — software documentation entry.
3. [Official OpenBuilds CONTROL documentation](https://docs.openbuilds.com/doku.php?id=docs%3Asoftware%3Aopenbuilds-control) — controller/G-code documentation entry.
4. [OpenBuilds CAM GitHub](https://github.com/OpenBuilds/OpenBuilds-CAM) — source repository, **AGPL-3.0**; do not copy source into AI CutPro without license review.
5. [OpenBuilds Fusion360 plasma postprocessor instructions](https://github.com/OpenBuilds/OpenBuilds-Fusion360-Postprocessor/blob/master/README-plasma.md) — exact documented plasma tool parameters and probe guidance.
6. [OpenBuilds pierce-delay support case](https://builds.openbuilds.com/threads/pierce-delay-issue-with-diy-plasma-table-openbuilds-control.16939/) — field issue illustrating cross-component timing.
7. [Avid CNC plasma setup checklist](https://www.avidcnc.com/cdata/cad/2018Q1/Plasma_CNC_User_Operations_Checklists_v0001.pdf) — independent manufacturer's checklist for kerf, feed, pierce delay/height and cut height.

## 4. Acceptance and uncertainty
- Source review is done at **architecture and parameter requirements** level, not a line-by-line code audit of OpenBuilds.
- Review of an actual controller profile, firmware, motion interface, arc signal, THC and documented G-code dialect remains **OPEN** until the exact equipment is selected.
- Neither OpenBuilds examples nor internet examples supply universal, safe process values for the user.
- No proprietary/source-code transfer from OpenBuilds; design ideas are independently implemented.
- No physical output or G-code generation is authorized by this document.
