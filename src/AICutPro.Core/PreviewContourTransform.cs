namespace AICutPro.Core;

/// <summary>
/// Reversible geometric translation and rotation of read-only analytic 2D contours.
/// Lines remain lines and circular arcs remain true circular arcs, never chords.
/// This is a CAD inspection primitive, not a CAM-approved edit or toolpath.
/// </summary>
public static class PreviewContourTransform
{
    public static PreviewPath Move(PreviewPath source, double dxMm, double dyMm)
    {
        ArgumentNullException.ThrowIfNull(source);
        Check(dxMm); Check(dyMm);
        return Map(source, p => new PreviewPoint(p.X+dxMm,p.Y+dyMm), 0);
    }

    public static PreviewPath Rotate(PreviewPath source, PreviewPoint pivot, double degrees)
    {
        ArgumentNullException.ThrowIfNull(source);
        Check(pivot.X);Check(pivot.Y);Check(degrees);
        if(Math.Abs(degrees)>360_000)
            throw new ArgumentOutOfRangeException(nameof(degrees));
        var radians=degrees*Math.PI/180d;
        var c=Math.Cos(radians);
        var s=Math.Sin(radians);
        return Map(source,p=>
        {
            var x=p.X-pivot.X;
            var y=p.Y-pivot.Y;
            return new PreviewPoint(pivot.X+x*c-y*s,pivot.Y+x*s+y*c);
        },degrees);
    }

    private static PreviewPath Map(PreviewPath source,Func<PreviewPoint,PreviewPoint> transform,
        double angleOffset)
    {
        if(source.Segments is null || source.Segments.Count is < 1 or > 10000)
            throw new FormatException("Invalid contour segment count.");
        PreviewPoint Checked(PreviewPoint p)
        {
            var result=transform(p);
            Check(result.X);Check(result.Y);
            return result;
        }
        var segments=new List<PreviewSegment>(source.Segments.Count);
        foreach(var segment in source.Segments)
        {
            switch(segment)
            {
                case PreviewLine l:
                    segments.Add(new PreviewLine(Checked(l.Start),Checked(l.End)));
                    break;
                case PreviewArc a:
                    Check(a.RadiusMm);Check(a.StartDegrees);Check(a.SweepDegrees);
                    if(a.RadiusMm<=0 || Math.Abs(a.SweepDegrees)<1e-9 ||
                       Math.Abs(a.SweepDegrees)>360)
                        throw new FormatException("Invalid circular arc.");
                    segments.Add(new PreviewArc(Checked(a.Center),a.RadiusMm,
                        ((a.StartDegrees+angleOffset)%360+360)%360,a.SweepDegrees));
                    break;
                default: throw new FormatException("Unsupported contour segment.");
            }
        }
        return new PreviewPath(segments,source.Closed);
    }

    private static void Check(double value)
    {
        if(!double.IsFinite(value)||Math.Abs(value)>1e9)
            throw new FormatException("Nonfinite or out-of-range CAD coordinate.");
    }
}
