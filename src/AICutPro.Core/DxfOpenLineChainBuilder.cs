namespace AICutPro.Core;

/// <summary>
/// Finds one unambiguous ordered open chain of DXF LINE entities on a common
/// layer, starting from a selected entity. Ambiguous branches, duplicates,
/// loops and disconnected entities are refused; there is no inferred joining.
/// The original DXF entities are never modified.
/// </summary>
public sealed record DxfOpenLineChain(
    bool IsValid,
    IReadOnlyList<int> SourceIndices,
    IReadOnlyList<Point2> Vertices,
    IReadOnlyList<string> Issues)
{
    public bool CanGenerateMachineCommands => false;
}

public static class DxfOpenLineChainBuilder
{
    public static DxfOpenLineChain Build(
        IReadOnlyList<DxfLine> lines, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(lines);
        DxfOpenLineChain Fail(string issue) => new(false, [], [], [issue]);
        if (lines.Count > 100_000 || selectedIndex < 0 || selectedIndex >= lines.Count)
            return Fail("Selected DXF LINE index is invalid.");
        var selected = lines[selectedIndex];
        if (selected is null || !Finite(selected.Start) || !Finite(selected.End)
            || selected.Start == selected.End || selected.Layer is null)
            return Fail("Selected DXF LINE is invalid.");
        var edges = new Dictionary<Point2, List<int>>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line is null || line.Layer != selected.Layer)
                continue;
            if (!Finite(line.Start) || !Finite(line.End) || line.Start == line.End)
                return Fail("Layer contains a degenerate or invalid DXF LINE.");
            Add(line.Start, i);
            Add(line.End, i);
        }
        void Add(Point2 p, int i)
        {
            if (!edges.TryGetValue(p, out var list))
                edges[p] = list = new List<int>();
            list.Add(i);
        }
        // Walk selected component, then verify its degrees. No auto-joining
        // based on a numerical tolerance or visual proximity.
        var component = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(selectedIndex);
        while (pending.Count > 0)
        {
            var i = pending.Pop();
            if (!component.Add(i)) continue;
            if (component.Count > 1000)
                return Fail("Open DXF chain exceeds the 1000-segment preview limit.");
            foreach (var end in new[] { lines[i].Start, lines[i].End })
                foreach (var next in edges[end])
                    if (!component.Contains(next)) pending.Push(next);
        }
        var counts = new Dictionary<Point2, int>();
        foreach (var i in component)
        {
            var line = lines[i];
            counts[line.Start] = counts.GetValueOrDefault(line.Start) + 1;
            counts[line.End] = counts.GetValueOrDefault(line.End) + 1;
        }
        if (counts.Values.Any(c => c > 2))
            return Fail("Open DXF chain contains a branch or duplicate connection.");
        var endpoints = counts.Where(p => p.Value == 1)
            .Select(p => p.Key).OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (endpoints.Length != 2)
            return Fail("DXF chain is closed or topologically ambiguous.");
        var current = endpoints[0];
        var vertices = new List<Point2> { current };
        var ordered = new List<int>();
        var visited = new HashSet<int>();
        while (visited.Count < component.Count)
        {
            var candidates = edges[current]
                .Where(i => component.Contains(i) && !visited.Contains(i)).ToArray();
            if (candidates.Length != 1)
                return Fail("DXF chain cannot be ordered uniquely.");
            var i = candidates[0];
            visited.Add(i);
            ordered.Add(i);
            var line = lines[i];
            current = line.Start == current ? line.End : line.Start;
            vertices.Add(current);
        }
        if (current != endpoints[1] || ordered.Count != component.Count)
            return Fail("DXF chain is not a single open path.");
        return new(true, ordered, vertices, []);
    }

    private static bool Finite(Point2 point)
        => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
