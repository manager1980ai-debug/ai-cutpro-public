namespace AICutPro.Core;

/// <summary>
/// Safe parametric presets for already imported open DXF lines.
/// A preset is a drafting suggestion, not a material or machine profile.
/// It does not mutate the DXF and cannot authorize CAM or machine motion.
/// </summary>
public enum SheetLinePreset
{
    StraightThroughCut,
    InsetThroughCut,
    AlternatingCutSkip,
    BendReferenceOnly,
    BendThroughCut,
    BendAlternatingCutSkip
}

public static class SheetLinePresetLibrary
{
    public static SheetLineOperation Create(
        SheetLinePreset preset, Point2 start, Point2 end, Sheet sheet,
        double startInsetMm = 5, double endInsetMm = 5,
        double cutLengthMm = 3, double skipLengthMm = 2)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (!Enum.IsDefined(preset))
            throw new ArgumentOutOfRangeException(nameof(preset));
        var kind = preset switch
        {
            SheetLinePreset.BendReferenceOnly or SheetLinePreset.BendThroughCut
                or SheetLinePreset.BendAlternatingCutSkip => SheetOperationKind.BendReference,
            SheetLinePreset.AlternatingCutSkip => SheetOperationKind.PerforatedCut,
            _ => SheetOperationKind.OpenThroughCut
        };
        var processing = preset switch
        {
            SheetLinePreset.BendReferenceOnly => SheetLineProcessingMode.NoCut,
            SheetLinePreset.AlternatingCutSkip or SheetLinePreset.BendAlternatingCutSkip
                => SheetLineProcessingMode.PerforatedThroughCut,
            _ => SheetLineProcessingMode.ThroughCut
        };
        var inset = preset == SheetLinePreset.InsetThroughCut;
        var operation = new SheetLineOperation(
            kind, start, end,
            inset ? startInsetMm : 0, inset ? endInsetMm : 0,
            processing == SheetLineProcessingMode.PerforatedThroughCut ? cutLengthMm : 0,
            processing == SheetLineProcessingMode.PerforatedThroughCut ? skipLengthMm : 0,
            processing);
        var preview = SheetOperationPlanner.InspectOpenLine(operation, sheet);
        if (!preview.IsValid)
            throw new FormatException(
                "Line preset rejected: " + string.Join("; ", preview.Issues));
        return operation;
    }
}
