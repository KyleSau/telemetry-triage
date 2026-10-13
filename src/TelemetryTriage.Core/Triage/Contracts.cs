using System.Text.Json.Serialization;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;

namespace TelemetryTriage.Core.Triage;

public sealed record RecommendedCheck(string Action, IReadOnlyList<string> Citations);

/// <summary>The structured triage every provider must produce (same shape as the Claude JSON schema).</summary>
public sealed record TriageReport(
    string Summary,
    string LikelyCause,
    IReadOnlyList<string> AffectedSubsystems,
    Severity Severity,
    IReadOnlyList<RecommendedCheck> RecommendedChecks,
    IReadOnlyList<string> Citations);

/// <summary>What a provider returned, plus the cost accounting needed for observability.</summary>
public sealed record TriageAttempt(
    TriageReport? Report,
    string Model,
    long InputTokens = 0,
    long OutputTokens = 0,
    decimal? EstimatedCostUsd = 0,
    string? FailureReason = null);

public interface ITriageProvider
{
    string Name { get; }

    Task<TriageAttempt> TriageAsync(EvidencePack evidence, CancellationToken cancellationToken = default);
}

[JsonConverter(typeof(JsonStringEnumConverter<TriageStatus>))]
public enum TriageStatus
{
    /// <summary>The primary provider's report passed validation.</summary>
    Accepted,
    /// <summary>The primary provider's report failed validation; the rule-based report was used instead.</summary>
    RejectedUsedFallback,
    /// <summary>The primary provider failed (error, refusal, truncation); the rule-based report was used instead.</summary>
    FailedUsedFallback,
}

public sealed record TriageOutcome(
    TriageReport Report,
    string Provider,
    string Model,
    TriageStatus Status,
    IReadOnlyList<string> Issues,
    double LatencyMs,
    long InputTokens,
    long OutputTokens,
    decimal? EstimatedCostUsd);

public sealed record TriagedIncident(Incident Incident, EvidencePack Evidence, TriageOutcome Triage);
