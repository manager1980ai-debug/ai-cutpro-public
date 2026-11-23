namespace AICutPro.Core;

/// <summary>
/// Conservative, read-only validation of an ordered open polyline. Adjacent
/// edges may share their connecting vertex, but may not retrace/overlap.
/// Nonadjacent edges may not cross, touch, or overlap. Near-coincident
/// coordinates are not snapped: that remains an explicit CAD repair decision.
/// </summary>
public static class OpenChainIntersectionCheck
{
    public static IReadOnlyList<string> Inspect(IReadOnlyList<Point2> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (vertices.Count < 2 || vertices.Count > 1001)
            return ["Open chain vertex count is outside the inspection limit."];
        for (var k = 0; k < vertices.Count; k++)
            if (!double.IsFinite(vertices[k].X) || !double.IsFinite(vertices[k].Y))
                return ["Open chain contains nonfinite coordinates."];

        // For a preview, fail closed on any ambiguous intersection, including
        // a T-junction, a retraced edge or a contact at a nonadjacent endpoint.
        for (var i = 0; i < vertices.Count - 1; i++)
        {
            var a = vertices[i];
            var b = vertices[i + 1];
            if (a == b)
                return [$"Open chain edge {i + 1} has zero length."];
            for (var j = i + 1; j < vertices.Count - 1; j++)
            {
                var c = vertices[j];
                var d = vertices[j + 1];
                if (!Intersects(a, b, c, d))
                    continue;

                if (j == i + 1 &&
                    b == c &&
                    !OverlapsBeyondJoint(a, b, d))
                    continue;

                return [$"Open chain edges {i + 1} and {j + 1} intersect or overlap."];
            }
        }
        return [];
    }

    private static bool OverlapsBeyondJoint(Point2 a, Point2 b, Point2 d)
    {
        // Adjacent edges a->b and b->d may turn or extend forward.
        // A collinear backward retrace is an overlap, not a valid corner.
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var bdx = d.X - b.X;
        var bdy = d.Y - b.Y;
        var cross = abx * bdy - aby * bdx;
        if (cross != 0) return false;
        return abx * bdx + aby * bdy < 0;
    }

    private static bool Intersects(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) ||
            Math.Max(c.X, d.X) < Math.Min(a.X, b.X) ||
            Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) ||
            Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y))
            return false;
        var o1 = Orientation(a, b, c);
        var o2 = Orientation(a, b, d);
        var o3 = Orientation(c, d, a);
        var o4 = Orientation(c, d, b);
        if (o1 == 0 && OnSegment(a, b, c)) return true;
        if (o2 == 0 && OnSegment(a, b, d)) return true;
        if (o3 == 0 && OnSegment(c, d, a)) return true;
        if (o4 == 0 && OnSegment(c, d, b)) return true;
        return (o1 > 0) != (o2 > 0) && (o3 > 0) != (o4 > 0);
    }

    private static double Orientation(Point2 a, Point2 b, Point2 c)
        => (b.X - a.X) * (c.Y - a.Y) -
           (b.Y - a.Y) * (c.X - a.X);

    private static bool OnSegment(Point2 a, Point2 b, Point2 p)
        => p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) &&
           p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
}
