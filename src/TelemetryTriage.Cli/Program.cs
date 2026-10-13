using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TelemetryTriage.Core.Evals;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Pipeline;
using TelemetryTriage.Core.Simulation;
using TelemetryTriage.Core.Streaming;
using TelemetryTriage.Core.Triage;
using TelemetryTriage.Infrastructure;

var command = args.FirstOrDefault() ?? "help";

try
{
    var options = ParseOptions(args.Skip(1).ToArray());
    return command switch
    {
        "run" => await RunAsync(options),
        "publish" => await PublishAsync(options),
        "evals" => await EvalsAsync(options),
        "scenarios" => ListScenarios(),
        _ => Help(),
    };
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

// run: publish a scenario to the bus and process it end to end (detect -> group -> triage).
static async Task<int> RunAsync(Dictionary<string, string> o)
{
    var scenario = ScenarioCatalog.Get(o.GetValueOrDefault("scenario", "correlated-fault"));
    var bus = o.GetValueOrDefault("bus", "inmemory");
    // Kafka runs get their own topic so a rerun never mixes with frames left over from a previous run.
    var stream = bus == "kafka" ? $"{TelemetryGenerator.DefaultStreamId}-{Guid.NewGuid():N}"[..17] : TelemetryGenerator.DefaultStreamId;
    using var host = BuildHost(o, new()
    {
        ["Bus:Kind"] = bus,
        ["Triage:Provider"] = o.GetValueOrDefault("triage", "rules"),
        ["Kafka:StreamName"] = stream,
    });
    // Resolve first so configuration errors (e.g. --triage claude without a key) fail before any work starts.
    var pipeline = host.Services.GetRequiredService<TriagePipeline>();
    var telemetryBus = host.Services.GetRequiredService<ITelemetryBus>();
    await host.StartAsync();

    Console.WriteLine($"Scenario '{scenario.Name}': {scenario.Description} (synthetic data, stream {stream}, bus {bus})");
    var summary = await ScenarioRunner.RunAsync(scenario, telemetryBus, pipeline, stream);
    var incidents = host.Services.GetRequiredService<IIncidentStore>().List();
    await Task.Delay(100); // let the async console logger drain before printing the report

    Console.WriteLine($"\nProcessed {summary.FramesProcessed} frames, {summary.Anomalies} anomalies, {summary.Incidents} incident(s), {summary.DuplicatesDropped} duplicates dropped.");
    foreach (var incident in incidents)
    {
        PrintIncident(incident);
    }

    if (o.TryGetValue("json", out var path))
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(incidents, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"Wrote {path}");
    }

    await host.StopAsync(); // disposing the host flushes the final metrics export
    return 0;
}

// publish: push a scenario's frames to Kafka for an API instance running with no Pipeline:Scenario.
static async Task<int> PublishAsync(Dictionary<string, string> o)
{
    var scenario = ScenarioCatalog.Get(o.GetValueOrDefault("scenario", "correlated-fault"));
    var stream = o.GetValueOrDefault("stream", TelemetryGenerator.DefaultStreamId);
    using var host = BuildHost(o, new() { ["Bus:Kind"] = "kafka", ["Kafka:StreamName"] = stream }, defaultOtel: "none");
    var bus = host.Services.GetRequiredService<ITelemetryBus>();
    foreach (var frame in TelemetryGenerator.Generate(scenario, stream))
    {
        await bus.PublishAsync(frame);
    }

    await bus.CompleteAsync();
    Console.WriteLine($"Published {scenario.Frames} frames of '{scenario.Name}' for stream {stream}.");
    return 0;
}

// evals: golden scenarios -> property checks. Offline by default; --live uses Claude (needs ANTHROPIC_API_KEY).
static async Task<int> EvalsAsync(Dictionary<string, string> o)
{
    var live = o.ContainsKey("live");
    if (live && Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is null)
    {
        Console.Error.WriteLine("evals --live requires ANTHROPIC_API_KEY; run without --live for the offline evals.");
        return 2;
    }

    using var host = BuildHost(o, new() { ["Triage:Provider"] = live ? "claude" : "rules" }, defaultOtel: "none");
    var runner = new EvalRunner(host.Services.GetRequiredService<ITriageProvider>(), host.Services.GetRequiredService<RunbookIndex>());
    var report = await runner.RunAsync(GoldenSet.Load(o.GetValueOrDefault("golden")));

    foreach (var check in report.Checks)
    {
        Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")}  {check.Case,-17} {check.Check,-36} {check.Detail}");
    }

    var passed = report.Checks.Count(c => c.Passed);
    Console.WriteLine($"\n{passed}/{report.Checks.Count} checks passed (provider: {report.Provider}). " +
        $"Triage latency total {report.TotalTriageMs:F1} ms, tokens in/out {report.InputTokens}/{report.OutputTokens}, " +
        $"estimated cost {(report.CostUsd is { } c ? $"${c:F4}" : "unknown")}.");
    return report.Passed ? 0 : 1;
}

static int ListScenarios()
{
    foreach (var s in ScenarioCatalog.All)
    {
        Console.WriteLine($"{s.Name,-17} {s.Description}");
    }

    return 0;
}

static int Help()
{
    Console.WriteLine("""
        telemetry-triage <command> [options]   (all data is synthetic)

          run        --scenario <name> [--bus inmemory|kafka] [--triage rules|claude] [--otel console|none] [--json <file>]
          publish    --scenario <name> [--stream <id>]          publish frames to Kafka only
          evals      [--live] [--golden <path>]                  golden-set evals; exit code 1 on failure
          scenarios                                              list scenarios
        """);
    return 0;
}

static void PrintIncident(TriagedIncident i)
{
    var t = i.Triage;
    Console.WriteLine($"\n=== {i.Incident.Id}  severity={t.Report.Severity}  provider={t.Provider}/{t.Model}  status={t.Status}  triage={t.LatencyMs} ms");
    Console.WriteLine($"Channels:     {string.Join(", ", i.Incident.Channels)}  (subsystems: {string.Join(", ", t.Report.AffectedSubsystems)})");
    Console.WriteLine($"Summary:      {t.Report.Summary}");
    Console.WriteLine($"Likely cause: {t.Report.LikelyCause}");
    Console.WriteLine("Recommended checks:");
    foreach (var check in t.Report.RecommendedChecks)
    {
        Console.WriteLine($"  - {check.Action}  [{string.Join(", ", check.Citations)}]");
    }

    Console.WriteLine($"Citations:    {string.Join(", ", t.Report.Citations)}");
    foreach (var issue in t.Issues)
    {
        Console.WriteLine($"Issue:        {issue}");
    }
}

static IHost BuildHost(Dictionary<string, string> o, Dictionary<string, string?> settings, string defaultOtel = "console")
{
    var builder = Host.CreateApplicationBuilder();
    settings["Observability:Exporter"] = o.GetValueOrDefault("otel", defaultOtel);
    builder.Configuration.AddInMemoryCollection(settings);
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(c =>
    {
        c.SingleLine = true;
        c.TimestampFormat = "HH:mm:ss.fff ";
    });
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    builder.Services.AddTelemetryTriage(builder.Configuration);
    builder.Services.AddTelemetryTriageObservability(builder.Configuration, "telemetry-triage-cli");
    return builder.Build();
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }

        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        result[args[i][2..]] = hasValue ? args[++i] : "true";
    }

    return result;
}
