using Microsoft.Extensions.Logging.Abstractions;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Pipeline;
using TelemetryTriage.Core.Simulation;
using TelemetryTriage.Core.Streaming;
using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Tests;

internal static class TestData
{
    public static readonly RunbookIndex Runbooks = RunbookIndex.Load();

    public static TelemetryFrame Frame(long seq, params (string Channel, double Value)[] readings) =>
        new("T", seq, TelemetryGenerator.Epoch + TelemetryGenerator.SamplePeriod * seq,
            readings.ToDictionary(r => r.Channel, r => r.Value));

    public static List<AnomalyEvent> Feed(TelemetryTriage.Core.Detection.IDetector detector, IEnumerable<TelemetryFrame> frames) =>
        frames.SelectMany(detector.Observe).ToList();

    public static TriagePipeline Pipeline(IIncidentStore store, ITriageProvider? provider = null) =>
        new(new TriageService(provider ?? new RuleBasedTriage(), new RuleBasedTriage(), new CitationValidator(), NullLogger<TriageService>.Instance),
            new EvidenceBuilder(Runbooks), store, new PipelineOptions(), NullLogger<TriagePipeline>.Instance);

    /// <summary>Runs a scenario through the real pipeline with the rule-based provider.</summary>
    public static async Task<IReadOnlyList<TriagedIncident>> RunAsync(string scenario)
    {
        var store = new InMemoryIncidentStore();
        await using var bus = new InMemoryTelemetryBus();
        await ScenarioRunner.RunAsync(ScenarioCatalog.Get(scenario), bus, Pipeline(store));
        return store.List();
    }

    public static async IAsyncEnumerable<BusMessage> AsMessages(IEnumerable<TelemetryFrame> frames)
    {
        foreach (var frame in frames)
        {
            await Task.Yield();
            yield return new BusMessage(frame, DateTimeOffset.UtcNow);
        }
    }
}
