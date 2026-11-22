namespace AICutPro.Core;

/// <summary>
/// Explicit, bounded tolerances for CAD preview validation in millimetres.
/// Does not alter the analytic intersection checker's fixed internal epsilon,
/// authorize CAM output, or describe a machine's positioning accuracy.
/// </summary>
public sealed class PreviewGeometryTolerancePolicy
{
    public double JoinMm { get; }
    public double TopologyMm { get; }

    private PreviewGeometryTolerancePolicy(double joinMm, double topologyMm)
    {
        JoinMm = joinMm;
        TopologyMm = topologyMm;
    }

    public static PreviewGeometryTolerancePolicy Create(
        double joinMm = 0.01, double topologyMm = 0.01)
    {
        static void RequireValid(double value, string name)
        {
            if (!double.IsFinite(value) || value < 1e-9 || value > 1)
                throw new ArgumentOutOfRangeException(name,
                    "Preview tolerance must be finite and between 1e-9 and 1 mm.");
        }
        RequireValid(joinMm, nameof(joinMm));
        RequireValid(topologyMm, nameof(topologyMm));
        return new(joinMm, topologyMm);
    }

    public IReadOnlyList<string> ValidateContour(PreviewPath path) =>
        PreviewContourValidator.Validate(path, JoinMm);

    public IReadOnlyList<string> ValidatePair(PreviewPath first, PreviewPath second) =>
        PreviewContourValidator.ValidatePair(first, second, TopologyMm);

    public PreviewTopologyResult Analyze(IReadOnlyList<PreviewPath> paths) =>
        PreviewContourTopology.Analyze(paths, TopologyMm);

    /// <summary>
    /// Full preview-only drawing inspection with explicit tolerances. Rejects
    /// malformed individual rings before checking nesting; never starts a machine.
    /// </summary>
    public PreviewTopologyResult ValidateDrawing(IReadOnlyList<PreviewPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > 1000)
            return new(false, [], 0, ["Invalid number of preview contours."]);
        for (var i = 0; i < paths.Count; i++)
        {
            if (paths[i] is null)
                return new(false, [], 0, [$"Contour {i + 1} is null."]);
            var problems = ValidateContour(paths[i]);
            if (problems.Count != 0)
                return new(false, [], 0,
                    problems.Select(p => $"Contour {i + 1}: {p}").ToArray());
        }
        // Every ring is individually inspected with JoinMm, while topology,
        // pair intersections and containment use the separate TopologyMm.
        return Analyze(paths);
    }
}
