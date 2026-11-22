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



def _on_arc(arc, point, tolerance=1e-7):
    center = (float(arc.dxf.center.x), float(arc.dxf.center.y))
    radius = float(arc.dxf.radius)
    if not math.isclose(math.dist(center, point), radius,
                        abs_tol=tolerance, rel_tol=0):
        return False
    start = float(arc.dxf.start_angle) % 360
    sweep = (float(arc.dxf.end_angle) - start) % 360
    angle = math.degrees(math.atan2(point[1]-center[1],
                                     point[0]-center[0])) % 360
    return (angle-start) % 360 <= sweep + math.degrees(tolerance/radius)


def _arc_circle_intersections(first, second, tolerance=1e-7):
    c1 = (float(first.dxf.center.x), float(first.dxf.center.y))
    c2 = (float(second.dxf.center.x), float(second.dxf.center.y))
    r1, r2 = float(first.dxf.radius), float(second.dxf.radius)
    delta = _subtract(c2, c1)
    distance = math.hypot(*delta)
    if distance <= tolerance:
        if abs(r1-r2) > tolerance:
            return []
        # Coincident circles: detect positive shared angular intervals,
        # not merely an endpoint. A midpoint of each arc is sufficient
        # when combined with checks of all endpoint angles.
        candidates = [_endpoints(first)[0], _endpoints(first)[1],
                      _endpoints(second)[0], _endpoints(second)[1]]
        a_start = float(first.dxf.start_angle)
        a_sweep = (float(first.dxf.end_angle)-a_start) % 360
        b_start = float(second.dxf.start_angle)
        b_sweep = (float(second.dxf.end_angle)-b_start) % 360
        for start, sweep, arc in [(a_start, a_sweep, second),
                                  (b_start, b_sweep, first)]:
            for fraction in (0.25, 0.5, 0.75):
                angle = math.radians(start + sweep*fraction)
                point = (c1[0]+r1*math.cos(angle),
                         c1[1]+r1*math.sin(angle))
                if _on_arc(arc, point, tolerance):
                    raise AssertionError("Overlapping coincident DXF ARC edges")
        return [p for p in candidates if _on_arc(first,p,tolerance)
                and _on_arc(second,p,tolerance)]
    if distance > r1+r2+tolerance or distance < abs(r1-r2)-tolerance:
        return []
    along = (r1*r1-r2*r2+distance*distance)/(2*distance)
    height_sq = r1*r1-along*along
    if height_sq < -tolerance*max(r1*r1,1):
        return []
    height = math.sqrt(max(0, height_sq))
    ux, uy = delta[0]/distance, delta[1]/distance
    base = (c1[0]+along*ux, c1[1]+along*uy)
    points = [(base[0]-height*uy,base[1]+height*ux),
              (base[0]+height*uy,base[1]-height*ux)]
    return [p for p in points if _on_arc(first,p,tolerance)
            and _on_arc(second,p,tolerance)]


def _line_arc_intersections(line, arc, tolerance=1e-7):
    start, end = _endpoints(line)
    c = (float(arc.dxf.center.x),float(arc.dxf.center.y))
    d, f = _subtract(end,start), _subtract(start,c)
    aa = d[0]*d[0]+d[1]*d[1]
    if aa <= tolerance*tolerance:
        raise AssertionError("Degenerate DXF LINE")
    bb = 2*(f[0]*d[0]+f[1]*d[1])
    cc = f[0]*f[0]+f[1]*f[1]-float(arc.dxf.radius)**2
    discriminant = bb*bb-4*aa*cc
    if discriminant < -tolerance*max(aa,1):
        return []
    root = math.sqrt(max(0,discriminant))
    points = []
    for t in ((-bb-root)/(2*aa),(-bb+root)/(2*aa)):
        if -tolerance/math.sqrt(aa) <= t <= 1+tolerance/math.sqrt(aa):
            p = (start[0]+d[0]*t,start[1]+d[1]*t)
            if _on_arc(arc,p,tolerance):
                points.append(p)
    return points


def check_curved_intersections(entities, tolerance=1e-7):
    """Independent analytic LINE/ARC and ARC/ARC cross and tangent checks.

    Only common endpoints may touch. Identical circles with overlapping
    angular intervals are rejected; this is not a hole-nesting certificate.
    """
    for i, first in enumerate(entities):
        for second in entities[i+1:]:
            if first.dxftype() == second.dxftype() == "LINE":
                continue
            if first.dxftype() == "ARC" and second.dxftype() == "ARC":
                points = _arc_circle_intersections(first, second, tolerance)
            else:
                line = first if first.dxftype() == "LINE" else second
                arc = first if first.dxftype() == "ARC" else second
                points = _line_arc_intersections(line,arc,tolerance)
            first_ends = _endpoints(first)
            second_ends = _endpoints(second)
            for point in points:
                is_first_end = any(math.dist(point,p) <= tolerance
                                   for p in first_ends)
                is_second_end = any(math.dist(point,p) <= tolerance
                                    for p in second_ends)
                if not (is_first_end and is_second_end):
                    raise AssertionError("Non-endpoint DXF curved intersection")


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



def _sample_edge(entity, forward=True):
    """Construct display-level polylines for independent ring containment."""
    if entity.dxftype() == "LINE":
        result = list(_endpoints(entity))
    else:
        center = entity.dxf.center
        radius = float(entity.dxf.radius)
        start = float(entity.dxf.start_angle)
        sweep = (float(entity.dxf.end_angle)-start) % 360
        # Fine sampled containment only; NOT a precision offset/CAM operation.
        count = max(24, math.ceil(sweep / 1.0))
        result = [
            (float(center.x)+radius*math.cos(math.radians(start+sweep*i/count)),
             float(center.y)+radius*math.sin(math.radians(start+sweep*i/count)))
            for i in range(count+1)
        ]
    return result if forward else result[::-1]


def _point_in_ring(point, polygon, tolerance=1e-7):
    """Return 1 inside, 0 outside, -1 on boundary (ray parity)."""
    x, y = point
    inside = False
    for a, b in zip(polygon, polygon[1:]):
        ab = _subtract(b, a)
        ap = _subtract(point, a)
        length = math.hypot(*ab)
        if length <= 1e-12:
            continue
        if abs(_cross(ab, ap))/length <= tolerance and (
            min(a[0],b[0])-tolerance <= x <= max(a[0],b[0])+tolerance and
            min(a[1],b[1])-tolerance <= y <= max(a[1],b[1])+tolerance
        ):
            return -1
        if (a[1] > y) != (b[1] > y):
            x_cross = a[0]+(y-a[1])*(b[0]-a[0])/(b[1]-a[1])
            if x_cross > x:
                inside = not inside
    return 1 if inside else 0


def check_ring_nesting(entities, expected_rings=None, expected_holes=None,
                       tolerance=1e-5):
    """Independent ring extraction and nesting parity, not CAD certification.

    Even nesting depths are material islands, odd nesting depths are holes.
    Disconnected outer rings are allowed. A caller must state expected hole
    count when the intended semantic role needs enforcing.
    """
    vertices = []
    adjacency = []
    def vertex(p):
        for i, q in enumerate(vertices):
            if math.dist(p,q) <= tolerance:
                return i
        vertices.append(p)
        adjacency.append([])
        return len(vertices)-1
    edge_nodes = []
    for i, entity in enumerate(entities):
        a,b = (vertex(p) for p in _endpoints(entity))
        if a == b:
            raise AssertionError("Collapsed ring edge")
        adjacency[a].append(i)
        adjacency[b].append(i)
        edge_nodes.append((a,b))
    if any(len(edges)!=2 for edges in adjacency):
        raise AssertionError("Noncycle edge graph in nesting check")
    unused = set(range(len(entities)))
    rings = []
    while unused:
        first = min(unused)
        start = edge_nodes[first][0]
        current = start
        ring = []
        for _ in range(len(entities)+1):
            # At the starting vertex either incident edge is a valid
            # direction. Afterwards the continuation is unambiguous.
            choices = ([first] if not ring else
                       sorted(e for e in adjacency[current] if e in unused))
            if len(choices)!=1:
                raise AssertionError("Ambiguous ring traversal")
            edge_id = choices[0]
            unused.remove(edge_id)
            a,b = edge_nodes[edge_id]
            forward = current == a
            points = _sample_edge(entities[edge_id], forward)
            if ring:
                if math.dist(ring[-1],points[0]) > tolerance:
                    raise AssertionError("Ring has inconsistent joined vertices")
                ring.extend(points[1:])
            else:
                ring.extend(points)
            current = b if forward else a
            if current == start:
                break
        else:
            raise AssertionError("Unclosed ring traversal")
        if len(ring)<4 or math.dist(ring[0],ring[-1]) > tolerance:
            raise AssertionError("Invalid sampled ring")
        area = sum(a[0]*b[1]-b[0]*a[1]
                   for a,b in zip(ring,ring[1:]))/2
        if abs(area) <= tolerance*tolerance:
            raise AssertionError("Degenerate ring area")
        rings.append(ring)
    if expected_rings is not None and len(rings)!=expected_rings:
        raise AssertionError(f"Expected {expected_rings} rings, got {len(rings)}")

    depths = []
    for i, ring in enumerate(rings):
        # A boundary vertex is a valid probe because distinct rings must not
        # intersect or touch (checked independently before this function).
        probe = ring[0]
        depth = 0
        for j, outer in enumerate(rings):
            if i==j:
                continue
            relation = _point_in_ring(probe,outer,tolerance)
            if relation < 0:
                raise AssertionError("Touching ring boundaries")
            depth += relation
        depths.append(depth)
    holes = sum(depth % 2 for depth in depths)
    if expected_holes is not None and holes != expected_holes:
        raise AssertionError(f"Expected {expected_holes} holes, got {holes}")
    return len(rings), holes


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
    def expect_curved_reject(entities, label):
        try:
            check_curved_intersections(entities)
        except AssertionError:
            return
        raise AssertionError("Negative regression: " + label)
    line_arc_cross = ezdxf.new().modelspace()
    line_arc_cross.add_line((-15, 0), (15, 0))
    line_arc_cross.add_arc((0, 0), 10, 0, 180)
    expect_curved_reject(list(line_arc_cross), "LINE/ARC crossing accepted")
    line_arc_tangent = ezdxf.new().modelspace()
    line_arc_tangent.add_line((-15, 10), (15, 10))
    line_arc_tangent.add_arc((0, 0), 10, 0, 180)
    expect_curved_reject(list(line_arc_tangent), "interior tangent accepted")
    shared_endpoint = ezdxf.new().modelspace()
    shared_endpoint.add_line((10, 0), (20, 0))
    shared_endpoint.add_arc((0, 0), 10, 0, 90)
    check_curved_intersections(list(shared_endpoint))
    arc_arc_cross = ezdxf.new().modelspace()
    arc_arc_cross.add_arc((0, 0), 10, 0, 180)
    arc_arc_cross.add_arc((10, 0), 10, 0, 180)
    expect_curved_reject(list(arc_arc_cross), "ARC/ARC crossing accepted")
    arc_overlap = ezdxf.new().modelspace()
    arc_overlap.add_arc((0, 0), 10, 0, 140)
    arc_overlap.add_arc((0, 0), 10, 80, 180)
    expect_curved_reject(list(arc_overlap), "coincident arc overlap accepted")
    separate_arcs = ezdxf.new().modelspace()
    separate_arcs.add_arc((0, 0), 10, 0, 90)
    separate_arcs.add_arc((40, 0), 10, 0, 90)
    check_curved_intersections(list(separate_arcs))
    def add_rectangle(space, x0, y0, x1, y1):
        points = [(x0,y0),(x1,y0),(x1,y1),(x0,y1)]
        for a,b in zip(points,points[1:]+points[:1]):
            space.add_line(a,b)
    nested = ezdxf.new().modelspace()
    add_rectangle(nested,0,0,100,100)
    add_rectangle(nested,20,20,40,40)
    if check_ring_nesting(list(nested),2,1) != (2,1):
        raise AssertionError("Nested hole classification regression")
    separate = ezdxf.new().modelspace()
    add_rectangle(separate,0,0,10,10)
    add_rectangle(separate,20,0,30,10)
    if check_ring_nesting(list(separate),2,0) != (2,0):
        raise AssertionError("Disjoint parts classification regression")
    try:
        check_ring_nesting(list(separate),2,1)
    except AssertionError:
        pass
    else:
        raise AssertionError("Negative regression: outside hole accepted")
    nested_island = ezdxf.new().modelspace()
    add_rectangle(nested_island,0,0,100,100)
    add_rectangle(nested_island,10,10,90,90)
    add_rectangle(nested_island,20,20,30,30)
    if check_ring_nesting(list(nested_island),3,1) != (3,1):
        raise AssertionError("Third nesting level must be a material island")
    touching_rings = ezdxf.new().modelspace()
    add_rectangle(touching_rings,0,0,100,100)
    add_rectangle(touching_rings,0,20,20,40)
    try:
        check_closed_contour_graph(list(touching_rings))
    except AssertionError:
        pass
    else:
        raise AssertionError("Negative regression: touching rings accepted")


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
    check_curved_intersections(entities)
    ring_count, hole_count = check_ring_nesting(entities,
        case.get('expected_rings'),case.get('expected_holes'))
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
        f"perimeter {perimeter:.6f} mm, graph {vertex_count} vertices/{edge_count} edges, "
        f"{ring_count} rings/{hole_count} holes"
    )


if __name__ == "__main__":
    run_negative_graph_regressions()
    folder = pathlib.Path(sys.argv[1])
    manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    if len(manifest) < 4:
        raise AssertionError("Expected four diverse DXF fixtures")
    for sample in manifest:
        check_sample(folder, sample)
