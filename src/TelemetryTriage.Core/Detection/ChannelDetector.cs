using TelemetryTriage.Core.Model;

namespace TelemetryTriage.Core.Detection;

public interface IDetector
{
    string Name { get; }

    /// <summary>Feeds one frame; returns anomalies whose episode starts at this frame (usually none).</summary>
    IReadOnlyList<AnomalyEvent> Observe(TelemetryFrame frame);
}

/// <summary>Result of evaluating one channel on one frame.</summary>
public readonly record struct Verdict(bool IsAnomalous, double Score, double Threshold, string Explanation = "");

/// <summary>
/// Base class for per-channel, stateful detectors. Subclasses only decide "is this sample anomalous";
/// the base class turns that into episodes: one <see cref="AnomalyEvent"/> when an episode starts,
/// then silence until the channel has been normal for <see cref="RearmAfterSamples"/> samples.
/// </summary>
public abstract class ChannelDetector : IDetector
{
    public const int RearmAfterSamples = 10;
    private readonly Dictionary<string, (bool Active, int NormalStreak)> _episodes = new();

    public abstract string Name { get; }

    public IReadOnlyList<AnomalyEvent> Observe(TelemetryFrame frame)
    {
        List<AnomalyEvent>? found = null;
        foreach (var spec in ChannelCatalog.All)
        {
            double? value = frame.Readings.TryGetValue(spec.Name, out var v) ? v : null;
            if (Evaluate(spec, frame, value) is not { } verdict)
            {
                continue;
            }

            if (StartsEpisode(spec.Name, verdict.IsAnomalous))
            {
                (found ??= []).Add(new AnomalyEvent(
                    AnomalyEvent.MakeId(spec.Name, Name, frame.Sequence),
                    frame.StreamId, spec.Name, spec.Subsystem, Name, frame.Sequence, frame.Timestamp,
                    value, Math.Round(verdict.Score, 3), Math.Round(verdict.Threshold, 3), verdict.Explanation));
            }
        }

        return found ?? (IReadOnlyList<AnomalyEvent>)[];
    }

    /// <summary>Returns null when the detector cannot judge this sample (warm-up, missing value...).</summary>
    protected abstract Verdict? Evaluate(ChannelSpec spec, TelemetryFrame frame, double? value);

    private bool StartsEpisode(string channel, bool anomalous)
    {
        var (active, normalStreak) = _episodes.GetValueOrDefault(channel);
        if (anomalous)
        {
            _episodes[channel] = (true, 0);
            return !active;
        }

        if (active)
        {
            normalStreak++;
            _episodes[channel] = (normalStreak < RearmAfterSamples, normalStreak);
        }

        return false;
    }
}

public static class DetectorSuite
{
    /// <summary>The default detector set, in the order they run. Each call returns fresh state.</summary>
    public static IReadOnlyList<IDetector> CreateDefault() =>
    [
        new ZScoreDetector(),
        new EwmaDriftDetector(),
        new RateOfChangeDetector(),
        new StuckValueDetector(),
        new MissingDataDetector(),
    ];
}
