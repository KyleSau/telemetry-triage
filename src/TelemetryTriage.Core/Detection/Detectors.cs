using TelemetryTriage.Core.Model;
using static System.FormattableString;

namespace TelemetryTriage.Core.Detection;

/// <summary>
/// Rolling z-score against the last <c>window</c> normal samples. Good at sudden jumps and steps.
/// Anomalous samples are kept out of the window so a step does not "teach" the baseline the new
/// level. The std is floored at the channel's noise spec so a flat window cannot explode the score.
/// Blind spot: a slow ramp moves the window with it, which is why <see cref="EwmaDriftDetector"/> exists.
/// </summary>
public sealed class ZScoreDetector(int window = 100, int minSamples = 30, double threshold = 5.0) : ChannelDetector
{
    private readonly Dictionary<string, Queue<double>> _windows = new();

    public override string Name => "zscore";

    protected override Verdict? Evaluate(ChannelSpec spec, TelemetryFrame frame, double? value)
    {
        if (value is not { } x)
        {
            return null;
        }

        var w = _windows.TryGetValue(spec.Name, out var q) ? q : _windows[spec.Name] = new Queue<double>();
        if (w.Count < minSamples)
        {
            Push(w, x);
            return null;
        }

        var mean = w.Average();
        var std = Math.Max(Stats.StdDev(w, mean), spec.NoiseStd);
        var z = (x - mean) / std;
        var anomalous = Math.Abs(z) > threshold;
        if (!anomalous)
        {
            Push(w, x);
        }

        return new Verdict(anomalous, z, threshold, anomalous
            ? Invariant($"z = {z:F1} (|z| > {threshold}); rolling mean {mean:F3}, std {std:F3} over {w.Count} samples")
            : "");
    }

    private void Push(Queue<double> w, double x)
    {
        w.Enqueue(x);
        if (w.Count > window)
        {
            w.Dequeue();
        }
    }
}

/// <summary>
/// EWMA control chart against a baseline frozen after warm-up. Catches slow drift that a rolling
/// window absorbs. Inputs are clipped to baseline +/- 4 sigma (winsorised) so a single spike cannot
/// masquerade as drift, while a sustained shift still pulls the average past the limit.
/// Limit = k * sigma * sqrt(alpha / (2 - alpha) + 1 / warmup): the steady-state std of an EWMA of white
/// noise plus the standard error of the baseline mean itself (without that term, false alarms appear).
/// </summary>
public sealed class EwmaDriftDetector(int warmup = 100, double alpha = 0.05, double k = 5.0, double clipSigma = 4.0)
    : ChannelDetector
{
    private sealed class State
    {
        public List<double> Warmup { get; } = [];
        public double Mean, Std, Ewma;
        public bool Ready;
    }

    private readonly Dictionary<string, State> _states = new();

    public override string Name => "ewma-drift";

    protected override Verdict? Evaluate(ChannelSpec spec, TelemetryFrame frame, double? value)
    {
        if (value is not { } x)
        {
            return null;
        }

        var s = _states.TryGetValue(spec.Name, out var st) ? st : _states[spec.Name] = new State();
        if (!s.Ready)
        {
            s.Warmup.Add(x);
            if (s.Warmup.Count == warmup)
            {
                s.Mean = s.Warmup.Average();
                s.Std = Math.Max(Stats.StdDev(s.Warmup, s.Mean), spec.NoiseStd);
                s.Ewma = s.Mean;
                s.Ready = true;
            }

            return null;
        }

        var clipped = Math.Clamp(x, s.Mean - clipSigma * s.Std, s.Mean + clipSigma * s.Std);
        s.Ewma = alpha * clipped + (1 - alpha) * s.Ewma;
        var limit = k * s.Std * Math.Sqrt(alpha / (2 - alpha) + 1.0 / warmup);
        var deviation = s.Ewma - s.Mean;
        var anomalous = Math.Abs(deviation) > limit;
        return new Verdict(anomalous, deviation, limit, anomalous
            ? Invariant($"EWMA {s.Ewma:F3} is {deviation:F3} {spec.Unit} from baseline {s.Mean:F3} (limit {limit:F3})")
            : "");
    }
}

/// <summary>Flags physically implausible jumps: |delta value / delta t| above the channel's max rate.</summary>
public sealed class RateOfChangeDetector : ChannelDetector
{
    private readonly Dictionary<string, (double Value, DateTimeOffset At)> _last = new();

    public override string Name => "rate";

    protected override Verdict? Evaluate(ChannelSpec spec, TelemetryFrame frame, double? value)
    {
        if (value is not { } x)
        {
            return null;
        }

        var hadPrevious = _last.TryGetValue(spec.Name, out var prev);
        _last[spec.Name] = (x, frame.Timestamp);
        var dt = (frame.Timestamp - prev.At).TotalSeconds;
        if (!hadPrevious || dt <= 0)
        {
            return null;
        }

        var rate = (x - prev.Value) / dt;
        var anomalous = Math.Abs(rate) > spec.MaxRatePerSecond;
        return new Verdict(anomalous, rate, spec.MaxRatePerSecond, anomalous
            ? Invariant($"rate {rate:F2} {spec.Unit}/s exceeds the {spec.MaxRatePerSecond} {spec.Unit}/s plausibility limit")
            : "");
    }
}

/// <summary>A noisy sensor never repeats a 3-decimal value N times in a row; a frozen one does.</summary>
public sealed class StuckValueDetector(int minRepeats = 10) : ChannelDetector
{
    private readonly Dictionary<string, (double Value, int Repeats)> _last = new();

    public override string Name => "stuck";

    protected override Verdict? Evaluate(ChannelSpec spec, TelemetryFrame frame, double? value)
    {
        if (value is not { } x)
        {
            return null;
        }

        var repeats = _last.TryGetValue(spec.Name, out var prev) && Math.Abs(prev.Value - x) < 1e-9 ? prev.Repeats + 1 : 0;
        _last[spec.Name] = (x, repeats);
        var anomalous = repeats >= minRepeats;
        return new Verdict(anomalous, repeats, minRepeats, anomalous
            ? Invariant($"value {x} repeated {repeats} times in a row; expected noise std {spec.NoiseStd} {spec.Unit}")
            : "");
    }
}

/// <summary>Flags a channel absent from N consecutive frames (sensor or telemetry-path dropout).</summary>
public sealed class MissingDataDetector(int minMissingFrames = 5) : ChannelDetector
{
    private readonly Dictionary<string, int> _missing = new();

    public override string Name => "missing";

    protected override Verdict? Evaluate(ChannelSpec spec, TelemetryFrame frame, double? value)
    {
        var missing = value is null ? _missing.GetValueOrDefault(spec.Name) + 1 : 0;
        _missing[spec.Name] = missing;
        var anomalous = missing >= minMissingFrames;
        return new Verdict(anomalous, missing, minMissingFrames, anomalous
            ? Invariant($"channel absent from {missing} consecutive frames")
            : "");
    }
}
