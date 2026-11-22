namespace AICutPro.Core;

/// <summary>
/// Analytic geometry-only inspection of a set of closed LINE/ARC rings.
/// Results are NOT CAD/CAM clearance and can never trigger equipment motion.
/// </summary>
public sealed record PreviewRingInfo(int Index, int? ParentIndex, int Depth,
    double AreaMm2, double PerimeterMm, bool IsMaterialBoundary);

public sealed record PreviewTopologyResult(bool IsValid,
    IReadOnlyList<PreviewRingInfo> Rings, double NetAreaMm2,
    IReadOnlyList<string> Issues)
{
    public bool CanStartMachine => false;
}

public static class PreviewContourTopology
{
    private const double Rad = Math.PI / 180d;
    private static bool Finite(double v) => double.IsFinite(v);
    private static double Normalize(double angle) => ((angle % 360) + 360) % 360;

    private static PreviewPoint Start(PreviewSegment s) => s switch
    {
        PreviewLine l => l.Start,
        PreviewArc a => new(a.Center.X+a.RadiusMm*Math.Cos(a.StartDegrees*Rad),
                            a.Center.Y+a.RadiusMm*Math.Sin(a.StartDegrees*Rad)),
        _ => new(double.NaN,double.NaN)
    };

    private static bool OnSweep(PreviewArc arc, double theta)
    {
        var angle = Normalize(theta/Rad);
        var start = Normalize(arc.StartDegrees);
        var progress = arc.SweepDegrees >= 0
            ? Normalize(angle-start) : Normalize(start-angle);
        // A full revolution in a single arc is intentionally not supported.
        return progress <= Math.Abs(arc.SweepDegrees) + 1e-9;
    }

    /// <summary>
    /// Horizontal analytic ray cast. Null means near a vertex, tangent or other
    /// numerically ambiguous geometry: fail closed instead of guessing.
    /// </summary>
    private static bool? Contains(PreviewPath boundary, PreviewPoint point, double tolerance)
    {
        if (!Finite(point.X) || !Finite(point.Y)) return null;
        var crossings = 0;
        foreach (var segment in boundary.Segments)
        {
            if (segment is PreviewLine line)
            {
                var a=line.Start; var b=line.End;
                if (!Finite(a.X)||!Finite(a.Y)||!Finite(b.X)||!Finite(b.Y)) return null;
                // A ray through a boundary vertex can be counted using the
                // conventional half-open rule, but near-vertex rays fail closed.
                if (Math.Abs(point.Y-a.Y) < tolerance*0.001 ||
                    Math.Abs(point.Y-b.Y) < tolerance*0.001)
                    return null;
                if ((a.Y > point.Y) != (b.Y > point.Y))
                {
                    var x=a.X+(point.Y-a.Y)*(b.X-a.X)/(b.Y-a.Y);
                    if (!Finite(x)||Math.Abs(x-point.X)<=tolerance) return null;
                    if (x>point.X) crossings++;
                }
            }
            else if (segment is PreviewArc arc)
            {
                var dy=(point.Y-arc.Center.Y)/arc.RadiusMm;
                if (!Finite(dy)) return null;
                if (Math.Abs(dy)>1+1e-12)continue;
                if (Math.Abs(Math.Abs(dy)-1)<1e-10)
                    return null; // tangent: ambiguous ray
                var theta=Math.Asin(dy);
                foreach (var angle in new[]{theta,Math.PI-theta})
                {
                    if (!OnSweep(arc,angle))continue;
                    var endpointDistance=Math.Min(
                        Math.Abs(Normalize(angle/Rad-arc.StartDegrees)),
                        Math.Abs(Normalize(arc.StartDegrees-angle/Rad)));
                    // Avoid endpoint double counting with adjacent segments.
                    var start=arc.StartDegrees*Rad;
                    var end=(arc.StartDegrees+arc.SweepDegrees)*Rad;
                    var atStart=Math.Abs(Math.Atan2(Math.Sin(angle-start),Math.Cos(angle-start)))<1e-9;
                    var atEnd=Math.Abs(Math.Atan2(Math.Sin(angle-end),Math.Cos(angle-end)))<1e-9;
                    if (atStart||atEnd) return null;
                    var x=arc.Center.X+arc.RadiusMm*Math.Cos(angle);
                    if (!Finite(x)||Math.Abs(x-point.X)<=tolerance) return null;
                    if (x>point.X) crossings++;
                }
            }
            else return null;
        }
        return crossings%2==1;
    }

    /// <summary>
    /// Computes nesting by strict analytical boundary nonintersection and ray containment.
    /// Supports alternate material/void islands by depth parity, but remains read-only.
    /// </summary>
    public static PreviewTopologyResult Analyze(IReadOnlyList<PreviewPath> paths,
        double toleranceMm = 0.01, double intersectionToleranceMm = 1e-7)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Finite(toleranceMm)||toleranceMm<=0||toleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!Finite(intersectionToleranceMm) || intersectionToleranceMm < 1e-9 || intersectionToleranceMm > 0.01)
            throw new ArgumentOutOfRangeException(nameof(intersectionToleranceMm));
        if (paths.Count is < 1 or > 1000)
            return Reject("Invalid number of closed rings.");
        var measurements=new PreviewContourMetricsResult[paths.Count];
        for(var i=0;i<paths.Count;i++)
        {
            measurements[i]=PreviewContourMetrics.Measure(paths[i],toleranceMm,intersectionToleranceMm);
            if(!measurements[i].IsValid)
                return Reject($"Ring {i+1}: {string.Join("; ",measurements[i].Issues)}");
        }
        for(int i=0;i<paths.Count;i++)
        for(int j=i+1;j<paths.Count;j++)
        {
            var issues=PreviewContourValidator.ValidatePair(paths[i],paths[j],toleranceMm,intersectionToleranceMm);
            if(issues.Count>0) return Reject($"Rings {i+1}/{j+1}: {string.Join("; ",issues)}");
        }

        var parents=new int[paths.Count];
        Array.Fill(parents,-1);
        for(int i=0;i<paths.Count;i++)
        {
            double smallest=double.PositiveInfinity;
            for(int j=0;j<paths.Count;j++)
            {
                if(i==j || measurements[j].AreaMm2<=measurements[i].AreaMm2)continue;
                // Different ray heights avoid false ambiguity when a witness sits
                // exactly level with a vertex or a tangent of the outer ring.
                bool? inside=null;
                foreach(var segment in paths[i].Segments.Take(12))
                {
                    foreach(var fraction in new[]{0.37,0.53,0.71})
                    {
                        var witness=segment switch
                        {
                            PreviewLine l => new PreviewPoint(
                                l.Start.X+(l.End.X-l.Start.X)*fraction,
                                l.Start.Y+(l.End.Y-l.Start.Y)*fraction),
                            PreviewArc a => new PreviewPoint(
                                a.Center.X+a.RadiusMm*Math.Cos((a.StartDegrees+a.SweepDegrees*fraction)*Rad),
                                a.Center.Y+a.RadiusMm*Math.Sin((a.StartDegrees+a.SweepDegrees*fraction)*Rad)),
                            _ => Start(segment)
                        };
                        inside=Contains(paths[j],witness,toleranceMm);
                        if(inside is not null)break;
                    }
                    if(inside is not null)break;
                }
                if(inside is null)
                    return Reject($"Ambiguous containment between rings {i+1} and {j+1}.");
                if(inside.Value && measurements[j].AreaMm2<smallest)
                {
                    smallest=measurements[j].AreaMm2;
                    parents[i]=j;
                }
            }
        }
        var info=new List<PreviewRingInfo>();
        double net=0;
        for(var i=0;i<paths.Count;i++)
        {
            int depth=0, current=i;
            while(parents[current]>=0)
            {
                current=parents[current];depth++;
                if(depth>paths.Count)return Reject("Cyclic containment.");
            }
            var material=depth%2==0;
            net+=material?measurements[i].AreaMm2:-measurements[i].AreaMm2;
            info.Add(new(i,parents[i]<0?null:parents[i],depth,
                measurements[i].AreaMm2,measurements[i].PerimeterMm,material));
        }
        if(!Finite(net)||net<=0)return Reject("Nonpositive or unstable net material area.");
        return new(true,info,net,[]);

        static PreviewTopologyResult Reject(string error) => new(false,[],0,[error]);
    }
}
