using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Simulation;

namespace TelemetryTriage.Tests;

public class GroupingTests
{
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(10);

    private static AnomalyEvent Anomaly(string channel, long seq) => new(
        AnomalyEvent.MakeId(channel, "zscore", seq), "T", channel, ChannelCatalog.Get(channel).Subsystem, "zscore",
        seq, At(seq), 1.0, 6.0, 5.0, "test");

    private static DateTimeOffset At(long seq) => TelemetryGenerator.Epoch + TelemetryGenerator.SamplePeriod * seq;

    [Fact]
    public void Anomalies_within_the_gap_form_one_incident_that_closes_after_the_gap()
    {
        var grouper = new IncidentGrouper(Gap);
        grouper.Add(Anomaly("bus_voltage", 300));
        grouper.Add(Anomaly("link_snr", 330)); // 3 s later

        Assert.Empty(grouper.CloseExpired(At(420))); // 9 s after the last anomaly: still open
        var incident = Assert.Single(grouper.CloseExpired(At(431)));

        Assert.Equal("INC-T-000300", incident.Id);
        Assert.Equal(["bus_voltage", "link_snr"], incident.Channels);
    }

    [Fact]
    public void Anomalies_further_apart_than_the_gap_form_separate_incidents()
    {
        var grouper = new IncidentGrouper(Gap);
        grouper.Add(Anomaly("bus_voltage", 100));
        var first = grouper.CloseExpired(At(250));
        grouper.Add(Anomaly("link_snr", 250));

        Assert.Equal("INC-T-000100", Assert.Single(first).Id);
        Assert.Equal("INC-T-000250", Assert.Single(grouper.CloseAll(At(260))).Id);
    }

    [Fact]
    public async Task Correlated_fault_channels_land_in_a_single_incident_with_a_stable_id()
    {
        var first = await TestData.RunAsync("correlated-fault");
        var second = await TestData.RunAsync("correlated-fault");

        var incident = Assert.Single(first);
        Assert.Equal(["bus_voltage", "link_snr", "battery_temp"], incident.Incident.Channels);
        Assert.Equal(incident.Incident.Id, Assert.Single(second).Incident.Id); // reprocessing is idempotent
    }
}
