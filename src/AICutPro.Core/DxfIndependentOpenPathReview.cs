namespace AICutPro.Core;

/// <summary>
/// Read-only diagnostics for distinct connected OPEN DXF components, including
/// cross-layer crossings which a per-component review cannot detect.
/// Closed pieces and nested profiles are evaluated by separate contour tools.
/// </summary>
public sealed record DxfIndependentPathReviewResult(
    bool IsValid, int OpenComponentCount, IReadOnlyList<string> Issues)
{
    public bool CanGenerateMachineCommands => false;
}

public static class DxfIndependentOpenPathReview
{
    public static DxfIndependentPathReviewResult Inspect(
        IReadOnlyList<DxfShape> shapes, Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(sheet);
        DxfIndependentPathReviewResult Fail(string issue, int count)
            => new(false, count, [issue]);
        if (shapes.Count is < 1 or > 100)
            return Fail("Open DXF component review supports 1..100 source shapes.", 0);

        var covered = new HashSet<int>();
        var paths = new List<(int Anchor, PreviewPath Path)>();
        for (var i = 0; i < shapes.Count; i++)
        {
            if (covered.Contains(i) ||
                shapes[i] is not (DxfLine or DxfArc or
                    DxfPolyline { Closed: false } or
                    DxfBulgedPolyline { Closed: false }))
                continue;
            var component = MixedOpenChainAssembler.Assemble(shapes, i, sheet);
            if (!component.IsValid || component.AnalyticPath is null)
                return Fail($"Open DXF component near entity {i + 1} is invalid: " +
                    string.Join("; ", component.Issues), paths.Count);
            foreach (var index in component.SourceIndices)
                covered.Add(index);
            paths.Add((i, component.AnalyticPath));
            if (paths.Count > 100)
                return Fail("Too many independent open DXF components.", paths.Count);
        }

        for (var i = 0; i < paths.Count; i++)
        for (var j = i + 1; j < paths.Count; j++)
        {
            var issues = PreviewContourValidator.ValidateDistinctPaths(
                paths[i].Path, paths[j].Path);
            if (issues.Count > 0)
                return Fail($"Independent open DXF components at sources " +
                    $"{paths[i].Anchor + 1}/{paths[j].Anchor + 1}: " +
                    string.Join("; ", issues), paths.Count);
        }
        return new(true, paths.Count, []);
    }
}
