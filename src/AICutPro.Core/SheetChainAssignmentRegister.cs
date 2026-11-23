namespace AICutPro.Core;

/// <summary>
/// A preview-only annotation bound to an exact connected component of imported
/// DXF LINEs. SelectedIndex is its stable anchor within the imported list,
/// not a generated toolpath or a replacement for DXF geometry.
/// </summary>
public sealed record SheetChainAnnotation(
    int SelectedLineIndex,
    bool ReverseDirection,
    SheetOperationKind Kind,
    SheetLineProcessingMode Processing,
    double StartInsetMm = 0,
    double EndInsetMm = 0,
    double CutLengthMm = 0,
    double SkipLengthMm = 0,
    bool StartWithCut = true);

public sealed class SheetChainAssignmentRegister
{
    private readonly Dictionary<int, SheetChainAnnotation> _items = new();
    public int Count => _items.Count;
    public bool CanGenerateMachineCommands => false;
    public IReadOnlyDictionary<int, SheetChainAnnotation> Snapshot()
        => new Dictionary<int, SheetChainAnnotation>(_items);
    public bool TryGet(int anchorIndex, out SheetChainAnnotation? item)
        => _items.TryGetValue(anchorIndex, out item);
    public bool Remove(int anchorIndex) => _items.Remove(anchorIndex);
    internal void ReplaceValidated(IReadOnlyDictionary<int, SheetChainAnnotation> items)
    {
        _items.Clear();
        foreach (var item in items)
            _items.Add(item.Key, item.Value);
    }

    /// <summary>All-or-nothing validation: no replacement on a rejected edit.</summary>
    public SheetOperationPreview Assign(
        SheetChainAnnotation annotation, IReadOnlyList<DxfLine> lines, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sheet);
        var result = Validate(annotation, lines, sheet);
        if (!result.IsValid)
            return result;
        // Do not store multiple annotations for the same component. Exact
        // connectivity prevents unintentional double-treatment of one line.
        var incoming = DxfOpenLineChainBuilder.Build(lines, annotation.SelectedLineIndex);
        foreach (var existing in _items)
        {
            if (existing.Key == annotation.SelectedLineIndex) continue;
            var chain = DxfOpenLineChainBuilder.Build(lines, existing.Key);
            if (!chain.IsValid ||
                chain.SourceIndices.Any(incoming.SourceIndices.Contains))
                return new(false, annotation.Kind, [],
                    ["Chain intersects another saved chain or its geometry changed."]);
        }
        _items[annotation.SelectedLineIndex] = annotation;
        return result;
    }

    public static SheetOperationPreview Validate(
        SheetChainAnnotation annotation, IReadOnlyList<DxfLine> lines, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sheet);
        var chain = DxfOpenLineChainBuilder.Build(
            lines, annotation.SelectedLineIndex, annotation.ReverseDirection);
        if (!chain.IsValid)
            return new(false, annotation.Kind, [], chain.Issues);
        return SheetOpenChainPlanner.Inspect(
            chain.Vertices, sheet, annotation.Kind, annotation.Processing,
            annotation.StartInsetMm, annotation.EndInsetMm,
            annotation.CutLengthMm, annotation.SkipLengthMm,
            annotation.StartWithCut);
    }
}
