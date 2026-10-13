using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using TelemetryTriage.Core.Model;

namespace TelemetryTriage.Core.Streaming;

/// <summary>
/// A delivered frame plus its acknowledgement. Consumers call <see cref="AckAsync"/> only after the
/// frame has been fully processed; an unacknowledged frame may be delivered again (at-least-once).
/// </summary>
public sealed class BusMessage(TelemetryFrame frame, DateTimeOffset enqueuedAt, Func<ValueTask>? ack = null)
{
    public TelemetryFrame Frame { get; } = frame;

    /// <summary>Wall-clock publish time (Kafka: message timestamp); used for the ingest-lag histogram.</summary>
    public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;

    public ValueTask AckAsync() => ack?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>Transport abstraction for one telemetry stream. Implementations: in-memory (default) and Kafka.</summary>
public interface ITelemetryBus : IAsyncDisposable
{
    ValueTask PublishAsync(TelemetryFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Producer is done. In-memory: ends the consumer's stream. Kafka: flushes; the topic stays open.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<BusMessage> ConsumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// System.Threading.Channels implementation. The channel is bounded with FullMode.Wait, so a slow
/// consumer makes PublishAsync wait instead of growing memory without limit (backpressure).
/// </summary>
public sealed class InMemoryTelemetryBus(int capacity = 1024) : ITelemetryBus
{
    private readonly Channel<(TelemetryFrame Frame, DateTimeOffset At)> _channel =
        Channel.CreateBounded<(TelemetryFrame, DateTimeOffset)>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private long _acked;

    public long AckedCount => Interlocked.Read(ref _acked);

    public ValueTask PublishAsync(TelemetryFrame frame, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync((frame, DateTimeOffset.UtcNow), cancellationToken);

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<BusMessage> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var (frame, at) in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return new BusMessage(frame, at, () =>
            {
                Interlocked.Increment(ref _acked);
                return ValueTask.CompletedTask;
            });
        }
    }

    public ValueTask DisposeAsync() => CompleteAsync();
}

/// <summary>Wire format shared by every transport: UTF-8 JSON, camelCase.</summary>
public static class TelemetryFrameJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(TelemetryFrame frame) => JsonSerializer.SerializeToUtf8Bytes(frame, Options);

    public static TelemetryFrame Deserialize(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<TelemetryFrame>(utf8, Options) ?? throw new JsonException("Empty telemetry frame.");
}
