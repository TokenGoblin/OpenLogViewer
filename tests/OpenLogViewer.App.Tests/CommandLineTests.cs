using OpenLogViewer.App;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Which argument is the log to open. Worth its own tests because getting it
/// wrong fails before the window is shown, as a modal dialog complaining about a
/// file called COM9 — which reads as the application hanging on startup.
/// </summary>
public class CommandLineTests
{
    [Fact]
    public void ABareArgumentIsTheLog() =>
        Assert.Equal("run.mlg", App.LogPathIn(["run.mlg"]));

    [Fact]
    public void ASwitchesValueIsNotTheLog() =>
        Assert.Null(App.LogPathIn(["--connect", "COM9"]));

    [Fact]
    public void TheLogIsFoundAfterASwitchAndItsValue() =>
        Assert.Equal("run.mlg", App.LogPathIn(["--connect", "COM9", "run.mlg"]));

    /// <summary>
    /// "--insights" stands alone — it opens the findings for whatever log is
    /// loaded. It was listed among the switches that take a value, so it ate the
    /// path after it: "--insights run.mlg" opened no log, while the same two the
    /// other way round worked, which reads as an intermittent fault.
    /// </summary>
    [Fact]
    public void InsightsDoesNotSwallowTheLogAfterIt() =>
        Assert.Equal("run.mlg", App.LogPathIn(["--insights", "run.mlg"]));

    [Fact]
    public void InsightsWorksWithTheLogBeforeItToo() =>
        Assert.Equal("run.mlg", App.LogPathIn(["run.mlg", "--insights"]));

    [Fact]
    public void NoLogIsNull() =>
        Assert.Null(App.LogPathIn(["--mcp"]));

    /// <summary>
    /// Every switch listed as taking a value must actually consume one, or it
    /// eats the log path; every switch that stands alone must not be listed, or
    /// it does the same. This walks the list rather than trusting it.
    /// </summary>
    [Fact]
    public void EverySwitchThatTakesAValueSwallowsThatValueAndNothingElse()
    {
        foreach (string option in App.TakesAValue)
        {
            Assert.Equal("run.mlg", App.LogPathIn([option, "its-value", "run.mlg"]));
            Assert.Null(App.LogPathIn([option, "its-value"]));
        }
    }
}
