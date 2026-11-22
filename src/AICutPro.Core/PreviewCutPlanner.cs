namespace AICutPro.Core;

/// <summary>Read-only proposed operation order. No kerf compensation, G-code, movement or start authorization.</summary>
public enum PreviewCutKind { InnerPolygon, CircularHole, OuterBoundary }
public sealed record PreviewCutOperation(
    int PartIndex, PreviewCutKind Kind, string Description,
    Point2? SuggestedPiercePoint, Point2? SuggestedBoundaryEntry,
    bool RequiresCamValidation);
public sealed record PreviewCutPlan(
    IReadOnlyList<PreviewCutOperation> Operations,
    IReadOnlyList<string> BlockingIssues)
{
    public bool CanStartMachine => false;
}

/// <summary>
/// Emits an inspection plan only for profiles already cleared by geometry preflight.
/// All internal cutouts are ordered before the enclosing outer boundary.
/// For a round hole, the center is proposed as the waste-side pierce candidate, not
/// as a qualified machine entry point. Final CAM must verify kerf, lead-in, material,
/// torch clearance, tilt, heat and falling-part risks.
/// </summary>
public static class PreviewCutPlanner
{
    public static PreviewCutPlan Plan(GeometryPreflightResult verified,
        double minimumHoleRadiusMm = 1)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (!double.IsFinite(minimumHoleRadiusMm) || minimumHoleRadiusMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumHoleRadiusMm));
        if (verified.Status != GeometryPreflightStatus.ReadyForCamReview ||
            verified.BlockingIssues.Count != 0 || verified.Profiles.Count == 0)
            return new([],["Геометрия не прошла предварительную проверку."]);
        var operations = new List<PreviewCutOperation>();
        var issues = new List<string>();
        for (int index = 0; index < verified.Profiles.Count; index++)
        {
            var part=verified.Profiles[index];
            if (part.Boundary.Count < 3 || !double.IsFinite(part.NetAreaMm2) ||
                part.NetAreaMm2 <= 0)
            {
                issues.Add($"Деталь {index+1}: неверный внешний контур.");
                continue;
            }
            for (int h = 0; h < part.InnerPolygons.Count; h++)
            {
                if (part.InnerPolygons[h].Count < 3)
                {
                    issues.Add($"Деталь {index+1}: неверное полигональное отверстие.");
                    continue;
                }
                operations.Add(new(index,PreviewCutKind.InnerPolygon,
                    $"Внутренний вырез {h+1}: до внешней границы",
                    null,null,true));
            }
            for (int h = 0; h < part.CircularHoles.Count; h++)
            {
                var hole=part.CircularHoles[h];
                if (!double.IsFinite(hole.Radius)||hole.Radius<=minimumHoleRadiusMm ||
                    !double.IsFinite(hole.Center.X)||!double.IsFinite(hole.Center.Y))
                {
                    issues.Add($"Деталь {index+1}: круглое отверстие {h+1} требует CAM-проверки размера.");
                    continue;
                }
                operations.Add(new(index,PreviewCutKind.CircularHole,
                    $"Круглое отверстие {h+1}: прожиг в отходной части, вход проверить",
                    hole.Center,new Point2(hole.Center.X+hole.Radius,hole.Center.Y),true));
            }
            operations.Add(new(index,PreviewCutKind.OuterBoundary,
                "Внешний контур: после всех внутренних вырезов",null,null,true));
        }
        // Fail closed: never return a partial plan that could conceal skipped holes.
        if (issues.Count>0) return new([],issues);
        return new(operations,[]);
    }
}
