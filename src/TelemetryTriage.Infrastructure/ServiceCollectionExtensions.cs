using Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Observability;
using TelemetryTriage.Core.Pipeline;
using TelemetryTriage.Core.Streaming;
using TelemetryTriage.Core.Triage;
using TelemetryTriage.Infrastructure.Claude;
using TelemetryTriage.Infrastructure.Kafka;

namespace TelemetryTriage.Infrastructure;

/// <summary>Composition root shared by the API and the CLI. Configuration chooses the adapters.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Bus:Kind = inmemory (default) | kafka. Triage:Provider = rules (default) | claude.
    /// Sections: Pipeline, Kafka, Claude, Runbooks:Directory.
    /// </summary>
    public static IServiceCollection AddTelemetryTriage(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(config.GetSection("Pipeline").Get<PipelineOptions>() ?? new PipelineOptions());
        services.AddSingleton(_ => RunbookIndex.Load(config["Runbooks:Directory"]));
        services.AddSingleton(sp => new EvidenceBuilder(sp.GetRequiredService<RunbookIndex>()));
        services.AddSingleton<CitationValidator>();
        services.AddSingleton<RuleBasedTriage>();
        services.AddSingleton<TriageService>();
        services.AddSingleton<IIncidentStore, InMemoryIncidentStore>();
        services.AddTransient<TriagePipeline>();

        if (string.Equals(config["Triage:Provider"], "claude", StringComparison.OrdinalIgnoreCase))
        {
            var claude = config.GetSection("Claude").Get<ClaudeOptions>() ?? new ClaudeOptions();
            services.AddSingleton(claude);
            services.AddSingleton(_ => new AnthropicClient
            {
                ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                    ?? throw new InvalidOperationException("Triage:Provider=claude requires the ANTHROPIC_API_KEY environment variable."),
                MaxRetries = claude.MaxRetries,
            });
            services.AddSingleton<ITriageProvider, ClaudeTriageProvider>();
        }
        else
        {
            services.AddSingleton<ITriageProvider>(sp => sp.GetRequiredService<RuleBasedTriage>());
        }

        if (string.Equals(config["Bus:Kind"], "kafka", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(config.GetSection("Kafka").Get<KafkaBusOptions>() ?? new KafkaBusOptions());
            services.AddSingleton<ITelemetryBus, KafkaTelemetryBus>();
        }
        else
        {
            services.AddSingleton<ITelemetryBus>(_ => new InMemoryTelemetryBus(config.GetValue("Bus:Capacity", 1024)));
        }

        return services;
    }

    /// <summary>Observability:Exporter = console (default) | otlp | none. OTLP honours the standard OTEL_EXPORTER_OTLP_* variables.</summary>
    public static IServiceCollection AddTelemetryTriageObservability(this IServiceCollection services, IConfiguration config, string serviceName)
    {
        var exporter = (config["Observability:Exporter"] ?? "console").ToLowerInvariant();
        if (exporter == "none")
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t =>
            {
                t.AddSource(TriageTelemetry.Name);
                if (exporter == "otlp")
                {
                    t.AddOtlpExporter();
                }
                else
                {
                    t.AddConsoleExporter();
                }
            })
            .WithMetrics(m =>
            {
                m.AddMeter(TriageTelemetry.Name);
                if (exporter == "otlp")
                {
                    m.AddOtlpExporter();
                }
                else
                {
                    m.AddConsoleExporter();
                }
            });
        return services;
    }
}
