namespace AICutPro.Core;

/// <summary>
/// Non-executable planning data for a proposed plasma process.
/// All physical process values must come from a verified machine/source cut chart.
/// This is NOT a G-code generator, controller API or manufacturing approval.
/// </summary>
public sealed record PlasmaProcessProfile(
    string Material,
    double ThicknessMm,
    string PowerSourceModel,
    string ControllerModel,
    string CutChartReference,
    double CurrentAmps,
    double FeedRateMmPerMinute,
    double KerfWidthMm,
    double PierceDelaySeconds,
    double PierceHeightMm,
    double CutHeightMm,
    bool OperatorConfirmed,
    bool RequiresArcOk,
    bool ArcOkSupported,
    bool ThcRequested,
    bool ThcSupported,
    bool ZMotionSupported)
{
    public bool CanStartMachine => false;

    /// <summary>
    /// Structural checks only. Successful validation never verifies cut-chart
    /// truth, G-code dialect, hardware wiring, consumables or torch safety.
    /// </summary>
    public IReadOnlyList<string> ValidateForPlanning()
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(Material))
            issues.Add("Material must be identified.");
        if (string.IsNullOrWhiteSpace(PowerSourceModel))
            issues.Add("Plasma power source must be identified.");
        if (string.IsNullOrWhiteSpace(ControllerModel))
            issues.Add("Controller must be identified.");
        if (string.IsNullOrWhiteSpace(CutChartReference))
            issues.Add("Verified machine cut-chart reference is required.");
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        if (!Positive(ThicknessMm)) issues.Add("Material thickness must be positive and finite.");
        if (!Positive(CurrentAmps)) issues.Add("Current must be positive and finite.");
        if (!Positive(FeedRateMmPerMinute)) issues.Add("Feed rate must be positive and finite.");
        if (!Positive(KerfWidthMm)) issues.Add("Kerf must be positive and finite.");
        if (!double.IsFinite(PierceDelaySeconds) || PierceDelaySeconds < 0)
            issues.Add("Pierce delay must be finite and nonnegative seconds.");
        if (!Positive(PierceHeightMm)) issues.Add("Pierce height must be positive and finite.");
        if (!Positive(CutHeightMm)) issues.Add("Cut height must be positive and finite.");
        if (Positive(PierceHeightMm) && Positive(CutHeightMm) &&
            PierceHeightMm <= CutHeightMm)
            issues.Add("Pierce height must exceed cut height for this supported profile.");
        if (!ZMotionSupported)
            issues.Add("Z motion is required for separate pierce/cut heights.");
        if (RequiresArcOk && !ArcOkSupported)
            issues.Add("Required Arc OK input is unavailable.");
        if (ThcRequested && !ThcSupported)
            issues.Add("THC was requested but is not supported.");
        if (!OperatorConfirmed)
            issues.Add("Operator has not confirmed the profile.");
        return issues;
    }
}
