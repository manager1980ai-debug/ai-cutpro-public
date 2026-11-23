namespace AICutPro.Core;

/// <summary>
/// Preview of a U-shaped tab: three independent through-cut sides and
/// a separate base line with an explicitly selected treatment.
/// No bend strength, kerf, toolpath or machine authorisation is inferred.
/// </summary>
public sealed record SheetUShapedPreview(
    bool IsValid,
    IReadOnlyList<SheetLineOperation> Operations,
    IReadOnlyList<SheetLinePreviewSegment> Segments,
    IReadOnlyList<string> Issues)
{
    public bool CanGenerateMachineCommands => false;
}

public static class SheetUShapedPreset
{
    /// <summary>
    /// Axis-aligned rectangle between lower-left (x,y) and upper-right
    /// (x+width,y+height). Bottom edge is the potential bend line.
    /// The other three edges form the open U cut.
    /// </summary>
    public static SheetUShapedPreview Inspect(
        Sheet sheet, double x, double y, double width, double height,
        SheetLineProcessingMode baseProcessing = SheetLineProcessingMode.NoCut,
        double cutLengthMm = 3, double skipLengthMm = 2)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var errors = new List<string>();
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0)
            errors.Add("U-shaped geometry requires finite positive dimensions.");
        if (!double.IsFinite(sheet.WidthMm) || !double.IsFinite(sheet.HeightMm)
            || sheet.WidthMm <= 0 || sheet.HeightMm <= 0
            || x < 0 || y < 0 || x > sheet.WidthMm - width
            || y > sheet.HeightMm - height)
            errors.Add("U-shaped geometry must lie inside sheet bounds.");
        if (baseProcessing is not (SheetLineProcessingMode.NoCut
            or SheetLineProcessingMode.SurfaceScore
            or SheetLineProcessingMode.ThroughCut
            or SheetLineProcessingMode.PerforatedThroughCut))
            errors.Add("Base treatment must be explicitly selected.");
        if (errors.Count > 0)
            return new(false, [], [], errors);

        var bottomLeft = new Point2(x, y);
        var topLeft = new Point2(x, y + height);
        var topRight = new Point2(x + width, y + height);
        var bottomRight = new Point2(x + width, y);
        var basePerforated = baseProcessing == SheetLineProcessingMode.PerforatedThroughCut;
        var operations = new SheetLineOperation[]
        {
            new(SheetOperationKind.OpenThroughCut, bottomLeft, topLeft,
                Processing: SheetLineProcessingMode.ThroughCut),
            new(SheetOperationKind.OpenThroughCut, topLeft, topRight,
                Processing: SheetLineProcessingMode.ThroughCut),
            new(SheetOperationKind.OpenThroughCut, topRight, bottomRight,
                Processing: SheetLineProcessingMode.ThroughCut),
            new(SheetOperationKind.BendReference, bottomRight, bottomLeft,
                CutLengthMm: basePerforated ? cutLengthMm : 0,
                SkipLengthMm: basePerforated ? skipLengthMm : 0,
                Processing: baseProcessing)
        };
        var segments = new List<SheetLinePreviewSegment>();
        foreach (var operation in operations)
        {
            var preview = SheetOperationPlanner.InspectOpenLine(operation, sheet);
            if (!preview.IsValid)
            {
                errors.AddRange(preview.Issues);
                return new(false, [], [], errors);
            }
            segments.AddRange(preview.Segments);
        }
        return new(true, operations, segments, []);
    }
}
