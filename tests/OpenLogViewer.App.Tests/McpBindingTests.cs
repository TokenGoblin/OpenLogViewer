using System.IO;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// That the server can only ever be reached from this machine.
/// </summary>
public class McpBindingTests
{
    /// <summary>
    /// Walks up from the test binaries to the repository, so this does not depend
    /// on where the tests were run from.
    /// </summary>
    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenLogViewer.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);

        return Path.Combine(directory!.FullName, "src");
    }

    [Fact]
    public void NothingBindsAWildcardAddress()
    {
        // A grep, as a test. The bind address is one token, it is the difference
        // between "nothing off this machine can reach it" and the opposite, and
        // nothing else in the codebase would notice it changing.
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            if (text.Contains("0.0.0.0", StringComparison.Ordinal)
                || text.Contains("UseUrls($\"http://*", StringComparison.Ordinal)
                || text.Contains("UseUrls($\"http://+", StringComparison.Ordinal))
            {
                offenders.Add(file);
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    [Fact]
    public void TheServerBindsLoopbackExplicitly()
    {
        string host = Path.Combine(SourceRoot(), "OpenLogViewer.App", "Mcp", "McpServerHost.cs");

        Assert.Contains("http://127.0.0.1:{port}", File.ReadAllText(host), StringComparison.Ordinal);
    }
}
