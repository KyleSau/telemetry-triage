using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TelemetryTriage.Tests;

/// <summary>Boots the real API in memory; its PipelineWorker replays the correlated-fault scenario on startup.</summary>
public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Observability:Exporter", "none");
        b.UseSetting("Pipeline:Scenario", "correlated-fault");
    });

    private async Task<JsonArray> WaitForIncidentsAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var incidents = await client.GetFromJsonAsync<JsonArray>("/incidents");
            if (incidents is { Count: > 0 })
            {
                return incidents;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The pipeline produced no incidents within 10 s.");
    }

    [Fact]
    public async Task Incidents_endpoint_lists_the_triaged_incident()
    {
        var client = _factory.CreateClient();

        var incident = Assert.Single(await WaitForIncidentsAsync(client))!;

        Assert.Equal("INC-SYNTH-01-000300", (string?)incident["id"]);
        Assert.Equal("Critical", (string?)incident["severity"]);
        Assert.Equal("Accepted", (string?)incident["triageStatus"]);
    }

    [Fact]
    public async Task Incident_detail_includes_evidence_and_a_cited_triage()
    {
        var client = _factory.CreateClient();
        await WaitForIncidentsAsync(client);

        var detail = (await client.GetFromJsonAsync<JsonObject>("/incidents/INC-SYNTH-01-000300"))!;

        Assert.NotEmpty(detail["triage"]!["report"]!["citations"]!.AsArray());
        Assert.NotEmpty(detail["evidence"]!["anomalies"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/incidents/INC-NOPE")).StatusCode);
    }

    [Fact]
    public async Task Severity_filter_and_health_endpoint_work()
    {
        var client = _factory.CreateClient();
        await WaitForIncidentsAsync(client);

        Assert.Single((await client.GetFromJsonAsync<JsonArray>("/incidents?minSeverity=Critical"))!);
        var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
    }
}
