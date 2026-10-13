using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Observability;

namespace TelemetryTriage.Core.Triage;

/// <summary>
/// Runs the configured provider, validates its report, and falls back to the deterministic report if
/// the provider fails or the report is rejected. An operator never sees an unvalidated report, and
/// every fallback is visible in the outcome status, the logs and the triage.validation.failures metric.
/// </summary>
public sealed class TriageService(
    ITriageProvider provider,
    RuleBasedTriage fallback,
    CitationValidator validator,
    ILogger<TriageService> logger)
{
    public async Task<TriageOutcome> TriageAsync(EvidencePack evidence, CancellationToken cancellationToken = default)
    {
        using var activity = TriageTelemetry.Source.StartActivity("triage.incident");
        activity?.SetTag("incident.id", evidence.IncidentId);
        activity?.SetTag("triage.provider", provider.Name);
        var started = Stopwatch.GetTimestamp();

        TriageAttempt attempt;
        try
        {
            attempt = await provider.TriageAsync(evidence, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Triage provider {Provider} threw for {IncidentId}", provider.Name, evidence.IncidentId);
            attempt = new TriageAttempt(null, provider.Name, FailureReason: $"provider exception: {ex.GetType().Name}");
        }

        var issues = new List<string>();
        var status = TriageStatus.Accepted;
        var report = attempt.Report;
        if (report is null)
        {
            status = TriageStatus.FailedUsedFallback;
            issues.Add(attempt.FailureReason ?? "provider returned no report");
        }
        else if (validator.Validate(report, evidence) is { IsValid: false } validation)
        {
            status = TriageStatus.RejectedUsedFallback;
            issues.AddRange(validation.Errors);
            TriageTelemetry.ValidationFailures.Add(1, new KeyValuePair<string, object?>("triage.provider", provider.Name));
        }

        if (status != TriageStatus.Accepted)
        {
            logger.LogWarning("Triage for {IncidentId} from {Provider} not used ({Status}): {Issues}",
                evidence.IncidentId, provider.Name, status, string.Join("; ", issues));
            report = fallback.Build(evidence);
        }

        var latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RecordMetrics(activity, attempt, status, latencyMs);
        return new TriageOutcome(report!, provider.Name, attempt.Model, status, issues, Math.Round(latencyMs, 2),
            attempt.InputTokens, attempt.OutputTokens, attempt.EstimatedCostUsd);
    }

    private void RecordMetrics(Activity? activity, TriageAttempt attempt, TriageStatus status, double latencyMs)
    {
        var providerTag = new KeyValuePair<string, object?>("triage.provider", provider.Name);
        var modelTag = new KeyValuePair<string, object?>("gen_ai.request.model", attempt.Model);
        TriageTelemetry.TriageDuration.Record(latencyMs, providerTag, new("triage.status", status.ToString()));
        TriageTelemetry.TokenUsage.Record(attempt.InputTokens, modelTag, new("gen_ai.token.type", "input"));
        TriageTelemetry.TokenUsage.Record(attempt.OutputTokens, modelTag, new("gen_ai.token.type", "output"));
        if (attempt.EstimatedCostUsd is { } cost)
        {
            TriageTelemetry.TriageCost.Record((double)cost, modelTag);
        }

        activity?.SetTag("triage.status", status.ToString());
        activity?.SetTag("gen_ai.request.model", attempt.Model);
        activity?.SetTag("gen_ai.usage.input_tokens", attempt.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", attempt.OutputTokens);
        activity?.SetTag("triage.cost_usd", attempt.EstimatedCostUsd);
    }
}
