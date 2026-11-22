namespace AICutPro.Core;

/// <summary>Dimensions are millimetres. The first MVP nests bounding rectangles, not arbitrary polygons.</summary>
public sealed record Sheet(double WidthMm, double HeightMm)
{
    public double AreaMm2 => WidthMm * HeightMm;
}
public sealed record Part(string Name, double WidthMm, double HeightMm, int Quantity);
public sealed record Placement(string Name, int Index, double Xmm, double Ymm, double WidthMm, double HeightMm, bool Rotated);
public sealed record NestingResult(Sheet Sheet, IReadOnlyList<Placement> Placements, IReadOnlyList<string> Warnings, double UsedAreaMm2)
{
    public double UtilizationPercent => Sheet.AreaMm2 == 0 ? 0 : 100 * UsedAreaMm2 / Sheet.AreaMm2;
    public double WasteAreaMm2 => Math.Max(0, Sheet.AreaMm2 - UsedAreaMm2);
}
public static class SampleJob
{
    public static Sheet Sheet => new(3000, 1500);
    public static IReadOnlyList<Part> Parts => [new("Кронштейн", 180, 220, 10)];
}
