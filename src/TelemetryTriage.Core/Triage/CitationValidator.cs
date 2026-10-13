using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TelemetryTriage.Core.Incidents;

namespace TelemetryTriage.Core.Triage;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Guardrail between any triage provider and the operator. A report is rejected if it:
/// cites an id that is not in the evidence pack, cites no anomaly, has a recommended check without a
/// citation, names a subsystem that is not in the evidence, or states a number that does not appear in
/// the evidence (matched to the precision written; small integers up to 10 are exempt as counts).
/// It cannot prove the reasoning is right; it proves the report only points at things that exist.
/// </summary>
public sealed partial class CitationValidator
{
    public ValidationResult Validate(TriageReport report, EvidencePack evidence)
    {
        var errors = new List<string>();
        var ids = evidence.EvidenceIds;
        var anomalyIds = evidence.Anomalies.Select(a => a.Id).ToHashSet();

        if (report.Citations.Count == 0)
        {
            errors.Add("report has no citations");
        }

        foreach (var id in report.Citations.Concat(report.RecommendedChecks.SelectMany(c => c.Citations)).Distinct())
        {
            if (!ids.Contains(id))
            {
                errors.Add($"citation '{id}' does not exist in the evidence");
            }
        }

        if (!report.Citations.Any(anomalyIds.Contains))
        {
            errors.Add("report must cite at least one anomaly event");
        }

        foreach (var check in report.RecommendedChecks.Where(c => c.Citations.Count == 0))
        {
            errors.Add($"recommended check has no citation: '{check.Action}'");
        }

        foreach (var subsystem in report.AffectedSubsystems.Where(s => !evidence.Subsystems.Contains(s)))
        {
            errors.Add($"subsystem '{subsystem}' is not in the evidence");
        }

        var grounded = EvidenceNumbers(evidence);
        var texts = new[] { ("summary", report.Summary), ("likely_cause", report.LikelyCause) }
            .Concat(report.RecommendedChecks.Select((c, i) => ($"recommended_checks[{i}]", c.Action)));
        foreach (var (field, text) in texts)
        {
            foreach (var token in ExtractNumbers(text).Where(t => !IsGrounded(t, grounded)))
            {
                errors.Add($"number {token} in {field} does not appear in the evidence");
            }
        }

        return new ValidationResult(errors.Count == 0, errors);
    }

    /// <summary>Every number the provider could have seen: JSON numbers plus numbers inside JSON strings.</summary>
    internal static List<double> EvidenceNumbers(EvidencePack evidence)
    {
        var numbers = new List<double>();
        using var doc = JsonDocument.Parse(evidence.ToJson());
        Walk(doc.RootElement);
        return numbers;

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        Walk(p.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
                case JsonValueKind.Number:
                    numbers.Add(e.GetDouble());
                    break;
                case JsonValueKind.String:
                    numbers.AddRange(ExtractNumbers(e.GetString()!).Select(Parse));
                    break;
            }
        }
    }

    /// <summary>Numbers that are not part of an identifier (e.g. the 300 in anom:x:zscore:300 or SYNTH-01).</summary>
    internal static IEnumerable<string> ExtractNumbers(string text) => NumberRegex().Matches(text).Select(m => m.Value);

    internal static bool IsGrounded(string token, IReadOnlyList<double> evidenceNumbers)
    {
        var value = Math.Abs(Parse(token));
        var dot = token.IndexOf('.');
        if (dot < 0 && value <= 10)
        {
            return true;
        }

        // "24.8" is grounded by 24.812 (rounding to the precision written), not by 24.9.
        var decimals = dot < 0 ? 0 : token.Length - dot - 1;
        var tolerance = 0.5 * Math.Pow(10, -decimals) + 1e-9;
        return evidenceNumbers.Any(e => Math.Abs(Math.Abs(e) - value) <= tolerance);
    }

    private static double Parse(string token) => double.Parse(token, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<![\w.:#\-])-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();
}
