namespace AICutPro.Core;

/// <summary>
/// Geometry-only check for a restricted DXF export/import round trip.
/// Net area alone is not an adequate integrity check: distinct drawings can
/// have identical net material area. This compares each individual boundary,
/// its analytic length, bounds and containment relationship.
/// This verifier does NOT authorize manufacturing or motion.
/// </summary>
public static class PreviewDxfRoundTripVerifier
{
    public static IReadOnlyList<PreviewPath> Verify(
        IReadOnlyList<PreviewPath> original,
        string exportedDxf,
        double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(exportedDxf);
        if (!double.IsFinite(toleranceMm) ||
            toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        var before = PreviewContourTopology.Analyze(original, toleranceMm);
        if (!before.IsValid)
            throw new FormatException("Source contours are not verified.");

        var decoded = DxfReader.Parse(exportedDxf);
        var reopened = PreviewDxfExporter.ValidateDocument(decoded, toleranceMm);
        var after = PreviewContourTopology.Analyze(reopened, toleranceMm);
        if (!after.IsValid || original.Count != reopened.Count)
            throw new FormatException("DXF round trip changed contour count or topology.");
        if (!Near(before.NetAreaMm2, after.NetAreaMm2, toleranceMm * toleranceMm))
            throw new FormatException("DXF round trip changed net material area.");

        // Ring order is not a DXF invariant: the assembler can reorder rings.
        // Match one-to-one using precise geometry fingerprints, not indices.
        var mapping = new int[original.Count];
        var assigned = new bool[reopened.Count];
        for (int i = 0; i < original.Count; i++)
        {
            var source = original[i];
            var sourceMetrics = PreviewContourMetrics.Measure(source, toleranceMm);
            var sourceBounds = PreviewContourBounds.Measure(source);
            if (!sourceMetrics.IsValid || !sourceBounds.IsValid)
                throw new FormatException("Source ring metrics could not be verified.");
            var matches = new List<int>();
            for (int j = 0; j < reopened.Count; j++)
            {
                if (assigned[j]) continue;
                var candidate = reopened[j];
                if (source.Segments.Count != candidate.Segments.Count ||
                    source.Segments.Count(s => s is PreviewLine) !=
                      candidate.Segments.Count(s => s is PreviewLine) ||
                    source.Segments.Count(s => s is PreviewArc) !=
                      candidate.Segments.Count(s => s is PreviewArc))
                    continue;
                var metrics = PreviewContourMetrics.Measure(candidate, toleranceMm);
                var bounds = PreviewContourBounds.Measure(candidate);
                if (!metrics.IsValid || !bounds.IsValid) continue;
                if (Near(sourceMetrics.AreaMm2, metrics.AreaMm2, toleranceMm * toleranceMm) &&
                    Near(sourceMetrics.PerimeterMm, metrics.PerimeterMm, toleranceMm) &&
                    Near(sourceBounds.MinX, bounds.MinX, toleranceMm) &&
                    Near(sourceBounds.MinY, bounds.MinY, toleranceMm) &&
                    Near(sourceBounds.MaxX, bounds.MaxX, toleranceMm) &&
                    Near(sourceBounds.MaxY, bounds.MaxY, toleranceMm) &&
                    SameBoundary(source, candidate, toleranceMm) &&
                    before.Rings[i].Depth == after.Rings[j].Depth &&
                    before.Rings[i].IsMaterialBoundary == after.Rings[j].IsMaterialBoundary)
                    matches.Add(j);
            }
            if (matches.Count != 1)
                throw new FormatException("DXF round trip has missing or ambiguous ring geometry.");
            var match = matches[0];
            mapping[i] = match;
            assigned[match] = true;
        }
        for (int i = 0; i < original.Count; i++)
        {
            var sourceParent = before.Rings[i].ParentIndex;
            var expectedParent = sourceParent is null ? null : (int?)mapping[sourceParent.Value];
            if (after.Rings[mapping[i]].ParentIndex != expectedParent)
                throw new FormatException("DXF round trip changed hole or island ownership.");
        }
        return reopened;
    }

    // The analytic boundary must be identical, not merely equal in area,
    // perimeter and bounding box. DXF may reorder/reverse the same segments.
    private static bool SameBoundary(
        PreviewPath source, PreviewPath candidate, double toleranceMm)
    {
        if (source.Segments.Count != candidate.Segments.Count) return false;
        var matched = new bool[candidate.Segments.Count];
        foreach (var sourceSegment in source.Segments)
        {
            var match = -1;
            for (int j = 0; j < candidate.Segments.Count; j++)
                if (!matched[j] && SameSegment(sourceSegment,
                        candidate.Segments[j], toleranceMm))
                {
                    match = j;
                    break;
                }
            if (match < 0) return false;
            matched[match] = true;
        }
        return true;
    }

    private static bool SameSegment(
        PreviewSegment a, PreviewSegment b, double toleranceMm)
    {
        var coordinateTolerance = Math.Max(1e-8, toleranceMm * 1e-4);
        bool PointNear(PreviewPoint p, PreviewPoint q) =>
            Near(p.X,q.X,coordinateTolerance) &&
            Near(p.Y,q.Y,coordinateTolerance);
        if (a is PreviewLine left && b is PreviewLine right)
            return (PointNear(left.Start,right.Start) &&
                    PointNear(left.End,right.End)) ||
                   (PointNear(left.Start,right.End) &&
                    PointNear(left.End,right.Start));
        if (a is PreviewArc first && b is PreviewArc second)
        {
            if (!PointNear(first.Center,second.Center) ||
                !Near(first.RadiusMm,second.RadiusMm,coordinateTolerance) ||
                !Near(Math.Abs(first.SweepDegrees),
                      Math.Abs(second.SweepDegrees),1e-8))
                return false;
            static PreviewPoint Point(PreviewArc arc, double angle)
            {
                var r=angle*Math.PI/180;
                return new(arc.Center.X+arc.RadiusMm*Math.Cos(r),
                           arc.Center.Y+arc.RadiusMm*Math.Sin(r));
            }
            var firstStart=Point(first,first.StartDegrees);
            var firstEnd=Point(first,first.StartDegrees+first.SweepDegrees);
            var secondStart=Point(second,second.StartDegrees);
            var secondEnd=Point(second,second.StartDegrees+second.SweepDegrees);
            return (Math.Sign(first.SweepDegrees)==Math.Sign(second.SweepDegrees) &&
                    PointNear(firstStart,secondStart) && PointNear(firstEnd,secondEnd)) ||
                   (Math.Sign(first.SweepDegrees)!=Math.Sign(second.SweepDegrees) &&
                    PointNear(firstStart,secondEnd) && PointNear(firstEnd,secondStart));
        }
        return false;
    }

    private static bool Near(double left, double right, double absoluteTolerance)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right)) return false;
        return Math.Abs(left - right) <= Math.Max(absoluteTolerance,
                   Math.Max(Math.Abs(left), Math.Abs(right)) * 1e-10);
    }
}
