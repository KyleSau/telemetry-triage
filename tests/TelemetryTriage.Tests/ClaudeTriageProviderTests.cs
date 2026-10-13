using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Observability;
using TelemetryTriage.Core.Triage;
using TelemetryTriage.Infrastructure.Claude;

namespace TelemetryTriage.Tests;

/// <summary>
/// Exercises the real Anthropic SDK request/response path against a fake HTTP handler. The response
/// bodies below are hand-written TEST FIXTURES shaped like the Messages API wire format; they are not
/// outputs captured from Claude.
/// </summary>
public class ClaudeTriageProviderTests
{
    private sealed class FakeAnthropicHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public JsonNode? RequestBody { get; private set; }
        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<EvidencePack> EvidenceAsync() => (await TestData.RunAsync("correlated-fault")).Single().Evidence;

    private static string Message(string stopReason, object[] content, string stopDetails = "null") =>
        $$"""
        {
          "id": "msg_fixture", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
          "content": {{JsonSerializer.Serialize(content)}},
          "stop_reason": "{{stopReason}}", "stop_sequence": null, "stop_details": {{stopDetails}},
          "usage": { "input_tokens": 3000, "output_tokens": 500, "cache_creation_input_tokens": null, "cache_read_input_tokens": null }
        }
        """;

    private static string TextReply(object report) => Message("end_turn", [new { type = "text", text = JsonSerializer.Serialize(report) }]);

    private static object GroundedReport(EvidencePack e) => new
    {
        summary = $"Bus voltage stepped down at sample {e.Anomalies[0].Sequence}.",
        likely_cause = $"Load fault on the EPS bus; voltage at {e.Anomalies[0].Value} V with link SNR degrading afterwards.",
        affected_subsystems = new[] { "EPS", "Comms" },
        severity = "critical",
        recommended_checks = new[] { new { action = e.Runbooks[0].Bullets[0], citations = new[] { e.Runbooks[0].Id } } },
        citations = new[] { e.Anomalies[0].Id, "win:bus_voltage", e.Runbooks[0].Id },
    };

    private static (ClaudeTriageProvider Provider, FakeAnthropicHandler Handler) Create(HttpStatusCode status, string body)
    {
        var handler = new FakeAnthropicHandler(status, body);
        var client = new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        return (new ClaudeTriageProvider(client, new ClaudeOptions(), NullLogger<ClaudeTriageProvider>.Instance), handler);
    }

    private static TriageService Service(ITriageProvider provider) =>
        new(provider, new RuleBasedTriage(), new CitationValidator(), NullLogger<TriageService>.Instance);

    [Fact]
    public async Task Request_uses_adaptive_thinking_explicit_effort_structured_output_and_server_side_fallback()
    {
        var evidence = await EvidenceAsync();
        var (provider, handler) = Create(HttpStatusCode.OK, TextReply(GroundedReport(evidence)));

        await provider.TriageAsync(evidence);

        var body = handler.RequestBody!;
        Assert.Equal("claude-opus-5-5", (string?)body["model"]);
        Assert.Equal("adaptive", (string?)body["thinking"]!["type"]);
        Assert.Equal("medium", (string?)body["output_config"]!["effort"]);
        Assert.Equal("json_schema", (string?)body["output_config"]!["format"]!["type"]);
        Assert.Equal("default", (string?)body["fallbacks"]);
        Assert.Contains("server-side-fallback-2026-07-01", handler.Request!.Headers.GetValues("anthropic-beta"));
        Assert.Null(body["tool_choice"]);
        Assert.DoesNotContain("budget_tokens", body.ToJsonString());
        var message = Assert.Single(body["messages"]!.AsArray()); // no assistant prefill
        Assert.Equal("user", (string?)message!["role"]);
    }

    [Fact]
    public async Task Grounded_reply_is_accepted_with_tokens_cost_and_metrics_recorded()
    {
        var evidence = await EvidenceAsync();
        var (provider, _) = Create(HttpStatusCode.OK, TextReply(GroundedReport(evidence)));
        var tokens = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == TriageTelemetry.Name && i.Name == "gen_ai.client.token.usage")
            {
                l.EnableMeasurementEvents(i);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(t => t.Key == "gen_ai.request.model" && (string?)t.Value == "claude-opus-5-5"))
            {
                tokens.Add(value);
            }
        });
        listener.Start();

        var outcome = await Service(provider).TriageAsync(evidence);

        Assert.Equal(TriageStatus.Accepted, outcome.Status);
        Assert.Equal(Severity.Critical, outcome.Report.Severity);
        Assert.Equal((3000L, 500L), (outcome.InputTokens, outcome.OutputTokens));
        Assert.Equal(0.022m, outcome.EstimatedCostUsd); // 3000 x $4/MTok + 500 x $20/MTok
        Assert.Contains(3000L, tokens);
        Assert.Contains(500L, tokens);
    }

    [Fact]
    public async Task Hallucinated_citation_and_invented_number_are_rejected_and_rules_are_used()
    {
        var evidence = await EvidenceAsync();
        var report = JsonSerializer.SerializeToNode(GroundedReport(evidence))!;
        report["likely_cause"] = "Bus dropped by 3.2 V because of a heater fault.";
        report["citations"] = new JsonArray("anom:bus_voltage:zscore:999");
        var (provider, _) = Create(HttpStatusCode.OK, TextReply(report));

        var outcome = await Service(provider).TriageAsync(evidence);

        Assert.Equal(TriageStatus.RejectedUsedFallback, outcome.Status);
        Assert.Contains("citation 'anom:bus_voltage:zscore:999' does not exist in the evidence", outcome.Issues);
        Assert.Contains("number 3.2 in likely_cause does not appear in the evidence", outcome.Issues);
        Assert.Equal(new RuleBasedTriage().Build(evidence).LikelyCause, outcome.Report.LikelyCause);
    }

    [Theory]
    [InlineData("refusal", """{"type": "refusal", "category": "cyber", "explanation": null}""", "refusal (category: cyber)")]
    [InlineData("max_tokens", "null", "truncated at max_tokens=16000")]
    public async Task Refusal_and_truncation_are_checked_before_content_and_fall_back(string stopReason, string details, string expected)
    {
        var evidence = await EvidenceAsync();
        var (provider, _) = Create(HttpStatusCode.OK, Message(stopReason, [], details));

        var outcome = await Service(provider).TriageAsync(evidence);

        Assert.Equal(TriageStatus.FailedUsedFallback, outcome.Status);
        Assert.Equal([expected], outcome.Issues);
        Assert.Equal(3000, outcome.InputTokens); // a refused call still costs input tokens
    }

    [Fact]
    public async Task Rate_limit_error_is_mapped_to_a_failed_attempt()
    {
        var evidence = await EvidenceAsync();
        var (provider, _) = Create(HttpStatusCode.TooManyRequests,
            """{"type": "error", "error": {"type": "rate_limit_error", "message": "fixture: slow down"}}""");

        var attempt = await provider.TriageAsync(evidence);

        Assert.Null(attempt.Report);
        Assert.StartsWith("rate limited (after SDK retries)", attempt.FailureReason);
    }
}
