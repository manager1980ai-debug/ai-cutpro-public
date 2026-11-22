namespace AICutPro.Core;

/// <summary>
/// Preview-only circular fillet of a convex or concave corner in a verified closed
/// LINE/ARC ring, where the two selected adjacent segments are LINE.
/// Builds an exact tangent ARC, never a polyline approximation.
/// This is CAD geometry only, not toolpath radius compensation.
/// </summary>
public static class PreviewCornerFilletEditor
{
    public static PreviewVertexEditResult FilletVertex(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int vertexIndex,
        double radiusMm, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!double.IsFinite(radiusMm) || radiusMm <= toleranceMm || radiusMm > 1_000_000)
            throw new FormatException("Fillet radius must be finite and greater than tolerance.");
        if (rings.Count is < 1 or > 1000 || ringIndex < 0 || ringIndex >= rings.Count)
            throw new FormatException("Invalid fillet ring index.");
        var before = PreviewContourTopology.Analyze(rings,toleranceMm);
        if (!before.IsValid)
            throw new FormatException("Source fillet topology is invalid.");
        var ring = rings[ringIndex];
        if (!ring.Closed || ring.Segments.Count is < 3 or > 1999)
            throw new FormatException("Fillet requires a bounded closed LINE/ARC ring.");
        var n=ring.Segments.Count;
        if (vertexIndex < 0 || vertexIndex >= n)
            throw new FormatException("Invalid fillet vertex index.");
        var previousIndex=(vertexIndex+n-1)%n;
        if (ring.Segments[previousIndex] is not PreviewLine incoming ||
            ring.Segments[vertexIndex] is not PreviewLine outgoing)
            throw new FormatException("Selected corner must join two LINE segments.");
        static double Distance(PreviewPoint a,PreviewPoint b) =>
            Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        if (Distance(incoming.End,outgoing.Start)>toleranceMm)
            throw new FormatException("Selected LINE segments are disconnected.");

        var corner=outgoing.Start;
        var previous=incoming.Start;
        var next=outgoing.End;
        var length1=Distance(corner,previous);
        var length2=Distance(corner,next);
        if (!double.IsFinite(length1) || !double.IsFinite(length2) ||
            length1<=toleranceMm || length2<=toleranceMm)
            throw new FormatException("Fillet adjacent edge is degenerate.");
        var ux=(previous.X-corner.X)/length1;
        var uy=(previous.Y-corner.Y)/length1;
        var vx=(next.X-corner.X)/length2;
        var vy=(next.Y-corner.Y)/length2;
        var cross=ux*vy-uy*vx;
        var dot=Math.Clamp(ux*vx+uy*vy,-1d,1d);
        var theta=Math.Acos(dot);
        if (Math.Abs(cross)<1e-6 || theta<1e-3 || Math.PI-theta<1e-3)
            throw new FormatException("Fillet corner is nearly straight or degenerate.");

        // The local turn sets CW/CCW for convex and concave corners.
        // Global orientation and material ownership are checked analytically
        // by PreviewContourTopology, including any unchanged ARC segments.
        var turn=-cross;

        var half=theta/2;
        var setback=radiusMm/Math.Tan(half);
        if (!double.IsFinite(setback) ||
            setback>=length1-toleranceMm || setback>=length2-toleranceMm)
            throw new FormatException("Fillet radius does not fit within adjacent edges.");
        var inPoint=new PreviewPoint(corner.X+ux*setback,corner.Y+uy*setback);
        var outPoint=new PreviewPoint(corner.X+vx*setback,corner.Y+vy*setback);
        var bisectorLength=Math.Sqrt((ux+vx)*(ux+vx)+(uy+vy)*(uy+vy));
        if (bisectorLength<1e-6)
            throw new FormatException("Fillet bisector is invalid.");
        var centerOffset=radiusMm/Math.Sin(half);
        var center=new PreviewPoint(
            corner.X+(ux+vx)/bisectorLength*centerOffset,
            corner.Y+(uy+vy)/bisectorLength*centerOffset);
        foreach(var point in new[]{inPoint,outPoint,center})
            if(!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
               Math.Abs(point.X)>1_000_000 || Math.Abs(point.Y)>1_000_000)
                throw new FormatException("Fillet exceeds supported coordinate range.");

        var start=Math.Atan2(inPoint.Y-center.Y,inPoint.X-center.X)*180/Math.PI;
        var sweep=Math.Sign(turn)*(180-theta*180/Math.PI);
        if(!double.IsFinite(sweep) || Math.Abs(sweep)<1e-6 || Math.Abs(sweep)>=180)
            throw new FormatException("Fillet ARC sweep is invalid.");
        var arc=new PreviewArc(center,radiusMm,start,sweep);
        // Replace only the two LINE neighbours and insert one analytic ARC.
        // All unrelated original ARC segments are retained without mutation.
        var segments=new List<PreviewSegment>(n+1);
        for(var i=0;i<n;i++)
        {
            if (i==previousIndex)
            {
                if(Distance(incoming.Start,inPoint)<=toleranceMm)
                    throw new FormatException("Fillet creates a degenerate incoming edge.");
                segments.Add(new PreviewLine(incoming.Start,inPoint));
                segments.Add(arc);
            }
            else if(i==vertexIndex)
            {
                if(Distance(outPoint,outgoing.End)<=toleranceMm)
                    throw new FormatException("Fillet creates a degenerate outgoing edge.");
                segments.Add(new PreviewLine(outPoint,outgoing.End));
            }
            else segments.Add(ring.Segments[i]);
        }
        var updated=rings.ToArray();
        updated[ringIndex]=new PreviewPath(segments,true);
        var after=PreviewContourTopology.Analyze(updated,toleranceMm);
        if(!after.IsValid || after.Rings.Count!=before.Rings.Count)
            throw new FormatException("Fillet invalidated topology: "+string.Join("; ",after.Issues));
        for(var i=0;i<before.Rings.Count;i++)
            if(before.Rings[i].ParentIndex!=after.Rings[i].ParentIndex ||
               before.Rings[i].Depth!=after.Rings[i].Depth ||
               before.Rings[i].IsMaterialBoundary!=after.Rings[i].IsMaterialBoundary)
                throw new FormatException("Fillet changed hole or island ownership.");
        var dxf=PreviewDxfExporter.Export(updated,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(updated,dxf,toleranceMm);
        return new(updated,after.NetAreaMm2,true);
    }
}
