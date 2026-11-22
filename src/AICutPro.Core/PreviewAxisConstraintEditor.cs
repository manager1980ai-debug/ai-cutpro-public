namespace AICutPro.Core;

/// <summary>
/// Apply a one-shot horizontal/vertical geometric constraint to a LINE edge.
/// The start vertex and edge length stay fixed; only its end vertex moves.
/// This is not a persistent parametric constraint solver and never enables CAM.
/// </summary>
public static class PreviewAxisConstraintEditor
{
    public static PreviewVertexEditResult AlignEdge(
        IReadOnlyList<PreviewPath> rings, int ringIndex, int segmentIndex,
        bool horizontal, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 ||
            toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (rings.Count is < 1 or > 1000 ||
            ringIndex < 0 || ringIndex >= rings.Count)
            throw new FormatException("Invalid constraint ring index.");
        var ring = rings[ringIndex];
        if (!ring.Closed || ring.Segments.Count is < 3 or > 2000 ||
            ring.Segments.Any(s => s is not PreviewLine) ||
            segmentIndex < 0 || segmentIndex >= ring.Segments.Count)
            throw new FormatException("Axis alignment requires a closed LINE-only contour and valid edge.");
        var edge=(PreviewLine)ring.Segments[segmentIndex];
        var dx=edge.End.X-edge.Start.X;
        var dy=edge.End.Y-edge.Start.Y;
        var length=Math.Sqrt(dx*dx+dy*dy);
        if (!double.IsFinite(length) || length<=toleranceMm)
            throw new FormatException("Cannot constrain a degenerate edge.");

        // Closest orientation along the requested axis. A zero component is
        // deterministically positive so the chosen direction is reproducible.
        double angle=horizontal?(dx>=0?0:180):(dy>=0?90:-90);
        var edited=PreviewVertexEditor.SetEdgeAngle(
            rings,ringIndex,segmentIndex,angle,toleranceMm);
        var actual=(PreviewLine)edited.Rings[ringIndex].Segments[segmentIndex];
        if(horizontal
            ? Math.Abs(actual.End.Y-actual.Start.Y)>1e-7
            : Math.Abs(actual.End.X-actual.Start.X)>1e-7)
            throw new FormatException("Axis alignment was not exact.");
        var actualLength=Math.Sqrt(
            Math.Pow(actual.End.X-actual.Start.X,2)+
            Math.Pow(actual.End.Y-actual.Start.Y,2));
        if(Math.Abs(actualLength-length)>Math.Max(1e-7,length*1e-10))
            throw new FormatException("Axis alignment changed selected edge length.");

        var serialized=PreviewDxfExporter.Export(edited.Rings,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(edited.Rings,serialized,toleranceMm);
        return edited;
    }
}
