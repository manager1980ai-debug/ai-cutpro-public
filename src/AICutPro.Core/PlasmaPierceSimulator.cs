namespace AICutPro.Core;

/// <summary>
/// Deterministic, non-executable rehearsal of the proposed plasma pierce sequence.
/// It has no device adapter, real clock, G-code, motion or torch output.
/// Simulated timestamps are NOT actual machine timings.
/// </summary>
public static class PlasmaPierceSimulator
{
    public sealed record Inputs(
        double ArcOkAtSeconds,
        double ArcLostAtSeconds = double.PositiveInfinity,
        double ArcOkTimeoutSeconds = 5,
        bool Connected = true,
        bool EmergencyStop = false,
        bool Alarm = false);

    public sealed record Event(string Stage, double VirtualSeconds);

    public sealed record Result(
        bool Completed, string? Failure, IReadOnlyList<Event> Events)
    {
        public bool CanStartMachine => false;
        public bool ProducesGCode => false;
    }

    public static Result Rehearse(PlasmaProcessProfile profile, Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inputs);
        var events = new List<Event>();
        Result Fault(string reason, double virtualTime = 0)
        {
            events.Add(new Event("Fault", virtualTime));
            return new(false, reason, events);
        }
        var problems = profile.ValidateForPlanning();
        if (problems.Count > 0)
            return Fault(string.Join("; ", problems));
        // Open-loop plasma timing is intentionally excluded until a particular
        // source/controller has undergone a separate physical safety review.
        if (!profile.RequiresArcOk || !profile.ArcOkSupported)
            return Fault("A verified Arc OK channel is mandatory in this simulator.");
        if (!double.IsFinite(inputs.ArcOkTimeoutSeconds) ||
            inputs.ArcOkTimeoutSeconds <= 0 || inputs.ArcOkTimeoutSeconds > 60)
            return Fault("Invalid simulated Arc OK timeout.");
        if (double.IsNaN(inputs.ArcOkAtSeconds) ||
            inputs.ArcOkAtSeconds < 0)
            return Fault("Invalid simulated Arc OK time.");
        if (double.IsNaN(inputs.ArcLostAtSeconds) ||
            inputs.ArcLostAtSeconds < 0)
            return Fault("Invalid simulated arc loss time.");
        if (inputs.EmergencyStop) return Fault("Simulated emergency stop is active.");
        if (inputs.Alarm) return Fault("Simulated machine alarm is active.");
        if (!inputs.Connected) return Fault("Simulated controller is disconnected.");

        events.Add(new("PiercePositioned", 0));
        events.Add(new("ArcRequested", 0));
        if (!double.IsFinite(inputs.ArcOkAtSeconds) ||
            inputs.ArcOkAtSeconds > inputs.ArcOkTimeoutSeconds)
            return Fault("Arc OK timeout; simulated cutting prohibited.",
                inputs.ArcOkTimeoutSeconds);
        if (inputs.ArcLostAtSeconds <= inputs.ArcOkAtSeconds)
            return Fault("Arc OK lost before confirmation.", inputs.ArcLostAtSeconds);
        events.Add(new("ArcConfirmed", inputs.ArcOkAtSeconds));
        var cutStart = inputs.ArcOkAtSeconds + profile.PierceDelaySeconds;
        if (!double.IsFinite(cutStart) || cutStart > 1_000_000)
            return Fault("Simulated pierce timeline overflow.", inputs.ArcOkAtSeconds);
        if (inputs.ArcLostAtSeconds <= cutStart)
            return Fault("Arc OK lost during pierce delay; simulated cutting prohibited.",
                inputs.ArcLostAtSeconds);
        events.Add(new("PierceDelayCompleted", cutStart));
        // The simulator ends at simulated permission to ENTER the cutting
        // phase. There is no fabricated trajectory, torch-off command or move.
        events.Add(new("CutPhaseReadyForSimulation", cutStart));
        return new(true, null, events);
    }
}
