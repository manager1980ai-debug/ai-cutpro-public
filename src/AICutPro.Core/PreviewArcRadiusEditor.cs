namespace AICutPro.Core;

/// <summary>
/// Exact, endpoint-fixed radius edit for one circular ARC in a closed
/// LINE/ARC contour. Maintains the original CW/CCW and minor/major branch,
/// all unrelated segments and the hole/island hierarchy. No CAM permission.
/// </summary>
public static class PreviewArcRadiusEditor
{
    public static PreviewVertexEditResult SetRadius(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int segmentIndex,
        double radiusMm, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!double.IsFinite(radiusMm) || radiusMm <= toleranceMm ||
            radiusMm > 1_000_000)
            throw new FormatException("Invalid requested ARC radius.");
        if (ringIndex < 0 || ringIndex >= rings.Count ||
            rings.Count is < 1 or > 1000)
            throw new FormatException("Invalid ARC ring index.");
        var original = PreviewContourTopology.Analyze(rings,toleranceMm);
        if (!original.IsValid)
            throw new FormatException("Source ARC topology is invalid.");
        var source = rings[ringIndex];
        if (!source.Closed || source.Segments.Count is < 2 or > 2000 ||
            segmentIndex < 0 || segmentIndex >= source.Segments.Count ||
            source.Segments[segmentIndex] is not PreviewArc arc)
            throw new FormatException("Selected segment must be a closed-contour ARC.");
        if (!double.IsFinite(arc.RadiusMm) || arc.RadiusMm <= toleranceMm ||
            !double.IsFinite(arc.SweepDegrees) ||
            Math.Abs(arc.SweepDegrees) <= 1e-9 ||
            Math.Abs(arc.SweepDegrees) >= 360)
            throw new FormatException("Degenerate ARC.");
        var rad = Math.PI / 180;
        PreviewPoint Point(double angle) =>
            new(arc.Center.X + arc.RadiusMm*Math.Cos(angle*rad),
                arc.Center.Y + arc.RadiusMm*Math.Sin(angle*rad));
        var first=Point(arc.StartDegrees);
        var last=Point(arc.StartDegrees+arc.SweepDegrees);
        var dx=last.X-first.X;
        var dy=last.Y-first.Y;
        var chord=Math.Sqrt(dx*dx+dy*dy);
        if (!double.IsFinite(chord) || chord <= toleranceMm)
            throw new FormatException("Selected ARC has an invalid chord.");
        var half=chord/2;
        // Geometrically impossible radii must never be rounded up to a
        // semicircle by an arbitrary epsilon near the chord/2 boundary.
        if (radiusMm < half)
            throw new FormatException("Requested radius is smaller than half the ARC chord.");
        var offset=Math.Sqrt(Math.Max(0,radiusMm*radiusMm-half*half));
        // The center's side of the directed chord is determined by the
        // traversal sign and whether the original arc is minor or major.
        var minor=Math.Abs(arc.SweepDegrees)<=180+1e-8;
        var side=Math.Sign(arc.SweepDegrees)*(minor?1:-1);
        var middleX=(first.X+last.X)/2;
        var middleY=(first.Y+last.Y)/2;
        var cx=middleX + side*(-dy/chord)*offset;
        var cy=middleY + side*(dx/chord)*offset;
        if (!double.IsFinite(cx) || !double.IsFinite(cy) ||
            Math.Abs(cx)>1_000_000 || Math.Abs(cy)>1_000_000)
            throw new FormatException("ARC center is outside the supported CAD coordinate range.");
        var start=Math.Atan2(first.Y-cy,first.X-cx)*180/Math.PI;
        var small=2*Math.Asin(Math.Min(1,half/radiusMm))*180/Math.PI;
        var newSweep=Math.Sign(arc.SweepDegrees)*(minor?small:360-small);
        if (Math.Abs(newSweep)>=360 || Math.Abs(newSweep)<=1e-9)
            throw new FormatException("Requested radius creates an unsupported ARC.");
        var newArc = new PreviewArc(new(cx,cy),radiusMm,start,newSweep);
        var predictedEnd = new PreviewPoint(
            cx+radiusMm*Math.Cos((start+newSweep)*rad),
            cy+radiusMm*Math.Sin((start+newSweep)*rad));
        var eps=Math.Max(1e-7,toleranceMm*0.001);
        if (Math.Sqrt(Math.Pow(predictedEnd.X-last.X,2)+
                      Math.Pow(predictedEnd.Y-last.Y,2))>eps)
            throw new FormatException("Edited ARC endpoints are not preserved.");
        var newSegments=source.Segments.ToArray();
        newSegments[segmentIndex]=newArc;
        var edited=rings.ToArray();
        edited[ringIndex]=new PreviewPath(newSegments,true);
        var after=PreviewContourTopology.Analyze(edited,toleranceMm);
        if (!after.IsValid || after.Rings.Count != original.Rings.Count)
            throw new FormatException("ARC radius edit invalidates contour topology.");
        for(int i=0;i<original.Rings.Count;i++)
            if(original.Rings[i].ParentIndex!=after.Rings[i].ParentIndex ||
               original.Rings[i].Depth!=after.Rings[i].Depth ||
               original.Rings[i].IsMaterialBoundary!=after.Rings[i].IsMaterialBoundary)
                throw new FormatException("ARC radius edit changes hole or island ownership.");
        var serialized=PreviewDxfExporter.Export(edited,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(edited,serialized,toleranceMm);
        return new(edited,after.NetAreaMm2,true);
    }
}
