namespace AICutPro.Core;

/// <summary>
/// A document-scoped, preview-only registry of explicitly assigned DXF LINE
/// operations. It does not authorize machine motion. A validated snapshot
/// may be exported and restored against the same imported line geometry.
/// </summary>
public sealed class SheetLineAssignmentRegister
{
    private readonly Dictionary<int, SheetLineOperation> _assignments = new();
    public int Count => _assignments.Count;
    public IReadOnlyDictionary<int, SheetLineOperation> Snapshot()
        => new Dictionary<int, SheetLineOperation>(_assignments);

    internal void ReplaceValidated(IReadOnlyDictionary<int, SheetLineOperation> items)
    {
        _assignments.Clear();
        foreach (var pair in items)
            _assignments.Add(pair.Key, pair.Value);
    }
    public bool CanGenerateMachineCommands => false;

    public bool TryGet(int lineIndex, out SheetLineOperation? operation)
        => _assignments.TryGetValue(lineIndex, out operation);

    public bool Remove(int lineIndex) => _assignments.Remove(lineIndex);

    /// <summary>
    /// Rejects edits that silently alter the imported CAD endpoints. This
    /// registry only stores a technological annotation of an existing line.
    /// </summary>
    public SheetOperationPreview Assign(
        int lineIndex, Point2 importedStart, Point2 importedEnd,
        SheetLineOperation operation, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (lineIndex < 0)
            return new(false, operation.Kind, [],
                ["DXF LINE index must be nonnegative."]);
        if (!double.IsFinite(importedStart.X) || !double.IsFinite(importedStart.Y)
            || !double.IsFinite(importedEnd.X) || !double.IsFinite(importedEnd.Y)
            || operation.Start != importedStart || operation.End != importedEnd)
            return new(false, operation.Kind, [],
                ["Operation endpoints must match the original DXF LINE."]);
        var preview = SheetOperationPlanner.InspectOpenLine(operation, sheet);
        if (preview.IsValid)
            _assignments[lineIndex] = operation;
        return preview;
    }
}
