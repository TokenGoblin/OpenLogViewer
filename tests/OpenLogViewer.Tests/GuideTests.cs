using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The guide is prose, so most of it cannot be tested. What can be is that it is
/// all there and none of it is empty, which is the failure a hand-written help
/// page actually has — and that it still describes the application, which is the
/// failure it acquires later.
/// </summary>
public class GuideTests
{
    [Fact]
    public void ThereAreSections() => Assert.NotEmpty(Guide.Sections);

    [Fact]
    public void EverySectionHasATitleABlurbAndEntries() =>
        Assert.All(Guide.Sections, section =>
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Title));
            Assert.False(string.IsNullOrWhiteSpace(section.Blurb));
            Assert.NotEmpty(section.Entries);
        });

    [Fact]
    public void EveryEntryHasATitleAndSomethingToSay() =>
        Assert.All(Guide.AllEntries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Body));

            // Long enough to be an explanation rather than a label.
            Assert.True(entry.Body.Length > 40, $"\"{entry.Title}\" says almost nothing");
        });

    [Fact]
    public void SectionTitlesAreDistinct()
    {
        string[] titles = [.. Guide.Sections.Select(s => s.Title)];

        Assert.Equal(titles.Length, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EntryTitlesAreDistinctWithinASection() =>
        Assert.All(Guide.Sections, section =>
        {
            string[] titles = [.. section.Entries.Select(e => e.Title)];

            Assert.Equal(titles.Length, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });

    [Fact]
    public void NoEntryIsLeftWithAPlaceholder() =>
        Assert.All(Guide.AllEntries, entry =>
        {
            Assert.DoesNotContain("TODO", entry.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TBD", entry.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Lorem", entry.Body, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// Every feature this project claims should be findable by somebody who knows
    /// only what it is called. This is the test that fails when a feature is
    /// added and the guide is not.
    /// </summary>
    [Theory]
    [InlineData("scatter")]
    [InlineData("histogram")]
    [InlineData("VE Calibration")]
    [InlineData("wideband delay")]
    [InlineData("calculated channel")]
    [InlineData("preset")]
    [InlineData("filter")]
    [InlineData("export")]
    [InlineData("fault code")]
    [InlineData("calculator")]
    [InlineData("gauge")]
    [InlineData("burn")]
    [InlineData("OBD2")]
    [InlineData("Bluetooth")]
    [InlineData("Wi-Fi")]
    [InlineData("record")]
    [InlineData("axis breakpoint")]
    [InlineData("colour scheme")]
    [InlineData("fixed scale")]
    [InlineData("stacked")]
    [InlineData("shift-drag")]
    [InlineData("span")]
    public void EveryFeatureIsDescribedSomewhere(string feature) =>
        Assert.Contains(Guide.AllEntries, e => e.Matches(feature));

    [Theory]
    [InlineData("Ctrl+O")]
    [InlineData("Ctrl+F")]
    [InlineData("Ctrl+K")]
    public void TheShortcutsAreListed(string keys) =>
        Assert.Contains(Guide.AllEntries, e => e.Keys.Contains(keys, StringComparison.Ordinal));

    [Fact]
    public void SearchingMatchesOnTitleBodyAndKeys()
    {
        var entry = new GuideEntry("Open a log", "Drop a file onto the window.", "Ctrl+O");

        Assert.True(entry.Matches("open"));
        Assert.True(entry.Matches("OPEN"));      // and without regard to case
        Assert.True(entry.Matches("window"));
        Assert.True(entry.Matches("Ctrl+O"));
        Assert.False(entry.Matches("histogram"));
    }

    [Fact]
    public void AnEntryWithNoShortcutSaysSo()
    {
        Assert.False(new GuideEntry("A", "B").HasKeys);
        Assert.True(new GuideEntry("A", "B", "Ctrl+O").HasKeys);
    }

    [Fact]
    public void AllEntriesCoversEverySection() =>
        Assert.Equal(Guide.Sections.Sum(s => s.Entries.Count), Guide.AllEntries.Count());
}
