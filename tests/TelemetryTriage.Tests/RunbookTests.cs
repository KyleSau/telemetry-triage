using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Tests;

public class RunbookTests
{
    [Fact]
    public void Sections_are_split_on_h2_headings_with_stable_ids_and_bullets()
    {
        const string markdown = "# Title\nintro\n## Bus Undervoltage\ntext\n- Check A.\n- Check B.\n## Other\n- C";

        var sections = RunbookIndex.Parse("demo", markdown).ToList();

        Assert.Equal(["rb:demo#bus-undervoltage", "rb:demo#other"], sections.Select(s => s.Id));
        Assert.Equal(["Check A.", "Check B."], sections[0].Bullets);
    }

    [Theory]
    [InlineData("rb:eps-power#bus-undervoltage", "bus", "voltage", "power", "step")]
    [InlineData("rb:comms-link#link-snr-degradation", "link", "snr", "signal", "comms", "step")]
    [InlineData("rb:sensor-health#single-sample-spikes", "spike", "jump", "transient")]
    public void Keyword_search_ranks_the_relevant_section_first(string expected, params string[] terms)
    {
        Assert.Equal(expected, TestData.Runbooks.Search(terms, top: 1).Single().Id);
    }
}
