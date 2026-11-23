namespace AICutPro.Core;

/// <summary>
/// A non-authorising inspection of each imported DXF LINE's explicitly
/// selected technology. It never classifies an open segment as defective
/// solely because it is not closed, and it never overrides contour preflight.
/// </summary>
public enum OpenLineIntentStatus
{
    NeedsReview,
    AssignedPreview,
    InvalidAssignment
}

public sealed record OpenLineIntentFinding(
    int LineIndex,
    string Layer,
    OpenLineIntentStatus Status,
    SheetOperationKind? Kind,
    SheetLineProcessingMode? Processing,
    string DiagnosticCode,
    string Explanation)
{
    public bool CanGenerateMachineCommands => false;
}

public static class OpenGeometryIntentReview
{
    public static IReadOnlyList<OpenLineIntentFinding> Inspect(
        IReadOnlyList<DxfLine> importedLines,
        Sheet sheet,
        SheetLineAssignmentRegister assignments,
        SheetUShapeAssignmentRegister? groups = null)
    {
        ArgumentNullException.ThrowIfNull(importedLines);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(assignments);
        if (importedLines.Count > 100_000)
            throw new ArgumentOutOfRangeException(nameof(importedLines));
        var findings = new List<OpenLineIntentFinding>(importedLines.Count);
        for (var i = 0; i < importedLines.Count; i++)
        {
            var line = importedLines[i];
            if (line is null)
            {
                findings.Add(new(i, "", OpenLineIntentStatus.InvalidAssignment,
                    null, null, "GEO-3099", "DXF LINE is missing."));
                continue;
            }
            var groupAssigned = groups is not null &&
                groups.TryGet(i, out var group) && group is not null &&
                SheetUShapedPreset.Inspect(
                    sheet, Math.Min(line.Start.X, line.End.X),
                    line.Start.Y, Math.Abs(line.End.X - line.Start.X),
                    group.HeightMm, group.BaseProcessing,
                    group.CutLengthMm, group.SkipLengthMm).IsValid &&
                line.Start.Y == line.End.Y;
            if (!assignments.TryGet(i, out var operation) || operation is null)
            {
                findings.Add(new(i, line.Layer,
                    groupAssigned ? OpenLineIntentStatus.AssignedPreview :
                        OpenLineIntentStatus.NeedsReview,
                    groupAssigned ? SheetOperationKind.BendReference : null,
                    groupAssigned ? groups!.Snapshot()[i].BaseProcessing : null,
                    groupAssigned ? "" : "GEO-3001",
                    groupAssigned
                        ? "U-shaped operation preview is explicitly assigned to this base line."
                        : "Open DXF LINE has no explicit technology; it may be a cut, bend, marking, or part boundary. Do not auto-close."));
                continue;
            }
            var preview = SheetOperationPlanner.InspectOpenLine(operation, sheet);
            var matches = operation.Start == line.Start && operation.End == line.End;
            var valid = matches && preview.IsValid;
            findings.Add(new(i, line.Layer,
                valid ? OpenLineIntentStatus.AssignedPreview :
                    OpenLineIntentStatus.InvalidAssignment,
                operation.Kind, operation.Processing,
                valid ? "" : "GEO-3099",
                valid
                    ? "An explicit preview technology exists. Contour and CAM checks remain independent."
                    : "Saved technology does not match current DXF LINE or sheet."));
        }
        return findings;
    }
}
