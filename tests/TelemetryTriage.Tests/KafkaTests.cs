using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Pipeline;
using TelemetryTriage.Core.Simulation;
using TelemetryTriage.Infrastructure.Kafka;

namespace TelemetryTriage.Tests;

/// <summary>Runs only when KAFKA_BOOTSTRAP_SERVERS is set (e.g. docker compose up kafka); otherwise reported as skipped.</summary>
public sealed class KafkaFactAttribute : FactAttribute
{
    public KafkaFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")))
        {
            Skip = "Set KAFKA_BOOTSTRAP_SERVERS to run Kafka integration tests.";
        }
    }
}

public class KafkaTests
{
    [Fact]
    public void Consumer_is_configured_for_at_least_once_and_producer_for_idempotent_writes()
    {
        var options = new KafkaBusOptions { BootstrapServers = "broker:9092", ConsumerGroup = "g" };

        var consumer = options.ToConsumerConfig();
        var producer = options.ToProducerConfig();

        Assert.False(consumer.EnableAutoOffsetStore); // offsets stored only after the pipeline acks
        Assert.True(consumer.EnableAutoCommit);       // ...and committed in the background
        Assert.Equal(AutoOffsetReset.Earliest, consumer.AutoOffsetReset);
        Assert.Equal(Acks.All, producer.Acks);
        Assert.True(producer.EnableIdempotence);
    }

    [Fact]
    public void Each_stream_gets_its_own_topic()
    {
        Assert.Equal("telemetry.synth-01", new KafkaBusOptions { StreamName = "SYNTH-01" }.Topic);
    }

    private static KafkaBusOptions LiveOptions(string stream) => new()
    {
        BootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")!,
        StreamName = stream,
        ConsumerGroup = $"tests-{stream}",
    };

    [KafkaFact]
    public async Task Scenario_flows_through_kafka_end_to_end()
    {
        var stream = $"it-{Guid.NewGuid():N}"[..11];
        await using var bus = new KafkaTelemetryBus(LiveOptions(stream), NullLogger<KafkaTelemetryBus>.Instance);
        var store = new InMemoryIncidentStore();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await ScenarioRunner.RunAsync(ScenarioCatalog.Get("correlated-fault"), bus, TestData.Pipeline(store), stream, timeout.Token);

        var incident = Assert.Single(store.List());
        Assert.Equal(Severity.Critical, incident.Triage.Report.Severity);
    }

    [KafkaFact]
    public async Task Unacknowledged_frames_are_redelivered_to_the_same_consumer_group()
    {
        var options = LiveOptions($"it-{Guid.NewGuid():N}"[..11]);
        await using (var producer = new KafkaTelemetryBus(options, NullLogger<KafkaTelemetryBus>.Instance))
        {
            foreach (var frame in TelemetryGenerator.Generate(ScenarioCatalog.Get("nominal")).Take(10))
            {
                await producer.PublishAsync(frame);
            }

            await producer.CompleteAsync();
        }

        async Task<List<long>> ConsumeAsync(int take, int ack)
        {
            await using var bus = new KafkaTelemetryBus(options, NullLogger<KafkaTelemetryBus>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var seen = new List<long>();
            await foreach (var message in bus.ConsumeAsync(timeout.Token))
            {
                seen.Add(message.Frame.Sequence);
                if (seen.Count <= ack)
                {
                    await message.AckAsync();
                }

                if (seen.Count == take)
                {
                    break; // leaving the loop closes the consumer, which commits stored offsets
                }
            }

            return seen;
        }

        Assert.Equal([0L, 1, 2, 3, 4, 5, 6], await ConsumeAsync(take: 7, ack: 5));
        Assert.Equal(5, (await ConsumeAsync(take: 1, ack: 0)).Single()); // frames 5 and 6 were never acked
    }
}
