using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The rules both readers ask about a time column, tested on their own rather
/// than only through a file. They used to be written out twice and disagree.
/// </summary>
public class TimeBaseTests
{
    /// <summary>Element by element, so a mismatch names the sample it is at.</summary>
    private static void Same(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i], 6);
    }

    // ----- does it run forwards ----------------------------------------------

    [Fact]
    public void AColumnThatRisesRises() =>
        Assert.True(TimeBase.Rises([0, 0.1, 0.2]));

    [Fact]
    public void AColumnThatStandsStillDoesNot() =>
        Assert.False(TimeBase.Rises([4, 4, 4]));

    [Fact]
    public void AColumnThatRunsBackwardsDoesNot() =>
        Assert.False(TimeBase.Rises([9, 5, 1]));

    /// <summary>
    /// The MLG reader's bug: NaN compares false against everything, so asking
    /// whether the last reading beat the first threw away three of seven real
    /// logs and read half an hour of driving as seven hours.
    /// </summary>
    [Fact]
    public void AHoleAtTheStartDoesNotHideThatItRises() =>
        Assert.True(TimeBase.Rises([double.NaN, 0, 0.1, 0.2]));

    [Fact]
    public void AHoleAtTheEndDoesNotHideThatItRises() =>
        Assert.True(TimeBase.Rises([0, 0.1, 0.2, double.NaN]));

    [Fact]
    public void NothingRealIsNoTimeBase() =>
        Assert.False(TimeBase.Rises([double.NaN, double.NaN]));

    [Fact]
    public void OneRealReadingIsNoTimeBase() =>
        Assert.False(TimeBase.Rises([double.NaN, 3, double.NaN]));

    // ----- does it ever go backwards -----------------------------------------

    /// <summary>
    /// The delimited reader's half of the same bug, with the opposite sign: it
    /// tested each reading against the one before it, and a NaN in between made
    /// that comparison false — so a column that plainly steps back was accepted.
    /// </summary>
    [Fact]
    public void AStepBackAcrossAHoleIsStillAStepBack() =>
        Assert.False(TimeBase.NeverFalls([5, double.NaN, 1]));

    [Fact]
    public void AHoleOnItsOwnIsNotAStepBack() =>
        Assert.True(TimeBase.NeverFalls([1, double.NaN, 5]));

    [Fact]
    public void ColumnsThatHoldTheirValueNeverFall() =>
        Assert.True(TimeBase.NeverFalls([1, 1, 2, 2]));

    // ----- closing the holes --------------------------------------------------

    [Fact]
    public void AColumnWithNoHolesIsLeftExactlyAsItIs()
    {
        double[] values = [0, 0.1, 0.2];

        Assert.Same(values, TimeBase.Fill(values));
    }

    [Fact]
    public void AHoleInTheMiddleIsInterpolatedBetweenItsNeighbours() =>
        Same([0, 1, 2, 3, 4], TimeBase.Fill([0, double.NaN, double.NaN, 3, 4]));

    /// <summary>
    /// Carried back at the rate of the rest rather than repeated, so the base
    /// still rises strictly — two samples at one instant hand a zero interval to
    /// everything that divides by one.
    /// </summary>
    [Fact]
    public void AHoleAtTheStartIsCarriedBackAtTheRateOfTheRest() =>
        Same([-1, 0, 1, 2], TimeBase.Fill([double.NaN, 0, 1, 2]));

    [Fact]
    public void AHoleAtTheEndIsCarriedOnAtTheRateOfTheRest() =>
        Same([0, 1, 2, 3], TimeBase.Fill([0, 1, 2, double.NaN]));

    [Fact]
    public void AFilledBaseHoldsNoHolesAnywhere()
    {
        double[] filled = TimeBase.Fill(
            [double.NaN, double.NaN, 2, double.NaN, 4, double.NaN, double.NaN]);

        Assert.All(filled, v => Assert.True(double.IsFinite(v)));

        for (int i = 1; i < filled.Length; i++)
            Assert.True(filled[i] > filled[i - 1], $"sample {i} did not rise");
    }

    [Fact]
    public void AColumnOfNothingRealIsHandedBackUntouched()
    {
        double[] values = [double.NaN, double.NaN];

        Assert.Same(values, TimeBase.Fill(values));
    }
}
