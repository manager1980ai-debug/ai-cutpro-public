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
        SheetUShapeAssignmentRegister? groups = null,
        SheetChainAssignmentRegister? chains = null)
    {
        ArgumentNullException.ThrowIfNull(importedLines);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(assignments);
        if (importedLines.Count > 100_000)
            throw new ArgumentOutOfRangeException(nameof(importedLines));
        // Snapshot and inspect group geometry without mutating registries.
        var chainMembers = new Dictionary<int, SheetChainAnnotation>();
        var invalidAnchors = new HashSet<int>();
        if (chains is not null)
            foreach (var annotation in chains.Snapshot().Values)
            {
                var preview = SheetChainAssignmentRegister.Validate(
                    annotation, importedLines, sheet);
                var path = DxfOpenLineChainBuilder.Build(
                    importedLines, annotation.SelectedLineIndex);
                if (!preview.IsValid || !path.IsValid)
                {
                    invalidAnchors.Add(annotation.SelectedLineIndex);
                    continue;
                }
                foreach (var index in path.SourceIndices)
                {
                    if (!chainMembers.TryAdd(index, annotation))
                        invalidAnchors.Add(index);
                }
            }
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
            if (invalidAnchors.Contains(i))
            {
                findings.Add(new(i, line.Layer, OpenLineIntentStatus.InvalidAssignment,
                    null, null, "GEO-3099",
                    "A saved chain is invalid, ambiguous or overlaps another chain."));
                continue;
            }
            var hasChain = chainMembers.TryGetValue(i, out var chainAnnotation);
            if (hasChain && (groupAssigned || assignments.TryGet(i, out _)))
            {
                findings.Add(new(i, line.Layer, OpenLineIntentStatus.InvalidAssignment,
                    null, null, "GEO-3099",
                    "More than one technological operation covers this imported line."));
                continue;
            }
            if (hasChain && chainAnnotation is not null)
            {
                findings.Add(new(i, line.Layer, OpenLineIntentStatus.AssignedPreview,
                    chainAnnotation.Kind, chainAnnotation.Processing, "",
                    "An explicit open-chain preview covers this DXF LINE; CAM remains blocked."));
                continue;
            }
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
