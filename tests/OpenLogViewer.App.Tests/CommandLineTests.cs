using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>Which argument on a command line is the log to open.</summary>
public class CommandLineTests
{
    [Fact]
    public void ASwitchWithItsOptionalValueLeftOffDoesNotSwallowTheNextSwitch()
    {
        // Found on a real MaxxECU session: the serial was left off, so
        // "--download-maxx-log" was skipped as the serial and its path was
        // opened as a log - and startup never got as far as connecting.
        string[] args = ["--connect-maxx-usb", "--download-maxx-log", @"C:\out\stored.bin", "--agent-api", "8767"];

        Assert.Null(App.LogPathIn(args));
    }

    [Fact]
    public void AValueIsStillSkippedWhenOneIsGiven()
    {
        string[] args = ["--connect-maxx-usb", "MX000000", "--connect", "COM9", "drive.mlg"];

        Assert.Equal("drive.mlg", App.LogPathIn(args));
    }

    [Fact]
    public void ASwitchLastOnTheLineIsFine()
    {
        Assert.Equal("drive.mlg", App.LogPathIn(["drive.mlg", "--connect-maxx-usb"]));
    }
}
