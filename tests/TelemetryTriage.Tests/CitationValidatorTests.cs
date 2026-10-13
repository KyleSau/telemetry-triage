using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;
using TelemetryTriage.Core.Simulation;
using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Tests;

public class CitationValidatorTests
{
    private const string AnomalyId = "anom:bus_voltage:zscore:300";
    private const string SectionId = "rb:eps-power#bus-undervoltage";

    private static readonly EvidencePack Evidence = new(
        "INC-T-000300",
        "T",
        [new AnomalyEvent(AnomalyId, "T", "bus_voltage", "EPS", "zscore", 300, TelemetryGenerator.Epoch, 24.812, -18.962, 5, "z = -19.0")],
        [new ChannelWindow("win:bus_voltage", "bus_voltage", "EPS", "V", 28.003, 0.05, 24.7, 28.1, 24.812, 0, false, [new(300, 24.812)])],
        [new RunbookSection(SectionId, "eps-power", "Bus undervoltage", "Below 26.5 V is undervoltage.\n- Check recent load commands.")]);

    private static TriageReport Report(
        string cause = "Load fault: bus at 24.8 V vs baseline 28.003 V, below the 26.5 V limit (z = -19.0).",
        string[]? subsystems = null,
        string[]? citations = null,
        RecommendedCheck[]? checks = null) =>
        new("Bus voltage stepped down at sample 300 on SYNTH-01.", cause, subsystems ?? ["EPS"], Severity.High,
            checks ?? [new RecommendedCheck("Check recent load commands.", [SectionId])],
            citations ?? [AnomalyId, "win:bus_voltage"]);

    private static ValidationResult Validate(TriageReport report) => new CitationValidator().Validate(report, Evidence);

    [Fact]
    public void Grounded_report_passes_including_numbers_rounded_to_the_precision_written()
    {
        var result = Validate(Report());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Citation_to_an_id_that_does_not_exist_is_rejected()
    {
        var result = Validate(Report(citations: [AnomalyId, "anom:bus_voltage:zscore:999"]));

        Assert.Contains("citation 'anom:bus_voltage:zscore:999' does not exist in the evidence", result.Errors);
    }

    [Theory]
    [InlineData("The bus dropped by 3.2 V.", "3.2")] // derived number: not in the evidence
    [InlineData("The bus fell to 24.9 V.", "24.9")] // 24.812 does not round to 24.9
    [InlineData("Current spiked to 140 A.", "140")] // invented value
    public void Numbers_not_present_in_the_evidence_are_rejected(string cause, string number)
    {
        var result = Validate(Report(cause: cause));

        Assert.Contains($"number {number} in likely_cause does not appear in the evidence", result.Errors);
    }

    [Fact]
    public void Subsystems_checks_and_anomaly_citations_must_be_grounded()
    {
        var result = Validate(Report(
            subsystems: ["EPS", "Propulsion"],
            citations: ["win:bus_voltage"],
            checks: [new RecommendedCheck("Reboot the flight computer.", [])]));

        Assert.Contains("subsystem 'Propulsion' is not in the evidence", result.Errors);
        Assert.Contains("report must cite at least one anomaly event", result.Errors);
        Assert.Contains("recommended check has no citation: 'Reboot the flight computer.'", result.Errors);
    }

    [Theory]
    [InlineData("see anom:bus_voltage:zscore:300 and INC-T-000300", 0)]
    [InlineData("value -18.962 at t=24.812", 2)]
    [InlineData("about 3 checks", 1)]
    public void Number_extraction_skips_identifiers(string text, int expected)
    {
        Assert.Equal(expected, CitationValidator.ExtractNumbers(text).Count());
    }
}
