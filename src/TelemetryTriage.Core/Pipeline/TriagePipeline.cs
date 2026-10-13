using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TelemetryTriage.Core.Detection;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Observability;
using TelemetryTriage.Core.Simulation;
using TelemetryTriage.Core.Streaming;
using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Core.Pipeline;

public sealed class PipelineOptions
{
    /// <summary>Session gap for incident grouping (event time).</summary>
    public TimeSpan IncidentGap { get; set; } = TimeSpan.FromSeconds(10);

    public int HistoryFrames { get; set; } = 1200;

    /// <summary>Closed incidents waiting for triage. When full, ingestion waits (backpressure).</summary>
    public int TriageQueueCapacity { get; set; } = 16;

    /// <summary>Concurrent triage calls; caps LLM spend rate and rate-limit pressure.</summary>
    public int TriageConcurrency { get; set; } = 2;
}

public sealed record PipelineSummary(long FramesProcessed, long DuplicatesDropped, int Anomalies, int Incidents);

/// <summary>
/// One pipeline run handles one stream (one Kafka topic / partition key):
/// consume -> drop duplicates -> detect -> group -> build evidence -> [bounded queue] -> triage -> store.
/// The hot path (detection) never waits on the LLM; triage runs on separate workers behind a bounded
/// queue so a slow model slows ingestion gracefully instead of exhausting memory.
/// </summary>
public sealed class TriagePipeline(
    TriageService triage,
    EvidenceBuilder evidenceBuilder,
    IIncidentStore store,
    PipelineOptions options,
    ILogger<TriagePipeline> logger)
{
    public async Task<PipelineSummary> RunAsync(
        IAsyncEnumerable<BusMessage> messages,
        Func<TelemetryFrame, bool>? isLastFrame = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = TriageTelemetry.Source.StartActivity("pipeline.run");
        var detectors = DetectorSuite.CreateDefault();
        var grouper = new IncidentGrouper(options.IncidentGap);
        var history = new FrameHistory(options.HistoryFrames);
        var queue = Channel.CreateBounded<(Incident, EvidencePack)>(new BoundedChannelOptions(options.TriageQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        var workers = Enumerable.Range(0, options.TriageConcurrency)
            .Select(_ => Task.Run(() => TriageWorkerAsync(queue.Reader, cancellationToken), cancellationToken))
            .ToArray();

        string? streamId = null;
        long highWaterMark = -1, frames = 0, duplicates = 0;
        int anomalies = 0, incidents = 0;
        var lastEventTime = DateTimeOffset.MinValue;

        async Task EnqueueAsync(IEnumerable<Incident> closed)
        {
            foreach (var incident in closed)
            {
                incidents++;
                logger.LogInformation("Incident {IncidentId} closed: {Channels} ({Count} anomalies)",
                    incident.Id, string.Join(",", incident.Channels), incident.Anomalies.Count);
                await queue.Writer.WriteAsync((incident, evidenceBuilder.Build(incident, history)), cancellationToken);
            }
        }

        try
        {
            await foreach (var message in messages.WithCancellation(cancellationToken))
            {
                var frame = message.Frame;
                streamId ??= frame.StreamId;
                if (frame.StreamId != streamId)
                {
                    throw new InvalidOperationException($"One pipeline per stream: got {frame.StreamId}, expected {streamId}.");
                }

                // Idempotent consumer: Kafka preserves order per partition, so anything at or below the
                // high-water mark is a redelivery (e.g. after a rebalance before offsets were committed).
                if (frame.Sequence <= highWaterMark)
                {
                    duplicates++;
                    TriageTelemetry.FramesDuplicate.Add(1);
                    await message.AckAsync();
                    continue;
                }

                highWaterMark = frame.Sequence;
                lastEventTime = frame.Timestamp;
                TriageTelemetry.IngestLag.Record((DateTimeOffset.UtcNow - message.EnqueuedAt).TotalMilliseconds);

                var started = Stopwatch.GetTimestamp();
                history.Add(frame);
                foreach (var anomaly in detectors.SelectMany(d => d.Observe(frame)))
                {
                    anomalies++;
                    grouper.Add(anomaly);
                    TriageTelemetry.Anomalies.Add(1, new("detector", anomaly.Detector), new("channel", anomaly.Channel));
                    logger.LogInformation("Anomaly {AnomalyId}: {Explanation}", anomaly.Id, anomaly.Explanation);
                }

                TriageTelemetry.DetectDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await EnqueueAsync(grouper.CloseExpired(frame.Timestamp));

                // Ack after the frame's effects are handed off. With a durable store you would persist the
                // closed incident (outbox) before acking; with the in-memory store a crash loses state anyway.
                await message.AckAsync();
                frames++;
                TriageTelemetry.FramesIngested.Add(1);
                if (isLastFrame?.Invoke(frame) == true)
                {
                    break;
                }
            }

            await EnqueueAsync(grouper.CloseAll(lastEventTime));
        }
        finally
        {
            queue.Writer.TryComplete();
            await Task.WhenAll(workers);
        }

        activity?.SetTag("pipeline.frames", frames);
        activity?.SetTag("pipeline.incidents", incidents);
        return new PipelineSummary(frames, duplicates, anomalies, incidents);
    }

    private async Task TriageWorkerAsync(ChannelReader<(Incident, EvidencePack)> reader, CancellationToken cancellationToken)
    {
        await foreach (var (incident, evidence) in reader.ReadAllAsync(cancellationToken))
        {
            var outcome = await triage.TriageAsync(evidence, cancellationToken);
            store.Upsert(new TriagedIncident(incident, evidence, outcome));
            TriageTelemetry.Incidents.Add(1, new("severity", outcome.Report.Severity.ToString()), new("triage.status", outcome.Status.ToString()));
            logger.LogInformation("Triaged {IncidentId}: {Severity} via {Provider} ({Status}) in {LatencyMs} ms",
                incident.Id, outcome.Report.Severity, outcome.Provider, outcome.Status, outcome.LatencyMs);
        }
    }
}

/// <summary>Publishes a synthetic scenario to a bus and runs the pipeline over it until the last frame.</summary>
public static class ScenarioRunner
{
    public static async Task<PipelineSummary> RunAsync(
        Scenario scenario,
        ITelemetryBus bus,
        TriagePipeline pipeline,
        string streamId = TelemetryGenerator.DefaultStreamId,
        CancellationToken cancellationToken = default)
    {
        var frames = TelemetryGenerator.Generate(scenario, streamId).ToList();
        var lastSequence = frames[^1].Sequence;
        var publish = Task.Run(async () =>
        {
            foreach (var frame in frames)
            {
                await bus.PublishAsync(frame, cancellationToken);
            }

            await bus.CompleteAsync(cancellationToken);
        }, cancellationToken);

        var summary = await pipeline.RunAsync(bus.ConsumeAsync(cancellationToken), f => f.Sequence >= lastSequence, cancellationToken);
        await publish;
        return summary;
    }
}
