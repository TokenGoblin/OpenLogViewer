using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Smoothing a trace for the eye.
///
/// A median rather than an average, because sensor noise arrives as spikes and
/// an average smears each spike across its whole window — one bad sample in
/// fifteen moving the line for fifteen samples is worse than the spike was.
/// </summary>
public class SmoothingTests
{
    [Fact]
    public void ASingleSpikeIsRemovedRatherThanSpread()
    {
        // The whole reason this is a median. An average of five would pull every
        // one of the five samples around the spike a fifth of the way towards
        // it, turning one wrong reading into five.
        double[] flat = [10, 10, 10, 100, 10, 10, 10];

        Assert.Equal([10, 10, 10, 10, 10, 10, 10], Smoothing.Median(flat, 5));
    }

    [Fact]
    public void AStepSurvivesRatherThanBeingRoundedOff()
    {
        // A median keeps an edge where an average makes a ramp of it. A step
        // from one pressure to another is a real thing the trace should show.
        double[] step = [0, 0, 0, 0, 0, 10, 10, 10, 10, 10];

        double[] smoothed = Smoothing.Median(step, 5);

        Assert.Equal(0, smoothed[3]);
        Assert.Equal(10, smoothed[6]);
    }

    [Fact]
    public void TheWindowShrinksAtTheEndsRatherThanInventingData()
    {
        // Padding an end bends the line towards whatever was invented, at
        // exactly the places people read a trace's start and finish.
        double[] rising = [1, 2, 3, 4, 5];

        double[] smoothed = Smoothing.Median(rising, 5);

        Assert.Equal(2, smoothed[0]);      // the median of 1, 2, 3
        Assert.Equal(4, smoothed[^1]);     // the median of 3, 4, 5
    }

    [Fact]
    public void AMissingSampleStaysMissingSoThePenStillLifts()
    {
        double[] gapped = [10, 10, double.NaN, 10, 10];

        double[] smoothed = Smoothing.Median(gapped, 3);

        Assert.True(double.IsNaN(smoothed[2]));
        Assert.Equal(10, smoothed[0]);
    }

    [Fact]
    public void MissingSamplesInsideAWindowThinTheEvidenceRatherThanPoisonIt()
    {
        double[] mostlyGone = [double.NaN, double.NaN, 7, double.NaN, double.NaN];

        Assert.Equal(7, Smoothing.Median(mostlyGone, 5)[2]);
    }

    [Fact]
    public void NoSmoothingLeavesEverySampleWhereItWas()
    {
        double[] noisy = [3, 100, 3, 100, 3];

        Assert.Equal(noisy, Smoothing.Median(noisy, 1));
        Assert.Equal(noisy, Smoothing.Median(noisy, Smoothing.Window(SmoothingLevel.None)));
    }

    [Fact]
    public void TheLevelsAreCountedInSamplesRatherThanSeconds()
    {
        // Noise of this kind is per reading, so a window stated in time smooths
        // nothing on a 1 Hz log and destroys a 50 Hz one. The user's own log is
        // 22,024 samples over 22,023 seconds.
        Assert.Equal(1, Smoothing.Window(SmoothingLevel.None));
        Assert.Equal(5, Smoothing.Window(SmoothingLevel.Light));
        Assert.Equal(15, Smoothing.Window(SmoothingLevel.Medium));
        Assert.Equal(51, Smoothing.Window(SmoothingLevel.Strong));
    }

    [Fact]
    public void AnEvenWindowTakesTheMeanOfTheMiddlePair()
    {
        double[] values = [1, 2, 3, 4];

        Assert.Equal(2.5, Smoothing.Median(values, 4)[1], 6);
    }

    // ----- what smoothing must never reach -----------------------------------

    [Fact]
    public void SmoothingIsRememberedAlongsideAColourAndAScale()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"olv-style-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ChannelStyleStore(path);

            Assert.True(store.SetSmoothing("Coolant_pressure", SmoothingLevel.Strong));
            Assert.True(store.SetColor("Coolant_pressure", 0x00FF00));

            var reopened = new ChannelStyleStore(path);
            ChannelStyle? style = reopened.For("Coolant_pressure");

            Assert.Equal(SmoothingLevel.Strong, style!.Smoothing);
            Assert.True(style.HasColor);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public void ClearingTheSmoothingAloneLeavesNoEntryBehind()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"olv-style-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ChannelStyleStore(path);

            store.SetSmoothing("AFR", SmoothingLevel.Light);
            store.SetSmoothing("AFR", SmoothingLevel.None);

            Assert.Null(store.For("AFR"));
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public void TheInsightsReadTheChannelAsLoggedWhateverIsSmoothed()
    {
        // The one that matters. A smoothed AFR hides exactly the single-sample
        // lean excursion that damages a piston, so nothing that judges an engine
        // may ever see one. Smoothing lives on the view's channel item and the
        // analysis is handed the log, so there is no path between them — this
        // pins that the log itself is never rewritten.
        int n = 200;
        double[] afr = [.. Enumerable.Range(0, n).Select(i => i == 100 ? 17.5 : 13.5)];

        var log = new LogDocument
        {
            FormatName = "test",
            FilePath = "spike.mlg",
            Time = new LogChannel("Time", "s", 2, [.. Enumerable.Range(0, n).Select(i => i * 0.1)]),
            Channels =
            [
                new LogChannel("RPM", "RPM", 2, [.. Enumerable.Repeat(3000.0, n)]),
                new LogChannel("MAP", "kPa", 2, [.. Enumerable.Repeat(150.0, n)]),
                new LogChannel("AFR", "AFR", 2, afr),
                new LogChannel("AFR Target 1", "AFR", 2, [.. Enumerable.Repeat(13.5, n)]),
                new LogChannel("CLT", "°F", 2, [.. Enumerable.Repeat(190.0, n)]),
            ],
        };

        // The spike is one sample in two hundred — a median of five erases it.
        Assert.Equal(13.5, Smoothing.Median(afr, 5)[100]);

        // And the log still holds it, so anything measuring finds it.
        Assert.Equal(17.5, log.FindChannel("AFR")!.At(100));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(16)]
    public void AnEvenWindowDoesNotRunOffTheEndOfItsBuffer(int window)
    {
        // The scratch buffer was sized to the window, but the span scanned runs
        // from i - window/2 to i + window/2, which is window + 1 samples. Every
        // even window threw on any input long enough to reach a full one. It
        // never fired in the application because the levels are all odd — but
        // the method is public and the test above documents even windows as
        // supported.
        double[] values = [.. Enumerable.Range(0, 40).Select(i => (double)i)];

        double[] smoothed = Smoothing.Median(values, window);

        Assert.Equal(values.Length, smoothed.Length);
        Assert.All(smoothed, v => Assert.False(double.IsNaN(v)));
    }
}
