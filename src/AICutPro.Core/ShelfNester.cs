namespace AICutPro.Core;

/// <summary>
/// Deterministic shelf-packing baseline. MVP intentionally uses bounding boxes and
/// therefore must NOT claim production-grade irregular nesting or collision-safe toolpaths.
/// </summary>
public static class ShelfNester
{
    public static NestingResult Arrange(Sheet sheet, IEnumerable<Part> parts, double gapMm = 8)
    {
        if (!double.IsFinite(sheet.WidthMm) || !double.IsFinite(sheet.HeightMm) ||
            sheet.WidthMm <= 0 || sheet.HeightMm <= 0 || !double.IsFinite(gapMm) || gapMm < 0)
            throw new ArgumentOutOfRangeException(nameof(sheet), "Sheet dimensions must be positive and gap nonnegative.");
        var all = parts.ToArray();
        foreach (var part in all)
            if (string.IsNullOrWhiteSpace(part.Name) || !double.IsFinite(part.WidthMm) ||
                !double.IsFinite(part.HeightMm) || part.WidthMm <= 0 || part.HeightMm <= 0 ||
                part.Quantity < 0 || part.Quantity > 100000)
                throw new ArgumentException("Part name, dimensions, or quantity are invalid.", nameof(parts));
        var placements = new List<Placement>();
        var warnings = new List<string>();
        double x = 0, y = 0, shelfHeight = 0, used = 0;
        // Largest first produces a reasonable deterministic baseline, not an optimal packing.
        foreach (var part in all.OrderByDescending(p => p.WidthMm * p.HeightMm).ThenBy(p => p.Name, StringComparer.Ordinal))
        for (var index = 0; index < part.Quantity; index++)
        {
            var width = part.WidthMm;
            var height = part.HeightMm;
            var rotated = false;
            if (x + width > sheet.WidthMm && x + height <= sheet.WidthMm && y + width <= sheet.HeightMm)
            {
                (width, height) = (height, width);
                rotated = true;
            }
            if (x + width > sheet.WidthMm)
            {
                x = 0;
                y += shelfHeight + gapMm;
                shelfHeight = 0;
                width = part.WidthMm;
                height = part.HeightMm;
                rotated = false;
            }
            if (width > sheet.WidthMm && height <= sheet.WidthMm)
            {
                (width, height) = (height, width);
                rotated = true;
            }
            if (x + width > sheet.WidthMm || y + height > sheet.HeightMm)
            {
                warnings.Add($"Нет места для {part.Name} #{index + 1}");
                continue;
            }
            placements.Add(new(part.Name, index + 1, x, y, width, height, rotated));
            used += width * height;
            x += width + gapMm;
            shelfHeight = Math.Max(shelfHeight, height);
        }
        return new(sheet, placements, warnings, used);
    }
}
