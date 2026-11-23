namespace AICutPro.Core;

/// <summary>
/// Conservative preview assembly of a single connected, open component of
/// LINE/ARC/open POLYLINE entities. Analytic segments are retained intact.
/// This is neither a manufacturing contour nor a controller toolpath.
/// </summary>
public sealed record MixedOpenChainPreview(
    bool IsValid, IReadOnlyList<int> SourceIndices,
    PreviewPath? AnalyticPath, double LengthMm,
    IReadOnlyList<string> Issues)
{
    public bool CanGenerateMachineCommands => false;
}

public static class MixedOpenChainAssembler
{
    private sealed record Edge(int SourceIndex, PreviewSegment Segment,
        PreviewPoint Start, PreviewPoint End);

    public static MixedOpenChainPreview Assemble(
        IReadOnlyList<DxfShape> shapes, int selectedShapeIndex,
        Sheet sheet, double joinToleranceMm = 0.001)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(sheet);
        MixedOpenChainPreview Fail(string issue) => new(false, [], null, 0, [issue]);
        if (shapes.Count is < 1 or > 1000 ||
            selectedShapeIndex < 0 || selectedShapeIndex >= shapes.Count ||
            !double.IsFinite(joinToleranceMm) ||
            joinToleranceMm <= 0 || joinToleranceMm > 0.01)
            return Fail("Invalid selected entity, count or joining tolerance.");
        if (!double.IsFinite(sheet.WidthMm) || !double.IsFinite(sheet.HeightMm) ||
            sheet.WidthMm <= 0 || sheet.HeightMm <= 0)
            return Fail("Invalid sheet dimensions.");

        var source = shapes[selectedShapeIndex];
        if (source is null || string.IsNullOrWhiteSpace(source.Layer))
            return Fail("Selected entity is invalid.");
        var edges = new List<Edge>();
        try
        {
            for (var i = 0; i < shapes.Count; i++)
            {
                if (shapes[i] is not { } shape || shape.Layer != source.Layer)
                    continue;
                switch (shape)
                {
                    case DxfLine line:
                        Add(i, new PreviewLine(P(line.Start), P(line.End)));
                        break;
                    case DxfArc arc:
                    {
                        if (!double.IsFinite(arc.Radius) || arc.Radius <= 0 ||
                            !double.IsFinite(arc.StartDeg) ||
                            !double.IsFinite(arc.EndDeg) ||
                            !double.IsFinite(arc.Center.X) ||
                            !double.IsFinite(arc.Center.Y) ||
                            Math.Abs(arc.EndDeg - arc.StartDeg) >= 360)
                            return Fail("Invalid or ambiguous ARC.");
                        var sweep = Normalize(arc.EndDeg - arc.StartDeg);
                        if (sweep <= 1e-9) return Fail("Ambiguous ARC sweep.");
                        Add(i, new PreviewArc(P(arc.Center), arc.Radius,
                            Normalize(arc.StartDeg), sweep));
                        break;
                    }
                    case DxfPolyline poly when !poly.Closed:
                        if (poly.Vertices.Count is < 2 or > 1000)
                            return Fail("Invalid open polyline vertices.");
                        for (var j = 1; j < poly.Vertices.Count; j++)
                            Add(i, new PreviewLine(
                                P(poly.Vertices[j - 1]), P(poly.Vertices[j])));
                        break;
                    case DxfBulgedPolyline bulged when !bulged.Closed:
                    {
                        var path = DxfBulgePreview.Build(bulged);
                        foreach (var segment in path.Segments)
                            Add(i, segment);
                        break;
                    }
                    case DxfCircle:
                    case DxfPolyline { Closed: true }:
                    case DxfBulgedPolyline { Closed: true }:
                        // Closed entities are separate pieces, not accidental
                        // continuations of an intentionally open operation.
                        break;
                    default:
                        return Fail("Unsupported shape in open-chain layer.");
                }
            }
        }
        catch (ArgumentException e)
        {
            return Fail("Invalid imported geometry: " + e.Message);
        }
        if (!edges.Any(e => e.SourceIndex == selectedShapeIndex))
            return Fail("Selected entity has no open LINE/ARC elements.");
        if (edges.Count is < 1 or > 1000)
            return Fail("Too many open analytic segments.");

        // No proximity-based snapping: a connection is accepted only within
        // explicit tolerance and all candidate nodes must be unambiguous.
        var nodes = new List<PreviewPoint>();
        var links = new (int A, int B)[edges.Count];
        int Node(PreviewPoint point)
        {
            var matches = Enumerable.Range(0, nodes.Count)
                .Where(i => Distance(nodes[i], point) <= joinToleranceMm).ToArray();
            if (matches.Length > 1)
                throw new FormatException("Ambiguous near-coincident endpoints.");
            if (matches.Length == 1) return matches[0];
            nodes.Add(point);
            return nodes.Count - 1;
        }
        try
        {
            for (var i = 0; i < edges.Count; i++)
            {
                var a = Node(edges[i].Start);
                var b = Node(edges[i].End);
                if (a == b) return Fail("Collapsed open segment.");
                links[i] = (a, b);
            }
        }
        catch (FormatException ex) { return Fail(ex.Message); }

        var incident = nodes.Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < edges.Count; i++)
        {
            incident[links[i].A].Add(i);
            incident[links[i].B].Add(i);
        }
        var visited = new HashSet<int>();
        var pending = new Stack<int>(Enumerable.Range(0, edges.Count)
            .Where(i => edges[i].SourceIndex == selectedShapeIndex));
        while (pending.Count > 0)
        {
            var index = pending.Pop();
            if (!visited.Add(index)) continue;
            foreach (var node in new[] { links[index].A, links[index].B })
                foreach (var neighbor in incident[node])
                    if (!visited.Contains(neighbor)) pending.Push(neighbor);
        }
        // An unconnected entity with a near-miss endpoint (e.g. a 0.1 mm
        // gap) is not treated as a harmless separate component. Require the
        // drawing author to repair or explicitly separate it first.
        const double NearMissMm = 0.2;
        foreach (var member in visited)
        foreach (var outsider in Enumerable.Range(0, edges.Count)
            .Where(index => !visited.Contains(index)))
        {
            if (new[] { edges[member].Start, edges[member].End }.Any(p =>
                new[] { edges[outsider].Start, edges[outsider].End }.Any(q =>
                    Distance(p, q) > joinToleranceMm &&
                    Distance(p, q) <= NearMissMm)))
                return Fail("Near-miss mixed entity endpoints require explicit CAD repair.");
        }
        var degrees = new Dictionary<int, int>();
        foreach (var edge in visited)
        {
            degrees[links[edge].A] = degrees.GetValueOrDefault(links[edge].A) + 1;
            degrees[links[edge].B] = degrees.GetValueOrDefault(links[edge].B) + 1;
        }
        if (degrees.Values.Any(x => x > 2))
            return Fail("Open mixed chain is branched or duplicated.");
        var ends = degrees.Where(x => x.Value == 1).Select(x => x.Key)
            .OrderBy(i => nodes[i].X).ThenBy(i => nodes[i].Y).ToArray();
        if (ends.Length != 2)
            return Fail("Selected mixed component is closed or ambiguous.");

        var current = ends[0];
        var ordered = new List<PreviewSegment>();
        var sourceIndices = new List<int>();
        var used = new HashSet<int>();
        while (used.Count < visited.Count)
        {
            var options = incident[current]
                .Where(i => visited.Contains(i) && !used.Contains(i)).ToArray();
            if (options.Length != 1)
                return Fail("Open mixed chain cannot be ordered uniquely.");
            var i = options[0];
            used.Add(i);
            sourceIndices.Add(edges[i].SourceIndex);
            var forward = links[i].A == current;
            ordered.Add(forward ? edges[i].Segment : Reverse(edges[i].Segment));
            current = forward ? links[i].B : links[i].A;
        }
        if (current != ends[1])
            return Fail("Mixed open path does not terminate at its second end.");

        // The original arc primitives are preserved. The bounded display
        // approximation is checked separately and MUST NOT be used for CAM.
        double length = 0;
        foreach (var segment in ordered)
        {
            length += segment switch
            {
                PreviewLine line => Distance(line.Start, line.End),
                PreviewArc arc => arc.RadiusMm *
                    Math.Abs(arc.SweepDegrees) * Math.PI / 180,
                _ => double.NaN
            };
        }
        if (!double.IsFinite(length) || length <= 0 || length > 1_000_000)
            return Fail("Mixed open path length is invalid.");

        var display = new List<Point2>();
        foreach (var segment in ordered)
        {
            if (segment is PreviewLine line)
            {
                if (display.Count == 0) display.Add(new(line.Start.X, line.Start.Y));
                display.Add(new(line.End.X, line.End.Y));
            }
            else if (segment is PreviewArc arc)
            {
                var sample = DxfOpenCurveInspector.Inspect(
                    new DxfArc(source.Layer,
                        new Point2(arc.Center.X, arc.Center.Y),
                        arc.RadiusMm, arc.SweepDegrees > 0
                            ? arc.StartDegrees
                            : arc.StartDegrees + arc.SweepDegrees,
                        arc.SweepDegrees > 0
                            ? arc.StartDegrees + arc.SweepDegrees
                            : arc.StartDegrees),
                    sheet);
                if (!sample.IsValid)
                    return Fail("Arc component failed bounded display inspection: " +
                        string.Join("; ", sample.Issues));
                var pts = arc.SweepDegrees > 0
                    ? sample.DisplayVertices.ToArray()
                    : sample.DisplayVertices.Reverse().ToArray();
                if (display.Count == 0) display.Add(pts[0]);
                display.AddRange(pts.Skip(1));
            }
            if (display.Count > 1001)
                return Fail("Mixed open chain exceeds display sample limit.");
        }
        foreach (var p in display)
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                p.X < -1e-9 || p.Y < -1e-9 ||
                p.X > sheet.WidthMm + 1e-9 ||
                p.Y > sheet.HeightMm + 1e-9)
                return Fail("Open mixed chain extends outside sheet.");
        var intersections = OpenChainIntersectionCheck.Inspect(display);
        if (intersections.Count > 0)
            return Fail("Mixed open chain display intersects itself: " +
                string.Join("; ", intersections));
        return new(true, sourceIndices.Distinct().ToArray(),
            new PreviewPath(ordered, false), length, []);

        void Add(int sourceIndex, PreviewSegment segment)
        {
            var start = Start(segment);
            var end = End(segment);
            if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
                !double.IsFinite(end.X) || !double.IsFinite(end.Y) ||
                Distance(start, end) <= joinToleranceMm)
                throw new ArgumentException("Invalid or degenerate mixed segment.");
            edges.Add(new(sourceIndex, segment, start, end));
        }
    }

    private static PreviewPoint P(Point2 p) => new(p.X, p.Y);
    private static double Normalize(double a) => (a % 360 + 360) % 360;
    private static double Distance(PreviewPoint a, PreviewPoint b) =>
        Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private static PreviewPoint On(PreviewArc arc, double degrees)
    {
        var angle = degrees * Math.PI / 180;
        return new(arc.Center.X + arc.RadiusMm * Math.Cos(angle),
                   arc.Center.Y + arc.RadiusMm * Math.Sin(angle));
    }
    private static PreviewPoint Start(PreviewSegment segment) => segment switch
    {
        PreviewLine line => line.Start,
        PreviewArc arc => On(arc, arc.StartDegrees),
        _ => new(double.NaN, double.NaN)
    };
    private static PreviewPoint End(PreviewSegment segment) => segment switch
    {
        PreviewLine line => line.End,
        PreviewArc arc => On(arc, arc.StartDegrees + arc.SweepDegrees),
        _ => new(double.NaN, double.NaN)
    };
    private static PreviewSegment Reverse(PreviewSegment segment) => segment switch
    {
        PreviewLine line => new PreviewLine(line.End, line.Start),
        PreviewArc arc => new PreviewArc(arc.Center, arc.RadiusMm,
            Normalize(arc.StartDegrees + arc.SweepDegrees), -arc.SweepDegrees),
        _ => throw new ArgumentException("Unsupported preview segment.")
    };
}
