namespace AICutPro.Core;

/// <summary>
/// Exact diameter edit for an analytic circle represented as four quarter
/// PreviewArc segments, including a circle reconstructed from DXF ARC entities.
/// Unrelated contours are left unchanged. No general arc editing or CAM.
/// </summary>
public static class PreviewArcCircleEditor
{
    public static DxfImportResult SetDiameter(
        DxfImportResult document, int ringIndex,
        double diameterMm, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(toleranceMm) ||
            toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!double.IsFinite(diameterMm) ||
            diameterMm <= 2*toleranceMm || diameterMm > 1_000_000)
            throw new FormatException("Invalid target circle diameter.");
        var source = PreviewDxfExporter.ValidateDocument(document,toleranceMm);
        var before = PreviewContourTopology.Analyze(source,toleranceMm);
        if (!before.IsValid || ringIndex < 0 || ringIndex >= source.Count)
            throw new FormatException("Invalid source ring.");
        var ring = source[ringIndex];
        if (!ring.Closed || ring.Segments.Count != 4 ||
            ring.Segments.Any(s => s is not PreviewArc))
            throw new FormatException("Only exact four-arc circles can be dimensioned.");

        var arcs = ring.Segments.Cast<PreviewArc>().ToArray();
        var first = arcs[0];
        var epsilon = Math.Max(1e-8,toleranceMm*1e-4);
        if (!double.IsFinite(first.RadiusMm) || first.RadiusMm<=epsilon ||
            Math.Abs(Math.Abs(arcs.Sum(a=>a.SweepDegrees))-360)>1e-8 ||
            arcs.Any(a =>
                Math.Abs(a.Center.X-first.Center.X)>epsilon ||
                Math.Abs(a.Center.Y-first.Center.Y)>epsilon ||
                Math.Abs(a.RadiusMm-first.RadiusMm)>epsilon ||
                Math.Abs(Math.Abs(a.SweepDegrees)-90)>1e-8 ||
                Math.Sign(a.SweepDegrees)!=Math.Sign(first.SweepDegrees)))
            throw new FormatException("ARC segments do not form an exact circle.");

        var newRadius = diameterMm/2;
        var altered = source.ToArray();
        altered[ringIndex] = new PreviewPath(
            arcs.Select(a => (PreviewSegment)(a with {RadiusMm = newRadius})).ToArray(),true);
        var after = PreviewContourTopology.Analyze(altered,toleranceMm);
        if (!after.IsValid || before.Rings.Count!=after.Rings.Count)
            throw new FormatException("Circle radius edit invalidates topology.");
        for(int i=0;i<before.Rings.Count;i++)
            if(before.Rings[i].ParentIndex!=after.Rings[i].ParentIndex ||
               before.Rings[i].Depth!=after.Rings[i].Depth ||
               before.Rings[i].IsMaterialBoundary!=after.Rings[i].IsMaterialBoundary)
                throw new FormatException("Circle radius edit changes hole or island ownership.");
        var sign = before.Rings[ringIndex].IsMaterialBoundary ? 1 : -1;
        var expectedArea = before.NetAreaMm2 +
            sign*Math.PI*(newRadius*newRadius-first.RadiusMm*first.RadiusMm);
        if (Math.Abs(after.NetAreaMm2-expectedArea)>
            Math.Max(1e-6,Math.Abs(expectedArea)*1e-9))
            throw new FormatException("Circle radius edit produced incorrect net material area.");

        var serialized = PreviewDxfExporter.Export(altered,toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(altered,serialized,toleranceMm);
        // Preserve the checked set as an actual DXF; user can edit/undo/export.
        return DxfReader.Parse(serialized);
    }
}
