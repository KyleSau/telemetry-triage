using Microsoft.Extensions.Diagnostics.HealthChecks;
using TelemetryTriage.Api;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTelemetryTriage(builder.Configuration);
builder.Services.AddTelemetryTriageObservability(builder.Configuration, "telemetry-triage-api");
builder.Services.AddSingleton<PipelineHealth>();
builder.Services.AddHostedService<PipelineWorker>();
builder.Services.AddHealthChecks().AddCheck<PipelineHealthCheck>("pipeline");

var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/incidents"));

app.MapGet("/incidents", (IIncidentStore store, Severity? minSeverity) =>
    store.List()
        .Where(i => minSeverity is null || i.Triage.Report.Severity >= minSeverity)
        .Select(i => new IncidentSummary(
            i.Incident.Id, i.Incident.StreamId, i.Incident.OpenedAt, i.Triage.Report.Severity, i.Triage.Status.ToString(),
            i.Incident.Channels, i.Incident.Subsystems, i.Triage.Report.Summary, i.Triage.Provider)));

app.MapGet("/incidents/{id}", (string id, IIncidentStore store) =>
    store.Get(id) is { } incident ? Results.Ok(incident) : Results.NotFound());

app.MapHealthChecks("/healthz");

app.Run();

/// <summary>Exposed for WebApplicationFactory in the tests.</summary>
public partial class Program;

namespace TelemetryTriage.Api
{
    public sealed record IncidentSummary(
        string Id,
        string StreamId,
        DateTimeOffset OpenedAt,
        Severity Severity,
        string TriageStatus,
        IReadOnlyList<string> Channels,
        IReadOnlyList<string> Subsystems,
        string Summary,
        string Provider);

    public sealed class PipelineHealthCheck(PipelineHealth health) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(health.State == PipelineState.Failed
                ? HealthCheckResult.Unhealthy(health.Error)
                : HealthCheckResult.Healthy(health.State.ToString()));
    }
}
