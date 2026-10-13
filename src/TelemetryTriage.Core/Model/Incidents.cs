using System.Text.Json.Serialization;

namespace TelemetryTriage.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity { Low, Medium, High, Critical }

/// <summary>
/// A single detector finding. One event is emitted when an anomalous episode starts on a channel,
/// not on every anomalous sample, so a sustained fault does not flood the incident.
/// </summary>
public sealed record AnomalyEvent(
    string Id,
    string StreamId,
    string Channel,
    string Subsystem,
    string Detector,
    long Sequence,
    DateTimeOffset Timestamp,
    double? Value,
    double Score,
    double Threshold,
    string Explanation)
{
    /// <summary>Deterministic id, so re-processing the same frames yields the same evidence ids.</summary>
    public static string MakeId(string channel, string detector, long sequence) =>
        $"anom:{channel}:{detector}:{sequence}";
}

/// <summary>Anomalies on one stream that occurred close together in event time.</summary>
public sealed record Incident(
    string Id,
    string StreamId,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    IReadOnlyList<AnomalyEvent> Anomalies)
{
    public long FirstSequence => Anomalies.Min(a => a.Sequence);
    public long LastSequence => Anomalies.Max(a => a.Sequence);
    public IReadOnlyList<string> Channels => Anomalies.Select(a => a.Channel).Distinct().ToList();
    public IReadOnlyList<string> Subsystems => Anomalies.Select(a => a.Subsystem).Distinct().ToList();
}
