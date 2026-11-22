namespace AICutPro.Core;

/// <summary>
/// Preview-only analytic tangent fillet at an incoming LINE to outgoing ARC
/// junction. No CAM output, interpolated geometry, or machine control.
/// </summary>
public static class PreviewLineArcFilletEditor
{
    private const double Rad=Math.PI/180;
    private static double Mod(double a)=>(a%360+360)%360;
    private static double Distance(PreviewPoint a,PreviewPoint b)=>
        Math.Hypot(a.X-b.X,a.Y-b.Y);

    public static PreviewVertexEditResult FilletIncomingLine(
        IReadOnlyList<PreviewPath> rings,int ringIndex,int arcSegmentIndex,
        double radiusMm,double toleranceMm=0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if(!double.IsFinite(toleranceMm)||toleranceMm<0.0001||toleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if(!double.IsFinite(radiusMm)||radiusMm<=toleranceMm||radiusMm>1_000_000)
            throw new FormatException("Invalid LINE/ARC fillet radius.");
        if(rings.Count is <1 or >1000 || ringIndex<0||ringIndex>=rings.Count)
            throw new FormatException("Invalid fillet ring.");
        var before=PreviewContourTopology.Analyze(rings,toleranceMm);
        if(!before.IsValid)throw new FormatException("Invalid source topology.");
        var path=rings[ringIndex];
        var n=path.Segments.Count;
        if(!path.Closed||n is <3 or >1999||arcSegmentIndex<0||arcSegmentIndex>=n)
            throw new FormatException("Invalid LINE/ARC segment selection.");
        int prev=(arcSegmentIndex+n-1)%n;
        if(path.Segments[prev] is not PreviewLine line ||
           path.Segments[arcSegmentIndex] is not PreviewArc arc)
            throw new FormatException("Select an outgoing ARC immediately after an incoming LINE.");
        var s=Math.Sign(arc.SweepDegrees);
        if(!double.IsFinite(arc.RadiusMm)||arc.RadiusMm<=toleranceMm||
           !double.IsFinite(arc.SweepDegrees)||Math.Abs(arc.SweepDegrees)<0.01||
           Math.Abs(arc.SweepDegrees)>=360)
            throw new FormatException("ARC cannot be trimmed.");
        var arcStart=new PreviewPoint(
            arc.Center.X+arc.RadiusMm*Math.Cos(arc.StartDegrees*Rad),
            arc.Center.Y+arc.RadiusMm*Math.Sin(arc.StartDegrees*Rad));
        var length=Distance(line.Start,line.End);
        if(Distance(line.End,arcStart)>toleranceMm||length<=2*toleranceMm)
            throw new FormatException("LINE/ARC endpoint disconnected or too short.");
        var ux=(line.End.X-line.Start.X)/length;
        var uy=(line.End.Y-line.Start.Y)/length;
        var nx=-uy;var ny=ux;
        var candidates=new List<(PreviewLine Line,PreviewArc Fillet,PreviewArc Arc,double Trim)>();
        foreach(double side in new[]{-1d,1d})
        foreach(double d in new[]{arc.RadiusMm+radiusMm,Math.Abs(arc.RadiusMm-radiusMm)})
        {
            if(d<=toleranceMm)continue;
            var wx=line.Start.X+side*radiusMm*nx-arc.Center.X;
            var wy=line.Start.Y+side*radiusMm*ny-arc.Center.Y;
            var along=wx*ux+wy*uy;
            var across=wx*nx+wy*ny;
            var square=d*d-across*across;
            if(square<0)continue;
            foreach(double root in new[]{-1d,1d})
            {
                var t=-along+root*Math.Sqrt(Math.Max(0,square));
                if(t<=toleranceMm||t>=length-toleranceMm)continue;
                var touchLine=new PreviewPoint(line.Start.X+t*ux,line.Start.Y+t*uy);
                var center=new PreviewPoint(touchLine.X+side*radiusMm*nx,
                                            touchLine.Y+side*radiusMm*ny);
                var cx=center.X-arc.Center.X;var cy=center.Y-arc.Center.Y;
                var centerDistance=Math.Hypot(cx,cy);
                if(centerDistance<=toleranceMm||!double.IsFinite(centerDistance))continue;
                foreach(double radial in new[]{-1d,1d})
                {
                    var touchArc=new PreviewPoint(arc.Center.X+radial*cx/centerDistance*arc.RadiusMm,
                                                  arc.Center.Y+radial*cy/centerDistance*arc.RadiusMm);
                    var angleArc=Math.Atan2(touchArc.Y-arc.Center.Y,
                                             touchArc.X-arc.Center.X)/Rad;
                    var trim=s>0?Mod(angleArc-arc.StartDegrees):Mod(arc.StartDegrees-angleArc);
                    if(trim<0.01||trim>=Math.Abs(arc.SweepDegrees)-0.01)continue;
                    var filletStart=Math.Atan2(touchLine.Y-center.Y,touchLine.X-center.X)/Rad;
                    var filletEnd=Math.Atan2(touchArc.Y-center.Y,touchArc.X-center.X)/Rad;
                    var sweep=side>0?Mod(filletEnd-filletStart):-Mod(filletStart-filletEnd);
                    if(Math.Abs(sweep)<=0.01||Math.Abs(sweep)>=180)continue;
                    var tx=side>0?-(touchArc.Y-center.Y)/radiusMm:
                                       (touchArc.Y-center.Y)/radiusMm;
                    var ty=side>0? (touchArc.X-center.X)/radiusMm:
                                      -(touchArc.X-center.X)/radiusMm;
                    var ax=-s*(touchArc.Y-arc.Center.Y)/arc.RadiusMm;
                    var ay= s*(touchArc.X-arc.Center.X)/arc.RadiusMm;
                    if(tx*ax+ty*ay<1-1e-7)continue;
                    if(!double.IsFinite(center.X)||!double.IsFinite(center.Y)||
                       Math.Abs(center.X)>1_000_000||Math.Abs(center.Y)>1_000_000)continue;
                    candidates.Add((new PreviewLine(line.Start,touchLine),
                        new PreviewArc(center,radiusMm,filletStart,sweep),
                        new PreviewArc(arc.Center,arc.RadiusMm,angleArc,
                                       arc.SweepDegrees-s*trim),trim));
                }
            }
        }
        foreach(var candidate in candidates.OrderBy(x=>x.Trim))
        {
            var segments=new List<PreviewSegment>(n+1);
            for(int i=0;i<n;i++)
            {
                if(i==prev){segments.Add(candidate.Line);segments.Add(candidate.Fillet);}
                else if(i==arcSegmentIndex)segments.Add(candidate.Arc);
                else segments.Add(path.Segments[i]);
            }
            var updated=rings.ToArray();
            updated[ringIndex]=new PreviewPath(segments,true);
            var after=PreviewContourTopology.Analyze(updated,toleranceMm);
            if(!after.IsValid||after.Rings.Count!=before.Rings.Count)continue;
            if(Enumerable.Range(0,before.Rings.Count).Any(i=>
                before.Rings[i].ParentIndex!=after.Rings[i].ParentIndex||
                before.Rings[i].Depth!=after.Rings[i].Depth||
                before.Rings[i].IsMaterialBoundary!=after.Rings[i].IsMaterialBoundary))
                continue;
            try
            {
                PreviewDxfRoundTripVerifier.Verify(
                    updated,PreviewDxfExporter.Export(updated,toleranceMm),toleranceMm);
                return new(updated,after.NetAreaMm2,true);
            }
            catch(FormatException){ }
        }
        throw new FormatException("No topology-valid tangent LINE/ARC fillet for this radius.");
    }
}
