namespace AICutPro.Core;

/// <summary>
/// Non-executable, analytic circular ARC-to-ARC tangent fillet for two
/// consecutive arcs of one verified, closed LINE/ARC contour.
/// Searches all finite circle-offset intersections, retains only forward
/// tangent branches and rejects any invalid contour or DXF round-trip.
/// </summary>
public static class PreviewArcArcFilletEditor
{
    private const double Deg = 180d / Math.PI;
    private const double Rad = Math.PI / 180d;

    private static double Normalize(double angle) => (angle % 360 + 360) % 360;
    private static PreviewPoint Point(PreviewArc a, double angle) =>
        new(a.Center.X+a.RadiusMm*Math.Cos(angle*Rad),
            a.Center.Y+a.RadiusMm*Math.Sin(angle*Rad));
    private static double Distance(PreviewPoint a, PreviewPoint b) =>
        Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));

    /// <param name="outgoingIndex">Index of second ARC; immediately preceding segment must be an ARC.</param>
    public static PreviewVertexEditResult Fillet(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int outgoingIndex,
        double radiusMm, double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(toleranceMm) || toleranceMm<0.0001 || toleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!double.IsFinite(radiusMm) || radiusMm<=toleranceMm || radiusMm>1_000_000)
            throw new FormatException("Invalid ARC/ARC fillet radius.");
        if (rings.Count is <1 or >1000 || ringIndex<0 || ringIndex>=rings.Count)
            throw new FormatException("Invalid ARC/ARC ring.");
        var before=PreviewContourTopology.Analyze(rings,toleranceMm);
        if(!before.IsValid)
            throw new FormatException("Unverified ARC/ARC input topology.");
        var source=rings[ringIndex];
        int count=source.Segments.Count;
        if(!source.Closed || count is <3 or >1999 ||
           outgoingIndex<0 || outgoingIndex>=count)
            throw new FormatException("Invalid ARC/ARC segment selection.");
        int incomingIndex=(outgoingIndex+count-1)%count;
        if(source.Segments[incomingIndex] is not PreviewArc incoming ||
           source.Segments[outgoingIndex] is not PreviewArc outgoing)
            throw new FormatException("Selected junction must consist of consecutive ARCs.");
        if(Distance(Point(incoming,incoming.StartDegrees+incoming.SweepDegrees),
                    Point(outgoing,outgoing.StartDegrees))>toleranceMm)
            throw new FormatException("ARC/ARC junction endpoints are disconnected.");
        foreach(var a in new[]{incoming,outgoing})
            if(!double.IsFinite(a.RadiusMm) || a.RadiusMm<=toleranceMm ||
               !double.IsFinite(a.SweepDegrees) ||
               Math.Abs(a.SweepDegrees)<=0.01 || Math.Abs(a.SweepDegrees)>=360)
                throw new FormatException("ARC/ARC source cannot be safely trimmed.");

        var dx=outgoing.Center.X-incoming.Center.X;
        var dy=outgoing.Center.Y-incoming.Center.Y;
        var spacing=Math.Sqrt(dx*dx+dy*dy);
        if(!double.IsFinite(spacing)||spacing<=toleranceMm)
            throw new FormatException("Concentric ARC/ARC junction is not supported.");
        var ux=dx/spacing;var uy=dy/spacing;
        var normalX=-uy;var normalY=ux;
        var candidates=new List<(PreviewArc First,PreviewArc Middle,PreviewArc Last,
                                 double TrimScore)>();
        foreach(var d1 in new[]{incoming.RadiusMm+radiusMm,
                                 Math.Abs(incoming.RadiusMm-radiusMm)})
        foreach(var d2 in new[]{outgoing.RadiusMm+radiusMm,
                                 Math.Abs(outgoing.RadiusMm-radiusMm)})
        {
            if(d1<=toleranceMm || d2<=toleranceMm)continue;
            if(spacing>d1+d2 || spacing<Math.Abs(d1-d2))continue;
            var along=(d1*d1-d2*d2+spacing*spacing)/(2*spacing);
            var heightSquared=d1*d1-along*along;
            if(heightSquared<-1e-7)continue;
            var height=Math.Sqrt(Math.Max(0,heightSquared));
            foreach(var side in new[]{-1d,1d})
            {
                var center=new PreviewPoint(incoming.Center.X+ux*along+side*normalX*height,
                                            incoming.Center.Y+uy*along+side*normalY*height);
                if(!double.IsFinite(center.X)||!double.IsFinite(center.Y)||
                   Math.Abs(center.X)>1_000_000||Math.Abs(center.Y)>1_000_000)continue;
                // Old circle and fillet circle may touch internally or externally.
                foreach(var radial1 in new[]{-1d,1d})
                foreach(var radial2 in new[]{-1d,1d})
                {
                    var aTouch=new PreviewPoint(
                        incoming.Center.X+radial1*(center.X-incoming.Center.X)
                            *incoming.RadiusMm/d1,
                        incoming.Center.Y+radial1*(center.Y-incoming.Center.Y)
                            *incoming.RadiusMm/d1);
                    var bTouch=new PreviewPoint(
                        outgoing.Center.X+radial2*(center.X-outgoing.Center.X)
                            *outgoing.RadiusMm/d2,
                        outgoing.Center.Y+radial2*(center.Y-outgoing.Center.Y)
                            *outgoing.RadiusMm/d2);
                    if(Math.Abs(Distance(aTouch,center)-radiusMm)>Math.Max(1e-6,toleranceMm*0.01) ||
                       Math.Abs(Distance(bTouch,center)-radiusMm)>Math.Max(1e-6,toleranceMm*0.01))
                        continue;
                    var aAngle=Math.Atan2(aTouch.Y-incoming.Center.Y,
                                          aTouch.X-incoming.Center.X)*Deg;
                    var bAngle=Math.Atan2(bTouch.Y-outgoing.Center.Y,
                                          bTouch.X-outgoing.Center.X)*Deg;
                    var firstSign=Math.Sign(incoming.SweepDegrees);
                    var secondSign=Math.Sign(outgoing.SweepDegrees);
                    var aProgress=firstSign>0?Normalize(aAngle-incoming.StartDegrees):
                                                Normalize(incoming.StartDegrees-aAngle);
                    var bProgress=secondSign>0?Normalize(bAngle-outgoing.StartDegrees):
                                                 Normalize(outgoing.StartDegrees-bAngle);
                    if(aProgress<=0.01||aProgress>=Math.Abs(incoming.SweepDegrees)-0.01 ||
                       bProgress<=0.01||bProgress>=Math.Abs(outgoing.SweepDegrees)-0.01)
                        continue;
                    var startAngle=Math.Atan2(aTouch.Y-center.Y,aTouch.X-center.X)*Deg;
                    var endAngle=Math.Atan2(bTouch.Y-center.Y,bTouch.X-center.X)*Deg;
                    foreach(var direction in new[]{-1,1})
                    {
                        var sweep=direction>0?Normalize(endAngle-startAngle):
                                              -Normalize(startAngle-endAngle);
                        if(Math.Abs(sweep)<=0.01||Math.Abs(sweep)>=180)continue;
                        static (double X,double Y) Tangent(
                            PreviewPoint p,PreviewPoint c,double radius,int dir)=>
                            (dir*-(p.Y-c.Y)/radius,dir*(p.X-c.X)/radius);
                        var t1=Tangent(aTouch,center,radiusMm,direction);
                        var t2=Tangent(bTouch,center,radiusMm,direction);
                        var original1=Tangent(aTouch,incoming.Center,incoming.RadiusMm,firstSign);
                        var original2=Tangent(bTouch,outgoing.Center,outgoing.RadiusMm,secondSign);
                        if(t1.X*original1.X+t1.Y*original1.Y<1-1e-7 ||
                           t2.X*original2.X+t2.Y*original2.Y<1-1e-7)
                            continue;
                        candidates.Add((
                            new PreviewArc(incoming.Center,incoming.RadiusMm,
                                incoming.StartDegrees,firstSign*aProgress),
                            new PreviewArc(center,radiusMm,startAngle,sweep),
                            new PreviewArc(outgoing.Center,outgoing.RadiusMm,
                                bAngle,outgoing.SweepDegrees-secondSign*bProgress),
                            Math.Abs(incoming.SweepDegrees)-aProgress+bProgress));
                    }
                }
            }
        }
        // Prefer minimal total angle trimmed; choose only topology-valid branches.
        foreach(var candidate in candidates.OrderBy(c=>c.TrimScore))
        {
            var segments=new List<PreviewSegment>(count+1);
            for(int i=0;i<count;i++)
            {
                if(i==incomingIndex)
                {
                    segments.Add(candidate.First);
                    segments.Add(candidate.Middle);
                }
                else if(i==outgoingIndex)segments.Add(candidate.Last);
                else segments.Add(source.Segments[i]);
            }
            var edited=rings.ToArray();
            edited[ringIndex]=new PreviewPath(segments,true);
            var after=PreviewContourTopology.Analyze(edited,toleranceMm);
            if(!after.IsValid||after.Rings.Count!=before.Rings.Count)continue;
            if(Enumerable.Range(0,before.Rings.Count).Any(i=>
               before.Rings[i].ParentIndex!=after.Rings[i].ParentIndex ||
               before.Rings[i].Depth!=after.Rings[i].Depth ||
               before.Rings[i].IsMaterialBoundary!=after.Rings[i].IsMaterialBoundary))
                continue;
            try
            {
                PreviewDxfRoundTripVerifier.Verify(
                    edited,PreviewDxfExporter.Export(edited,toleranceMm),toleranceMm);
                return new(edited,after.NetAreaMm2,true);
            }
            catch(FormatException) { /* Reject candidate, never bypass DXF checks. */ }
        }
        throw new FormatException("No validated tangent ARC/ARC fillet for this radius.");
    }
}
