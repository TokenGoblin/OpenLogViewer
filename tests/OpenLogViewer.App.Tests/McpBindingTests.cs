using System.IO;
using OpenLogViewer.App.Mcp;
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

    // ----- and who is allowed to talk to it -----------------------------------
    //
    // Loopback keeps other machines out and does nothing about this one. A page
    // in a browser can post to 127.0.0.1 as readily as an agent can, and a
    // hostname that resolves there makes the browser treat it as that site's own
    // origin — which is the rebinding attack the MCP transport spec requires an
    // Origin check against.

    [Fact]
    public void AnAgentSendsNoOriginAndIsAllowed() =>
        Assert.True(McpServerHost.OriginIsLocal(default));

    [Theory]
    [InlineData("http://127.0.0.1:7071")]
    [InlineData("http://localhost:7071")]
    [InlineData("http://[::1]:7071")]
    public void APageServedFromThisMachineIsAllowed(string origin) =>
        Assert.True(McpServerHost.OriginIsLocal(origin));

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://evil.test")]
    [InlineData("https://127.0.0.1.attacker.test")]
    [InlineData("http://192.168.0.10")]
    public void APageServedFromAnywhereElseIsRefused(string origin) =>
        Assert.False(McpServerHost.OriginIsLocal(origin));

    /// <summary>A sandboxed frame or a file:// page, which names no site at all.</summary>
    [Fact]
    public void AnOriginOfNullIsRefused() =>
        Assert.False(McpServerHost.OriginIsLocal("null"));

    [Fact]
    public void SomethingThatIsNotAnOriginAtAllIsRefused() =>
        Assert.False(McpServerHost.OriginIsLocal("not a url"));

    /// <summary>
    /// No browser sends two. Somebody sending two is hoping the one that gets
    /// read is not the one that gets checked.
    /// </summary>
    [Fact]
    public void MoreThanOneOriginIsRefused() =>
        Assert.False(McpServerHost.OriginIsLocal(new[] { "http://127.0.0.1", "https://evil.test" }));
}
