namespace AICutPro.Core;

/// <summary>
/// Read-only collision review between OPEN connected LINE/ARC/bulge chains and
/// standalone CLOSED circles or LWPOLYLINE rings, even on different layers.
/// This does not establish hole ownership, nesting, kerf or CAM clearance.
/// </summary>
public static class DxfOpenClosedContactReview
{
    public static IReadOnlyList<string> Inspect(
        IReadOnlyList<DxfShape> shapes, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(sheet);
        if (shapes.Count is < 1 or > 100)
            return ["DXF open/closed interaction review is limited to 1..100 shapes."];
        var closed = new List<(int Index, PreviewPath Path)>();
        for (var i = 0; i < shapes.Count; i++)
        {
            PreviewPath? path = null;
            try
            {
                switch (shapes[i])
                {
                    case DxfCircle c:
                    {
                        if (!double.IsFinite(c.Radius) || c.Radius <= 0 ||
                            !double.IsFinite(c.Center.X) ||
                            !double.IsFinite(c.Center.Y))
                            return [$"Invalid circle at source {i + 1}."];
                        path = new PreviewPath(Enumerable.Range(0, 4)
                            .Select< int, PreviewSegment >(k =>
                                new PreviewArc(
                                    new PreviewPoint(c.Center.X, c.Center.Y),
                                    c.Radius, 90d * k, 90)).ToArray(), true);
                        break;
                    }
                    case DxfPolyline { Closed: true } p:
                    {
                        if (p.Vertices.Count is < 3 or > 1000)
                            return [$"Closed polyline at {i + 1} has invalid size."];
                        var segments = new List<PreviewSegment>();
                        for (var j = 0; j < p.Vertices.Count; j++)
                        {
                            var a = p.Vertices[j];
                            var b = p.Vertices[(j + 1) % p.Vertices.Count];
                            segments.Add(new PreviewLine(
                                new PreviewPoint(a.X, a.Y),
                                new PreviewPoint(b.X, b.Y)));
                        }
                        path = new PreviewPath(segments, true);
                        break;
                    }
                    case DxfBulgedPolyline { Closed: true } p:
                        path = DxfBulgePreview.Build(p);
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                return [$"Unsupported closed geometry at {i + 1}: {ex.Message}"];
            }
            if (path is not null)
                closed.Add((i, path));
        }
        if (closed.Count == 0) return [];
        var seenOpen = new HashSet<int>();
        for (var i = 0; i < shapes.Count; i++)
        {
            if (seenOpen.Contains(i) || shapes[i] is not
                (DxfLine or DxfArc or DxfPolyline { Closed: false } or
                 DxfBulgedPolyline { Closed: false }))
                continue;
            var chain = MixedOpenChainAssembler.Assemble(shapes, i, sheet);
            if (!chain.IsValid || chain.AnalyticPath is null)
                return [$"Invalid open component at {i + 1}: " +
                    string.Join("; ", chain.Issues)];
            foreach (var source in chain.SourceIndices)
                seenOpen.Add(source);
            foreach (var ring in closed)
            {
                var hits = PreviewContourValidator.ValidateDistinctPaths(
                    chain.AnalyticPath, ring.Path);
                if (hits.Count > 0)
                    return [$"Open DXF component {i + 1} intersects or overlaps " +
                        $"closed source {ring.Index + 1}: " + string.Join("; ", hits)];
            }
        }
        return [];
    }
}
