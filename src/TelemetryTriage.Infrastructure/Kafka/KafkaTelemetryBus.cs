using System.Runtime.CompilerServices;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Streaming;

namespace TelemetryTriage.Infrastructure.Kafka;

public sealed class KafkaBusOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string TopicPrefix { get; set; } = "telemetry.";
    public string StreamName { get; set; } = "SYNTH-01";
    public string ConsumerGroup { get; set; } = "telemetry-triage";

    /// <summary>Topic per stream, e.g. telemetry.synth-01. Messages are keyed by stream id.</summary>
    public string Topic => TopicPrefix + StreamName.ToLowerInvariant();

    public ProducerConfig ToProducerConfig() => new()
    {
        BootstrapServers = BootstrapServers,
        Acks = Acks.All,            // wait for all in-sync replicas
        EnableIdempotence = true,   // broker de-duplicates producer retries; keeps per-partition order
    };

    public ConsumerConfig ToConsumerConfig() => new()
    {
        BootstrapServers = BootstrapServers,
        GroupId = ConsumerGroup,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        // At-least-once: offsets are *stored* only when the pipeline acks a processed frame, and the
        // background auto-commit only commits stored offsets. A crash re-delivers unacked frames.
        EnableAutoOffsetStore = false,
        EnableAutoCommit = true,
        AutoCommitIntervalMs = 1000,
    };
}

/// <summary>
/// Confluent.Kafka transport. Delivery is at-least-once; the pipeline makes redelivery harmless by
/// dropping sequence numbers it has already processed and by upserting incidents under deterministic ids.
/// </summary>
public sealed class KafkaTelemetryBus(KafkaBusOptions options, ILogger<KafkaTelemetryBus> logger) : ITelemetryBus
{
    private IProducer<string, byte[]>? _producer;
    private volatile Error? _deliveryError;
    private bool _topicReady;

    public async ValueTask PublishAsync(TelemetryFrame frame, CancellationToken cancellationToken = default)
    {
        await EnsureTopicAsync();
        ThrowIfDeliveryFailed();
        _producer ??= new ProducerBuilder<string, byte[]>(options.ToProducerConfig()).Build();
        // Produce() only enqueues: librdkafka batches messages (linger.ms) and reports each delivery on a
        // background thread. Awaiting ProduceAsync per frame would cost a broker round trip per message.
        // Ordering is still guaranteed per partition because the producer is idempotent.
        _producer.Produce(options.Topic, new Message<string, byte[]>
        {
            Key = frame.StreamId,
            Value = TelemetryFrameJson.Serialize(frame),
        }, report =>
        {
            if (report.Error.IsError)
            {
                _deliveryError ??= report.Error;
            }
        });
    }

    /// <summary>Blocks until every queued frame is acknowledged by the broker, then surfaces any delivery failure.</summary>
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        _producer?.Flush(cancellationToken);
        ThrowIfDeliveryFailed();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDeliveryFailed()
    {
        if (_deliveryError is { } error)
        {
            throw new KafkaException(error);
        }
    }

    public async IAsyncEnumerable<BusMessage> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureTopicAsync();
        using var consumer = new ConsumerBuilder<string, byte[]>(options.ToConsumerConfig()).Build();
        consumer.Subscribe(options.Topic);
        logger.LogInformation("Consuming {Topic} as group {Group}", options.Topic, options.ConsumerGroup);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Consume() blocks; run it off the caller's thread with a short timeout so cancellation is honoured.
                var result = await Task.Run(() => consumer.Consume(TimeSpan.FromMilliseconds(250)), cancellationToken);
                if (result?.Message is null)
                {
                    continue;
                }

                if (TryDeserialize(result) is not { } frame)
                {
                    consumer.StoreOffset(result); // skip poison message (a dead-letter topic is on the roadmap)
                    continue;
                }

                yield return new BusMessage(frame, result.Message.Timestamp.UtcDateTime, () =>
                {
                    consumer.StoreOffset(result);
                    return ValueTask.CompletedTask;
                });
            }
        }
        finally
        {
            consumer.Close(); // commits stored offsets and leaves the group cleanly
        }
    }

    public ValueTask DisposeAsync()
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
        return ValueTask.CompletedTask;
    }

    private TelemetryFrame? TryDeserialize(ConsumeResult<string, byte[]> result)
    {
        try
        {
            return TelemetryFrameJson.Deserialize(result.Message.Value);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Skipping undecodable message at {TopicPartitionOffset}", result.TopicPartitionOffset);
            return null;
        }
    }

    private async Task EnsureTopicAsync()
    {
        if (_topicReady)
        {
            return;
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();
        try
        {
            // One partition: all frames of a stream share a key anyway, and order matters for detection.
            await admin.CreateTopicsAsync([new TopicSpecification { Name = options.Topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
        }

        _topicReady = true;
    }
}
