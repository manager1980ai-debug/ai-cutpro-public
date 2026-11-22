namespace AICutPro.Core;

/// <summary>
/// Bounded, read-only 2D drafting primitive for moving one vertex of a strictly
/// line-only closed ring. The complete ring set is validated before returning.
/// Circular arcs must never be silently distorted by moving their endpoints.
/// A result cannot authorize CAM or physical equipment control.
/// </summary>
public sealed record PreviewVertexEditResult(
    IReadOnlyList<PreviewPath> Rings,
    double NetAreaMm2,
    bool IsValidForPreview)
{
    public bool CanStartMachine => false;
}

public static class PreviewVertexEditor
{
    /// <param name="ringIndex">Zero-based index in a verified ring set.</param>
    /// <param name="vertexIndex">Zero-based index; segment i starts at vertex i.</param>
    public static PreviewVertexEditResult MoveVertex(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int vertexIndex,
        PreviewPoint position, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if(!double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
           Math.Abs(position.X)>1_000_000 || Math.Abs(position.Y)>1_000_000)
            throw new FormatException("Invalid or unbounded vertex coordinates.");
        if(!double.IsFinite(toleranceMm)||toleranceMm<0.0001||toleranceMm>1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if(rings.Count is < 1 or > 1000 ||
           ringIndex<0 || ringIndex>=rings.Count)
            throw new FormatException("Invalid ring index.");
        var original=PreviewContourTopology.Analyze(rings,toleranceMm);
        if(!original.IsValid)
            throw new FormatException("Original contour topology is not verified.");
        var source=rings[ringIndex];
        if(!source.Closed || source.Segments.Count is < 3 or > 2000 ||
           source.Segments.Any(s=>s is not PreviewLine))
            throw new FormatException("Vertex editing requires a closed line-only ring (no ARC).");
        if(vertexIndex<0 || vertexIndex>=source.Segments.Count)
            throw new FormatException("Invalid vertex index.");
        var vertices=source.Segments.Cast<PreviewLine>().Select(l=>l.Start).ToArray();
        // Never invent a new topology from an already disconnected original.
        for(int i=0;i<vertices.Length;i++)
        {
            var line=(PreviewLine)source.Segments[i];
            var next=vertices[(i+1)%vertices.Length];
            if(Distance(line.End,next)>toleranceMm)
                throw new FormatException("Original ring has mismatched vertex endpoints.");
        }
        vertices[vertexIndex]=position;
        var editedLines=new List<PreviewSegment>(vertices.Length);
        for(int i=0;i<vertices.Length;i++)
        {
            var a=vertices[i];
            var b=vertices[(i+1)%vertices.Length];
            if(Distance(a,b)<=toleranceMm)
                throw new FormatException("Vertex edit creates a degenerate edge.");
            editedLines.Add(new PreviewLine(a,b));
        }
        var updated=rings.ToArray();
        updated[ringIndex]=new PreviewPath(editedLines,true);
        var checkedTopology=PreviewContourTopology.Analyze(updated,toleranceMm);
        if(!checkedTopology.IsValid)
            throw new FormatException("Vertex edit invalidates ring topology: " +
                string.Join("; ",checkedTopology.Issues));
        EnsureNestingUnchanged(original, checkedTopology);
        return new(updated,checkedTopology.NetAreaMm2,true);
    }


    /// <summary>
    /// Removes one vertex of a line-only ring by joining its two adjacent edges.
    /// This changes the shape and is permitted only after complete topology validation.
    /// It never edits arcs, drops an entire ring, or enables machine control.
    /// </summary>
    public static PreviewVertexEditResult DeleteVertex(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int vertexIndex,
        double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (rings.Count is < 1 or > 1000 || ringIndex < 0 || ringIndex >= rings.Count)
            throw new FormatException("Invalid ring index.");
        var original = PreviewContourTopology.Analyze(rings, toleranceMm);
        if (!original.IsValid)
            throw new FormatException("Original contour topology is not verified.");
        var source = rings[ringIndex];
        if (!source.Closed || source.Segments.Count is < 4 or > 2000 ||
            source.Segments.Any(s => s is not PreviewLine))
            throw new FormatException("Deletion requires a closed line-only ring with at least four vertices.");
        if (vertexIndex < 0 || vertexIndex >= source.Segments.Count)
            throw new FormatException("Invalid vertex index.");
        var vertices = source.Segments.Cast<PreviewLine>().Select(s => s.Start).ToList();
        for (int i = 0; i < vertices.Count; i++)
        {
            var segment = (PreviewLine)source.Segments[i];
            if (Distance(segment.End, vertices[(i + 1) % vertices.Count]) > toleranceMm)
                throw new FormatException("Original ring has mismatched vertex endpoints.");
        }
        vertices.RemoveAt(vertexIndex);
        var lines = new List<PreviewSegment>(vertices.Count);
        for (int i = 0; i < vertices.Count; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Count];
            if (Distance(a, b) <= toleranceMm)
                throw new FormatException("Deletion creates a degenerate edge.");
            lines.Add(new PreviewLine(a, b));
        }
        var updated = rings.ToArray();
        updated[ringIndex] = new PreviewPath(lines, true);
        var verified = PreviewContourTopology.Analyze(updated, toleranceMm);
        if (!verified.IsValid)
            throw new FormatException("Vertex deletion invalidates ring topology: " +
                string.Join("; ", verified.Issues));
        EnsureNestingUnchanged(original, verified);
        return new(updated, verified.NetAreaMm2, true);
    }

    /// <summary>
    /// Subdivides a straight edge by adding one interior vertex. The geometry must
    /// remain collinear, so this drafting operation never changes the ring area.
    /// Arcs and near-endpoint insertions are rejected rather than approximated.
    /// </summary>
    public static PreviewVertexEditResult InsertVertex(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int segmentIndex,
        PreviewPoint position, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
            Math.Abs(position.X) > 1_000_000 || Math.Abs(position.Y) > 1_000_000)
            throw new FormatException("Invalid or unbounded vertex coordinates.");
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (rings.Count is < 1 or > 1000 || ringIndex < 0 || ringIndex >= rings.Count)
            throw new FormatException("Invalid ring index.");
        var original = PreviewContourTopology.Analyze(rings, toleranceMm);
        if (!original.IsValid)
            throw new FormatException("Original contour topology is not verified.");
        var source = rings[ringIndex];
        if (!source.Closed || source.Segments.Count is < 3 or >= 2000 ||
            source.Segments.Any(s => s is not PreviewLine))
            throw new FormatException("Insertion requires a closed line-only ring.");
        if (segmentIndex < 0 || segmentIndex >= source.Segments.Count)
            throw new FormatException("Invalid segment index.");
        var segment = (PreviewLine)source.Segments[segmentIndex];
        var dx = segment.End.X - segment.Start.X;
        var dy = segment.End.Y - segment.Start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (!double.IsFinite(lengthSquared) || lengthSquared <= 4 * toleranceMm * toleranceMm)
            throw new FormatException("Edge is too short to subdivide.");
        var parameter = ((position.X - segment.Start.X) * dx +
                         (position.Y - segment.Start.Y) * dy) / lengthSquared;
        if (parameter <= 0 || parameter >= 1 ||
            Distance(segment.Start, position) <= toleranceMm ||
            Distance(position, segment.End) <= toleranceMm)
            throw new FormatException("Vertex must be strictly inside the edge.");
        var projected = new PreviewPoint(segment.Start.X + parameter * dx,
                                         segment.Start.Y + parameter * dy);
        if (Distance(projected, position) > toleranceMm)
            throw new FormatException("Vertex is not on the selected straight edge.");
        // Use the exact projected point to avoid introducing a tiny artificial kink.
        var segments = source.Segments.ToList();
        segments[segmentIndex] = new PreviewLine(segment.Start, projected);
        segments.Insert(segmentIndex + 1, new PreviewLine(projected, segment.End));
        var updated = rings.ToArray();
        updated[ringIndex] = new PreviewPath(segments, true);
        var verified = PreviewContourTopology.Analyze(updated, toleranceMm);
        if (!verified.IsValid ||
            Math.Abs(verified.NetAreaMm2 - original.NetAreaMm2) >
            Math.Max(1e-7, Math.Abs(original.NetAreaMm2) * 1e-10))
            throw new FormatException("Insertion changes verified contour geometry.");
        return new(updated, verified.NetAreaMm2, true);
    }

    // Valid rings are not enough: changing a hole into an independent part,
    // or silently changing a material island into a void, is a drafting error.
    // Preserve the original ring ownership and parity across vertex edits.
    private static void EnsureNestingUnchanged(
        PreviewTopologyResult original, PreviewTopologyResult updated)
    {
        if (!original.IsValid || !updated.IsValid ||
            original.Rings.Count != updated.Rings.Count)
            throw new FormatException("Contour nesting cannot be verified.");
        for (int i = 0; i < original.Rings.Count; i++)
        {
            var before = original.Rings[i];
            var after = updated.Rings[i];
            if (before.Index != after.Index ||
                before.ParentIndex != after.ParentIndex ||
                before.Depth != after.Depth ||
                before.IsMaterialBoundary != after.IsMaterialBoundary)
                throw new FormatException(
                    "Vertex edit would change which contours are holes or material islands.");
        }
    }

    private static double Distance(PreviewPoint a,PreviewPoint b) =>
        Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
}
