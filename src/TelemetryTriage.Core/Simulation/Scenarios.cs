namespace TelemetryTriage.Core.Simulation;

public enum FaultKind
{
    /// <summary>Channel is absent from frames.</summary>
    Dropout,
    /// <summary>Channel repeats its last good value.</summary>
    Stuck,
    /// <summary>Adds Magnitude per sample, accumulating (a slow ramp).</summary>
    Drift,
    /// <summary>Adds Magnitude for the fault duration (use duration 1 for a single-sample spike).</summary>
    Spike,
    /// <summary>Moves the level by Magnitude over three samples and holds it.</summary>
    Step,
}

/// <summary>Ground truth for one injected fault. Evals measure detection latency from StartSequence.</summary>
public sealed record FaultSpec(string Channel, FaultKind Kind, long StartSequence, long DurationSamples, double Magnitude = 0)
{
    public bool IsActive(long sequence) => sequence >= StartSequence && sequence < StartSequence + DurationSamples;
}

public sealed record Scenario(string Name, string Description, int Frames, IReadOnlyList<FaultSpec> Faults, int Seed = 7);

/// <summary>Synthetic scenarios. 10 Hz, 60 seconds each, faults injected at sample 300 (t = 30 s).</summary>
public static class ScenarioCatalog
{
    private const int Frames = 600;
    private const long FaultStart = 300;
    private const long ToEnd = Frames - FaultStart;

    public static readonly IReadOnlyList<Scenario> All =
    [
        new("nominal", "No faults; used to measure false positives.", Frames, []),
        new("sensor-dropout", "battery_temp disappears from telemetry for 5 s.", Frames,
            [new("battery_temp", FaultKind.Dropout, FaultStart, 50)]),
        new("stuck-value", "tank_pressure freezes at its last value for 10 s.", Frames,
            [new("tank_pressure", FaultKind.Stuck, FaultStart, 100)]),
        new("drift", "avionics_temp ramps slowly upward (+0.04 degC/s) until the end of the run.", Frames,
            [new("avionics_temp", FaultKind.Drift, FaultStart, ToEnd, 0.004)]),
        new("spike", "attitude_rate shows a single-sample +1.2 deg/s spike.", Frames,
            [new("attitude_rate", FaultKind.Spike, FaultStart, 1, 1.2)]),
        new("correlated-fault",
            "Power bus sags 3.2 V; battery temperature then climbs and link SNR drops 6 dB (shared root cause).",
            Frames,
            [
                new("bus_voltage", FaultKind.Step, FaultStart, ToEnd, -3.2),
                new("battery_temp", FaultKind.Drift, FaultStart + 2, ToEnd - 2, 0.03),
                new("link_snr", FaultKind.Step, FaultStart + 5, ToEnd - 5, -6.0),
            ]),
    ];

    public static Scenario Get(string name) =>
        All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"Unknown scenario '{name}'. Known: {string.Join(", ", All.Select(s => s.Name))}", nameof(name));
}
