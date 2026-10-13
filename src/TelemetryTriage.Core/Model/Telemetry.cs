namespace TelemetryTriage.Core.Model;

/// <summary>
/// One sample instant for one (synthetic) telemetry source. Every channel sampled at that instant is a
/// key in <see cref="Readings"/>; a channel missing from the dictionary is a dropout for that frame.
/// </summary>
public sealed record TelemetryFrame(
    string StreamId,
    long Sequence,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, double> Readings);

/// <summary>Static description of a channel: units, owning subsystem, nominal behaviour and limits.</summary>
public sealed record ChannelSpec(
    string Name,
    string Unit,
    string Subsystem,
    double Nominal,
    double NoiseStd,
    double MaxRatePerSecond,
    IReadOnlyList<string> Keywords);

/// <summary>
/// The fictional vehicle's channel list. Values are invented for the demo and do not describe any
/// real vehicle. MaxRatePerSecond is set well above the rate noise (about 14x NoiseStd at 10 Hz)
/// so that the rate-of-change detector only fires on physically implausible jumps.
/// </summary>
public static class ChannelCatalog
{
    public static readonly IReadOnlyList<ChannelSpec> All =
    [
        new("bus_voltage", "V", "EPS", 28.0, 0.05, 5.0, ["power", "voltage", "bus", "undervoltage"]),
        new("battery_temp", "degC", "EPS", 22.0, 0.05, 5.0, ["battery", "temperature", "thermal"]),
        new("tank_pressure", "kPa", "Propulsion", 2400.0, 2.0, 200.0, ["tank", "pressure", "pressurant"]),
        new("avionics_temp", "degC", "Thermal", 35.0, 0.05, 5.0, ["avionics", "temperature", "thermal"]),
        new("attitude_rate", "deg/s", "GNC", 0.0, 0.02, 2.0, ["attitude", "rate", "gyro"]),
        new("link_snr", "dB", "Comms", 18.0, 0.3, 30.0, ["link", "snr", "signal", "rf"]),
    ];

    private static readonly Dictionary<string, ChannelSpec> ByName = All.ToDictionary(c => c.Name);

    public static ChannelSpec Get(string name) =>
        ByName.TryGetValue(name, out var spec) ? spec : throw new KeyNotFoundException($"Unknown channel '{name}'.");

    /// <summary>Subsystems whose excursions are treated as more severe by the rule-based triage.</summary>
    public static bool IsCritical(string subsystem) => subsystem is "EPS" or "Propulsion";
}
