using TelemetryTriage.Core.Detection;
using TelemetryTriage.Core.Simulation;
using static TelemetryTriage.Tests.TestData;

namespace TelemetryTriage.Tests;

public class DetectorTests
{
    /// <summary>Small deterministic wiggle (std about 0.014) so detectors have something noise-like.</summary>
    private static double Wiggle(long i) => 0.01 * ((i % 5) - 2);

    [Fact]
    public void ZScore_flags_a_spike_once_per_episode()
    {
        var frames = Enumerable.Range(0, 150).Select(i => Frame(i, ("attitude_rate", i == 120 ? 1.0 : Wiggle(i))));

        var anomalies = Feed(new ZScoreDetector(), frames);

        var a = Assert.Single(anomalies);
        Assert.Equal(("attitude_rate", 120L, "anom:attitude_rate:zscore:120"), (a.Channel, a.Sequence, a.Id));
        Assert.True(a.Score > 5);
    }

    [Fact]
    public void Rolling_zscore_misses_slow_drift_but_ewma_catches_it()
    {
        var frames = TelemetryGenerator.Generate(ScenarioCatalog.Get("drift")).ToList();

        var zscore = Feed(new ZScoreDetector(), frames).Where(a => a.Channel == "avionics_temp");
        var ewma = Feed(new EwmaDriftDetector(), frames).Where(a => a.Channel == "avionics_temp").ToList();

        Assert.Empty(zscore); // the rolling window moves with the ramp
        Assert.InRange(Assert.Single(ewma).Sequence, 300, 360);
    }

    [Fact]
    public void Ewma_ignores_a_single_spike_because_inputs_are_clipped()
    {
        var frames = TelemetryGenerator.Generate(ScenarioCatalog.Get("spike"));

        Assert.Empty(Feed(new EwmaDriftDetector(), frames));
    }

    [Fact]
    public void Rate_of_change_flags_physically_implausible_jumps()
    {
        // 28.0 -> 27.0 V in 0.1 s is 10 V/s, above the 5 V/s limit for bus_voltage.
        var anomalies = Feed(new RateOfChangeDetector(), [Frame(0, ("bus_voltage", 28.0)), Frame(1, ("bus_voltage", 27.0))]);

        Assert.Equal(-10, Assert.Single(anomalies).Score, precision: 6);
    }

    [Fact]
    public void Stuck_value_fires_after_ten_identical_repeats()
    {
        // 2409.5 first appears at sample 10; its tenth repeat is sample 20.
        var frames = Enumerable.Range(0, 30).Select(i => Frame(i, ("tank_pressure", i < 10 ? 2400 + i : 2409.5)));

        Assert.Equal(20, Assert.Single(Feed(new StuckValueDetector(), frames)).Sequence);
    }

    [Fact]
    public void Missing_data_fires_after_five_frames_and_rearms_after_recovery()
    {
        var present = new HashSet<long>(Enumerable.Range(0, 40).Select(i => (long)i).Except([5, 6, 7, 8, 9, 10, 30, 31, 32, 33, 34]));
        var frames = Enumerable.Range(0, 40).Select(i => present.Contains(i) ? Frame(i, ("battery_temp", 22.0)) : Frame(i));

        var anomalies = Feed(new MissingDataDetector(), frames).Where(a => a.Channel == "battery_temp").Select(a => a.Sequence);

        Assert.Equal([9L, 34L], anomalies);
    }

    [Fact]
    public void Nominal_false_alarm_rate_is_bounded()
    {
        // 100 runs x 600 frames x 6 channels of clean synthetic data (about 600k channel-samples).
        // Measured: 1 false alarm episode (seed 47). The bound documents the trade-off with sensitivity.
        var falseAlarms = Enumerable.Range(1, 100).Sum(seed =>
        {
            var detectors = DetectorSuite.CreateDefault();
            return TelemetryGenerator.Generate(ScenarioCatalog.Get("nominal") with { Seed = seed })
                .Sum(f => detectors.Sum(d => d.Observe(f).Count));
        });

        Assert.InRange(falseAlarms, 0, 2);
    }
}
