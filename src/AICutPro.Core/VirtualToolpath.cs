namespace AICutPro.Core;

/// <summary>
/// Geometry-only simulation path. Never produces machine commands or G-code.
/// Coordinates are millimetres. A path is intentionally not kerf-compensated.
/// </summary>
public readonly record struct PreviewPoint(double X, double Y);
public abstract record PreviewSegment;
public sealed record PreviewLine(PreviewPoint Start, PreviewPoint End) : PreviewSegment;
public sealed record PreviewArc(PreviewPoint Center, double RadiusMm,
    double StartDegrees, double SweepDegrees) : PreviewSegment;
public sealed record PreviewPath(IReadOnlyList<PreviewSegment> Segments, bool Closed);
public sealed record PreviewPathResult(IReadOnlyList<PreviewPoint> Points, double LengthMm,
    IReadOnlyList<string> Warnings, bool IsValidForPreview);

public static class VirtualToolpath
{
    // This is display accuracy, not manufacturing precision.
    public static PreviewPathResult Sample(PreviewPath path, double maxChordErrorMm = 0.1,
        double joinToleranceMm = 0.01)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (!Finite(maxChordErrorMm) || maxChordErrorMm <= 0 ||
            !Finite(joinToleranceMm) || joinToleranceMm < 0)
            throw new ArgumentOutOfRangeException(nameof(maxChordErrorMm));

        var warnings = new List<string>();
        var points = new List<PreviewPoint>();
        double length = 0;
        PreviewPoint? previousEnd = null;
        PreviewPoint? firstStart = null;

        if (path.Segments is null || path.Segments.Count == 0)
            return Invalid("Empty path");
        if (path.Segments.Count > 10000)
            return Invalid("Too many segments for preview");

        foreach (var segment in path.Segments)
        {
            PreviewPoint start, end;
            IReadOnlyList<PreviewPoint> samples;
            double segmentLength;
            if (segment is PreviewLine line)
            {
                start = line.Start; end = line.End;
                if (!Valid(start) || !Valid(end)) return Invalid("Non-finite line coordinates");
                segmentLength = Distance(start, end);
                if (segmentLength <= 0 || !Finite(segmentLength)) return Invalid("Degenerate line");
                samples = new[] { start, end };
            }
            else if (segment is PreviewArc arc)
            {
                if (!Valid(arc.Center) || !Finite(arc.RadiusMm) ||
                    !Finite(arc.StartDegrees) || !Finite(arc.SweepDegrees) ||
                    arc.RadiusMm <= 0 || Math.Abs(arc.SweepDegrees) <= 1e-9 || Math.Abs(arc.SweepDegrees) > 360)
                    return Invalid("Invalid circular arc");
                double angle = Math.PI / 180;
                start = PointOnArc(arc, arc.StartDegrees * angle);
                end = PointOnArc(arc, (arc.StartDegrees + arc.SweepDegrees) * angle);
                if (!Valid(start) || !Valid(end)) return Invalid("Arc coordinates out of range");
                segmentLength = arc.RadiusMm * Math.Abs(arc.SweepDegrees) * angle;
                if (!Finite(segmentLength)) return Invalid("Arc length out of range");
                double stepRadians = maxChordErrorMm >= arc.RadiusMm
                    ? Math.PI : 2 * Math.Acos(1 - maxChordErrorMm / arc.RadiusMm);
                int steps = (int)Math.Ceiling(Math.Abs(arc.SweepDegrees) * angle / stepRadians);
                if (steps > 100000) return Invalid("Arc requires too many preview samples");
                steps = Math.Max(1, steps);
                var sampled = new List<PreviewPoint>(steps + 1);
                for (int i = 0; i <= steps; i++)
                    sampled.Add(PointOnArc(arc, (arc.StartDegrees +
                        arc.SweepDegrees * i / steps) * angle));
                samples = sampled;
            }
            else return Invalid("Unsupported segment");

            if (previousEnd is not null && Distance(previousEnd.Value, start) > joinToleranceMm)
                return Invalid("Disconnected segments");
            firstStart ??= start;
            if (points.Count == 0) points.AddRange(samples);
            else points.AddRange(samples.Skip(1));
            previousEnd = end;
            length += segmentLength;
            if (!Finite(length)) return Invalid("Path length out of range");
        }

        if (path.Closed && Distance(previousEnd!.Value, firstStart!.Value) > joinToleranceMm)
            return Invalid("Open contour marked as closed");
        warnings.Add("VISUAL PREVIEW ONLY: no kerf compensation, lead-in/out, collision checks or machine output");
        if (!path.Closed) warnings.Add("Open path: not a closed cutting profile");
        return new(points, length, warnings, true);

        PreviewPathResult Invalid(string reason) =>
            new(Array.Empty<PreviewPoint>(), 0, new[] { reason }, false);
    }

    private static PreviewPoint PointOnArc(PreviewArc arc, double radians) =>
        new(arc.Center.X + arc.RadiusMm * Math.Cos(radians),
            arc.Center.Y + arc.RadiusMm * Math.Sin(radians));
    private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
    private static bool Valid(PreviewPoint p) => Finite(p.X) && Finite(p.Y);
    private static double Distance(PreviewPoint a, PreviewPoint b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
