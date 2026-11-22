namespace AICutPro.Core;

/// <summary>
/// Edits one explicitly imported DXF CIRCLE representing a nested hole.
/// Fail-closed on unsupported DXF content, ambiguous circle identity,
/// topology changes and touching/crossing boundaries. Preview only.
/// </summary>
public static class PreviewCircleHoleEditor
{
    public static DxfImportResult SetHole(
        DxfImportResult document, int circleIndex,
        PreviewPoint center, double diameterMm, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) ||
            Math.Abs(center.X) > 1_000_000 || Math.Abs(center.Y) > 1_000_000 ||
            !double.IsFinite(diameterMm) || diameterMm <= 2 * toleranceMm ||
            diameterMm > 1_000_000)
            throw new FormatException("Invalid circle position or diameter.");
        var originalRings = PreviewDxfExporter.ValidateDocument(document, toleranceMm);
        var before = PreviewContourTopology.Analyze(originalRings, toleranceMm);
        var circles = document.Shapes.OfType<DxfCircle>().ToArray();
        if (circleIndex < 0 || circleIndex >= circles.Length)
            throw new FormatException("Invalid DXF CIRCLE index.");
        var originalCircle = circles[circleIndex];
        int? ringIndex = null;
        for (int i = 0; i < originalRings.Count; i++)
        {
            if (!CircleRing(originalRings[i], originalCircle, toleranceMm))
                continue;
            if (ringIndex is not null)
                throw new FormatException("Ambiguous source circle ring.");
            ringIndex = i;
        }
        if (ringIndex is null || before.Rings[ringIndex.Value].ParentIndex is null ||
            before.Rings[ringIndex.Value].IsMaterialBoundary)
            throw new FormatException("Selected CIRCLE must be a verified inner hole.");

        var editedShapes = document.Shapes.ToArray();
        var circleNumber = 0;
        var replaced = false;
        for (int i = 0; i < editedShapes.Length; i++)
        {
            if (editedShapes[i] is not DxfCircle circle) continue;
            if (circleNumber++ != circleIndex) continue;
            editedShapes[i] = circle with
            {
                Center = new Point2(center.X, center.Y),
                Radius = diameterMm / 2
            };
            replaced = true;
            break;
        }
        if (!replaced) throw new FormatException("Circle not found.");
        var editedDocument = document with { Shapes = editedShapes };
        var changedRings = PreviewDxfExporter.ValidateDocument(editedDocument, toleranceMm);
        var after = PreviewContourTopology.Analyze(changedRings, toleranceMm);
        if (!after.IsValid || before.Rings.Count != after.Rings.Count)
            throw new FormatException("Circle edit changed contour count or topology.");
        // The assembler processes source entities in deterministic order. If that
        // changes, reject rather than silently reassign hole/part identity.
        for (int i = 0; i < before.Rings.Count; i++)
            if (before.Rings[i].ParentIndex != after.Rings[i].ParentIndex ||
                before.Rings[i].Depth != after.Rings[i].Depth ||
                before.Rings[i].IsMaterialBoundary != after.Rings[i].IsMaterialBoundary)
                throw new FormatException("Circle edit changed hole or island ownership.");
        if (!CircleRing(changedRings[ringIndex.Value],
                (DxfCircle)editedShapes.First(s => s is DxfCircle c &&
                    Math.Abs(c.Center.X-center.X)<1e-9 &&
                    Math.Abs(c.Center.Y-center.Y)<1e-9 &&
                    Math.Abs(c.Radius-diameterMm/2)<1e-9), toleranceMm))
            throw new FormatException("Edited circular geometry cannot be confirmed.");
        var exported = PreviewDxfExporter.Export(changedRings, toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(changedRings, exported, toleranceMm);
        return editedDocument;
    }

    private static bool CircleRing(PreviewPath path, DxfCircle circle, double tolerance)
    {
        if (!path.Closed || path.Segments.Count != 4 ||
            path.Segments.Any(s => s is not PreviewArc)) return false;
        var arcs = path.Segments.Cast<PreviewArc>();
        var coordTol = Math.Max(1e-8, tolerance * 0.0001);
        return arcs.All(a =>
            Math.Abs(a.Center.X-circle.Center.X)<coordTol &&
            Math.Abs(a.Center.Y-circle.Center.Y)<coordTol &&
            Math.Abs(a.RadiusMm-circle.Radius)<coordTol &&
            Math.Abs(Math.Abs(a.SweepDegrees)-90)<1e-8);
    }
}
