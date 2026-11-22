"""Independent DXF entity/perimeter verification using ezdxf.

This is an external serialization check, NOT an independent CAD topology
certificate or authorization to create toolpaths.
"""
import json
import math
import pathlib
import sys

import ezdxf


def _endpoints(entity):
    """Read actual geometry from ezdxf, independently of AI CutPro's parser."""
    if entity.dxftype() == "LINE":
        a, b = entity.dxf.start, entity.dxf.end
        return (float(a.x), float(a.y)), (float(b.x), float(b.y))
    center = entity.dxf.center
    radius = float(entity.dxf.radius)
    def at(degrees):
        theta = math.radians(float(degrees))
        return (float(center.x) + radius * math.cos(theta),
                float(center.y) + radius * math.sin(theta))
    return at(entity.dxf.start_angle), at(entity.dxf.end_angle)


def _cross(a, b):
    return a[0] * b[1] - a[1] * b[0]


def _subtract(a, b):
    return (a[0] - b[0], a[1] - b[1])


def check_line_intersections(entities, tolerance=1e-7):
    """Reject non-endpoint LINE/LINE intersections and collinear overlap.

    Exact curved intersections remain a separate B3 task. Touching shared
    endpoints is allowed, because neighboring contour edges must join.
    """
    lines = [_endpoints(entity) for entity in entities
             if entity.dxftype() == "LINE"]
    for i, (a, b) in enumerate(lines):
        r = _subtract(b, a)
        rr = r[0] * r[0] + r[1] * r[1]
        if rr <= tolerance * tolerance:
            raise AssertionError("Degenerate DXF LINE")
        for c, d in lines[i + 1:]:
            v = _subtract(d, c)
            vv = v[0] * v[0] + v[1] * v[1]
            if vv <= tolerance * tolerance:
                raise AssertionError("Degenerate DXF LINE")
            w = _subtract(c, a)
            denominator = _cross(r, v)
            scale = math.sqrt(rr * vv)
            if abs(denominator) <= tolerance * scale:
                # Parallel: check collinearity using perpendicular distance.
                if abs(_cross(w, r)) > tolerance * math.sqrt(rr):
                    continue
                t0 = (w[0] * r[0] + w[1] * r[1]) / rr
                other = _subtract(d, a)
                t1 = (other[0] * r[0] + other[1] * r[1]) / rr
                overlap = min(1.0, max(t0, t1)) - max(0.0, min(t0, t1))
                if overlap * math.sqrt(rr) > tolerance:
                    raise AssertionError("Overlapping collinear DXF LINE edges")
                continue
            t = _cross(w, v) / denominator
            u = _cross(w, r) / denominator
            t_eps = tolerance / math.sqrt(rr)
            u_eps = tolerance / math.sqrt(vv)
            if -t_eps <= t <= 1 + t_eps and -u_eps <= u <= 1 + u_eps:
                at_a_endpoint = abs(t) <= t_eps or abs(t - 1) <= t_eps
                at_c_endpoint = abs(u) <= u_eps or abs(u - 1) <= u_eps
                if not (at_a_endpoint and at_c_endpoint):
                    raise AssertionError("Non-endpoint DXF LINE intersection")


def check_closed_contour_graph(entities, tolerance=1e-5):
    """Every exported LINE/ARC endpoint must have exactly two incident edges.

    Multiple disjoint rings are allowed (parts and holes). Open paths, T-joints,
    duplicated edges, and zero-length edges are rejected. This is a limited
    independent graph check, not a complete curved intersection/topology solver.
    """
    if not entities:
        raise AssertionError("DXF contains no contour edges")
    vertices = []
    degrees = []
    edges = []
    def vertex(point):
        if not all(math.isfinite(value) for value in point):
            raise AssertionError("Nonfinite DXF endpoint")
        for i, other in enumerate(vertices):
            if math.dist(point, other) <= tolerance:
                return i
        vertices.append(point)
        degrees.append(0)
        return len(vertices) - 1

    for entity in entities:
        start, end = _endpoints(entity)
        a, b = vertex(start), vertex(end)
        if a == b:
            raise AssertionError("Zero-length or collapsed DXF edge")
        degrees[a] += 1
        degrees[b] += 1
        edges.append((a, b))
    if any(degree != 2 for degree in degrees):
        raise AssertionError(
            "Open or branching DXF contour: endpoint degrees "
            + str(sorted(set(degrees)))
        )
    # Parallel edges can legitimately make a two-edge circle segment pair.
    # Reject only truly duplicated geometric entities, including reversal.
    identities = set()
    for entity in entities:
        if entity.dxftype() == "LINE":
            start, end = _endpoints(entity)
            a = tuple(round(v, 6) for v in start)
            b = tuple(round(v, 6) for v in end)
            identity = ("LINE", tuple(sorted((a, b))))
        else:
            c = entity.dxf.center
            identity = ("ARC", round(float(c.x), 6),
                        round(float(c.y), 6),
                        round(float(entity.dxf.radius), 6),
                        round(float(entity.dxf.start_angle) % 360, 6),
                        round(float(entity.dxf.end_angle) % 360, 6))
        if identity in identities:
            raise AssertionError("Duplicate DXF geometric edge")
        identities.add(identity)
    return len(vertices), len(edges)


def run_negative_graph_regressions():
    """Known-bad independent inputs must never pass the graph audit."""
    doc = ezdxf.new()
    msp = doc.modelspace()
    msp.add_line((0, 0), (10, 0))
    msp.add_line((10, 0), (10, 10))
    try:
        check_closed_contour_graph(list(msp))
    except AssertionError:
        pass
    else:
        raise AssertionError("Negative regression: open chain accepted")
    msp.add_line((10, 10), (0, 0))
    check_closed_contour_graph(list(msp))
    msp.add_line((10, 0), (20, 0))
    try:
        check_closed_contour_graph(list(msp))
    except AssertionError:
        pass
    else:
        raise AssertionError("Negative regression: branching graph accepted")
    crossing = ezdxf.new().modelspace()
    crossing.add_line((0, 0), (10, 10))
    crossing.add_line((0, 10), (10, 0))
    try:
        check_line_intersections(list(crossing))
    except AssertionError:
        pass
    else:
        raise AssertionError("Negative regression: crossed LINE edges accepted")
    overlap = ezdxf.new().modelspace()
    overlap.add_line((0, 0), (10, 0))
    overlap.add_line((5, 0), (15, 0))
    try:
        check_line_intersections(list(overlap))
    except AssertionError:
        pass
    else:
        raise AssertionError("Negative regression: overlapped LINE edges accepted")
    touching = ezdxf.new().modelspace()
    touching.add_line((0, 0), (10, 0))
    touching.add_line((10, 0), (10, 10))
    check_line_intersections(list(touching))


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

    vertex_count, edge_count = check_closed_contour_graph(entities)
    check_line_intersections(entities)
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
    if len(radii) != len(expected_radii):
        raise AssertionError(f"{path.name}: ARC radii count mismatch")
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
        f"perimeter {perimeter:.6f} mm, graph {vertex_count} vertices/{edge_count} edges"
    )


if __name__ == "__main__":
    run_negative_graph_regressions()
    folder = pathlib.Path(sys.argv[1])
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    if len(manifest) < 4:
        raise AssertionError("Expected four diverse DXF fixtures")
    for sample in manifest:
        check_sample(folder, sample)
