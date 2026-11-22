namespace AICutPro.Core;

/// <summary>
/// Bounded, non-executable ordered rehearsal of several independent pierce
/// attempts. No motion between contours, cutting duration, real controller
/// IO, G-code, automatic retry or permission to operate machinery.
/// Each attempt owns its own local virtual clock, starting at zero.
/// </summary>
public static class PlasmaPierceBatchSimulator
{
    public sealed record Attempt(string ContourId,
        PlasmaPierceSimulator.Inputs Inputs);

    public sealed record Outcome(string ContourId, int SequenceNumber,
        PlasmaPierceSimulator.Result Result);

    public sealed record Report(
        bool Completed, string? Failure, IReadOnlyList<Outcome> Outcomes)
    {
        public bool CanStartMachine => false;
        public bool ProducesGCode => false;
        public bool AllowsAutomaticResume => false;
    }

    public static Report Rehearse(
        PlasmaProcessProfile profile, IReadOnlyList<Attempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(attempts);
        if (attempts.Count is < 1 or > 100)
            return new(false, "A rehearsal needs 1 to 100 pierce attempts.", []);
        // Validate all identifiers before starting any virtual episode:
        // order and identity must be unambiguous for failure reports.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attempt in attempts)
        {
            if (attempt is null || string.IsNullOrWhiteSpace(attempt.ContourId) ||
                attempt.ContourId.Length > 128 || attempt.Inputs is null)
                return new(false, "Invalid pierce attempt.", []);
            if (!seen.Add(attempt.ContourId))
                return new(false, "Duplicate pierce contour identifier.", []);
        }

        var outcomes = new List<Outcome>();
        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index];
            var result = PlasmaPierceSimulator.Rehearse(profile, attempt.Inputs);
            outcomes.Add(new(attempt.ContourId, index + 1, result));
            if (!result.Completed)
                return new(false,
                    $"Contour {attempt.ContourId}, pierce {index + 1}: {result.Failure}",
                    outcomes.ToArray());
        }
        return new(true, null, outcomes.ToArray());
    }
}
