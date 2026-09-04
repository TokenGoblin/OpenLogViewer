using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The defaults the documentation states, against the defaults the software has.
///
/// <para>
/// This exists because of a tooltip. "Record as soon as I connect" told people it
/// was on by default for as long as it was off by default, which is the worst
/// shape this kind of error takes: everything else in the product said the right
/// thing, so there was nothing to notice, and the one place a person actually
/// reads before deciding whether their run is being captured was the place that
/// lied.
/// </para>
/// <para>
/// A documented default is a promise, and nothing else in the build checks one.
/// A test can, because both halves are readable: the number is in the markdown
/// and the value is in the code. Where a default can be observed rather than
/// declared — the settings file especially — it is read off a real instance, so
/// this measures behaviour rather than agreeing with a constant that has itself
/// drifted.
/// </para>
/// </summary>
public class DocumentedDefaultsTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenLogViewer.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    /// <summary>A file at the top of the repository, such as the README.</summary>
    private static string RootDoc(string name)
    {
        string path = Path.Combine(RepositoryRoot(), name);

        Assert.True(File.Exists(path), $"{name} is missing");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Bold marks a default in a table and says nothing about the value, so it
    /// is taken off before a number is looked for.
    /// </summary>
    private static string Unemphasised(string text) =>
        text.Replace("**", "", StringComparison.Ordinal);

    private static string Doc(string name)
    {
        string path = Path.Combine(RepositoryRoot(), "docs", name);

        Assert.True(File.Exists(path), $"docs/{name} is missing");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// A settings store with nothing behind it, which is what a new install has.
    /// </summary>
    private static SettingsStore FreshSettings()
    {
        string folder = Path.Combine(Path.GetTempPath(), "olv-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        return new SettingsStore(Path.Combine(folder, "settings.json"));
    }

    /// <summary>
    /// The default column of the settings.json table in configuration.md, by key.
    ///
    /// The table is five columns and the third is the default. Parsed rather than
    /// matched against a literal, so reformatting the table does not fail a test
    /// about the numbers in it.
    /// </summary>
    private static Dictionary<string, string> DocumentedSettingsDefaults()
    {
        string text = Doc("configuration.md");

        int from = text.IndexOf("## settings.json", StringComparison.Ordinal);
        Assert.True(from >= 0, "configuration.md no longer has a settings.json section");

        int to = text.IndexOf("\n## ", from + 1, StringComparison.Ordinal);
        string section = to > from ? text[from..to] : text[from..];

        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string line in section.Split('\n'))
        {
            if (!line.StartsWith("| `", StringComparison.Ordinal)) continue;

            string[] cells = [.. line.Split('|', StringSplitOptions.None).Select(c => c.Trim())];

            // Leading and trailing empties either side of the pipes.
            if (cells.Length < 5) continue;

            string key = cells[1].Trim('`');
            defaults[key] = cells[3].Trim('`');
        }

        return defaults;
    }

    [Fact]
    public void TheSettingsTableStillParses()
    {
        Dictionary<string, string> documented = DocumentedSettingsDefaults();

        // If the parse silently broke, every comparison below would pass by
        // having nothing to compare.
        Assert.True(
            documented.Count >= 10,
            $"only {documented.Count} settings rows parsed out of configuration.md");
    }

    /// <summary>
    /// Each documented default against the value a new install actually has.
    /// </summary>
    [Fact]
    public void DocumentedSettingsDefaultsMatchAFreshInstall()
    {
        Dictionary<string, string> documented = DocumentedSettingsDefaults();
        using var _ = new TempFolder();
        SettingsStore settings = FreshSettings();

        var actual = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "1",
            ["themeId"] = ThemeCatalog.DefaultId,
            ["liveRate"] = settings.LiveRate.ToString(CultureInfo.InvariantCulture),
            ["singleRequestBlock"] = settings.SingleRequestBlock ? "true" : "false",
            ["recordOnConnect"] = settings.RecordOnConnect ? "true" : "false",
            ["units"] = settings.Units.ToString(),
            ["dataFolder"] = settings.DataFolder ?? "*(unset)*",
            ["recordingFolder"] = settings.RecordingFolder ?? "*(unset)*",
            ["knownEcus"] = settings.KnownEcus.Count == 0 ? "*(unset)*" : "set",
            ["ecuLastUsed"] = settings.EcuLastUsed.Count == 0 ? "*(unset)*" : "set",
            ["obd2BatchDeaths"] = settings.Obd2BatchDeaths.Count == 0 ? "*(unset)*" : "set",
        };

        var wrong = new List<string>();

        foreach ((string key, string expected) in actual)
        {
            if (!documented.TryGetValue(key, out string? stated))
            {
                wrong.Add($"{key}: not documented at all");
                continue;
            }

            if (!string.Equals(stated, expected, StringComparison.OrdinalIgnoreCase))
                wrong.Add($"{key}: docs say \"{stated}\", a fresh install has \"{expected}\"");
        }

        // And nothing documented that the software does not have.
        foreach (string key in documented.Keys)
            if (!actual.ContainsKey(key))
                wrong.Add($"{key}: documented, but not a setting");

        Assert.True(
            wrong.Count == 0,
            "configuration.md disagrees with the software:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The ceiling a rate is clamped to, which the settings table states as the
    /// top of the valid range.
    /// </summary>
    [Fact]
    public void TheDocumentedLiveRateCeilingIsTheRealOne() =>
        Assert.Contains(
            $"up to {SettingsStore.MaximumLiveRate.ToString("F0", CultureInfo.InvariantCulture)}",
            Doc("configuration.md"),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every rate the menu offers is in the table, and none that it does not.
    /// </summary>
    [Fact]
    public void TheLoggingRateTableMatchesTheMenu()
    {
        string text = Unemphasised(Doc("live-connection.md"));
        var missing = new List<string>();

        foreach (double rate in MainViewModel.LiveRates)
        {
            string row = $"| {rate.ToString("N0", CultureInfo.InvariantCulture)} Hz |";

            if (!text.Contains(row, StringComparison.Ordinal)) missing.Add($"{rate:N0} Hz");
        }

        Assert.True(
            missing.Count == 0,
            "live-connection.md does not list every rate the menu offers: " + string.Join(", ", missing));

        // The default has to be marked as the default, since that is the fact a
        // reader is actually after.
        Assert.Contains(
            $"| {SettingsStore.DefaultLiveRate:N0} Hz | Default",
            text,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The VE calibration settings table, against the record's own defaults.
    /// </summary>
    [Fact]
    public void TheVeSettingsTableMatchesTheDefaults()
    {
        var defaults = new VeAnalysisSettings();
        string text = Doc("ve-calibration.md");
        var wrong = new List<string>();

        if (!text.Contains($"| **Min samples** | {defaults.MinimumSamples} |", StringComparison.Ordinal))
            wrong.Add($"Min samples should be documented as {defaults.MinimumSamples}");

        if (!text.Contains(
            $"| **Max change %** | {defaults.MaxChangePercent.ToString("G", CultureInfo.InvariantCulture)} |",
            StringComparison.Ordinal))
        {
            wrong.Add($"Max change % should be documented as {defaults.MaxChangePercent}");
        }

        // The delay defaults to none, which is the whole point of the section
        // about finding it from the log.
        Assert.Equal(0, defaults.MeasurementDelaySamples);

        if (!text.Contains("| **Wideband delay, s** | 0 (none) |", StringComparison.Ordinal))
            wrong.Add("Wideband delay should be documented as 0 (none)");

        // The weighting formula is quoted in the page and is the reason a thin
        // cell moves less; a change to it that left the page alone would be a
        // documented rule the software no longer follows.
        if (!text.Contains("n / (n + MinSamples)", StringComparison.Ordinal))
            wrong.Add("the thin-cell weighting formula is no longer quoted");

        Assert.True(
            wrong.Count == 0,
            "ve-calibration.md disagrees with VeAnalysisSettings:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The smoothing table, against the windows the code actually uses.
    /// </summary>
    [Fact]
    public void TheSmoothingTableMatchesTheWindows()
    {
        string text = Doc("user-guide.md");
        var wrong = new List<string>();

        foreach (SmoothingLevel level in Enum.GetValues<SmoothingLevel>())
        {
            if (level == SmoothingLevel.None) continue;

            string row = $"| **{Smoothing.Name(level)}** | {Smoothing.Window(level)} samples |";

            if (!text.Contains(row, StringComparison.Ordinal))
                wrong.Add($"{Smoothing.Name(level)} is a median of {Smoothing.Window(level)} samples");
        }

        Assert.True(
            wrong.Count == 0,
            "user-guide.md disagrees with Smoothing:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The colour-scheme count, which appears in two places and is the sort of
    /// number that quietly stops being true.
    /// </summary>
    [Fact]
    public void TheSchemeCountIsRight()
    {
        int themes = ThemeCatalog.Themes.Count;
        string spelled = themes switch
        {
            12 => "Twelve",
            13 => "Thirteen",
            14 => "Fourteen",
            15 => "Fifteen",
            16 => "Sixteen",
            _ => themes.ToString(CultureInfo.InvariantCulture),
        };

        Assert.Contains(spelled, RootDoc("README.md"), StringComparison.OrdinalIgnoreCase);

        // Every scheme has to be named in the table, or the count is a number
        // with nothing behind it.
        string text = Doc("user-guide.md");
        string[] unnamed = [.. ThemeCatalog.Themes
            .Where(t => !text.Contains(t.Name, StringComparison.Ordinal))
            .Select(t => t.Name)];

        Assert.True(unnamed.Length == 0, "user-guide.md does not name: " + string.Join(", ", unnamed));
    }

    /// <summary>
    /// The scatter's percentile trim, stated as percentiles in the page and as a
    /// fraction in the code.
    /// </summary>
    [Fact]
    public void TheScatterTrimIsDocumentedAsThePercentileItIs()
    {
        int low = (int)Math.Round(ScatterBins.Trim * 100);
        int high = 100 - low;

        Assert.Contains(
            $"{low}nd and {high}th percentiles",
            Doc("histogram-and-scatter.md"),
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            $"{low}nd to {high}th percentile",
            Doc("histogram-and-scatter.md"),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How long a lost link is waited on, which the guide, the page and the
    /// troubleshooting table all state.
    /// </summary>
    [Fact]
    public void TheReconnectWindowIsDocumented()
    {
        double seconds = new LiveSessionSettings().ReconnectFor.TotalSeconds;

        Assert.Equal(60, seconds);
        Assert.Contains("one minute", Doc("live-connection.md"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{seconds:F0} s", Doc("troubleshooting.md"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The calculators, as the navigation list actually declares them.
    ///
    /// Read out of the source because they are built into a private field when
    /// the window opens; there is nothing to ask. That is the same bargain
    /// <c>McpBindingTests</c> takes for the bind address — a grep is a poor tool
    /// and a great deal better than nothing checking at all.
    /// </summary>
    private static IReadOnlyList<string> CalculatorNames()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "OpenLogViewer.App", "CalculatorsWindow.xaml.cs"));

        // The field is declared empty before it is ever filled in, so the
        // assignment wanted is the one with calculators in it rather than the
        // first one the file happens to contain.
        Match? block = Regex
            .Matches(source, "_calculators\\s*=\\s*\\[(.*?)\\n\\s*\\];", RegexOptions.Singleline)
            .OrderByDescending(m => Regex.Matches(m.Groups[1].Value, "new\\(").Count)
            .FirstOrDefault();

        Assert.NotNull(block);

        return [.. Regex.Matches(block!.Groups[1].Value, "new\\(\"[^\"]+\",\\s*\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)];
    }

    [Fact]
    public void TheCalculatorListStillReads() =>
        Assert.True(CalculatorNames().Count > 10, "far fewer calculators than expected — has the list moved?");

    /// <summary>
    /// Every calculator, named everywhere the set is enumerated.
    ///
    /// <para>
    /// The Tools tooltip listed nine of fifteen, and none of the six added after
    /// it was written. A list of things is the shape of documentation that rots
    /// most quietly: nothing about nine correct entries looks wrong, and the only
    /// way to notice is to count them against the software.
    /// </para>
    /// <para>
    /// Names rather than groups, because those are different words: the group is
    /// "Running costs" and the calculator in it is "Fuel economy", and a tooltip
    /// naming the group sends somebody looking for a heading that is not in the
    /// list they were told to look at.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryCalculatorIsNamedWhereverTheyAreListed()
    {
        IReadOnlyList<string> calculators = CalculatorNames();

        string xaml = WebUtility.HtmlDecode(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "OpenLogViewer.App", "MainWindow.xaml")));

        // Anchored on the click handler rather than on the label or on a word in
        // the tooltip. The label carries an access key inside the word —
        // "Ca_lculators" — and a tooltip that has stopped listing them is the
        // very thing being tested for, so neither is something to find it by.
        string element = Regex.Matches(xaml, "<MenuItem\\b.*?/>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .FirstOrDefault(m => m.Contains("OnCalculatorsClick", StringComparison.Ordinal))
            ?? "";

        Assert.False(element.Length == 0, "there is no longer a menu item opening the calculators");

        Match tooltip = Regex.Match(element, "ToolTip=\"([^\"]*)\"", RegexOptions.Singleline);
        Assert.True(tooltip.Success, "the Calculators menu item no longer has a tooltip at all");

        var places = new (string Where, string Text)[]
        {
            ("the Tools ▸ Calculators tooltip", tooltip.Groups[1].Value),
            ("docs/user-guide.md", Doc("user-guide.md")),
            ("the in-app guide", string.Join("\n", Guide.AllEntries.Select(e => e.Body))),
        };

        var wrong = new List<string>();

        foreach ((string where, string text) in places)
            foreach (string calculator in calculators)
                if (!text.Contains(calculator, StringComparison.OrdinalIgnoreCase))
                    wrong.Add($"{where} does not name \"{calculator}\"");

        Assert.True(wrong.Count == 0, string.Join("\n  ", wrong.Prepend("Calculators are missing:")));
    }

    /// <summary>
    /// And the count that goes with the list, which is the part a reader trusts
    /// without checking.
    /// </summary>
    [Fact]
    public void TheCalculatorCountIsRight()
    {
        int count = CalculatorNames().Count;
        string spelled = count switch
        {
            13 => "Thirteen",
            14 => "Fourteen",
            15 => "Fifteen",
            16 => "Sixteen",
            17 => "Seventeen",
            _ => count.ToString(CultureInfo.InvariantCulture),
        };

        Assert.Contains(spelled, Doc("user-guide.md"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(spelled, RootDoc("README.md"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            spelled,
            string.Join("\n", Guide.AllEntries.Select(e => e.Body)),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates a temporary folder and takes it away again.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "olv-docs-" + Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }
    }
}
