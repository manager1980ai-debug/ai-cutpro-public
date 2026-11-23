namespace AICutPro.Core;

/// <summary>
/// Read-only selection of an imported DXF LINE by proximity to its finite
/// centreline. Does not classify an open line as a cutting operation.
/// </summary>
public static class DxfLinePicker
{
    /// <returns>Zero-based index among DxfLine objects, or null if no unambiguous hit.</returns>
    public static int? Pick(
        IReadOnlyList<DxfLine> lines, Point2 pointer,
        double toleranceMm, double ambiguityToleranceMm = 0.01)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!double.IsFinite(pointer.X) || !double.IsFinite(pointer.Y) ||
            !double.IsFinite(toleranceMm) || toleranceMm <= 0 ||
            !double.IsFinite(ambiguityToleranceMm) || ambiguityToleranceMm < 0)
            return null;

        int? bestIndex = null;
        var best = double.PositiveInfinity;
        var second = double.PositiveInfinity;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var ax = line.Start.X;
            var ay = line.Start.Y;
            var bx = line.End.X;
            var by = line.End.Y;
            if (!double.IsFinite(ax) || !double.IsFinite(ay) ||
                !double.IsFinite(bx) || !double.IsFinite(by))
                continue;
            var dx = bx - ax;
            var dy = by - ay;
            var length2 = dx * dx + dy * dy;
            if (!double.IsFinite(length2) || length2 <= 1e-18)
                continue;
            var fraction = Math.Clamp(
                ((pointer.X - ax) * dx + (pointer.Y - ay) * dy) / length2, 0, 1);
            var px = ax + fraction * dx;
            var py = ay + fraction * dy;
            var distance = Math.Sqrt((pointer.X - px) * (pointer.X - px) +
                                     (pointer.Y - py) * (pointer.Y - py));
            if (!double.IsFinite(distance) || distance > toleranceMm)
                continue;
            if (distance < best)
            {
                second = best;
                best = distance;
                bestIndex = i;
            }
            else if (distance < second)
                second = distance;
        }
        // If multiple segments coincide or nearly coincide, do not guess.
        return bestIndex.HasValue && second - best > ambiguityToleranceMm
            ? bestIndex : null;
    }
}
