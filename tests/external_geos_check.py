"""GEOS/Shapely cross-check of an exported AI CutPro DXF fixture.

Independent geometry engine: GEOS, via Shapely, on *flattened* circular arcs.
The polygonal approximation is bounded by the stated tessellation resolution.
This is NOT an exact CAD/B-rep or industrial cutting certification.
"""
import json
import math
import pathlib
import sys

import ezdxf
from shapely.geometry import LineString
from shapely.ops import polygonize, unary_union


def segment_coords(entity):
    kind = entity.dxftype()
    if kind == "LINE":
        a, b = entity.dxf.start, entity.dxf.end
        return [(round(a.x, 9), round(a.y, 9)),
                (round(b.x, 9), round(b.y, 9))]
    if kind != "ARC":
        raise AssertionError(f"GEOS oracle refuses unsupported entity: {kind}")
    start = float(entity.dxf.start_angle)
    sweep = (float(entity.dxf.end_angle) - start) % 360
    radius = float(entity.dxf.radius)
    center = entity.dxf.center
    if not (0 < sweep < 360 and math.isfinite(radius) and radius > 0):
        raise AssertionError("Invalid ARC in independent geometry input")
    # Half-degree max chord angle; this is intentionally not exact arc geometry.
    pieces = max(1, math.ceil(sweep / 0.5))
    return [
        (round(center.x + radius * math.cos(math.radians(start + sweep*i/pieces)), 9),
         round(center.y + radius * math.sin(math.radians(start + sweep*i/pieces)), 9))
        for i in range(pieces + 1)
    ]


def check_geos_oracle(folder):
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    case = next((c for c in manifest
                 if c["file"] == "constrained_rectangle_with_hole.dxf"), None)
    if case is None:
        raise AssertionError("Missing known rectangle-plus-circle regression fixture")
    drawing = ezdxf.readfile(folder / case["file"])
    if drawing.header.get("$INSUNITS") != 4:
        raise AssertionError("GEOS oracle expects explicitly millimetre DXF")
    entities = list(drawing.modelspace())
    if not entities or any(e.dxftype() not in ("LINE", "ARC") for e in entities):
        raise AssertionError("GEOS oracle supports LINE/ARC only")
    linework = [LineString(segment_coords(e)) for e in entities]
    # GEOS independently nodes the segments and assembles polygon faces.
    faces = list(polygonize(unary_union(linework)))
    if len(faces) != 2:
        raise AssertionError(f"Expected two GEOS faces (plate and hole), got {len(faces)}")
    material = max(faces, key=lambda face: face.area)
    holes = min(faces, key=lambda face: face.area)
    if len(material.interiors) != 1 or not material.is_valid or not holes.is_valid:
        raise AssertionError("GEOS did not independently identify one plate hole")
    if material.intersection(holes).area > 1e-7:
        # Sharing the hole boundary is valid; overlapping interior is not.
        raise AssertionError("Hole interior overlaps material polygon")
    # Oracle sourced from the explicit fixture geometry, not AI CutPro's
    # manifest perimeter or the product's topology/area calculations.
    # The final contour is the rectangle [0,110] x [0,100], with R10 hole.
    expected_area = 110.0 * 100.0 - math.pi * 10.0**2
    if abs(material.area - expected_area) > 0.03:
        raise AssertionError(
            f"GEOS net area {material.area:.6f} vs independent analytic "
            f"rectangle-minus-circle oracle {expected_area:.6f}")
    print(f"GEOS/Shapely independent preview PASS: {len(faces)} faces, "
          f"{len(material.interiors)} hole, net {material.area:.5f} mm² "
          "(arcs sampled at <=0.5 degrees; CAM not certified)")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python tests/external_geos_check.py fixture-directory")
    check_geos_oracle(pathlib.Path(sys.argv[1]))
