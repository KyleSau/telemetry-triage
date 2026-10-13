using System.Text.Json;
using System.Text.Json.Serialization;
using TelemetryTriage.Core.Detection;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Core.Incidents;

public sealed record WindowPoint(long Seq, double? Value);

/// <summary>Raw samples for one channel around the incident, with a pre-incident baseline.</summary>
public sealed record ChannelWindow(
    string Id,
    string Channel,
    string Subsystem,
    string Unit,
    double? BaselineMean,
    double? BaselineStd,
    double? Min,
    double? Max,
    double? Last,
    int MissingSamples,
    bool ReturnedToBaseline,
    IReadOnlyList<WindowPoint> Points);

/// <summary>
/// Everything a triage provider may look at for one incident. Every item has an id, and the
/// serialized pack is exactly what the LLM sees, so it is also the ground truth for citation and
/// number validation.
/// </summary>
public sealed record EvidencePack(
    string IncidentId,
    string StreamId,
    IReadOnlyList<AnomalyEvent> Anomalies,
    IReadOnlyList<ChannelWindow> Windows,
    IReadOnlyList<RunbookSection> Runbooks)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonIgnore]
    public IReadOnlySet<string> EvidenceIds =>
        Anomalies.Select(a => a.Id).Concat(Windows.Select(w => w.Id)).Concat(Runbooks.Select(r => r.Id)).ToHashSet();

    [JsonIgnore]
    public IReadOnlySet<string> Subsystems =>
        Anomalies.Select(a => a.Subsystem).Concat(Windows.Select(w => w.Subsystem)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Bounded history of recent frames for one stream, used to cut evidence windows.</summary>
public sealed class FrameHistory(int capacity)
{
    private readonly Queue<TelemetryFrame> _frames = new();

    public long LatestSequence { get; private set; } = -1;

    public void Add(TelemetryFrame frame)
    {
        _frames.Enqueue(frame);
        LatestSequence = frame.Sequence;
        if (_frames.Count > capacity)
        {
            _frames.Dequeue();
        }
    }

    public List<TelemetryFrame> Range(long fromSequence, long toSequence) =>
        _frames.Where(f => f.Sequence >= fromSequence && f.Sequence <= toSequence).ToList();
}

/// <summary>Turns a closed incident into an <see cref="EvidencePack"/>: channel windows + retrieved runbook sections.</summary>
public sealed class EvidenceBuilder(RunbookIndex runbooks, int baselineFrames = 100, int maxPointsPerChannel = 60, int topSections = 3)
{
    private const int LeadInFrames = 20;

    private static readonly Dictionary<string, string[]> DetectorTerms = new()
    {
        ["zscore"] = ["spike", "step"],
        ["ewma-drift"] = ["drift", "trend"],
        ["rate"] = ["jump", "transient"],
        ["stuck"] = ["stuck", "frozen", "flatline"],
        ["missing"] = ["dropout", "missing", "gap"],
    };

    public EvidencePack Build(Incident incident, FrameHistory history)
    {
        var windows = incident.Channels.Select(c => BuildWindow(ChannelCatalog.Get(c), incident, history)).ToList();
        // Query = what is affected (channel keywords, subsystem) + what kind of symptom (detector terms).
        var terms = incident.Anomalies.SelectMany(a =>
            ChannelCatalog.Get(a.Channel).Keywords
                .Concat(a.Channel.Split('_'))
                .Append(a.Subsystem)
                .Concat(DetectorTerms.GetValueOrDefault(a.Detector, [])));
        return new EvidencePack(incident.Id, incident.StreamId, incident.Anomalies, windows, runbooks.Search(terms, topSections));
    }

    private ChannelWindow BuildWindow(ChannelSpec spec, Incident incident, FrameHistory history)
    {
        double? ValueOf(TelemetryFrame f) => f.Readings.TryGetValue(spec.Name, out var v) ? v : null;
        static double R(double x) => Math.Round(x, 3);

        var first = incident.FirstSequence;
        var baseline = history.Range(first - baselineFrames, first - 1).Select(ValueOf).OfType<double>().ToList();
        var hasBaseline = baseline.Count > 1;
        double? mean = hasBaseline ? R(baseline.Average()) : null;
        double? std = hasBaseline ? R(Stats.StdDev(baseline, baseline.Average())) : null;

        var frames = history.Range(first - LeadInFrames, history.LatestSequence);
        var anomalySeqs = incident.Anomalies.Where(a => a.Channel == spec.Name).Select(a => a.Sequence).ToHashSet();
        var stride = Math.Max(1, (int)Math.Ceiling(frames.Count / (double)maxPointsPerChannel));
        var points = frames
            .Where((f, i) => i % stride == 0 || i == frames.Count - 1 || anomalySeqs.Contains(f.Sequence))
            .Select(f => new WindowPoint(f.Sequence, ValueOf(f)))
            .ToList();

        var values = frames.Select(ValueOf).OfType<double>().ToList();
        var last = frames.Count > 0 ? ValueOf(frames[^1]) : null;
        var returned = last is { } l && mean is { } m && Math.Abs(l - m) <= 4 * Math.Max(std ?? 0, spec.NoiseStd);

        return new ChannelWindow(
            $"win:{spec.Name}", spec.Name, spec.Subsystem, spec.Unit, mean, std,
            values.Count > 0 ? values.Min() : null, values.Count > 0 ? values.Max() : null, last,
            frames.Count(f => ValueOf(f) is null), returned, points);
    }
}
