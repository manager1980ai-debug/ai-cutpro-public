namespace AICutPro.Core;

/// <summary>
/// Analytical 2D extents of preview LINE/ARC contours. Circular arc extrema
/// are checked at axis-aligned cardinal angles, never approximated by chords.
/// This is a drafting aid, not a manufacturing or machine-motion approval.
/// </summary>
public sealed record PreviewContourBoundsResult(
    bool IsValid, double MinX, double MinY, double MaxX, double MaxY,
    IReadOnlyList<string> Issues)
{
    public double WidthMm => IsValid ? MaxX - MinX : 0;
    public double HeightMm => IsValid ? MaxY - MinY : 0;
    public bool CanStartMachine => false;
}

public static class PreviewContourBounds
{
    private static double Normalize(double degrees) => ((degrees % 360)+360)%360;

    public static PreviewContourBoundsResult Measure(PreviewPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if(path.Segments is null || path.Segments.Count is < 1 or > 10000)
            return Invalid("Invalid contour segment count.");
        double minX=double.PositiveInfinity,minY=double.PositiveInfinity;
        double maxX=double.NegativeInfinity,maxY=double.NegativeInfinity;
        void Add(double x,double y)
        {
            if(!double.IsFinite(x)||!double.IsFinite(y))
                throw new FormatException("Nonfinite contour bounds.");
            minX=Math.Min(minX,x);minY=Math.Min(minY,y);
            maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);
        }
        try
        {
            foreach(var segment in path.Segments)
            {
                if(segment is PreviewLine l)
                {
                    Add(l.Start.X,l.Start.Y);
                    Add(l.End.X,l.End.Y);
                }
                else if(segment is PreviewArc a)
                {
                    if(!double.IsFinite(a.Center.X)||!double.IsFinite(a.Center.Y)||
                       !double.IsFinite(a.RadiusMm)||a.RadiusMm<=0||
                       !double.IsFinite(a.StartDegrees)||!double.IsFinite(a.SweepDegrees)||
                       Math.Abs(a.SweepDegrees)<1e-9||Math.Abs(a.SweepDegrees)>360)
                        return Invalid("Invalid analytic arc.");
                    void AddAngle(double angle)
                    {
                        var radians=angle*Math.PI/180;
                        Add(a.Center.X+a.RadiusMm*Math.Cos(radians),
                            a.Center.Y+a.RadiusMm*Math.Sin(radians));
                    }
                    AddAngle(a.StartDegrees);
                    AddAngle(a.StartDegrees+a.SweepDegrees);
                    foreach(double angle in new[]{0d,90d,180d,270d})
                    {
                        var delta=a.SweepDegrees>0
                            ? Normalize(angle-a.StartDegrees)
                            : Normalize(a.StartDegrees-angle);
                        if(delta<=Math.Abs(a.SweepDegrees)+1e-10)
                            AddAngle(angle);
                    }
                }
                else return Invalid("Unsupported analytic contour segment.");
            }
            if(maxX<minX||maxY<minY)return Invalid("No finite geometry.");
            return new(true,minX,minY,maxX,maxY,[]);
        }
        catch(FormatException ex)
        {
            return Invalid(ex.Message);
        }
    }

    private static PreviewContourBoundsResult Invalid(string message) =>
        new(false,0,0,0,0,[message]);
}
