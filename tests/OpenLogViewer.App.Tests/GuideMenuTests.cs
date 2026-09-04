using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The guide against the menus, in both directions.
///
/// <para>
/// This exists because of two failures that had both already happened. A whole
/// connection — Subaru over SSM — shipped with a menu item and no mention of it
/// anywhere, and channel smoothing did the same; neither was caught, because the
/// only guard was a hand-written list of features to look for, and nobody adds
/// the feature they forgot. And the guide went on telling people to use an
/// "Export ▾" button in the toolbar for some time after Export became a menu.
/// </para>
/// <para>
/// So: every menu item has to be findable in the guide unless it is explicitly
/// excused, and every menu path the guide names has to still exist. Both lists
/// are derived from the XAML rather than typed here, which is the point — a test
/// that has to be told about a new feature is the test that missed these two.
/// </para>
/// </summary>
public class GuideMenuTests
{
    /// <summary>
    /// Menu items that need no entry of their own, each with the reason.
    ///
    /// Deliberately explicit and deliberately short. Adding to it is a decision
    /// somebody makes on purpose, which is the opposite of a feature quietly
    /// going undocumented.
    /// </summary>
    private static readonly Dictionary<string, string> NeedsNoGuideEntry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Help"] = "the name of a menu rather than a thing to do",
        ["Guide"] = "switches to the guide; the guide need not describe its own menu item",
        ["Exit"] = "closes the application",
        ["Disconnect"] = "the opposite of Connect, which is covered",
        ["How to use this app"] = "opens the guide; the guide need not describe its own menu item",
        ["Connect an AI agent (MCP)"] = "opens the guide at the AI agent section",
        ["Documentation online"] = "opens a browser",
        ["About OpenLogViewer"] = "a version box",
        ["Delete filter"] = "a context-menu verb; filters are covered",
        ["Delete preset"] = "a context-menu verb; presets are covered",
        ["Overwrite with current selection"] = "a context-menu verb; presets are covered",
        ["Delete calculated channel"] = "a context-menu verb; calculated channels are covered",
    };

    /// <summary>
    /// Walks up from the test binaries to the repository, so this does not depend
    /// on where the tests were run from.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenLogViewer.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static string MainWindowXaml() =>
        File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "OpenLogViewer.App", "MainWindow.xaml"));

    /// <summary>
    /// Every menu label the XAML states outright.
    ///
    /// Bound headers are skipped rather than guessed at: what "{Binding
    /// RecordLabel}" says depends on whether a session is recording, and a test
    /// cannot read it off the markup. So are the "…" placeholders, which are
    /// what a menu filled in when it opens holds until it is opened.
    /// </summary>
    private static IReadOnlyList<string> MenuLabels()
    {
        var labels = new List<string>();

        foreach (Match match in Regex.Matches(
            MainWindowXaml(), "<MenuItem[^>]*?Header=\"([^\"]*)\"", RegexOptions.Singleline))
        {
            string header = match.Groups[1].Value;

            if (header.StartsWith('{') || header.Length == 0 || header == "…") continue;

            // "_Open log…" is how WPF marks the Alt key; nobody sees the
            // underscore, and the guide should not have to carry it.
            string label = WebUtility.HtmlDecode(header).Replace("_", "", StringComparison.Ordinal).Trim();

            if (label.Length > 0 && !labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                labels.Add(label);
        }

        return labels;
    }

    /// <summary>Everything the guide says, as one body of text to search.</summary>
    private static string GuideText() =>
        string.Join("\n", Guide.Sections.SelectMany(section =>
            new[] { section.Title, section.Blurb }
                .Concat(section.Entries.SelectMany(e => new[] { e.Title, e.Body, e.Keys }))));

    /// <summary>An ellipsis is part of the label on screen and noise in prose.</summary>
    private static string WithoutEllipsis(string label) => label.TrimEnd('…').Trim();

    [Fact]
    public void TheXamlStillHasMenusToRead()
    {
        // If the extraction silently stopped working, every other test here would
        // pass by having nothing to check.
        Assert.True(MenuLabels().Count > 50, "far fewer menu items than expected — has the markup moved?");
    }

    /// <summary>
    /// The test that fails when a feature is added and the guide is not.
    /// </summary>
    [Fact]
    public void EveryMenuItemIsInTheGuide()
    {
        string guide = GuideText();
        var missing = new List<string>();

        foreach (string label in MenuLabels())
        {
            string wanted = WithoutEllipsis(label);

            if (NeedsNoGuideEntry.ContainsKey(wanted)) continue;
            if (guide.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;

            missing.Add(label);
        }

        Assert.True(
            missing.Count == 0,
            "The guide does not mention:\n  " + string.Join("\n  ", missing)
            + "\n\nAdd an entry to Guide.cs, or excuse it in NeedsNoGuideEntry with a reason.");
    }

    /// <summary>
    /// The other direction: the guide is not still describing a menu that moved.
    ///
    /// Every "Menu ▸ Item" the guide writes has to be followed by the exact text
    /// of a real menu label. This is what would have caught the guide sending
    /// people to an Export button in the toolbar months after Export became an
    /// item in the File menu.
    /// </summary>
    [Fact]
    public void EveryMenuPathTheGuideNamesStillExists()
    {
        IReadOnlyList<string> labels = MenuLabels();
        var wrong = new List<string>();

        foreach (GuideEntry entry in Guide.AllEntries)
        {
            foreach (Match match in Regex.Matches(entry.Body, "▸\\s*(.+)"))
            {
                string after = match.Groups[1].Value;

                // Longest first, so "Data folder" does not satisfy a path that
                // actually names something longer beginning with it.
                bool found = labels
                    .OrderByDescending(l => l.Length)
                    .Any(l => after.StartsWith(WithoutEllipsis(l), StringComparison.OrdinalIgnoreCase));

                if (!found)
                {
                    string quoted = after.Length > 40 ? after[..40] + "…" : after;
                    wrong.Add($"\"{entry.Title}\" points at: {quoted}");
                }
            }
        }

        Assert.True(
            wrong.Count == 0,
            "The guide names a menu item that does not exist:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>Every button label the XAML states outright, whitespace flattened.</summary>
    private static IReadOnlyList<string> ButtonLabels()
    {
        var labels = new List<string>();

        foreach (Match match in Regex.Matches(MainWindowXaml(), "Content=\"([^\"{]*)\""))
        {
            // The markup spaces a glyph off its word — "ƒ  Calculators" — which
            // is a matter of how it is set rather than what it is called, so it
            // is not something prose should have to copy.
            string label = Regex.Replace(WebUtility.HtmlDecode(match.Groups[1].Value), @"\s+", " ").Trim();

            if (label.Length > 0 && !labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                labels.Add(label);
        }

        return labels;
    }

    /// <summary>
    /// "X in the toolbar" has to name something that is actually in the toolbar.
    ///
    /// This is the half of the stale-label problem that the menu-path test cannot
    /// see: the guide sent people to an "Export ▾" button in the toolbar long
    /// after Export became an item in the File menu, and a ▾ is still correct for
    /// Connect, so the arrow alone proves nothing.
    /// </summary>
    [Fact]
    public void EveryToolbarButtonTheGuideNamesStillExists()
    {
        IReadOnlyList<string> buttons = ButtonLabels();
        var wrong = new List<string>();

        foreach (GuideEntry entry in Guide.AllEntries)
        {
            string body = Regex.Replace(entry.Body, @"\s+", " ");
            int at = body.IndexOf("in the toolbar", StringComparison.OrdinalIgnoreCase);

            while (at >= 0)
            {
                string before = body[..at].TrimEnd();

                if (!buttons.Any(b => before.EndsWith(b, StringComparison.OrdinalIgnoreCase)))
                {
                    string tail = before.Length > 40 ? "…" + before[^40..] : before;
                    wrong.Add($"\"{entry.Title}\" claims a toolbar button: {tail}");
                }

                at = body.IndexOf("in the toolbar", at + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.True(
            wrong.Count == 0,
            "The guide names a toolbar button that does not exist:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// An excuse for a menu item that is no longer there is an excuse nobody
    /// re-examined, and it hides the next thing that lands under the same name.
    /// </summary>
    [Fact]
    public void NothingIsExcusedThatIsNoLongerAMenuItem()
    {
        IReadOnlyList<string> labels = [.. MenuLabels().Select(WithoutEllipsis)];

        string[] stale = [.. NeedsNoGuideEntry.Keys
            .Where(excused => !labels.Contains(excused, StringComparer.OrdinalIgnoreCase))];

        Assert.True(
            stale.Length == 0,
            "NeedsNoGuideEntry excuses items that are not in the menus any more:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void EveryExcuseGivesAReason() =>
        Assert.All(NeedsNoGuideEntry, excuse =>
            Assert.False(string.IsNullOrWhiteSpace(excuse.Value), $"{excuse.Key} is excused without a reason"));
}
