using TelemetryTriage.Core.Evals;
using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Tests;

public class EvalTests
{
    [Fact]
    public async Task Golden_set_passes_offline_with_the_rule_based_provider()
    {
        var report = await new EvalRunner(new RuleBasedTriage(), TestData.Runbooks).RunAsync(GoldenSet.Load());

        var failures = report.Checks.Where(c => !c.Passed).Select(c => $"{c.Case}/{c.Check}: {c.Detail}");
        Assert.True(report.Passed, string.Join(Environment.NewLine, failures));
        Assert.Contains(report.Checks, c => c.Check == "grouping" && c.Case == "correlated-fault");
        Assert.Contains(report.Checks, c => c.Case == "ordering");
    }

    [Fact]
    public async Task Evals_fail_when_the_provider_cites_evidence_that_does_not_exist()
    {
        var report = await new EvalRunner(new HallucinatingProvider(), TestData.Runbooks).RunAsync(GoldenSet.Load());

        Assert.False(report.Passed);
        Assert.All(report.Checks.Where(c => c.Check.StartsWith("citations:", StringComparison.Ordinal)), c => Assert.False(c.Passed));
    }

    /// <summary>A deliberately bad provider: proves the eval actually detects ungrounded citations.</summary>
    private sealed class HallucinatingProvider : ITriageProvider
    {
        public string Name => "hallucinating";

        public Task<TriageAttempt> TriageAsync(TelemetryTriage.Core.Incidents.EvidencePack evidence, CancellationToken cancellationToken = default)
        {
            var report = new RuleBasedTriage().Build(evidence) with { Citations = ["anom:made-up:zscore:1"] };
            return Task.FromResult(new TriageAttempt(report, "fake"));
        }
    }
}
