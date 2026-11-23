namespace AICutPro.Core;

/// <summary>
/// Read-only detection of conflicting preview annotations across the legacy
/// LINE, U-shape, straight-chain and mixed LINE/ARC registries. Only imported
/// DXF shape membership is compared, not machine kerf or swept tool clearance.
/// </summary>
public static class SheetTechnologyOverlapReview
{
    public static IReadOnlyList<string> Inspect(
        IReadOnlyList<DxfShape> shapes, Sheet sheet,
        SheetLineAssignmentRegister lines,
        SheetUShapeAssignmentRegister uShapes,
        SheetChainAssignmentRegister straightChains,
        SheetMixedChainAssignmentRegister mixedChains)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(uShapes);
        ArgumentNullException.ThrowIfNull(straightChains);
        ArgumentNullException.ThrowIfNull(mixedChains);
        var errors = new List<string>();
        if (shapes.Count > 1000 || shapes.Count < 1)
            return ["DXF exceeds supported cross-annotation review size."];
        var lineIndices = shapes.Select((shape, i) => (shape, i))
            .Where(x => x.shape is DxfLine).Select(x => x.i).ToArray();
        var sourceLines = shapes.OfType<DxfLine>().ToArray();
        var occupied = new Dictionary<int, string>();

        void Reserve(int shapeIndex, string owner)
        {
            if (shapeIndex < 0 || shapeIndex >= shapes.Count)
            {
                errors.Add($"Invalid source index for {owner}.");
                return;
            }
            if (occupied.TryGetValue(shapeIndex, out var other))
                errors.Add($"DXF shape {shapeIndex + 1} belongs to both {other} and {owner}.");
            else
                occupied[shapeIndex] = owner;
        }
        foreach (var item in lines.Snapshot())
            Reserve(item.Key >= 0 && item.Key < lineIndices.Length
                ? lineIndices[item.Key] : -1, $"LINE {item.Key + 1}");
        foreach (var item in uShapes.Snapshot())
            Reserve(item.Key >= 0 && item.Key < lineIndices.Length
                ? lineIndices[item.Key] : -1, $"U-group {item.Key + 1}");
        foreach (var item in straightChains.Snapshot())
        {
            var path = DxfOpenLineChainBuilder.Build(sourceLines, item.Key);
            if (!path.IsValid)
            {
                errors.Add($"Straight chain {item.Key + 1} has changed geometry.");
                continue;
            }
            foreach (var member in path.SourceIndices)
                Reserve(lineIndices[member], $"straight chain {item.Key + 1}");
        }
        foreach (var item in mixedChains.Snapshot())
        {
            var path = SheetMixedChainAssignmentRegister.Validate(
                item.Value, shapes, sheet);
            if (!path.IsValid)
            {
                errors.Add($"Mixed chain {item.Key + 1} has changed geometry.");
                continue;
            }
            foreach (var index in path.SourceIndices)
                Reserve(index, $"mixed chain {item.Key + 1}");
        }
        return errors;
    }
}
