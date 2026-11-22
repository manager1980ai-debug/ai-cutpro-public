namespace AICutPro.Core;

/// <summary>
/// Presentation-only sampling for a DXF CCW ARC. Never use sampled points as a cutting path.
/// </summary>
public static class DxfArcPreview
{
    public static IReadOnlyList<Point2> Sample(DxfArc arc, double maxStepDegrees = 4)
    {
        if (arc.Radius <= 0 || !double.IsFinite(arc.Radius) ||
            !double.IsFinite(arc.Center.X) || !double.IsFinite(arc.Center.Y) ||
            !double.IsFinite(arc.StartDeg) || !double.IsFinite(arc.EndDeg) ||
            maxStepDegrees <= 0 || !double.IsFinite(maxStepDegrees))
            throw new ArgumentException("Invalid ARC parameters");
        static double Normalize(double degrees) => ((degrees % 360) + 360) % 360;
        var start = Normalize(arc.StartDeg);
        var sweep = Normalize(arc.EndDeg - arc.StartDeg);
        if (sweep < 1e-9) throw new ArgumentException("Ambiguous zero/full-circle ARC");
        var segments = Math.Clamp((int)Math.Ceiling(sweep / maxStepDegrees), 2, 720);
        var result = new Point2[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var theta = (start + sweep * i / segments) * Math.PI / 180;
            result[i] = new Point2(arc.Center.X + arc.Radius * Math.Cos(theta),
                                   arc.Center.Y + arc.Radius * Math.Sin(theta));
        }
        return result;
    }
}
