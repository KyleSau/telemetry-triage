using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TelemetryTriage.Core.Triage;

/// <summary>One "## " section of a runbook markdown file. Id format: rb:{file}#{heading-slug}.</summary>
public sealed record RunbookSection(string Id, string Runbook, string Heading, string Body)
{
    [JsonIgnore]
    public IReadOnlyList<string> Bullets =>
        Body.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..]).ToList();
}

/// <summary>
/// Deliberately simple retrieval: weighted keyword overlap. With a handful of runbooks this is
/// transparent, deterministic and debuggable; embeddings or BM25 would be the next step at scale.
/// </summary>
public sealed partial class RunbookIndex(IReadOnlyList<RunbookSection> sections)
{
    public IReadOnlyList<RunbookSection> Sections => sections;

    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "runbooks");

    public static RunbookIndex Load(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"Runbook directory not found: {dir}");
        }

        return new RunbookIndex(Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => Parse(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f))).ToList());
    }

    public static IEnumerable<RunbookSection> Parse(string runbook, string markdown)
    {
        string? heading = null;
        var body = new List<string>();
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n').Append("## <eof>"))
        {
            if (!line.StartsWith("## ", StringComparison.Ordinal))
            {
                body.Add(line);
                continue;
            }

            if (heading is not null)
            {
                yield return new RunbookSection($"rb:{runbook}#{Slug(heading)}", runbook, heading, string.Join('\n', body).Trim());
            }

            heading = line[3..].Trim();
            body.Clear();
        }
    }

    /// <summary>
    /// Score = 3 x (distinct query words in the heading) + (distinct query words in the body).
    /// Ties break by id so results are deterministic.
    /// </summary>
    public IReadOnlyList<RunbookSection> Search(IEnumerable<string> terms, int top = 3)
    {
        var query = terms.SelectMany(Words).ToHashSet();
        int Score(RunbookSection s) =>
            3 * Words(s.Heading).Distinct().Count(query.Contains) + Words(s.Body).Distinct().Count(query.Contains);
        return sections
            .Select(s => (Section: s, Score: Score(s)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Section.Id, StringComparer.Ordinal)
            .Take(top)
            .Select(x => x.Section)
            .ToList();
    }

    /// <summary>Lower-case words with a naive plural strip ("spikes" -> "spike") so queries match either form.</summary>
    private static IEnumerable<string> Words(string text) =>
        WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .Select(w => w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) ? w[..^1] : w);

    private static string Slug(string heading) => string.Join('-', WordRegex().Matches(heading.ToLowerInvariant()).Select(m => m.Value));

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordRegex();
}
