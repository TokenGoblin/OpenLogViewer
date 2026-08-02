using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class MathChannelTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
        }
    }

    private static LogDocument Log() => new()
    {
        FilePath = "x",
        Time = new LogChannel("Time", "s", 3, [0, 0.1, 0.2, 0.3], preservePrecision: true),
        Channels =
        [
            new LogChannel("RPM", "RPM", 0, [800, 2000, 4000, 6000]),
            new LogChannel("AFR", "AFR", 2, [13.0, 14.7, 12.0, double.NaN]),
            new LogChannel("AFR Target 1", "AFR", 2, [14.7, 14.7, 12.5, 12.5]),
        ],
        FormatName = "test",
    };

    private static MathChannel Definition(string name, string expression) =>
        new() { Name = name, Expression = expression, Units = "AFR", Digits = 2 };

    [Fact]
    public void ADefinitionBecomesAChannelOverTheWholeLog()
    {
        MathChannelResult result = MathChannelBuilder.Build(
            Log(), [Definition("AFR Error", "AFR - AFR Target 1")]);

        LogChannel channel = Assert.Single(result.Channels);

        Assert.Empty(result.Problems);
        Assert.Equal("AFR Error", channel.Name);
        Assert.Equal(4, channel.Length);
        Assert.Equal(-1.7, channel.At(0), 4);
        Assert.Equal(0.0, channel.At(1), 4);
        Assert.Equal(-0.5, channel.At(2), 4);
    }

    [Fact]
    public void AMissingReadingLeavesAGapRatherThanANumber()
    {
        // The plot draws NaN as a break. Filling it would invent data.
        MathChannelResult result = MathChannelBuilder.Build(
            Log(), [Definition("AFR Error", "AFR - AFR Target 1")]);

        Assert.True(double.IsNaN(result.Channels[0].At(3)));
    }

    [Fact]
    public void TheTimeBaseCanBeReadLikeAnyOtherChannel()
    {
        MathChannelResult result = MathChannelBuilder.Build(Log(), [Definition("Half", "Time / 2")]);

        Assert.Equal(0.05, result.Channels[0].At(1), 4);
    }

    [Fact]
    public void ACalculatedChannelCanBuildOnAnEarlierOne()
    {
        MathChannelResult result = MathChannelBuilder.Build(Log(),
        [
            Definition("AFR Error", "AFR - AFR Target 1"),
            Definition("Error Percent", "AFR Error / AFR Target 1 * 100"),
        ]);

        Assert.Equal(2, result.Channels.Count);
        Assert.Equal(-1.7 / 14.7 * 100, result.Channels[1].At(0), 3);
    }

    [Fact]
    public void ADefinitionCannotReadOneDeclaredAfterIt()
    {
        // Order is the only thing keeping this from being a cycle.
        MathChannelResult result = MathChannelBuilder.Build(Log(),
        [
            Definition("Second", "First * 2"),
            Definition("First", "RPM / 2"),
        ]);

        Assert.Single(result.Channels);
        Assert.Equal("First", result.Channels[0].Name);
        Assert.Contains("First", Assert.Single(result.Problems).Reason);
    }

    [Fact]
    public void ADefinitionThatNamesAnAbsentChannelIsReportedNotThrown()
    {
        // A definition kept from another car must not stop this log opening.
        MathChannelResult result = MathChannelBuilder.Build(
            Log(), [Definition("Power", "Torque * RPM / 5252")]);

        Assert.Empty(result.Channels);
        MathChannelProblem problem = Assert.Single(result.Problems);
        Assert.Equal("Power", problem.Name);
        Assert.Contains("Torque", problem.Reason);
    }

    [Fact]
    public void OneBrokenDefinitionDoesNotStopTheOthers()
    {
        MathChannelResult result = MathChannelBuilder.Build(Log(),
        [
            Definition("Broken", "RPM +"),
            Definition("Fine", "RPM * 2"),
        ]);

        Assert.Equal("Fine", Assert.Single(result.Channels).Name);
        Assert.Equal("Broken", Assert.Single(result.Problems).Name);
    }

    [Fact]
    public void ANameAlreadyInTheLogIsRefused()
    {
        // Two channels with one name are indistinguishable everywhere they are
        // used — the sidebar, the axis pickers, the filters.
        MathChannelResult result = MathChannelBuilder.Build(Log(), [Definition("RPM", "RPM * 2")]);

        Assert.Empty(result.Channels);
        Assert.Contains("already has", Assert.Single(result.Problems).Reason);
    }

    [Fact]
    public void ADisabledDefinitionIsSkippedSilently()
    {
        MathChannelResult result = MathChannelBuilder.Build(
            Log(), [Definition("AFR Error", "AFR - AFR Target 1") with { Enabled = false }]);

        Assert.Empty(result.Channels);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void AnInfiniteResultBecomesAGap()
    {
        // Dividing by a channel that reaches zero would otherwise give the
        // channel an infinite range and flatten every real value against it.
        var doc = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0, 0.1], preservePrecision: true),
            Channels = [new LogChannel("RPM", "RPM", 0, [0, 1000])],
            FormatName = "test",
        };

        MathChannelResult result = MathChannelBuilder.Build(doc, [Definition("Inverse", "1000 / RPM")]);

        Assert.True(double.IsNaN(result.Channels[0].At(0)));
        Assert.Equal(1.0, result.Channels[0].At(1), 4);
        Assert.Equal(1.0, result.Channels[0].Max, 4);
    }

    [Fact]
    public void TheUnitsAndPrecisionAreTakenFromTheDefinition()
    {
        MathChannelResult result = MathChannelBuilder.Build(Log(),
            [new MathChannel { Name = "Boost bar", Expression = "RPM / 1000", Units = "bar", Digits = 3 }]);

        Assert.Equal("bar", result.Channels[0].Units);
        Assert.Equal("2.000", result.Channels[0].Format(2.0));
    }

    // ----- persistence ------------------------------------------------------

    private string TempFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"olv-math-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _temp.Add(directory);
        return Path.Combine(directory, "math.json");
    }

    [Fact]
    public void DefinitionsSurviveARestart()
    {
        string path = TempFile();

        var store = new MathChannelStore(path);
        store.Add(Definition("AFR Error", "AFR - AFR Target 1"));

        MathChannel reloaded = Assert.Single(new MathChannelStore(path).Channels);
        Assert.Equal("AFR Error", reloaded.Name);
        Assert.Equal("AFR - AFR Target 1", reloaded.Expression);
        Assert.Equal("AFR", reloaded.Units);
        Assert.True(reloaded.Enabled);
    }

    [Fact]
    public void ADuplicateNameIsRefused()
    {
        var store = new MathChannelStore(TempFile());
        store.Add(Definition("AFR Error", "AFR - AFR Target 1"));

        Assert.Throws<InvalidOperationException>(() => store.Add(Definition("afr error", "1")));
    }

    [Fact]
    public void EditingReplacesInPlaceAndKeepsTheOrder()
    {
        string path = TempFile();
        var store = new MathChannelStore(path);
        store.Add(Definition("First", "RPM"));
        store.Add(Definition("Second", "AFR"));

        store.Replace(store.Channels[0], Definition("First", "RPM * 2"));

        var reloaded = new MathChannelStore(path);
        Assert.Equal(["First", "Second"], reloaded.Channels.Select(c => c.Name));
        Assert.Equal("RPM * 2", reloaded.Channels[0].Expression);
    }

    [Fact]
    public void RemovingPersists()
    {
        string path = TempFile();
        var store = new MathChannelStore(path);
        store.Add(Definition("Gone", "RPM"));

        Assert.True(store.Remove(store.Channels[0]));
        Assert.Empty(new MathChannelStore(path).Channels);
    }

    [Fact]
    public void AnEntryWithNoExpressionIsDroppedOnLoad()
    {
        // The file is meant to be hand-editable, so it has to survive a bad edit.
        string path = TempFile();
        File.WriteAllText(path, """
            {
              "version": 1,
              "channels": [
                { "name": "Good", "expression": "RPM * 2" },
                { "name": "NoExpression" },
                { "expression": "RPM" }
              ]
            }
            """);

        Assert.Equal(["Good"], new MathChannelStore(path).Channels.Select(c => c.Name));
    }

    [Fact]
    public void AMissingFileIsSimplyNoChannels() =>
        Assert.Empty(new MathChannelStore(Path.Combine(Path.GetTempPath(), $"olv-none-{Guid.NewGuid():N}.json")).Channels);
}
