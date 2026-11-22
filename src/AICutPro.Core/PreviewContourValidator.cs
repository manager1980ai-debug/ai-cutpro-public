namespace AICutPro.Core;

/// <summary>
/// Conservative analytic intersection checker for already connected preview paths.
/// No kerf, manufacturing tolerances or equipment control. Near-degenerate situations are rejected.
/// </summary>
public static class PreviewContourValidator
{
    private const double DefaultIntersectionToleranceMm = 1e-7;
    private static double Cross(PreviewPoint a, PreviewPoint b) => a.X * b.Y - a.Y * b.X;
    private static PreviewPoint Sub(PreviewPoint a, PreviewPoint b) => new(a.X-b.X,a.Y-b.Y);
    private static double Dot(PreviewPoint a, PreviewPoint b) => a.X*b.X + a.Y*b.Y;
    private static double Dist(PreviewPoint a, PreviewPoint b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private static double Normalize(double a) => (a%360+360)%360;
    private static PreviewPoint OnArc(PreviewArc a, double angleDegrees)
    {
        var t=angleDegrees*Math.PI/180;
        return new(a.Center.X+a.RadiusMm*Math.Cos(t),a.Center.Y+a.RadiusMm*Math.Sin(t));
    }
    private static (PreviewPoint Start,PreviewPoint End) Ends(PreviewSegment s) => s switch
    {
        PreviewLine l => (l.Start,l.End),
        PreviewArc a => (OnArc(a,a.StartDegrees),OnArc(a,a.StartDegrees+a.SweepDegrees)),
        _ => throw new ArgumentException("Unsupported preview segment")
    };
    private static bool AngleInArc(PreviewArc arc,PreviewPoint p)
    {
        var angle=Normalize(Math.Atan2(p.Y-arc.Center.Y,p.X-arc.Center.X)*180/Math.PI);
        var start=Normalize(arc.StartDegrees);
        var sweep=arc.SweepDegrees;
        var progress=sweep>=0 ? Normalize(angle-start) : Normalize(start-angle);
        return progress<=Math.Abs(sweep)+1e-6;
    }
    private static IReadOnlyList<PreviewPoint>? Intersections(PreviewSegment s,PreviewSegment t, double eps)
    {
        if(s is PreviewLine a && t is PreviewLine b) return LineLine(a,b,eps);
        if(s is PreviewLine l && t is PreviewArc arc) return LineArc(l,arc,eps);
        if(s is PreviewArc arc1 && t is PreviewLine line) return LineArc(line,arc1,eps);
        if(s is PreviewArc c && t is PreviewArc d) return ArcArc(c,d,eps);
        return null;
    }
    private static IReadOnlyList<PreviewPoint>? LineLine(PreviewLine a,PreviewLine b,double eps)
    {
        var p=a.Start; var r=Sub(a.End,a.Start);
        var q=b.Start; var s=Sub(b.End,b.Start);
        var det=Cross(r,s);
        var qmp=Sub(q,p);
        if(Math.Abs(det)<=eps)
        {
            if(Math.Abs(Cross(qmp,r))>eps) return [];
            // Collinear segments may share exactly one endpoint (a legitimate
            // subdivision), but overlapping interiors remain ambiguous.
            var useX = Math.Abs(r.X) >= Math.Abs(r.Y);
            var a0 = useX ? a.Start.X : a.Start.Y;
            var a1 = useX ? a.End.X : a.End.Y;
            var b0 = useX ? b.Start.X : b.Start.Y;
            var b1 = useX ? b.End.X : b.End.Y;
            var lo = Math.Max(Math.Min(a0,a1),Math.Min(b0,b1));
            var hi = Math.Min(Math.Max(a0,a1),Math.Max(b0,b1));
            if (hi < lo-eps) return [];
            if (hi > lo+eps) return null;
            foreach (var endpoint in new[]{a.Start,a.End})
                if (Dist(endpoint,b.Start)<=eps || Dist(endpoint,b.End)<=eps)
                    return [endpoint];
            return null;
        }
        var u=Cross(qmp,r)/det;
        var t=Cross(qmp,s)/det;
        if(t < -eps || t > 1+eps || u < -eps || u > 1+eps) return [];
        return [new(p.X+t*r.X,p.Y+t*r.Y)];
    }
    private static IReadOnlyList<PreviewPoint>? LineArc(PreviewLine l,PreviewArc arc,double eps)
    {
        var d=Sub(l.End,l.Start);
        var f=Sub(l.Start,arc.Center);
        var A=Dot(d,d);
        if(A<=eps*eps) return null;
        var B=2*Dot(f,d);
        var C=Dot(f,f)-arc.RadiusMm*arc.RadiusMm;
        var discriminant=B*B-4*A*C;
        if(discriminant < -eps) return [];
        var result=new List<PreviewPoint>();
        var root=Math.Sqrt(Math.Max(0,discriminant));
        foreach(var parameter in new[]{(-B-root)/(2*A),(-B+root)/(2*A)})
        {
            if(parameter < -eps || parameter > 1+eps)continue;
            var p=new PreviewPoint(l.Start.X+parameter*d.X,l.Start.Y+parameter*d.Y);
            if(AngleInArc(arc,p) && !result.Any(x=>Dist(x,p)<=eps))
                result.Add(p);
        }
        return result;
    }
    private static IReadOnlyList<PreviewPoint>? ArcArc(PreviewArc a,PreviewArc b,double eps)
    {
        var delta=Sub(b.Center,a.Center);
        var distance=Math.Sqrt(delta.X*delta.X+delta.Y*delta.Y);
        if(distance<=eps)
        {
            if (Math.Abs(a.RadiusMm-b.RadiusMm)>eps) return [];
            // Exact angular interval overlap check, split at the 360-degree seam.
            // A common endpoint is allowed; a positive sweep overlap is not.
            static IReadOnlyList<(double Lo,double Hi)> Ranges(PreviewArc arc)
            {
                var begin=Normalize(arc.StartDegrees+
                    Math.Min(0,arc.SweepDegrees));
                var finish=begin+Math.Abs(arc.SweepDegrees);
                return finish<=360
                    ? [(begin,finish)]
                    : [(begin,360),(0,finish-360)];
            }
            var ra=Ranges(a);var rb=Ranges(b);
            foreach(var ia in ra)
            foreach(var ib in rb)
                if(Math.Min(ia.Hi,ib.Hi)-Math.Max(ia.Lo,ib.Lo)>1e-8)
                    return null;
            var endpoints=new[]{Ends(a).Start,Ends(a).End,Ends(b).Start,Ends(b).End};
            var shared=new List<PreviewPoint>();
            foreach(var p in endpoints)
                if (AngleInArc(a,p) && AngleInArc(b,p) &&
                    !shared.Any(q=>Dist(p,q)<=eps))shared.Add(p);
            return shared;
        }
        if(distance>a.RadiusMm+b.RadiusMm+eps ||
           distance<Math.Abs(a.RadiusMm-b.RadiusMm)-eps)return [];
        var x=(a.RadiusMm*a.RadiusMm-b.RadiusMm*b.RadiusMm+distance*distance)/(2*distance);
        var heightSquared=a.RadiusMm*a.RadiusMm-x*x;
        if(heightSquared < -eps) return [];
        var h=Math.Sqrt(Math.Max(0,heightSquared));
        var ux=delta.X/distance;var uy=delta.Y/distance;
        var center=new PreviewPoint(a.Center.X+ux*x,a.Center.Y+uy*x);
        var points=new[]
        {
            new PreviewPoint(center.X-uy*h,center.Y+ux*h),
            new PreviewPoint(center.X+uy*h,center.Y-ux*h)
        };
        return points.Where(p=>AngleInArc(a,p)&&AngleInArc(b,p))
            .DistinctBy(p=>(Math.Round(p.X,7),Math.Round(p.Y,7))).ToArray();
    }

    /// <summary>
    /// Checks contacts between TWO distinct analytic preview rings. A touching or crossing
    /// boundary is an error even when each ring is individually simple. This does not
    /// determine nesting, hole ownership, or eligibility for CAM.
    /// </summary>
    public static IReadOnlyList<string> ValidatePair(PreviewPath first, PreviewPath second,
        double toleranceMm = 0.01, double intersectionToleranceMm = DefaultIntersectionToleranceMm)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (!double.IsFinite(intersectionToleranceMm) || intersectionToleranceMm < 1e-9 || intersectionToleranceMm > 0.01)
            throw new ArgumentOutOfRangeException(nameof(intersectionToleranceMm));
        if (!double.IsFinite(toleranceMm) || toleranceMm <= 0 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!first.Closed || !second.Closed ||
            first.Segments.Count < 2 || second.Segments.Count < 2 ||
            first.Segments.Count > 2000 || second.Segments.Count > 2000)
            return ["Invalid or unsupported contour pair."];
        // Guard the quadratic preview comparison from untrusted drawings.
        if ((long)first.Segments.Count * second.Segments.Count > 1000000)
            return ["Contour pair exceeds bounded intersection inspection."];
        for (int i = 0; i < first.Segments.Count; i++)
        for (int j = 0; j < second.Segments.Count; j++)
        {
            var hits = Intersections(first.Segments[i], second.Segments[j], intersectionToleranceMm);
            if (hits is null)
                return [$"Ambiguous coincident or overlapping boundaries at {i + 1}/{j + 1}."];
            if (hits.Count > 0)
                return [$"Distinct contours intersect or touch at segments {i + 1}/{j + 1}."];
        }
        return [];
    }

    public static IReadOnlyList<string> Validate(PreviewPath path,double joinToleranceMm=0.01,
        double intersectionToleranceMm=DefaultIntersectionToleranceMm)
    {
        if(path is null)throw new ArgumentNullException(nameof(path));
        if (!double.IsFinite(intersectionToleranceMm) || intersectionToleranceMm < 1e-9 || intersectionToleranceMm > 0.01)
            throw new ArgumentOutOfRangeException(nameof(intersectionToleranceMm));
        if(!path.Closed)return ["The contour is not closed."];
        if(!double.IsFinite(joinToleranceMm)||joinToleranceMm<=0||joinToleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(joinToleranceMm));
        var segments=path.Segments;
        if(segments==null||segments.Count<2||segments.Count>10000)
            return ["Invalid number of contour segments."];
        var sampled=VirtualToolpath.Sample(path,joinToleranceMm:joinToleranceMm);
        if(!sampled.IsValidForPreview) return sampled.Warnings;
        // Bounded O(N²) check.
        if(segments.Count>2000)return ["Contour too complex for analytic intersection preview."];
        var warnings=new List<string>();
        for(int i=0;i<segments.Count;i++)
        for(int j=i+1;j<segments.Count;j++)
        {
            var intersections=Intersections(segments[i],segments[j],intersectionToleranceMm);
            if(intersections is null)
            {
                warnings.Add($"Ambiguous overlapping or coincident segments {i+1} and {j+1}.");
                return warnings;
            }
            bool neighbors=(j==i+1)||(i==0&&j==segments.Count-1);
            // Adjacent two-segment closed paths share both endpoints.
            var (ai,bi)=Ends(segments[i]);
            var (aj,bj)=Ends(segments[j]);
            foreach(var p in intersections)
            {
                var shared=neighbors && (
                    Dist(p,ai)<=joinToleranceMm && (Dist(p,aj)<=joinToleranceMm||Dist(p,bj)<=joinToleranceMm) ||
                    Dist(p,bi)<=joinToleranceMm && (Dist(p,aj)<=joinToleranceMm||Dist(p,bj)<=joinToleranceMm));
                if(!shared)
                {
                    warnings.Add($"Segments {i+1} and {j+1} intersect or touch away from a shared endpoint.");
                    return warnings;
                }
            }
        }
        return warnings;
    }
}
