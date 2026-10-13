using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Simulation;
using TelemetryTriage.Core.Streaming;
using static TelemetryTriage.Tests.TestData;

namespace TelemetryTriage.Tests;

public class StreamingTests
{
    [Fact]
    public async Task In_memory_bus_preserves_order_completes_and_counts_acks()
    {
        await using var bus = new InMemoryTelemetryBus();
        for (var i = 0; i < 5; i++)
        {
            await bus.PublishAsync(Frame(i));
        }

        await bus.CompleteAsync();

        var seen = new List<long>();
        await foreach (var message in bus.ConsumeAsync())
        {
            seen.Add(message.Frame.Sequence);
            await message.AckAsync();
        }

        Assert.Equal([0L, 1, 2, 3, 4], seen);
        Assert.Equal(5, bus.AckedCount);
    }

    [Fact]
    public async Task Bounded_bus_applies_backpressure_when_full()
    {
        await using var bus = new InMemoryTelemetryBus(capacity: 2);
        Assert.True(bus.PublishAsync(Frame(0)).IsCompleted);
        Assert.True(bus.PublishAsync(Frame(1)).IsCompleted);

        var third = bus.PublishAsync(Frame(2)).AsTask();
        Assert.False(third.IsCompleted); // the producer waits instead of buffering without bound

        await using var reader = bus.ConsumeAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pipeline_drops_redelivered_frames_and_produces_the_same_incident()
    {
        // Simulate at-least-once redelivery: frames 290..310 arrive twice (e.g. after a consumer restart).
        var frames = TelemetryGenerator.Generate(ScenarioCatalog.Get("spike")).ToList();
        var withDuplicates = frames.Take(311).Concat(frames.Skip(290)).ToList();
        var store = new InMemoryIncidentStore();

        var summary = await Pipeline(store).RunAsync(AsMessages(withDuplicates));

        Assert.Equal(21, summary.DuplicatesDropped);
        Assert.Equal(600, summary.FramesProcessed);
        Assert.Equal("INC-SYNTH-01-000300", Assert.Single(store.List()).Incident.Id);
    }

    [Fact]
    public void Frame_json_round_trips_including_dropped_channels()
    {
        var frame = Frame(42, ("bus_voltage", 27.5));

        var back = TelemetryFrameJson.Deserialize(TelemetryFrameJson.Serialize(frame));

        Assert.Equal((frame.StreamId, frame.Sequence, frame.Timestamp), (back.StreamId, back.Sequence, back.Timestamp));
        Assert.Equal(27.5, back.Readings["bus_voltage"]);
        Assert.False(back.Readings.ContainsKey("link_snr"));
    }
}
