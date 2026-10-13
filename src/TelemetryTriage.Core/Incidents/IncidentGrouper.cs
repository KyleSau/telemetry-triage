using TelemetryTriage.Core.Model;

namespace TelemetryTriage.Core.Incidents;

/// <summary>
/// Session-window grouping in event time: an anomaly joins the open incident for its stream if it
/// arrives within <c>gap</c> of that incident's latest anomaly; otherwise it opens a new one. An
/// incident closes once event time has moved <c>gap</c> past its latest anomaly. Using frame
/// timestamps (not wall clock) makes grouping identical whether data is live or replayed.
/// </summary>
public sealed class IncidentGrouper(TimeSpan gap)
{
    private readonly Dictionary<string, List<AnomalyEvent>> _open = new();

    public void Add(AnomalyEvent anomaly)
    {
        if (!_open.TryGetValue(anomaly.StreamId, out var members))
        {
            _open[anomaly.StreamId] = members = [];
        }

        members.Add(anomaly);
    }

    /// <summary>Closes incidents whose latest anomaly is older than <c>gap</c> relative to <paramref name="now"/>.</summary>
    public IReadOnlyList<Incident> CloseExpired(DateTimeOffset now) =>
        Close(_open.Where(kv => now - kv.Value.Max(a => a.Timestamp) > gap).Select(kv => kv.Key).ToList(), now);

    /// <summary>Closes everything still open (end of stream or shutdown).</summary>
    public IReadOnlyList<Incident> CloseAll(DateTimeOffset now) => Close(_open.Keys.ToList(), now);

    private List<Incident> Close(List<string> streams, DateTimeOffset now)
    {
        var closed = new List<Incident>();
        foreach (var stream in streams)
        {
            var members = _open[stream].OrderBy(a => a.Sequence).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
            _open.Remove(stream);
            var first = members[0];
            // Deterministic id: re-processing the same frames (at-least-once redelivery) upserts, not duplicates.
            closed.Add(new Incident($"INC-{stream}-{first.Sequence:D6}", stream, first.Timestamp, now, members));
        }

        return closed;
    }
}
