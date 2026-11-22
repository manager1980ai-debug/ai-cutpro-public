namespace AICutPro.Core;

/// <summary>
/// Edits one explicitly imported DXF CIRCLE representing a nested hole.
/// Fail-closed on unsupported DXF content, ambiguous circle identity,
/// topology changes and touching/crossing boundaries. Preview only.
/// </summary>
public static class PreviewCircleHoleEditor
{
    /// <summary>
    /// Adds an explicit circular void strictly within an existing material
    /// boundary. The resulting hole must be a new depth-one child of an
    /// existing outer part, never a cut through an island or an old hole.
    /// Geometry is preview only; no CAM or machine authorization.
    /// </summary>
    public static DxfImportResult AddHole(
        DxfImportResult document, PreviewPoint center,
        double diameterMm, double toleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0.0001 || toleranceMm > 1)
            throw new ArgumentOutOfRangeException(nameof(toleranceMm));
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) ||
            Math.Abs(center.X) > 1_000_000 || Math.Abs(center.Y) > 1_000_000 ||
            !double.IsFinite(diameterMm) || diameterMm <= 2 * toleranceMm ||
            diameterMm > 1_000_000)
            throw new FormatException("Invalid hole center or diameter.");
        var sourcePaths = PreviewDxfExporter.ValidateDocument(document, toleranceMm);
        var original = PreviewContourTopology.Analyze(sourcePaths, toleranceMm);
        if (!original.IsValid)
            throw new FormatException("Original contour topology is invalid.");
        if (document.Shapes.Count >= 10000)
            throw new FormatException("DXF entity count limit reached.");
        var newShape = new DxfCircle("HOLE",
            new Point2(center.X, center.Y), diameterMm / 2);
        var updated = document with {
            Shapes = document.Shapes.Concat(new DxfShape[] { newShape }).ToArray()
        };
        var afterPaths = PreviewDxfExporter.ValidateDocument(updated, toleranceMm);
        var after = PreviewContourTopology.Analyze(afterPaths, toleranceMm);
        if (!after.IsValid || after.Rings.Count != original.Rings.Count + 1)
            throw new FormatException("Adding the circle did not create one verified contour.");
        for (int i = 0; i < original.Rings.Count; i++)
        {
            if (original.Rings[i].ParentIndex != after.Rings[i].ParentIndex ||
                original.Rings[i].Depth != after.Rings[i].Depth ||
                original.Rings[i].IsMaterialBoundary != after.Rings[i].IsMaterialBoundary)
                throw new FormatException("Adding a circle changed existing contour ownership.");
        }
        var inserted = after.Rings.Count - 1;
        if (!CircleRing(afterPaths[inserted], newShape, toleranceMm) ||
            after.Rings[inserted].Depth != 1 ||
            after.Rings[inserted].IsMaterialBoundary ||
            after.Rings[inserted].ParentIndex is not int parent ||
            !after.Rings[parent].IsMaterialBoundary ||
            after.Rings[parent].Depth != 0)
            throw new FormatException("New circle must be a hole strictly inside an outer part.");
        var expectedArea = original.NetAreaMm2 - Math.PI * newShape.Radius * newShape.Radius;
        if (Math.Abs(after.NetAreaMm2 - expectedArea) >
            Math.Max(1e-6, Math.Abs(expectedArea) * 1e-9))
            throw new FormatException("Unexpected material area after adding hole.");
        var serialized = PreviewDxfExporter.Export(afterPaths, toleranceMm);
        PreviewDxfRoundTripVerifier.Verify(afterPaths, serialized, toleranceMm);
        return updated;
    }

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
                originalCircle with { Center = new Point2(center.X, center.Y),
                                      Radius = diameterMm / 2 }, toleranceMm))
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
