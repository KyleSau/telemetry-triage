using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Pipeline;
using TelemetryTriage.Core.Simulation;
using TelemetryTriage.Core.Streaming;
using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Core.Evals;

public sealed record DetectionExpectation(string Channel, IReadOnlyList<string> Detectors, int WithinSamples);

public sealed record GoldenCase(
    string Scenario,
    int ExpectIncidents,
    IReadOnlyList<DetectionExpectation>? Detections = null,
    IReadOnlyList<string>? GroupedChannels = null,
    Severity? MinSeverity = null,
    Severity? MaxSeverity = null);

public sealed record SeverityOrder(string Higher, string Lower);

public sealed record GoldenSet(IReadOnlyList<GoldenCase> Cases, IReadOnlyList<SeverityOrder> SeverityOrder)
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "evals", "golden-set.json");

    public static GoldenSet Load(string? path = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        return JsonSerializer.Deserialize<GoldenSet>(File.ReadAllText(path ?? DefaultPath), options)
            ?? throw new InvalidDataException("Golden set is empty.");
    }
}

public sealed record EvalCheck(string Case, string Check, bool Passed, string Detail);

public sealed record EvalReport(IReadOnlyList<EvalCheck> Checks, string Provider, double TotalTriageMs, long InputTokens, long OutputTokens, decimal? CostUsd)
{
    public bool Passed => Checks.All(c => c.Passed);
}

/// <summary>
/// Runs every golden scenario through the real pipeline (in-memory bus) with the given provider and
/// checks properties rather than exact text: detection latency, incident count and grouping,
/// citation validity, and severity bounds and ordering. The same runner backs the xUnit eval test
/// and the CLI "evals" command (which exits non-zero on failure).
/// </summary>
public sealed class EvalRunner(ITriageProvider provider, RunbookIndex runbooks)
{
    private readonly CitationValidator _validator = new();

    public async Task<EvalReport> RunAsync(GoldenSet golden, CancellationToken cancellationToken = default)
    {
        var checks = new List<EvalCheck>();
        var severities = new Dictionary<string, Severity?>();
        var outcomes = new List<TriageOutcome>();

        foreach (var c in golden.Cases)
        {
            var scenario = ScenarioCatalog.Get(c.Scenario);
            var incidents = await RunScenarioAsync(scenario, cancellationToken);
            outcomes.AddRange(incidents.Select(i => i.Triage));
            var maxSeverity = incidents.Count == 0 ? (Severity?)null : incidents.Max(i => i.Triage.Report.Severity);
            severities[c.Scenario] = maxSeverity;

            void Check(string name, bool passed, string detail) => checks.Add(new EvalCheck(c.Scenario, name, passed, detail));

            Check("incident-count", incidents.Count == c.ExpectIncidents, $"expected {c.ExpectIncidents}, got {incidents.Count}");

            var anomalies = incidents.SelectMany(i => i.Incident.Anomalies).ToList();
            foreach (var d in c.Detections ?? [])
            {
                var start = scenario.Faults.Where(f => f.Channel == d.Channel).Min(f => f.StartSequence);
                var hit = anomalies
                    .Where(a => a.Channel == d.Channel && d.Detectors.Contains(a.Detector) && a.Sequence >= start)
                    .MinBy(a => a.Sequence);
                var latency = hit is null ? (long?)null : hit.Sequence - start;
                Check($"detect:{d.Channel}", latency <= d.WithinSamples,
                    hit is null ? $"no {string.Join("/", d.Detectors)} anomaly on {d.Channel}" : $"{hit.Detector} after {latency} samples (budget {d.WithinSamples})");
            }

            if (c.GroupedChannels is { Count: > 0 } grouped)
            {
                var holders = incidents.Where(i => i.Incident.Channels.Intersect(grouped).Any()).ToList();
                Check("grouping", holders.Count == 1 && grouped.All(holders[0].Incident.Channels.Contains),
                    $"{grouped.Count} fault channels spread over {holders.Count} incident(s)");
            }

            foreach (var i in incidents)
            {
                var validation = _validator.Validate(i.Triage.Report, i.Evidence);
                Check($"citations:{i.Incident.Id}", i.Triage.Status == TriageStatus.Accepted && validation.IsValid,
                    i.Triage.Status == TriageStatus.Accepted
                        ? (validation.IsValid ? "all citations and numbers grounded" : string.Join("; ", validation.Errors))
                        : $"{i.Triage.Status}: {string.Join("; ", i.Triage.Issues)}");
            }

            if (c.MinSeverity is { } min && c.MaxSeverity is { } max)
            {
                Check("severity", maxSeverity >= min && maxSeverity <= max, $"expected {min}..{max}, got {maxSeverity?.ToString() ?? "none"}");
            }
        }

        foreach (var order in golden.SeverityOrder)
        {
            var (hi, lo) = (severities.GetValueOrDefault(order.Higher), severities.GetValueOrDefault(order.Lower));
            checks.Add(new EvalCheck("ordering", $"{order.Higher}>{order.Lower}", hi > lo, $"{hi} vs {lo}"));
        }

        var cost = outcomes.Any(o => o.EstimatedCostUsd is null) ? null : outcomes.Sum(o => o.EstimatedCostUsd);
        return new EvalReport(checks, provider.Name, outcomes.Sum(o => o.LatencyMs),
            outcomes.Sum(o => o.InputTokens), outcomes.Sum(o => o.OutputTokens), cost);
    }

    private async Task<IReadOnlyList<TriagedIncident>> RunScenarioAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var store = new InMemoryIncidentStore();
        var service = new TriageService(provider, new RuleBasedTriage(), _validator, NullLogger<TriageService>.Instance);
        var pipeline = new TriagePipeline(service, new EvidenceBuilder(runbooks), store, new PipelineOptions(), NullLogger<TriagePipeline>.Instance);
        await using var bus = new InMemoryTelemetryBus();
        await ScenarioRunner.RunAsync(scenario, bus, pipeline, cancellationToken: cancellationToken);
        return store.List();
    }
}
