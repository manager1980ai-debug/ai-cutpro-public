"""Independent DXF entity/perimeter verification using ezdxf.

This is an external serialization check, NOT an independent CAD topology
certificate or authorization to create toolpaths.
"""
import json
import math
import pathlib
import sys

import ezdxf


def check_sample(root: pathlib.Path, case: dict) -> None:
    path = root / case["file"]
    doc = ezdxf.readfile(path)
    auditor = doc.audit()
    if auditor.has_errors:
        raise AssertionError(f"{path.name}: ezdxf auditor errors: {auditor.errors}")
    if doc.dxfversion != "AC1032":
        raise AssertionError(f"{path.name}: unexpected DXF version {doc.dxfversion}")
    if int(doc.header.get("$INSUNITS", 0)) != 4:
        raise AssertionError(f"{path.name}: units not declared as millimetres")
    entities = list(doc.modelspace())
    types = [e.dxftype() for e in entities]
    if set(types) - {"LINE", "ARC"}:
        raise AssertionError(f"{path.name}: unsupported entities {sorted(set(types))}")
    if types.count("LINE") != case["lines"] or types.count("ARC") != case["arcs"]:
        raise AssertionError(
            f"{path.name}: expected {case['lines']} LINE/{case['arcs']} ARC, "
            f"found {types.count('LINE')}/{types.count('ARC')}"
        )

    radii = []
    perimeter = 0.0
    for entity in entities:
        if entity.dxftype() == "LINE":
            start, end = entity.dxf.start, entity.dxf.end
            perimeter += math.dist(start, end)
        else:
            radius = float(entity.dxf.radius)
            if not math.isfinite(radius) or radius <= 0:
                raise AssertionError(f"{path.name}: invalid radius")
            angle = (float(entity.dxf.end_angle) -
                     float(entity.dxf.start_angle)) % 360
            if not 0 < angle < 360:
                raise AssertionError(f"{path.name}: degenerate ARC sweep")
            radii.append(radius)
            perimeter += radius * math.radians(angle)
    expected_radii = sorted(case["radii"])
    for actual, expected in zip(sorted(radii), expected_radii):
        if not math.isclose(actual, expected, abs_tol=1e-7, rel_tol=1e-9):
            raise AssertionError(
                f"{path.name}: radius mismatch, {actual} vs {expected}")
    if not math.isclose(perimeter, case["perimeter"],
                        rel_tol=1e-9, abs_tol=1e-6):
        raise AssertionError(
            f"{path.name}: independent perimeter {perimeter}, "
            f"internal analytic perimeter {case['perimeter']}")
    print(
        f"{path.name}: independent ezdxf PASS, "
        f"{case['lines']} lines, {case['arcs']} arcs, "
        f"perimeter {perimeter:.6f} mm"
    )


if __name__ == "__main__":
    folder = pathlib.Path(sys.argv[1])
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    if len(manifest) < 4:
        raise AssertionError("Expected four diverse DXF fixtures")
    for sample in manifest:
        check_sample(folder, sample)
