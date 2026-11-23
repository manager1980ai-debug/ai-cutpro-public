namespace AICutPro.Core;

/// <summary>
/// Document-scoped annotations for U-shaped previews. These are generated
/// geometric previews, not imported DXF entities or approved machine paths.
/// </summary>
public sealed record SheetUShapeAnnotation(
    int BaseLineIndex,
    double HeightMm,
    SheetLineProcessingMode BaseProcessing,
    double CutLengthMm = 3,
    double SkipLengthMm = 2);

public sealed class SheetUShapeAssignmentRegister
{
    private readonly Dictionary<int, SheetUShapeAnnotation> _items = new();
    public int Count => _items.Count;
    public bool CanGenerateMachineCommands => false;
    public IReadOnlyDictionary<int, SheetUShapeAnnotation> Snapshot()
        => new Dictionary<int, SheetUShapeAnnotation>(_items);
    public bool TryGet(int baseLineIndex, out SheetUShapeAnnotation? annotation)
        => _items.TryGetValue(baseLineIndex, out annotation);
    public bool Remove(int baseLineIndex) => _items.Remove(baseLineIndex);

    internal void ReplaceValidated(IReadOnlyDictionary<int, SheetUShapeAnnotation> values)
    {
        _items.Clear();
        foreach (var pair in values)
            _items.Add(pair.Key, pair.Value);
    }

    public SheetUShapedPreview Assign(
        SheetUShapeAnnotation annotation, IReadOnlyList<DxfLine> lines, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sheet);
        if (annotation.BaseLineIndex < 0 || annotation.BaseLineIndex >= lines.Count)
            return new(false, [], [], ["U-shaped base DXF LINE index is invalid."]);
        var source = lines[annotation.BaseLineIndex];
        if (source is null || !double.IsFinite(source.Start.X) ||
            !double.IsFinite(source.Start.Y) || !double.IsFinite(source.End.X) ||
            !double.IsFinite(source.End.Y) ||
            source.Start.Y != source.End.Y)
            return new(false, [], [], ["U-shaped base must be a finite horizontal DXF LINE."]);
        var preview = SheetUShapedPreset.Inspect(
            sheet,
            Math.Min(source.Start.X, source.End.X),
            source.Start.Y,
            Math.Abs(source.End.X - source.Start.X),
            annotation.HeightMm,
            annotation.BaseProcessing,
            annotation.CutLengthMm,
            annotation.SkipLengthMm);
        if (preview.IsValid)
            _items[annotation.BaseLineIndex] = annotation;
        return preview;
    }
}
