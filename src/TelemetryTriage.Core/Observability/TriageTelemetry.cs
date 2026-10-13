using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TelemetryTriage.Core.Observability;

/// <summary>
/// All traces and metrics come from one ActivitySource and one Meter, both from the BCL
/// (System.Diagnostics), so Core has no OpenTelemetry dependency. Hosts subscribe by name and pick
/// exporters (console by default, OTLP optional). Token metrics follow the OpenTelemetry GenAI
/// semantic conventions (gen_ai.*). Per-frame work is measured with histograms, not spans: a span per
/// 10 Hz frame would be noise; spans are reserved for the pipeline run and each triage.
/// </summary>
public static class TriageTelemetry
{
    public const string Name = "TelemetryTriage";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> FramesIngested =
        Meter.CreateCounter<long>("telemetry.frames.ingested", "{frame}", "Frames processed by the pipeline");

    public static readonly Counter<long> FramesDuplicate =
        Meter.CreateCounter<long>("telemetry.frames.duplicates", "{frame}", "Redelivered frames dropped by the idempotency check");

    public static readonly Histogram<double> IngestLag =
        Meter.CreateHistogram("telemetry.ingest.lag", "ms", "Wall-clock time from publish to processing",
            advice: Buckets(1, 5, 10, 25, 50, 100, 250, 500, 1000, 5000));

    public static readonly Histogram<double> DetectDuration =
        Meter.CreateHistogram("detection.duration", "ms", "Time to run all detectors on one frame",
            advice: Buckets(0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 5, 25));

    public static readonly Counter<long> Anomalies =
        Meter.CreateCounter<long>("detection.anomalies", "{anomaly}", "Anomaly episodes started, by detector and channel");

    public static readonly Counter<long> Incidents =
        Meter.CreateCounter<long>("incidents.triaged", "{incident}", "Incidents triaged, by severity and status");

    public static readonly Histogram<double> TriageDuration =
        Meter.CreateHistogram("triage.duration", "ms", "End-to-end triage latency including validation",
            advice: Buckets(1, 10, 50, 100, 500, 1000, 2500, 5000, 10000, 30000, 60000));

    public static readonly Counter<long> ValidationFailures =
        Meter.CreateCounter<long>("triage.validation.failures", "{report}", "Provider reports rejected by the citation validator");

    public static readonly Histogram<double> TriageCost =
        Meter.CreateHistogram("triage.cost", "USD", "Estimated LLM cost per triage",
            advice: Buckets(0, 0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1));

    public static readonly Histogram<long> TokenUsage =
        Meter.CreateHistogram("gen_ai.client.token.usage", "{token}", "LLM tokens per triage call",
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = [0, 256, 1024, 4096, 16384, 65536] });

    /// <summary>Explicit buckets (via the BCL advice API) so sub-millisecond and multi-second timings both resolve.</summary>
    private static InstrumentAdvice<double> Buckets(params double[] bounds) => new() { HistogramBucketBoundaries = bounds };
}
