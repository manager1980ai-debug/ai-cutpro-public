namespace AICutPro.Core;

/// <summary>
/// An explicit technological meaning assigned to CAD geometry. DXF layers,
/// line styles and open endpoints alone cannot prove user intent.
/// This model is display/inspection only; it cannot produce machine commands.
/// </summary>
public enum SheetOperationKind
{
    Unassigned,
    OuterContour,
    InnerCutout,
    OpenThroughCut,
    PerforatedCut,
    BendReference,
    Marking
}

/// <summary>
/// Parameters in millimetres. Start/EndInset trim the intended open operation
/// (not the underlying drawing). Cut/Skip apply only to PerforatedCut.
/// </summary>
/// <summary>
/// Physical preview treatment, independent from why a line exists in CAD.
/// AutoLegacy preserves existing saved-less preview callers.
/// </summary>
public enum SheetLineProcessingMode
{
    AutoLegacy,
    NoCut,
    SurfaceScore,
    ThroughCut,
    PerforatedThroughCut
}

public sealed record SheetLineOperation(
    SheetOperationKind Kind,
    Point2 Start,
    Point2 End,
    double StartInsetMm = 0,
    double EndInsetMm = 0,
    double CutLengthMm = 0,
    double SkipLengthMm = 0,
    SheetLineProcessingMode Processing = SheetLineProcessingMode.AutoLegacy);

public sealed record SheetLinePreviewSegment(Point2 Start, Point2 End, bool IsCut);

public sealed record SheetOperationPreview(
    bool IsValid,
    SheetOperationKind Kind,
    IReadOnlyList<SheetLinePreviewSegment> Segments,
    IReadOnlyList<string> Issues)
{
    // Architectural safety gate: no validated CAM path, motion or G-code.
    public bool CanGenerateMachineCommands => false;
}

/// <summary>
/// Read-only line preview similar in concept to cut/skip operations in laser
/// software. Does not interpret dash-style DXF rendering as a cutting command.
/// </summary>
public static class SheetOperationPlanner
{
    public static SheetOperationPreview InspectOpenLine(
        SheetLineOperation operation, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(sheet);
        var errors = new List<string>();
        if (!Enum.IsDefined(operation.Processing))
            errors.Add("Unknown line processing mode.");
        if (operation.Kind is not (SheetOperationKind.OpenThroughCut
            or SheetOperationKind.PerforatedCut or SheetOperationKind.BendReference
            or SheetOperationKind.Marking))
            errors.Add("Assign an explicit open-line operation before preview.");
        if (!double.IsFinite(sheet.WidthMm) || !double.IsFinite(sheet.HeightMm)
            || sheet.WidthMm <= 0 || sheet.HeightMm <= 0)
            errors.Add("Sheet dimensions are invalid.");
        bool Valid(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y)
            && p.X >= 0 && p.Y >= 0
            && p.X <= sheet.WidthMm && p.Y <= sheet.HeightMm;
        if (!Valid(operation.Start) || !Valid(operation.End))
            errors.Add("Line endpoints must lie inside confirmed sheet bounds.");
        if (!double.IsFinite(operation.StartInsetMm) || operation.StartInsetMm < 0
            || !double.IsFinite(operation.EndInsetMm) || operation.EndInsetMm < 0)
            errors.Add("Line end insets must be finite and nonnegative.");
        var processing = operation.Processing == SheetLineProcessingMode.AutoLegacy
            ? operation.Kind switch
            {
                SheetOperationKind.PerforatedCut => SheetLineProcessingMode.PerforatedThroughCut,
                SheetOperationKind.OpenThroughCut => SheetLineProcessingMode.ThroughCut,
                _ => SheetLineProcessingMode.NoCut
            }
            : operation.Processing;
        if (processing == SheetLineProcessingMode.PerforatedThroughCut)
        {
            if (!double.IsFinite(operation.CutLengthMm) || operation.CutLengthMm <= 0
                || !double.IsFinite(operation.SkipLengthMm) || operation.SkipLengthMm <= 0)
                errors.Add("Perforation requires positive finite cut and skip lengths.");
        }
        else if (operation.CutLengthMm != 0 || operation.SkipLengthMm != 0)
            errors.Add("Cut/skip parameters require a perforation operation.");
        var dx = operation.End.X - operation.Start.X;
        var dy = operation.End.Y - operation.Start.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(distance) || distance <= 1e-9 || distance > 1_000_000)
            errors.Add("Open line length is degenerate or exceeds inspection limit.");
        if (double.IsFinite(distance) && double.IsFinite(operation.StartInsetMm)
            && double.IsFinite(operation.EndInsetMm)
            && operation.StartInsetMm + operation.EndInsetMm >= distance)
            errors.Add("Combined end insets remove the complete open line.");
        if (errors.Count > 0)
            return new(false, operation.Kind, [], errors);

        var unitX = dx / distance;
        var unitY = dy / distance;
        Point2 At(double t) => new(
            operation.Start.X + unitX * t,
            operation.Start.Y + unitY * t);
        var end = distance - operation.EndInsetMm;
        var cursor = operation.StartInsetMm;
        var segments = new List<SheetLinePreviewSegment>();
        if (processing != SheetLineProcessingMode.PerforatedThroughCut)
        {
            // A bend intent may have an explicitly selected through-cut treatment.
            // This is visual intent only, not engineering approval or torch control.
            var cutting = processing == SheetLineProcessingMode.ThroughCut;
            segments.Add(new(At(cursor), At(end), cutting));
        }
        else
        {
            // Bounded before creating anything. A very fine dotted pattern can
            // otherwise allocate millions of preview fragments.
            var period = operation.CutLengthMm + operation.SkipLengthMm;
            if (!double.IsFinite(period) || period <= 0
                || (end - cursor) / period > 5000)
                return new(false, operation.Kind, [],
                    ["Perforation needs too many segments for safe preview."]);
            var cut = true;
            while (cursor < end - 1e-9)
            {
                if (segments.Count >= 10_000)
                    return new(false, operation.Kind, [],
                        ["Perforation preview segment limit exceeded."]);
                var length = cut ? operation.CutLengthMm : operation.SkipLengthMm;
                var next = Math.Min(end, cursor + length);
                if (next <= cursor)
                    return new(false, operation.Kind, [],
                        ["Perforation progress is too small for stable preview."]);
                segments.Add(new(At(cursor), At(next), cut));
                cursor = next;
                cut = !cut;
            }
        }
        return new(true, operation.Kind, segments, []);
    }
}
