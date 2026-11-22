namespace AICutPro.Core;

/// <summary>
/// Read-only CAD preflight. Passing this gate NEVER authorizes G-code or machine motion.
/// A separate, currently unimplemented CAM and controller safety approval is mandatory.
/// </summary>
public enum GeometryPreflightStatus
{
    Rejected,
    ReadyForCamReview
}
public sealed record GeometryPreflightResult(
    GeometryPreflightStatus Status,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<PolygonProfile> Profiles,
    double NetAreaMm2)
{
    public bool CanGenerateMachineCommands => false;
}

/// <summary>
/// Fail-closed preflight of the deliberately limited DXF subset.
/// Rejects any ignored geometry, unconfirmed units, open polylines, arcs, ambiguous
/// contour topology, unrealistic coordinates, and sheet/margin violations.
/// </summary>
public static class GeometryPreflight
{
    public static GeometryPreflightResult Inspect(
        DxfImportResult document, Sheet sheet, double marginMm = 0,
        double maximumCoordinateMm = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sheet);
        if (!double.IsFinite(sheet.WidthMm) || !double.IsFinite(sheet.HeightMm) ||
            sheet.WidthMm <= 0 || sheet.HeightMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(sheet));
        if (!double.IsFinite(marginMm) || marginMm < 0 ||
            marginMm * 2 >= sheet.WidthMm || marginMm * 2 >= sheet.HeightMm)
            throw new ArgumentOutOfRangeException(nameof(marginMm));
        if (!double.IsFinite(maximumCoordinateMm) || maximumCoordinateMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCoordinateMm));

        var issues = new List<string>();
        if (document.InsUnits != 4)
            issues.Add("Единицы DXF не подтверждены как миллиметры ($INSUNITS=4).");
        issues.AddRange(document.Warnings.Select(w => "DXF: " + w));
        if (document.Shapes.Count == 0)
            issues.Add("В DXF нет поддерживаемых геометрических элементов.");
        if (document.Shapes.Count > 10_000)
            issues.Add("Превышен лимит сложности геометрии.");
        if (document.Shapes.Any(x => x is DxfArc))
            issues.Add("Дуги ARC ещё не допущены к расчёту технологической геометрии.");
        if (document.Shapes.OfType<DxfPolyline>().Any(p => !p.Closed))
            issues.Add("Найдена незамкнутая полилиния.");
        if (document.Shapes.Any(x => x is not (DxfLine or DxfCircle or DxfPolyline)))
            issues.Add("Найдена неподдерживаемая геометрия.");
        bool Valid(double x) => double.IsFinite(x) && Math.Abs(x) <= maximumCoordinateMm;
        bool PointOk(Point2 p) => Valid(p.X) && Valid(p.Y);
        foreach (var shape in document.Shapes)
        {
            bool ok = shape switch
            {
                DxfLine l => PointOk(l.Start) && PointOk(l.End),
                DxfCircle c => PointOk(c.Center) && Valid(c.Radius) && c.Radius > 0,
                DxfPolyline p => p.Vertices.Count >= 3 && p.Vertices.All(PointOk),
                _ => false
            };
            if (!ok)
            {
                issues.Add("Геометрия содержит неверные или чрезмерные координаты.");
                break;
            }
        }
        if (issues.Count > 0) return Block(issues);

        var analysis = ProfileAnalyzer.Analyze(document);
        issues.AddRange(analysis.Warnings.Select(w => "Топология: " + w));
        if (analysis.Profiles.Count == 0)
            issues.Add("Не обнаружено ни одной проверенной замкнутой детали.");

        // The origin of this inspection is DXF drawing coordinates. Real nesting
        // will supply transforms, rotation, kerf and machine constraints later.
        foreach (var profile in analysis.Profiles)
        {
            if (!double.IsFinite(profile.NetAreaMm2) || profile.NetAreaMm2 <= 0)
            {
                issues.Add("Недопустимая чистая площадь детали.");
                continue;
            }
            var vertices = profile.Boundary;
            if (vertices.Count < 3 ||
                vertices.Any(p => !PointOk(p) ||
                    p.X < marginMm || p.X > sheet.WidthMm - marginMm ||
                    p.Y < marginMm || p.Y > sheet.HeightMm - marginMm))
                issues.Add("Внешний контур выходит за лист или нарушает отступ.");
        }
        if (issues.Count > 0) return Block(issues);
        return new(GeometryPreflightStatus.ReadyForCamReview,[],analysis.Profiles,
            analysis.Profiles.Sum(p => p.NetAreaMm2));
    }

    private static GeometryPreflightResult Block(IReadOnlyList<string> issues) =>
        new(GeometryPreflightStatus.Rejected,issues.ToArray(),[],0);
}
