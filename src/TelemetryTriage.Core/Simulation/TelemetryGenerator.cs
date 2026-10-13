using TelemetryTriage.Core.Model;

namespace TelemetryTriage.Core.Simulation;

/// <summary>
/// Deterministic synthetic telemetry: nominal value + Gaussian noise, quantised to 3 decimals, with
/// faults applied on top. Noise is drawn for every channel on every frame (even during a dropout) so
/// that a fault scenario and the nominal scenario with the same seed share identical noise.
/// </summary>
public static class TelemetryGenerator
{
    public const string DefaultStreamId = "SYNTH-01";
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan SamplePeriod = TimeSpan.FromMilliseconds(100);

    public static IEnumerable<TelemetryFrame> Generate(Scenario scenario, string streamId = DefaultStreamId)
    {
        var rng = new Random(scenario.Seed);
        var lastValue = new Dictionary<string, double>();

        for (long seq = 0; seq < scenario.Frames; seq++)
        {
            var readings = new Dictionary<string, double>();
            foreach (var spec in ChannelCatalog.All)
            {
                var value = spec.Nominal + spec.NoiseStd * NextGaussian(rng);
                var dropped = false;

                foreach (var fault in scenario.Faults.Where(f => f.Channel == spec.Name && f.IsActive(seq)))
                {
                    var elapsed = seq - fault.StartSequence;
                    switch (fault.Kind)
                    {
                        case FaultKind.Dropout: dropped = true; break;
                        case FaultKind.Stuck: value = lastValue.GetValueOrDefault(spec.Name, value); break;
                        case FaultKind.Drift: value += fault.Magnitude * elapsed; break;
                        case FaultKind.Spike: value += fault.Magnitude; break;
                        case FaultKind.Step: value += fault.Magnitude * Math.Min(1.0, (elapsed + 1) / 3.0); break;
                    }
                }

                value = Math.Round(value, 3);
                lastValue[spec.Name] = value;
                if (!dropped)
                {
                    readings[spec.Name] = value;
                }
            }

            yield return new TelemetryFrame(streamId, seq, Epoch + SamplePeriod * seq, readings);
        }
    }

    /// <summary>Box-Muller transform: two uniforms to one standard normal sample.</summary>
    private static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
