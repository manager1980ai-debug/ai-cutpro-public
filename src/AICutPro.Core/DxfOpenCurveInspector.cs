namespace AICutPro.Core;

/// <summary>
/// A read-only drawing preview for one imported ARC or open POLYLINE.
/// Sampled chords are display approximations only, never CAM/toolpaths.
/// Exact arc geometry remains in the imported DXF model.
/// </summary>
public sealed record DxfOpenCurvePreview(
    bool IsValid, IReadOnlyList<Point2> DisplayVertices,
    double SourceLengthMm, IReadOnlyList<string> Issues)
{
    public bool CanGenerateMachineCommands => false;
}

public static class DxfOpenCurveInspector
{
    public static DxfOpenCurvePreview Inspect(
        DxfShape shape, Sheet sheet, double maximumChordErrorMm = 0.05)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(sheet);
        DxfOpenCurvePreview Fail(string reason) => new(false, [], 0, [reason]);
        if (!double.IsFinite(maximumChordErrorMm) ||
            maximumChordErrorMm < 0.001 || maximumChordErrorMm > 0.5)
            return Fail("Display chord tolerance must be within 0.001..0.5 mm.");
        if (!double.IsFinite(sheet.WidthMm) || !double.IsFinite(sheet.HeightMm) ||
            sheet.WidthMm <= 0 || sheet.HeightMm <= 0)
            return Fail("Sheet dimensions are invalid.");

        var vertices = new List<Point2>();
        double analyticLength = 0;
        try
        {
            switch (shape)
            {
                case DxfArc arc:
                {
                    var start = ((arc.StartDeg % 360) + 360) % 360;
                    var sweep = ((arc.EndDeg - arc.StartDeg) % 360 + 360) % 360;
                    if (sweep <= 1e-9 || !double.IsFinite(arc.Radius) || arc.Radius <= 0)
                        return Fail("ARC has invalid radius or ambiguous sweep.");
                    var count = Samples(arc.Radius, sweep, maximumChordErrorMm);
                    for (var i = 0; i <= count; i++)
                    {
                        var angle = (start + sweep * i / count) * Math.PI / 180;
                        vertices.Add(new(arc.Center.X + arc.Radius * Math.Cos(angle),
                                         arc.Center.Y + arc.Radius * Math.Sin(angle)));
                    }
                    analyticLength = arc.Radius * sweep * Math.PI / 180;
                    break;
                }
                case DxfPolyline poly when !poly.Closed:
                {
                    if (poly.Vertices.Count is < 2 or > 1001)
                        return Fail("Open polyline vertex count is out of range.");
                    vertices.AddRange(poly.Vertices);
                    for (var i = 1; i < vertices.Count; i++)
                    {
                        var dx = vertices[i].X - vertices[i - 1].X;
                        var dy = vertices[i].Y - vertices[i - 1].Y;
                        analyticLength += Math.Sqrt(dx * dx + dy * dy);
                    }
                    break;
                }
                case DxfBulgedPolyline curved when !curved.Closed:
                {
                    if (curved.Vertices.Count is < 2 or > 1001)
                        return Fail("Bulged polyline vertex count is out of range.");
                    var analytic = DxfBulgePreview.Build(curved);
                    for (var i = 0; i < analytic.Segments.Count; i++)
                    {
                        var start = curved.Vertices[i];
                        var end = curved.Vertices[i + 1];
                        if (i == 0) vertices.Add(start);
                        if (analytic.Segments[i] is PreviewLine)
                        {
                            var dx = end.X - start.X;
                            var dy = end.Y - start.Y;
                            analyticLength += Math.Sqrt(dx * dx + dy * dy);
                        }
                        else if (analytic.Segments[i] is PreviewArc arc)
                        {
                            var count = Samples(arc.RadiusMm,
                                Math.Abs(arc.SweepDegrees), maximumChordErrorMm);
                            for (var j = 1; j < count; j++)
                            {
                                var angle = (arc.StartDegrees +
                                    arc.SweepDegrees * j / count) * Math.PI / 180;
                                vertices.Add(new(
                                    arc.Center.X + arc.RadiusMm * Math.Cos(angle),
                                    arc.Center.Y + arc.RadiusMm * Math.Sin(angle)));
                            }
                            analyticLength += arc.RadiusMm *
                                Math.Abs(arc.SweepDegrees) * Math.PI / 180;
                        }
                        else return Fail("Unsupported polyline segment.");
                        vertices.Add(end);
                        if (vertices.Count > 1001)
                            return Fail("Curved display preview exceeds vertex limit.");
                    }
                    break;
                }
                default:
                    return Fail("Only an ARC or an open POLYLINE can be inspected.");
            }
        }
        catch (ArgumentException ex)
        {
            return Fail("Invalid imported curve: " + ex.Message);
        }
        if (vertices.Count < 2 || vertices.Count > 1001 ||
            !double.IsFinite(analyticLength) || analyticLength <= 1e-9 ||
            analyticLength > 1_000_000)
            return Fail("Open curve length or preview complexity is invalid.");
        foreach (var point in vertices)
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X < -1e-9 || point.Y < -1e-9 ||
                point.X > sheet.WidthMm + 1e-9 ||
                point.Y > sheet.HeightMm + 1e-9)
                return Fail("Curve extends outside the drawing sheet.");
        var crossings = OpenChainIntersectionCheck.Inspect(vertices);
        if (crossings.Count > 0)
            return Fail("Curved display approximation intersects itself: " +
                string.Join("; ", crossings));
        return new(true, vertices, analyticLength, []);
    }

    private static int Samples(double radius, double sweepDegrees, double error)
    {
        if (!double.IsFinite(radius) || radius <= 0 ||
            !double.IsFinite(sweepDegrees) || sweepDegrees <= 0 ||
            sweepDegrees >= 360)
            throw new ArgumentException("Unsupported circular sweep or radius.");
        var step = error >= radius ? Math.PI / 2 :
            2 * Math.Acos(1 - error / radius);
        if (!double.IsFinite(step) || step <= 0)
            throw new ArgumentException("Circular display step is invalid.");
        var requested = Math.Ceiling(sweepDegrees * Math.PI / 180 / step);
        if (!double.IsFinite(requested) || requested > 1000)
            throw new ArgumentException("Curve needs too many display samples.");
        return Math.Max(2, (int)requested);
    }
}
