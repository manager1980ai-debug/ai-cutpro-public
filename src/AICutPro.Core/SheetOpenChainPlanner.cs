namespace AICutPro.Core;

/// <summary>
/// Read-only preview of a deliberately ordered, connected open path.
/// Unlike a DXF linetype, cut/skip here is an explicit preview treatment.
/// Never emits toolpaths, G-code or machine authorization.
/// </summary>
public static class SheetOpenChainPlanner
{
    public static SheetOperationPreview Inspect(
        IReadOnlyList<Point2> vertices, Sheet sheet,
        SheetOperationKind kind, SheetLineProcessingMode processing,
        double startInsetMm = 0, double endInsetMm = 0,
        double cutLengthMm = 0, double skipLengthMm = 0,
        bool startWithCut = true)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(sheet);
        var issues = new List<string>();
        if (vertices.Count < 2 || vertices.Count > 1001)
            issues.Add("Open chain must contain between 2 and 1001 vertices.");
        if (kind is not (SheetOperationKind.OpenThroughCut or
            SheetOperationKind.PerforatedCut or SheetOperationKind.BendReference or
            SheetOperationKind.Marking))
            issues.Add("Explicit open-chain intent is required.");
        if (!Enum.IsDefined(processing) || processing == SheetLineProcessingMode.AutoLegacy)
            issues.Add("Open chain needs an explicit processing mode.");
        if (!double.IsFinite(sheet.WidthMm) || !double.IsFinite(sheet.HeightMm) ||
            sheet.WidthMm <= 0 || sheet.HeightMm <= 0)
            issues.Add("Sheet dimensions are invalid.");
        if (!double.IsFinite(startInsetMm) || startInsetMm < 0 ||
            !double.IsFinite(endInsetMm) || endInsetMm < 0)
            issues.Add("Open-chain insets must be finite and nonnegative.");
        if (processing == SheetLineProcessingMode.PerforatedThroughCut)
        {
            if (!double.IsFinite(cutLengthMm) || cutLengthMm <= 0 ||
                !double.IsFinite(skipLengthMm) || skipLengthMm <= 0 ||
                !double.IsFinite(cutLengthMm + skipLengthMm))
                issues.Add("Cut/skip lengths must be finite and positive.");
        }
        else if (cutLengthMm != 0 || skipLengthMm != 0)
            issues.Add("Cut/skip parameters are only valid for perforation.");
        if (issues.Count != 0)
            return new(false, kind, [], issues);

        var lengths = new double[vertices.Count - 1];
        double total = 0;
        for (var i = 0; i < vertices.Count; i++)
        {
            var p = vertices[i];
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                p.X < 0 || p.Y < 0 || p.X > sheet.WidthMm || p.Y > sheet.HeightMm)
                issues.Add("Open-chain vertex lies outside sheet or is invalid.");
            if (i == 0) continue;
            var prev = vertices[i - 1];
            var dx = p.X - prev.X;
            var dy = p.Y - prev.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (!double.IsFinite(length) || length <= 1e-9)
                issues.Add("Open chain has a degenerate segment.");
            lengths[i - 1] = length;
            total += length;
        }
        if (!double.IsFinite(total) || total > 1_000_000 || total <= 1e-9)
            issues.Add("Open chain exceeds safe length limits.");
        if (double.IsFinite(total) && double.IsFinite(startInsetMm) &&
            double.IsFinite(endInsetMm) && startInsetMm + endInsetMm >= total)
            issues.Add("Insets remove the entire open chain.");
        if (vertices[0] == vertices[^1])
            issues.Add("Closed paths cannot be treated as open chains.");
        if (issues.Count > 0)
            return new(false, kind, [], issues);

        var start = startInsetMm;
        var end = total - endInsetMm;
        var segments = new List<SheetLinePreviewSegment>();
        var perforated = processing == SheetLineProcessingMode.PerforatedThroughCut;
        var cut = processing == SheetLineProcessingMode.ThroughCut ||
            (perforated && startWithCut);
        var position = start;
        var patternRemain = perforated
            ? (startWithCut ? cutLengthMm : skipLengthMm)
            : double.PositiveInfinity;
        while (position < end - 1e-9)
        {
            if (segments.Count >= 10_000)
                return new(false, kind, [],
                    ["Open-chain preview segment limit exceeded."]);
            // Determine the current source edge and clamp every generated
            // fragment at a vertex, while keeping Cut/Skip phase continuous.
            var accumulated = 0.0;
            var edge = 0;
            while (edge < lengths.Length - 1 &&
                accumulated + lengths[edge] <= position + 1e-10)
            {
                accumulated += lengths[edge];
                edge++;
            }
            var remainingOnEdge = accumulated + lengths[edge] - position;
            var step = Math.Min(end - position,
                Math.Min(remainingOnEdge, patternRemain));
            if (!double.IsFinite(step) || step <= 1e-10)
                return new(false, kind, [],
                    ["Open-chain fragmentation is numerically unstable."]);
            Point2 At(double absolute)
            {
                var fraction = (absolute - accumulated) / lengths[edge];
                var a = vertices[edge];
                var b = vertices[edge + 1];
                return new Point2(a.X + (b.X - a.X) * fraction,
                    a.Y + (b.Y - a.Y) * fraction);
            }
            segments.Add(new(At(position), At(position + step), cut));
            position += step;
            if (perforated)
            {
                patternRemain -= step;
                if (patternRemain <= 1e-9)
                {
                    cut = !cut;
                    patternRemain = cut ? cutLengthMm : skipLengthMm;
                }
            }
        }
        return new(true, kind, segments, []);
    }
}
