using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// That the documentation set still joins up, and that nothing in it is a
/// character no human typed.
///
/// <para>
/// Nineteen pages linking to one another is enough that nobody follows them all
/// by hand, and a link that stopped resolving looks exactly like one that never
/// did — it is only ever found by a reader, who is by then the person the page
/// was supposed to help. Renaming a heading is enough to break one, and a heading
/// gets renamed for good reasons.
/// </para>
/// <para>
/// The control-character check is here for a smaller and stupider reason. A
/// scripted edit once wrote a literal backspace into a workflow file and into six
/// commands in a page, because a shell collapsed "\\b" on its way to Python. The
/// YAML would not parse and the commands could not be copied, and neither showed
/// up in a diff, since a backspace is invisible in every tool that renders one.
/// </para>
/// </summary>
public class DocumentationLinkTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenLogViewer.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    /// <summary>Everything a link in this set can point at, by repository path.</summary>
    private static IReadOnlyList<string> MarkdownFiles(string root) =>
    [
        Path.Combine(root, "README.md"),
        Path.Combine(root, "CHANGELOG.md"),
        .. Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md").Order(),
    ];

    /// <summary>
    /// The anchor a heading gets, as GitHub derives it: lower case, punctuation
    /// dropped, spaces hyphenated.
    /// </summary>
    private static string Anchor(string heading)
    {
        string text = heading.Trim().ToLowerInvariant();

        text = Regex.Replace(text, "[`*_]", "");
        text = Regex.Replace(text, @"[^\w\s-]", "");

        return Regex.Replace(text, @"\s+", "-").Trim('-');
    }

    private static HashSet<string> AnchorsIn(string path) =>
        [.. Regex.Matches(File.ReadAllText(path), "^#{1,6}[ \t]+(.*)$", RegexOptions.Multiline)
            .Select(m => Anchor(m.Groups[1].Value))];

    /// <summary>Every markdown link in a file, as it is written.</summary>
    private static IEnumerable<string> LinksIn(string path) =>
        Regex.Matches(File.ReadAllText(path), @"\[[^\]]*\]\(([^)\s]+)\)", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(target => !target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                             && !target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ThereIsADocumentationSetToCheck()
    {
        string root = RepositoryRoot();

        Assert.True(
            MarkdownFiles(root).Count > 15,
            "far fewer documentation pages than expected — has docs/ moved?");
    }

    /// <summary>
    /// Every relative link points at a file that is there.
    /// </summary>
    [Fact]
    public void EveryLinkResolvesToAFile()
    {
        string root = RepositoryRoot();
        var broken = new List<string>();

        foreach (string file in MarkdownFiles(root))
        {
            string folder = Path.GetDirectoryName(file)!;

            foreach (string target in LinksIn(file))
            {
                string path = target.Split('#')[0];

                if (path.Length == 0) continue;

                if (!File.Exists(Path.GetFullPath(Path.Combine(folder, path)))
                    && !Directory.Exists(Path.GetFullPath(Path.Combine(folder, path))))
                {
                    broken.Add($"{Path.GetFileName(file)} → {target}");
                }
            }
        }

        Assert.True(broken.Count == 0, "Links to nothing:\n  " + string.Join("\n  ", broken));
    }

    /// <summary>
    /// And every "#somewhere" points at a heading that exists, in this page or
    /// the one it names.
    /// </summary>
    [Fact]
    public void EveryAnchorResolvesToAHeading()
    {
        string root = RepositoryRoot();
        IReadOnlyList<string> files = MarkdownFiles(root);

        Dictionary<string, HashSet<string>> anchors = files.ToDictionary(
            f => Path.GetFullPath(f),
            AnchorsIn,
            StringComparer.OrdinalIgnoreCase);

        var broken = new List<string>();

        foreach (string file in files)
        {
            string folder = Path.GetDirectoryName(file)!;

            foreach (string target in LinksIn(file))
            {
                if (!target.Contains('#')) continue;

                string[] parts = target.Split('#', 2);
                string path = parts[0].Length == 0
                    ? Path.GetFullPath(file)
                    : Path.GetFullPath(Path.Combine(folder, parts[0]));

                // A link out to a file this set does not own — the licence, a
                // source file — carries no headings to check.
                if (!anchors.TryGetValue(path, out HashSet<string>? found)) continue;

                if (!found.Contains(parts[1]))
                    broken.Add($"{Path.GetFileName(file)} → {target}");
            }
        }

        Assert.True(broken.Count == 0, "Anchors to nothing:\n  " + string.Join("\n  ", broken));
    }

    /// <summary>
    /// The generated wiki joins up too, on its own flat page names.
    ///
    /// It is built from the same pages, so a broken link here is a fault in the
    /// transform rather than in the writing — which is exactly why checking the
    /// source set would not find it.
    /// </summary>
    [Fact]
    public void TheGeneratedWikiJoinsUp()
    {
        string wiki = Path.Combine(RepositoryRoot(), "wiki");

        if (!Directory.Exists(wiki)) return;

        string[] files = [.. Directory.EnumerateFiles(wiki, "*.md").Order()];
        Assert.True(files.Length > 15, "far fewer wiki pages than expected");

        // A lambda rather than the method group: GetFileNameWithoutExtension is
        // declared to return string?, and a method group cannot carry the
        // NotNullIfNotNull that makes it string here, so the inferred key type
        // is string? and fails the notnull constraint under -warnaserror.
        Dictionary<string, HashSet<string>> anchors = files.ToDictionary(
            f => Path.GetFileNameWithoutExtension(f)!,
            AnchorsIn,
            StringComparer.OrdinalIgnoreCase);

        var broken = new List<string>();

        foreach (string file in files)
        {
            string page = Path.GetFileNameWithoutExtension(file);

            foreach (string target in LinksIn(file))
            {
                string[] parts = target.Split('#', 2);
                string wanted = parts[0].Length == 0 ? page : parts[0];

                if (!anchors.TryGetValue(wanted, out HashSet<string>? found))
                {
                    broken.Add($"{page} → {target} (no such page)");
                    continue;
                }

                if (parts.Length > 1 && !found.Contains(parts[1]))
                    broken.Add($"{page} → {target} (no such heading)");
            }
        }

        Assert.True(broken.Count == 0, "The wiki does not join up:\n  " + string.Join("\n  ", broken));
    }

    /// <summary>
    /// Which page each document becomes, read out of the generator so there is
    /// one map rather than two.
    /// </summary>
    private static Dictionary<string, string> WikiPageMap(string root)
    {
        string script = File.ReadAllText(Path.Combine(root, "tools", "build-wiki.ps1"));

        Match block = Regex.Match(script, @"\$pages\s*=\s*\[ordered\]\s*@\{(.*?)\n\}", RegexOptions.Singleline);
        Assert.True(block.Success, "build-wiki.ps1 no longer declares a $pages map");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match entry in Regex.Matches(block.Groups[1].Value, @"'([^']+)'\s*=\s*'([^']+)'"))
            map[entry.Groups[1].Value] = entry.Groups[2].Value;

        return map;
    }

    /// <summary>
    /// The wiki says the same thing as the page it came from.
    ///
    /// <para>
    /// The generator rewrites links and nothing else, and a link is ASCII, so
    /// every character above 127 in a wiki page must appear in its source in the
    /// same order. That is a narrow claim and it catches a wide fault: the first
    /// build of this wiki was read back through a codepage that is not UTF-8, and
    /// shipped with 1,249 characters turned into mojibake — every em-dash, every
    /// bullet, every box-drawing line in the architecture diagram.
    /// </para>
    /// <para>
    /// Nothing noticed, and the freshness check least of all: it compared one
    /// corrupted copy against another corrupted copy and found them identical.
    /// This is the check that would have.
    /// </para>
    /// </summary>
    [Fact]
    public void TheWikiSaysWhatItsSourceSays()
    {
        string root = RepositoryRoot();
        string wiki = Path.Combine(root, "wiki");

        if (!Directory.Exists(wiki)) return;

        Dictionary<string, string> pages = WikiPageMap(root);
        Assert.True(pages.Count > 15, "the page map came back nearly empty");

        var wrong = new List<string>();

        foreach ((string source, string page) in pages)
        {
            string from = Path.Combine(root, "docs", source);
            string to = Path.Combine(wiki, page + ".md");

            if (!File.Exists(from) || !File.Exists(to)) continue;

            string expected = new([.. File.ReadAllText(from).Where(c => c > 127)]);
            string actual = new([.. File.ReadAllText(to).Where(c => c > 127)]);

            if (expected == actual) continue;

            int at = 0;
            while (at < expected.Length && at < actual.Length && expected[at] == actual[at]) at++;

            string had = at < expected.Length ? $"U+{(int)expected[at]:X4}" : "(end)";
            string got = at < actual.Length ? $"U+{(int)actual[at]:X4}" : "(end)";

            wrong.Add($"{page}: character {at} above ASCII should be {had}, is {got}");
        }

        Assert.True(
            wrong.Count == 0,
            "The wiki has not come through intact:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// No character in the documentation that nobody typed.
    ///
    /// Tabs included, and not out of fussiness: the same collapsed escape that
    /// wrote a backspace into a command wrote a tab beside it, and both came from
    /// one bad edit. A page has no need of either.
    /// </summary>
    [Fact]
    public void NothingContainsACharacterNobodyTyped()
    {
        string root = RepositoryRoot();

        IEnumerable<string> files = MarkdownFiles(root)
            .Concat(Directory.Exists(Path.Combine(root, "wiki"))
                ? Directory.EnumerateFiles(Path.Combine(root, "wiki"), "*.md")
                : [])
            .Concat(Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml"));

        var offenders = new List<string>();

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c is '\n' or '\r') continue;
                if (!char.IsControl(c) && c != '\t') continue;

                int line = text.Take(i).Count(ch => ch == '\n') + 1;

                offenders.Add(
                    $"{Path.GetFileName(file)} line {line}: U+{(int)c:X4}");

                break;
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Control characters, which are invisible in a diff:\n  " + string.Join("\n  ", offenders));
    }
}
