using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Working out how far the fuelling is out from a log alone.
///
/// This could not run at all without the tune's own table, which meant it could
/// not run on a MaxxECU — whose tune cannot be read — nor on any log opened away
/// from the car it came from. The ECU's grid is still better when there is one,
/// because its cells line up with the cells being tuned. It is no longer the
/// difference between working and not.
/// </summary>
public class VeWithoutATuneTests
{
    /// <summary>
    /// A run where the mixture is ten per cent rich everywhere: lambda 0.9
    /// against a target of 1.0.
    /// </summary>
    private static (LogChannel Rpm, LogChannel Load, LogChannel Lambda, LogChannel Target) Rich(
        double measured = 0.9, int samples = 400)
    {
        var rpm = new double[samples];
        var load = new double[samples];
        var lambda = new double[samples];
        var target = new double[samples];

        for (int i = 0; i < samples; i++)
        {
            rpm[i] = 1000 + (i % 20) * 200;
            load[i] = 30 + (i % 10) * 15;
            lambda[i] = measured;
            target[i] = 1.0;
        }

        return (
            new LogChannel("RPM", "rpm", 0, rpm),
            new LogChannel("MAP", "kPa", 0, load),
            new LogChannel("Lambda", "", 3, lambda),
            new LogChannel("Lambda target", "", 3, target));
    }

    [Fact]
    public void AGridCanBeTakenFromTheLogItself()
    {
        (LogChannel rpm, LogChannel load, _, _) = Rich();

        TuneTable grid = VeAnalysis.GridFrom(rpm, load, 8, 6, 0, 399);

        Assert.Equal(8, grid.Columns);
        Assert.Equal(6, grid.Rows);

        // Spread over what the channel actually did, and in order.
        Assert.True(grid.X.Breakpoints[0] >= rpm.Min);
        Assert.True(grid.X.Breakpoints[^1] <= rpm.Max);
        Assert.True(grid.X.Breakpoints.Zip(grid.X.Breakpoints.Skip(1)).All(p => p.Second > p.First));
    }

    [Fact]
    public void AMixtureRichOfTargetAsksForLessFuel()
    {
        // The direction is the thing worth being certain of. Lambda under target
        // is richer than asked for, which means the ECU thought there was more
        // air than there was, which means the number in the cell is too high.
        (LogChannel rpm, LogChannel load, LogChannel lambda, LogChannel target) = Rich(measured: 0.9);

        TuneTable grid = VeAnalysis.GridFrom(rpm, load, 4, 4, 0, 399);
        VeAnalysisResult result = VeAnalysis.Analyse(grid, rpm, load, lambda, target, 0, 399);

        Assert.False(result.IsEmpty);

        double?[] changes = [.. result.ChangePercent.Cast<double?>().Where(v => v is not null)];

        Assert.NotEmpty(changes);
        Assert.All(changes, c => Assert.True(c < 0, $"a rich cell asked for {c:F1}%"));
        Assert.All(changes, c => Assert.Equal(-10, c!.Value, 1));
    }

    [Fact]
    public void AMixtureLeanOfTargetAsksForMoreFuel()
    {
        (LogChannel rpm, LogChannel load, LogChannel lambda, LogChannel target) = Rich(measured: 1.08);

        TuneTable grid = VeAnalysis.GridFrom(rpm, load, 4, 4, 0, 399);
        VeAnalysisResult result = VeAnalysis.Analyse(grid, rpm, load, lambda, target, 0, 399);

        double?[] changes = [.. result.ChangePercent.Cast<double?>().Where(v => v is not null)];

        Assert.NotEmpty(changes);
        Assert.All(changes, c => Assert.Equal(8, c!.Value, 1));
    }

    [Fact]
    public void WithNoCurrentValuesThereIsNoSuggestedValue()
    {
        // Only an honest answer is available: how far out the cell is. Inventing
        // a new number would mean inventing the old one.
        (LogChannel rpm, LogChannel load, LogChannel lambda, LogChannel target) = Rich();

        TuneTable grid = VeAnalysis.GridFrom(rpm, load, 4, 4, 0, 399);
        VeAnalysisResult result = VeAnalysis.Analyse(grid, rpm, load, lambda, target, 0, 399);

        Assert.All(result.Suggested.Cast<double?>(), Assert.Null);
        Assert.Contains(result.ChangePercent.Cast<double?>(), v => v is not null);
    }

    [Fact]
    public void WithTheTunesOwnValuesTheNewNumberIsGivenToo()
    {
        // The better case, unchanged: a grid carrying the current fuelling gets
        // a value to write as well as a percentage.
        (LogChannel rpm, LogChannel load, LogChannel lambda, LogChannel target) = Rich(measured: 0.9);

        TuneTable grid = VeAnalysis.GridFrom(rpm, load, 4, 4, 0, 399);
        var values = new double[4, 4];

        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                values[c, r] = 80;

        VeAnalysisResult result = VeAnalysis.Analyse(
            grid with { Values = values }, rpm, load, lambda, target, 0, 399);

        double?[] suggested = [.. result.Suggested.Cast<double?>().Where(v => v is not null)];

        Assert.NotEmpty(suggested);
        Assert.All(suggested, v => Assert.Equal(72, v!.Value, 1));
    }

    [Fact]
    public void MeasuringInAfrAgainstALambdaTargetIsRefused()
    {
        // Found on a real log. A MaxxECU logs both AFR and lambda, nothing about
        // the names says they are on different scales, and dividing 12.5 by 0.9
        // reports every cell as fifteen per cent lean — a full table of
        // confident nonsense, which is worse than an empty one because it looks
        // like an answer.
        (LogChannel rpm, LogChannel load, _, LogChannel target) = Rich();

        var afr = new LogChannel("AFR", "", 2, [.. Enumerable.Repeat(13.2, 400)]);

        VeAnalysisResult result = VeAnalysis.Analyse(
            VeAnalysis.GridFrom(rpm, load, 4, 4, 0, 399), rpm, load, afr, target, 0, 399);

        Assert.True(result.HasProblem);
        Assert.True(result.IsEmpty);

        // And it names both channels, because "pick a different one" is no use
        // without saying which two disagree.
        Assert.Contains("AFR", result.Problem!, StringComparison.Ordinal);
        Assert.Contains("Lambda target", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadlyMistunedEngineIsStillAnalysed()
    {
        // The guard must not catch a real answer. Twenty-five per cent out is a
        // genuinely poor tune and well inside what this should act on.
        (LogChannel rpm, LogChannel load, LogChannel lambda, LogChannel target) = Rich(measured: 1.25);

        VeAnalysisResult result = VeAnalysis.Analyse(
            VeAnalysis.GridFrom(rpm, load, 4, 4, 0, 399), rpm, load, lambda, target, 0, 399,
            settings: new VeAnalysisSettings { MaxChangePercent = 30 });

        Assert.False(result.HasProblem);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void ACellCrossedTwiceIsNotEnoughToActOn()
    {
        // What makes this trustworthy is what it refuses to do.
        (LogChannel rpm, LogChannel load, LogChannel lambda, LogChannel target) = Rich(samples: 8);

        TuneTable grid = VeAnalysis.GridFrom(rpm, load, 8, 8, 0, 7);

        VeAnalysisResult result = VeAnalysis.Analyse(
            grid, rpm, load, lambda, target, 0, 7,
            settings: new VeAnalysisSettings { MinimumSamples = 12 });

        Assert.True(result.IsEmpty);
        Assert.True(result.CellsThin > 0);
    }

    [Fact]
    public void OnePassNeverAsksForMoreThanItIsAllowed()
    {
        // A cell read during an accel-enrichment event can imply a correction
        // far larger than the table is actually wrong by.
        (LogChannel rpm, LogChannel load, LogChannel lambda, LogChannel target) = Rich(measured: 0.4);

        TuneTable grid = VeAnalysis.GridFrom(rpm, load, 4, 4, 0, 399);

        VeAnalysisResult result = VeAnalysis.Analyse(
            grid, rpm, load, lambda, target, 0, 399,
            settings: new VeAnalysisSettings { MaxChangePercent = 15 });

        Assert.All(
            result.ChangePercent.Cast<double?>().Where(v => v is not null),
            c => Assert.True(Math.Abs(c!.Value) <= 15.0001, $"asked for {c:F1}%"));
    }
}
