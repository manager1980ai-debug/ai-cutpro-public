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


def _geos_faces(folder, filename):
    drawing = ezdxf.readfile(folder / filename)
    if drawing.header.get("$INSUNITS") != 4:
        raise AssertionError(f"{filename}: expected millimetre DXF")
    entities = list(drawing.modelspace())
    if not entities or any(e.dxftype() not in ("LINE", "ARC") for e in entities):
        raise AssertionError(f"{filename}: unsupported geometry for GEOS test")
    linework = [LineString(segment_coords(e)) for e in entities]
    faces = list(polygonize(unary_union(linework)))
    if not faces or not all(face.is_valid for face in faces):
        raise AssertionError(f"{filename}: invalid polygonization result")
    return faces


def check_geos_oracle(folder):
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    files = {c["file"] for c in manifest}
    if not {"constrained_rectangle_with_hole.dxf",
            "two_parts_nested_island.dxf"} <= files:
        raise AssertionError("Expected both independent GEOS DXF fixtures")

    faces = _geos_faces(folder, "constrained_rectangle_with_hole.dxf")
    if len(faces) != 2:
        raise AssertionError(f"Expected plate and hole faces, got {len(faces)}")
    material = max(faces, key=lambda face: face.area)
    hole = min(faces, key=lambda face: face.area)
    if len(material.interiors) != 1 or material.intersection(hole).area > 1e-7:
        raise AssertionError("GEOS did not identify a nonoverlapping plate hole")
    # The edited outer plate is [0,110] x [10,100] with a circular R10 void.
    expected = 110 * 90 - math.pi * 10**2
    if abs(material.area - expected) > 0.03:
        raise AssertionError(f"GEOS plate net area {material.area} != {expected}")

    nested_faces = _geos_faces(folder, "two_parts_nested_island.dxf")
    if len(nested_faces) != 6:
        raise AssertionError(f"Expected six independent nested faces; got {len(nested_faces)}")
    from shapely.geometry import Polygon
    shells = [Polygon(face.exterior) for face in nested_faces]
    counts = {0: 0, 1: 0, 2: 0, 3: 0}
    net = 0.0
    for index, face in enumerate(nested_faces):
        witness = face.representative_point()
        depth = sum(1 for j, shell in enumerate(shells)
                    if j != index and shell.area > shells[index].area
                    and shell.contains(witness))
        if depth not in counts:
            raise AssertionError(f"Unexpected nesting depth {depth}")
        counts[depth] += 1
        if depth % 2 == 0:
            net += face.area
    if counts != {0: 2, 1: 2, 2: 1, 3: 1}:
        raise AssertionError(f"Wrong GEOS material/void hierarchy: {counts}")
    # First plate: 10000 - 3600 + 900 - 100; second: 3600 - 400.
    expected_net = (100**2 - 60**2 + 30**2 - 10**2) + (60**2 - 20**2)
    if abs(net - expected_net) > 1e-7:
        raise AssertionError(f"GEOS two-part net {net} != {expected_net}")

    print(f"GEOS/Shapely independent preview PASS: plate/hole {material.area:.5f} mm²; "
          f"two plates, {len(nested_faces)} faces, hierarchy {counts}, "
          f"nested net {net:.5f} mm² (CAD/CAM not certified)")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python tests/external_geos_check.py fixture-directory")
    check_geos_oracle(pathlib.Path(sys.argv[1]))
