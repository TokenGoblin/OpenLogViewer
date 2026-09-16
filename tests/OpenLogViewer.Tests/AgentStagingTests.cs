using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Where an agent leaves a file for a person instead of, or alongside, writing
/// to the ECU's working memory.
///
/// The property worth guarding here is not the content of the file — staging
/// never touches an engine, so a mistaken CSV is a mistaken CSV — but that a
/// file name handed over by whatever is on the far end of the agent API cannot
/// land anywhere except inside this one folder.
/// </summary>
public class AgentStagingTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        }
    }

    private AgentStaging NewStaging()
    {
        string root = Path.Combine(Path.GetTempPath(), $"olv-stage-{Guid.NewGuid():N}");
        _temp.Add(root);

        return new AgentStaging(new Workspace(root));
    }

    // ----- the folder itself ---------------------------------------------------

    [Fact]
    public void TheFolderSitsUnderTheWorkspaceRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"olv-stage-{Guid.NewGuid():N}");
        _temp.Add(root);

        var staging = new AgentStaging(new Workspace(root));

        Assert.Equal(Path.Combine(root, "AgentStaging"), staging.Folder);
    }

    [Fact]
    public void TheFolderIsNotCreatedUntilSomethingIsStaged()
    {
        AgentStaging staging = NewStaging();

        Assert.False(Directory.Exists(staging.Folder));
    }

    // ----- writing ---------------------------------------------------------

    [Fact]
    public void StagingWritesTheFileAndHandsBackItsFullPath()
    {
        AgentStaging staging = NewStaging();

        string path = staging.Stage("tune.msq", "<msq/>");

        Assert.True(File.Exists(path));
        Assert.Equal("<msq/>", File.ReadAllText(path));
        Assert.Equal(Path.Combine(staging.Folder, "tune.msq"), path);
    }

    [Fact]
    public void StagingTheSameNameTwiceOverwritesRatherThanFailing()
    {
        AgentStaging staging = NewStaging();

        staging.Stage("tune.msq", "first");
        string path = staging.Stage("tune.msq", "second");

        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehindAfterAStage()
    {
        // The atomic-write discipline the rest of the application's exports
        // already use: written to a .tmp file and moved into place, so an
        // interrupted stage never leaves a truncated file where a person might
        // find and open it.
        AgentStaging staging = NewStaging();

        staging.Stage("tune.msq", "content");

        Assert.DoesNotContain(
            Directory.GetFiles(staging.Folder), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    // ----- path safety -------------------------------------------------------

    [Theory]
    [InlineData("../escaped.msq")]
    [InlineData("..\\escaped.msq")]
    [InlineData("sub/escaped.msq")]
    [InlineData("sub\\escaped.msq")]
    [InlineData("../../etc/passwd")]
    public void ANameThatTriesToLeaveTheFolderIsRefused(string filename)
    {
        AgentStaging staging = NewStaging();

        Assert.Throws<ArgumentException>(() => staging.Stage(filename, "anything"));
    }

    [Fact]
    public void AnAbsolutePathIsRefusedEvenWhenItPointsInsideTheFolder()
    {
        AgentStaging staging = NewStaging();
        string trick = Path.Combine(staging.Folder, "tune.msq");

        Assert.Throws<ArgumentException>(() => staging.Stage(trick, "anything"));
    }

    [Fact]
    public void AnEmptyNameIsRefused()
    {
        AgentStaging staging = NewStaging();

        Assert.Throws<ArgumentException>(() => staging.Stage("", "anything"));
        Assert.Throws<ArgumentException>(() => staging.Stage("   ", "anything"));
    }

    [Fact]
    public void NothingIsWrittenWhenTheNameIsRefused()
    {
        AgentStaging staging = NewStaging();

        try { staging.Stage("../escaped.msq", "anything"); }
        catch (ArgumentException) { }

        // Refused before the folder is even created — there is nowhere for a
        // half-attempt to have landed.
        Assert.False(Directory.Exists(staging.Folder));
    }

    // ----- listing -----------------------------------------------------------

    [Fact]
    public void ListingAnUnusedFolderIsEmptyRatherThanAnError()
    {
        AgentStaging staging = NewStaging();

        Assert.Empty(staging.ListStaged());
    }

    [Fact]
    public void ListingReportsWhatWasStagedWithItsSizeAndPath()
    {
        AgentStaging staging = NewStaging();

        string path = staging.Stage("tune.msq", "0123456789");

        AgentStagedFile file = Assert.Single(staging.ListStaged());
        Assert.Equal("tune.msq", file.Name);
        Assert.Equal(path, file.Path);
        Assert.Equal(10, file.Bytes);
    }

    [Fact]
    public void ListingComesBackNewestFirst()
    {
        AgentStaging staging = NewStaging();

        staging.Stage("first.csv", "a");
        Thread.Sleep(20);
        staging.Stage("second.csv", "b");

        IReadOnlyList<AgentStagedFile> listed = staging.ListStaged();

        Assert.Equal("second.csv", listed[0].Name);
        Assert.Equal("first.csv", listed[1].Name);
    }
}
