using TelemetryTriage.Core.Pipeline;
using TelemetryTriage.Core.Simulation;
using TelemetryTriage.Core.Streaming;

namespace TelemetryTriage.Api;

public enum PipelineState { Starting, Running, Idle, Failed }

public sealed class PipelineHealth
{
    public volatile PipelineState State = PipelineState.Starting;
    public string? Error { get; set; }
}

/// <summary>
/// Runs the detection + triage pipeline inside the API process. With Pipeline:Scenario set (default)
/// it replays that synthetic scenario through the configured bus and then idles, serving results.
/// With no scenario it consumes the bus until shutdown (Kafka mode; publish with the CLI).
/// </summary>
public sealed class PipelineWorker(
    TriagePipeline pipeline,
    ITelemetryBus bus,
    IConfiguration config,
    PipelineHealth health,
    ILogger<PipelineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scenario = config["Pipeline:Scenario"];
        var streamId = config["Kafka:StreamName"] ?? TelemetryGenerator.DefaultStreamId;
        try
        {
            health.State = PipelineState.Running;
            var summary = string.IsNullOrWhiteSpace(scenario)
                ? await pipeline.RunAsync(bus.ConsumeAsync(stoppingToken), cancellationToken: stoppingToken)
                : await ScenarioRunner.RunAsync(ScenarioCatalog.Get(scenario), bus, pipeline, streamId, stoppingToken);
            logger.LogInformation("Pipeline finished: {Summary}", summary);
            health.State = PipelineState.Idle;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            health.Error = ex.Message;
            health.State = PipelineState.Failed;
            logger.LogError(ex, "Pipeline failed");
        }
    }
}
