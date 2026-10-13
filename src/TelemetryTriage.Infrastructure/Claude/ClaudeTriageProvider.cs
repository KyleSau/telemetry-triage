using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Infrastructure.Claude;

public sealed class ModelPrice
{
    public decimal InputPerMTok { get; set; }
    public decimal OutputPerMTok { get; set; }
}

public sealed class ClaudeOptions
{
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high | max. Thinking is always adaptive on this model; effort is the cost/latency knob.</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Thinking tokens count toward this limit, so it is sized well above the JSON reply.</summary>
    public int MaxTokens { get; set; } = 16000;

    /// <summary>Opt in to server-side refusal fallback (fallbacks: "default", beta server-side-fallback-2026-07-01).</summary>
    public bool ServerSideFallback { get; set; } = true;

    public int MaxRetries { get; set; } = 2;

    /// <summary>USD per million tokens, keyed by the model that served the response. Configurable; verify against current pricing.</summary>
    public Dictionary<string, ModelPrice> Pricing { get; set; } = new()
    {
        ["claude-opus-5-5"] = new() { InputPerMTok = 4m, OutputPerMTok = 20m },
        ["claude-opus-5"] = new() { InputPerMTok = 5m, OutputPerMTok = 25m },
        ["claude-opus-4-8"] = new() { InputPerMTok = 5m, OutputPerMTok = 25m },
    };
}

/// <summary>
/// LLM triage via the official Anthropic C# SDK. Structured outputs constrain the reply to the
/// <see cref="TriageReport"/> schema; stop_reason is checked before content is used; SDK errors are
/// mapped to a failed attempt so <see cref="TriageService"/> can fall back to the rule-based report.
/// </summary>
public sealed class ClaudeTriageProvider(AnthropicClient client, ClaudeOptions options, ILogger<ClaudeTriageProvider> logger)
    : ITriageProvider
{
    internal const string SystemPrompt =
        """
        You triage anomalies in SYNTHETIC telemetry from a fictional demo vehicle (SYNTH-01). You receive one
        incident as an evidence pack in JSON: anomaly events from deterministic detectors, windows of channel
        samples with a pre-incident baseline, and runbook sections. Write a triage for a console operator.

        Rules:
        - Cite evidence only by the exact "id" values in the pack (anom:..., win:..., rb:...). Every recommended
          check needs at least one citation, and the top-level citations must include at least one anomaly id.
        - Only use numbers exactly as they appear in the evidence. Do not compute new numbers such as differences,
          percentages or rates; refer to the values themselves instead.
        - affected_subsystems must be subsystem names that appear in the evidence.
        - Severity: critical = excursions in more than one subsystem including EPS or Propulsion, not recovered;
          high = sustained excursion on an EPS or Propulsion channel; medium = single-subsystem drift or loss of
          observability (stuck or missing data); low = transient that returned to baseline.
        - Prefer the runbook's checks when they apply. If the evidence does not support a cause, say so.
        """;

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Name => "claude";

    public async Task<TriageAttempt> TriageAsync(EvidencePack evidence, CancellationToken cancellationToken = default)
    {
        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(BuildRequest(evidence), cancellationToken);
        }
        catch (AnthropicRateLimitException ex)
        {
            return Failed($"rate limited (after SDK retries): {ex.Message}");
        }
        catch (Anthropic5xxException ex)
        {
            return Failed($"server error: {ex.Message}");
        }
        catch (AnthropicIOException ex)
        {
            return Failed($"network error: {ex.Message}");
        }
        catch (AnthropicBadRequestException ex)
        {
            return Failed($"bad request (check model id and parameters): {ex.Message}");
        }
        catch (AnthropicApiException ex)
        {
            return Failed($"API error: {ex.Message}");
        }

        var model = response.Model.Raw();
        var (input, output) = (response.Usage.InputTokens, response.Usage.OutputTokens);
        var cost = EstimateCost(model, input, output);

        // Check stop_reason before reading content: a refusal can arrive with empty or partial content,
        // and max_tokens means the JSON is truncated.
        if (response.StopReason == "refusal")
        {
            var category = response.StopDetails?.Category?.Raw() ?? "unspecified";
            return new TriageAttempt(null, model, input, output, cost, $"refusal (category: {category})");
        }

        if (response.StopReason == "max_tokens")
        {
            return new TriageAttempt(null, model, input, output, cost, $"truncated at max_tokens={options.MaxTokens}");
        }

        var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        try
        {
            var report = JsonSerializer.Deserialize<TriageReport>(json, ReportJson);
            return new TriageAttempt(report, model, input, output, cost, report is null ? "empty JSON" : null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Claude returned unparseable triage JSON for {IncidentId}", evidence.IncidentId);
            return new TriageAttempt(null, model, input, output, cost, "unparseable JSON");
        }

        TriageAttempt Failed(string reason)
        {
            logger.LogWarning("Claude triage failed for {IncidentId}: {Reason}", evidence.IncidentId, reason);
            return new TriageAttempt(null, options.Model, EstimatedCostUsd: null, FailureReason: reason);
        }
    }

    internal MessageCreateParams BuildRequest(EvidencePack evidence) => new()
    {
        Model = options.Model,
        MaxTokens = options.MaxTokens,
        System = SystemPrompt,
        Messages =
        [
            new()
            {
                Role = Role.User,
                Content = $"Evidence pack for incident {evidence.IncidentId}:\n{evidence.ToJson()}\n\nReturn the triage JSON.",
            },
        ],
        Thinking = new BetaThinkingConfigAdaptive(),
        OutputConfig = new BetaOutputConfig
        {
            Effort = options.Effort.ToLowerInvariant() switch
            {
                "low" => Effort.Low,
                "medium" => Effort.Medium,
                "high" => Effort.High,
                "max" => Effort.Max,
                var other => throw new InvalidOperationException($"Unsupported Claude effort '{other}'."),
            },
            Format = new BetaJsonOutputFormat { Schema = TriageSchema.Value },
        },
        Betas = options.ServerSideFallback ? [AnthropicBeta.ServerSideFallback2026_07_01] : null,
        Fallbacks = options.ServerSideFallback ? (BetaFallbacksParam)new Default() : null,
    };

    private decimal? EstimateCost(string model, long inputTokens, long outputTokens) =>
        options.Pricing.TryGetValue(model, out var price)
            ? Math.Round((inputTokens * price.InputPerMTok + outputTokens * price.OutputPerMTok) / 1_000_000m, 6)
            : null;
}

/// <summary>JSON schema for structured outputs. Every object sets additionalProperties: false, as required.</summary>
internal static class TriageSchema
{
    public static readonly Dictionary<string, JsonElement> Value = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "likely_cause", "affected_subsystems", "severity", "recommended_checks", "citations"],
          "properties": {
            "summary": { "type": "string", "description": "One or two sentences on what happened." },
            "likely_cause": { "type": "string", "description": "Most likely cause, grounded in the cited evidence." },
            "affected_subsystems": { "type": "array", "items": { "type": "string" } },
            "severity": { "type": "string", "enum": ["low", "medium", "high", "critical"] },
            "recommended_checks": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["action", "citations"],
                "properties": {
                  "action": { "type": "string" },
                  "citations": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "citations": { "type": "array", "items": { "type": "string" }, "description": "Evidence ids used." }
          }
        }
        """)!;
}
