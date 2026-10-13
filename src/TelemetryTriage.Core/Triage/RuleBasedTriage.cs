using System.Globalization;
using TelemetryTriage.Core.Incidents;
using TelemetryTriage.Core.Model;

namespace TelemetryTriage.Core.Triage;

/// <summary>
/// Offline, deterministic triage. It is the default provider, the fallback when the LLM output is
/// rejected, and the baseline the LLM has to beat. It only quotes numbers taken from the evidence,
/// so its reports always pass <see cref="CitationValidator"/> (asserted by the evals).
/// </summary>
public sealed class RuleBasedTriage : ITriageProvider
{
    public const string ModelName = "rule-based-v1";

    public string Name => "rules";

    public Task<TriageAttempt> TriageAsync(EvidencePack evidence, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TriageAttempt(Build(evidence), ModelName));

    public TriageReport Build(EvidencePack e)
    {
        var anomalies = e.Anomalies.OrderBy(a => a.Sequence).ToList();
        var lead = anomalies[0];
        var leadWindow = e.Windows.First(w => w.Channel == lead.Channel);
        var subsystems = anomalies.Select(a => a.Subsystem).Distinct().ToList();
        var channels = string.Join(", ", anomalies.Select(a => a.Channel).Distinct());
        var critical = subsystems.Any(ChannelCatalog.IsCritical);
        var recovered = e.Windows.All(w => w.ReturnedToBaseline);

        var (cause, severity) = anomalies switch
        {
            _ when anomalies.All(a => a.Detector is "stuck" or "missing") => (
                lead.Detector == "stuck"
                    ? $"{lead.Channel} froze at {F(lead.Value)} from sample {lead.Sequence}. A frozen value on a noisy sensor points to a sensor or telemetry-path fault rather than a physical change; {lead.Subsystem} is unobserved on this channel meanwhile."
                    : $"{lead.Channel} stopped arriving in telemetry frames at sample {lead.Sequence} (dropout). Likely a sensor, harness or encoding fault; {lead.Subsystem} is unobserved on this channel meanwhile.",
                Severity.Medium),
            _ when subsystems.Count > 1 => (
                $"Correlated excursion across {string.Join(", ", subsystems)}. {lead.Channel} moved first at sample {lead.Sequence} ({F(lead.Value)} {leadWindow.Unit} vs baseline {F(leadWindow.BaselineMean)}), followed by {string.Join(", ", anomalies.Select(a => a.Channel).Distinct().Skip(1))}. A shared upstream cause in {lead.Subsystem} is more likely than independent faults.",
                critical && !recovered ? Severity.Critical : Severity.High),
            _ when recovered && anomalies[^1].Sequence - lead.Sequence <= 10 => (
                $"Short transient on {channels} at sample {lead.Sequence} (value {F(lead.Value)} vs baseline {F(leadWindow.BaselineMean)}) that returned to baseline. Consistent with a single-event upset or measurement glitch.",
                Severity.Low),
            _ when lead.Detector == "ewma-drift" => (
                $"Gradual drift on {channels}: the EWMA was {F(lead.Score)} {leadWindow.Unit} from baseline {F(leadWindow.BaselineMean)} at sample {lead.Sequence} and has not recovered. Possible thermal trend or calibration drift.",
                critical ? Severity.High : Severity.Medium),
            _ => (
                $"Sustained excursion on {channels} in {lead.Subsystem} from sample {lead.Sequence} (latest {F(leadWindow.Last)} {leadWindow.Unit} vs baseline {F(leadWindow.BaselineMean)}).",
                critical ? Severity.High : Severity.Medium),
        };

        var sections = e.Runbooks.Take(2).ToList();
        var checks = sections
            .SelectMany(s => s.Bullets.Take(2).Select(b => new RecommendedCheck(b, [s.Id, lead.Id])))
            .ToList();
        var citations = anomalies.Select(a => a.Id)
            .Concat(e.Windows.Select(w => w.Id))
            .Concat(sections.Select(s => s.Id))
            .ToList();

        var summary = $"Detectors flagged {channels} ({string.Join(", ", subsystems)}) starting at sample {lead.Sequence}; first signal: {lead.Detector} on {lead.Channel}.";
        return new TriageReport(summary, cause, subsystems, severity, checks, citations);
    }

    /// <summary>Formats exactly as stored in the evidence (values are already rounded to 3 decimals).</summary>
    private static string F(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
}
