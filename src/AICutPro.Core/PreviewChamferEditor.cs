namespace AICutPro.Core;

/// <summary>
/// Drafting-only symmetric chamfer: replaces a vertex of one closed LINE-only
/// contour with a straight segment between points on the adjacent edges.
/// No kerf compensation, toolpath, CAM or machine authorization.
/// </summary>
public static class PreviewChamferEditor
{
    public static PreviewVertexEditResult ChamferVertex(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int vertexIndex,
        double setbackMm, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(toleranceMm) ||
            toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!double.IsFinite(setbackMm) || setbackMm <= toleranceMm ||
            setbackMm > 1_000_000)
            throw new FormatException("Chamfer setback must be finite and greater than tolerance.");
        if (rings.Count is < 1 or > 1000 ||
            ringIndex < 0 || ringIndex >= rings.Count)
            throw new FormatException("Invalid chamfer ring index.");
        var original = PreviewContourTopology.Analyze(rings, toleranceMm);
        if (!original.IsValid)
            throw new FormatException("Source contour topology is invalid.");
        var ring = rings[ringIndex];
        if (!ring.Closed || ring.Segments.Count is < 3 or > 1999 ||
            ring.Segments.Any(x => x is not PreviewLine))
            throw new FormatException("Chamfer requires a bounded closed LINE-only contour.");
        if (vertexIndex < 0 || vertexIndex >= ring.Segments.Count)
            throw new FormatException("Invalid chamfer vertex index.");
        var lines = ring.Segments.Cast<PreviewLine>().ToArray();
        var vertices = lines.Select(x => x.Start).ToArray();
        var count = vertices.Length;
        for (var i = 0; i < count; i++)
            if (Distance(lines[i].End, vertices[(i+1)%count]) > toleranceMm)
                throw new FormatException("Original LINE endpoints are disconnected.");

        var corner = vertices[vertexIndex];
        var prev = vertices[(vertexIndex+count-1)%count];
        var next = vertices[(vertexIndex+1)%count];
        var previousLength = Distance(prev, corner);
        var nextLength = Distance(next, corner);
        if (!double.IsFinite(previousLength) || !double.IsFinite(nextLength) ||
            setbackMm >= previousLength-toleranceMm ||
            setbackMm >= nextLength-toleranceMm)
            throw new FormatException("Chamfer setback must fit strictly inside both adjacent edges.");
        var ax = (prev.X-corner.X)/previousLength;
        var ay = (prev.Y-corner.Y)/previousLength;
        var bx = (next.X-corner.X)/nextLength;
        var by = (next.Y-corner.Y)/nextLength;
        var sine = ax*by-ay*bx;
        if (Math.Abs(sine) < 1e-6)
            throw new FormatException("Chamfer corner must have a non-collinear angle.");

        var left = new PreviewPoint(corner.X+ax*setbackMm,
                                    corner.Y+ay*setbackMm);
        var right = new PreviewPoint(corner.X+bx*setbackMm,
                                     corner.Y+by*setbackMm);
        foreach (var point in new[]{left,right})
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                Math.Abs(point.X)>1_000_000 || Math.Abs(point.Y)>1_000_000)
                throw new FormatException("Chamfer is outside supported CAD range.");
        if (Distance(left,right)<=toleranceMm)
            throw new FormatException("Chamfer edge is degenerate.");
        var changedVertices = new List<PreviewPoint>(count+1);
        for (var i = 0; i < count; i++)
        {
            if (i==vertexIndex)
            {
                changedVertices.Add(left);
                changedVertices.Add(right);
            }
            else changedVertices.Add(vertices[i]);
        }
        var segments = new List<PreviewSegment>(changedVertices.Count);
        for (var i = 0; i < changedVertices.Count; i++)
        {
            var p = changedVertices[i];
            var q = changedVertices[(i+1)%changedVertices.Count];
            if (Distance(p,q)<=toleranceMm)
                throw new FormatException("Chamfer creates degenerate edge.");
            segments.Add(new PreviewLine(p,q));
        }
        var edited = rings.ToArray();
        edited[ringIndex]=new PreviewPath(segments,true);
        var after = PreviewContourTopology.Analyze(edited,toleranceMm);
        if (!after.IsValid || after.Rings.Count!=original.Rings.Count)
            throw new FormatException("Chamfer invalidates topology: "+string.Join("; ",after.Issues));
        for (var i=0; i<original.Rings.Count; i++)
            if (original.Rings[i].ParentIndex!=after.Rings[i].ParentIndex ||
                original.Rings[i].Depth!=after.Rings[i].Depth ||
                original.Rings[i].IsMaterialBoundary!=after.Rings[i].IsMaterialBoundary)
                throw new FormatException("Chamfer changes hole or island ownership.");
        var dxf=PreviewDxfExporter.Export(edited,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(edited,dxf,toleranceMm);
        return new(edited,after.NetAreaMm2,true);
    }

    private static double Distance(PreviewPoint a,PreviewPoint b) =>
        Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
}
