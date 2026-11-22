namespace AICutPro.Core;

/// <summary>
/// Analytic area/perimeter of ONE already closed LINE/ARC preview ring.
/// Does not infer holes, containment, offsets, manufacturing validity or machine commands.
/// </summary>
public sealed record PreviewContourMetricsResult(
    bool IsValid, double SignedAreaMm2, double AreaMm2, double PerimeterMm,
    string Winding, IReadOnlyList<string> Issues)
{
    public bool CanStartMachine => false;
}

public static class PreviewContourMetrics
{
    /// <summary>Green's theorem, with arcs integrated exactly rather than flattened into chords.</summary>
    public static PreviewContourMetricsResult Measure(PreviewPath path, double joinToleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(path);
        var problems = PreviewContourValidator.Validate(path, joinToleranceMm);
        if (problems.Count > 0)
            return Invalid(problems);
        // Shift the coordinate origin near the contour to reduce catastrophic cancellation
        // for drawings with large world coordinates and small enclosed areas.
        var first = path.Segments[0] switch
        {
            PreviewLine line => line.Start,
            PreviewArc arc => OnArc(arc, arc.StartDegrees),
            _ => new PreviewPoint(double.NaN, double.NaN)
        };
        if (!Valid(first)) return Invalid(["Invalid reference point."]);
        double doubleArea = 0;
        double length = 0;
        foreach (var segment in path.Segments)
        {
            double contribution;
            double segmentLength;
            if (segment is PreviewLine line)
            {
                var a = Translate(line.Start, first);
                var b = Translate(line.End, first);
                contribution = a.X * b.Y - b.X * a.Y;
                segmentLength = Distance(line.Start, line.End);
            }
            else if (segment is PreviewArc arc)
            {
                var center = Translate(arc.Center, first);
                var start = arc.StartDegrees * Math.PI / 180d;
                var sweep = arc.SweepDegrees * Math.PI / 180d;
                var end = start + sweep;
                var r = arc.RadiusMm;
                contribution = r * (center.X * (Math.Sin(end) - Math.Sin(start)) -
                                    center.Y * (Math.Cos(end) - Math.Cos(start))) +
                               r * r * sweep;
                segmentLength = r * Math.Abs(sweep);
            }
            else return Invalid(["Unsupported segment."]);
            if (!double.IsFinite(contribution) || !double.IsFinite(segmentLength))
                return Invalid(["Contour numeric overflow."]);
            doubleArea += contribution;
            length += segmentLength;
        }
        var signedArea = doubleArea / 2;
        if (!double.IsFinite(signedArea) || !double.IsFinite(length) ||
            Math.Abs(signedArea) <= 1e-9 || length <= 0)
            return Invalid(["Degenerate or numerically unstable enclosed area."]);
        return new(true, signedArea, Math.Abs(signedArea), length,
            signedArea > 0 ? "CCW" : "CW", []);
    }

    private static PreviewPoint OnArc(PreviewArc arc, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new(arc.Center.X + arc.RadiusMm * Math.Cos(radians),
                   arc.Center.Y + arc.RadiusMm * Math.Sin(radians));
    }
    private static PreviewPoint Translate(PreviewPoint p, PreviewPoint origin) =>
        new(p.X - origin.X, p.Y - origin.Y);
    private static double Distance(PreviewPoint a, PreviewPoint b) =>
        Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private static bool Valid(PreviewPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static PreviewContourMetricsResult Invalid(IReadOnlyList<string> issues) =>
        new(false, 0, 0, 0, "UNKNOWN", issues);
}
