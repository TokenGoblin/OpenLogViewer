using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Camshafts offered by description, so the page works without a cam card.
///
/// A preset list is only worth having if the numbers in it are the numbers real
/// cams have, and there is no formula to check them against — they are
/// conventions. What can be checked is that they are internally consistent with
/// how cams are actually specified, and that is what most of this file does: the
/// overlap each entry implies is worked out from its own durations and lobe
/// separation and held against what catalogue grinds of that description come out
/// at, and the gap between the seat-to-seat and 0.050 in figures is held inside
/// the range real grinds show.
///
/// The rest guards the trap the list exists to avoid — a 0.050 in duration ending
/// up in a box that wants seat to seat.
/// </summary>
public class CamProfileTests
{
    /// <summary>
    /// The overlap formula against two grinds whose catalogue overlap is known:
    /// a 252/252 on a 110 lobe separation is 32°, and Comp's XE274H at 274/286 on
    /// 110 is 60°. Both fall out of (DI + DE)/2 − 2·LSA exactly.
    /// </summary>
    [Theory]
    [InlineData(252, 252, 110, 32)]
    [InlineData(274, 286, 110, 60)]
    [InlineData(300, 300, 106, 88)]
    public void OverlapMatchesTheCatalogueFigure(
        double intake, double exhaust, double lsa, double expected)
    {
        CamProfile cam = new("test", intake, exhaust, 0, 0, lsa, 30, 600, "");

        Assert.Equal(expected, cam.OverlapDeg, 6);
    }

    /// <summary>
    /// The ladder has to actually be a ladder: every step is a longer cam with
    /// more overlap than the one below it. A list that wandered would be worse
    /// than no list, because the whole point is that picking further down the
    /// list is picking a wilder engine.
    /// </summary>
    [Fact]
    public void EveryStepUpIsALongerCamWithMoreOverlap()
    {
        var ladder = CamProfiles.All.Where(c => !c.IsCustom).ToList();

        Assert.Equal(5, ladder.Count);

        for (int i = 1; i < ladder.Count; i++)
        {
            Assert.True(
                ladder[i].IntakeDurationDeg > ladder[i - 1].IntakeDurationDeg,
                $"{ladder[i].Name} is not longer than {ladder[i - 1].Name}");

            Assert.True(
                ladder[i].ExhaustDurationDeg > ladder[i - 1].ExhaustDurationDeg,
                $"{ladder[i].Name} exhaust is not longer than {ladder[i - 1].Name}");

            Assert.True(
                ladder[i].OverlapDeg > ladder[i - 1].OverlapDeg,
                $"{ladder[i].Name} has no more overlap than {ladder[i - 1].Name}");
        }
    }

    /// <summary>
    /// Seat-to-seat runs somewhere around 44 to 52 crank degrees longer than the
    /// figure at 0.050 in on real grinds. Holding every entry to that is what
    /// stops one of them quietly carrying a pair of numbers no cam could have.
    ///
    /// The band is wider on the exhaust side than an even spread would suggest,
    /// and deliberately so: exhaust lobes are commonly ground to open quicker, and
    /// Comp's own XE274H — 274/286 seat to seat, 230/236 at 0.050 in — shows 44 on
    /// the inlet against 50 on the exhaust. Narrowing this to look tidier would
    /// mean rejecting cams that exist.
    /// </summary>
    [Fact]
    public void BothDurationFiguresAreConsistentWithARealGrind()
    {
        foreach (CamProfile cam in CamProfiles.All.Where(c => !c.IsCustom))
        {
            double intakeGap = cam.IntakeDurationDeg - cam.IntakeAtFiftyDeg;
            double exhaustGap = cam.ExhaustDurationDeg - cam.ExhaustAtFiftyDeg;

            Assert.InRange(intakeGap, 42, 54);
            Assert.InRange(exhaustGap, 42, 54);
        }
    }

    /// <summary>
    /// The seat-to-seat figure is always the larger one. Getting these the wrong
    /// way round in a table is easy and would shorten every pipe on the page.
    /// </summary>
    [Fact]
    public void SeatToSeatIsAlwaysTheLongerFigure()
    {
        foreach (CamProfile cam in CamProfiles.All.Where(c => !c.IsCustom))
        {
            Assert.True(cam.IntakeDurationDeg > cam.IntakeAtFiftyDeg, cam.Name);
            Assert.True(cam.ExhaustDurationDeg > cam.ExhaustAtFiftyDeg, cam.Name);
        }
    }

    /// <summary>
    /// Overlap climbs from something a standard car would idle on to something
    /// with no idle at all, and the top of the ladder really is race-only. If the
    /// spread collapsed, the five choices would stop being five choices.
    /// </summary>
    [Fact]
    public void TheLadderSpansStockToNoIdleAtAll()
    {
        var ladder = CamProfiles.All.Where(c => !c.IsCustom).ToList();

        Assert.InRange(ladder[0].OverlapDeg, 20, 40);
        Assert.True(ladder[^1].OverlapDeg > 95, $"race cam overlap is {ladder[^1].OverlapDeg:N0}°");

        // The mild end keeps its vacuum — which is the thing a stock cam is for,
        // rather than being indistinguishable from no cam at all.
        Assert.Contains("vacuum", CamProfiles.OverlapVerdict(ladder[0].OverlapDeg),
            StringComparison.Ordinal);

        Assert.Contains("race only", CamProfiles.OverlapVerdict(ladder[^1].OverlapDeg),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A wilder engine draws cooler air and runs a hotter pipe. Small effects, but
    /// they should at least point the right way.
    /// </summary>
    [Fact]
    public void GasConditionsMoveTheWayABuildActuallyDoes()
    {
        var ladder = CamProfiles.All.Where(c => !c.IsCustom).ToList();

        for (int i = 1; i < ladder.Count; i++)
        {
            Assert.True(ladder[i].ChargeCelsius < ladder[i - 1].ChargeCelsius, ladder[i].Name);
            Assert.True(ladder[i].ExhaustCelsius > ladder[i - 1].ExhaustCelsius, ladder[i].Name);
        }

        // And the whole spread is worth only a few per cent of a pipe, because
        // length follows the square root of absolute temperature.
        double mildest = ManifoldTuning.SpeedOfSoundExhaust(ladder[0].ExhaustCelsius);
        double wildest = ManifoldTuning.SpeedOfSoundExhaust(ladder[^1].ExhaustCelsius);

        Assert.InRange(wildest / mildest, 1.0, 1.10);
    }

    // ----- finding a profile again ----------------------------------------------

    /// <summary>Every entry has to be findable from the numbers it puts in the boxes.</summary>
    [Fact]
    public void EveryProfileIsFoundFromItsOwnDurations()
    {
        foreach (CamProfile cam in CamProfiles.All.Where(c => !c.IsCustom))
        {
            Assert.Equal(cam.Name, CamProfiles.For(cam.IntakeDurationDeg, cam.ExhaustDurationDeg).Name);

            Assert.Equal(
                cam.Name,
                CamProfiles.All[CamProfiles.IndexFor(cam.IntakeDurationDeg, cam.ExhaustDurationDeg)].Name);
        }
    }

    /// <summary>
    /// Anything that is not one of the five is somebody's own cam, and is labelled
    /// as such rather than being rounded to the nearest entry.
    /// </summary>
    [Theory]
    [InlineData(268, 274)]
    [InlineData(252, 300)]
    [InlineData(0, 0)]
    [InlineData(-10, 260)]
    public void AnythingElseIsTheirOwnCam(double intake, double exhaust)
    {
        Assert.True(CamProfiles.For(intake, exhaust).IsCustom);
        Assert.Equal(CamProfiles.All.Count - 1, CamProfiles.IndexFor(intake, exhaust));
    }

    /// <summary>
    /// Matching is on the durations only. Somebody who picks a cam and then sets
    /// their own charge temperature still has that cam, and the list must go on
    /// saying so.
    /// </summary>
    [Fact]
    public void ChangingTheGasDoesNotChangeWhichCamItIs()
    {
        CamProfile performance = CamProfiles.All[2];

        Assert.Equal(
            performance.Name,
            CamProfiles.For(performance.IntakeDurationDeg, performance.ExhaustDurationDeg).Name);
    }

    // ----- and they have to work on the page -------------------------------------

    /// <summary>
    /// Every profile has to produce a design the calculator is happy with. A preset
    /// that tripped the page's own duration guard would be the worst possible
    /// thing to ship in a list aimed at people who cannot check it.
    /// </summary>
    [Fact]
    public void EveryProfileProducesAWorkableDesign()
    {
        foreach (CamProfile cam in CamProfiles.All.Where(c => !c.IsCustom))
        {
            ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
            {
                IntakeDurationDeg = cam.IntakeDurationDeg,
                ExhaustDurationDeg = cam.ExhaustDurationDeg,
                IntakeAirCelsius = cam.ChargeCelsius,
                ExhaustCelsius = cam.ExhaustCelsius,
            });

            Assert.DoesNotContain(plan.Warnings, w => w.Severity == "stop");

            Assert.True(plan.Intake.Recommended.LengthMm > 0, cam.Name);
            Assert.True(plan.Exhaust.Recommended.LengthMm > 0, cam.Name);
            Assert.True(plan.Intake.RunnerDiameterMm > 0, cam.Name);
            Assert.True(plan.Exhaust.PrimaryDiameterMm > 0, cam.Name);
        }
    }

    /// <summary>
    /// The reason the seat-to-seat distinction is laboured everywhere: putting the
    /// 0.050 in figure in the box instead costs about a fifth of every length, and
    /// nothing about the result looks wrong.
    ///
    /// This test exists to keep that claim honest, since the documentation makes it.
    /// </summary>
    [Fact]
    public void UsingTheFiftyThouFigureWouldShortenEveryPipeByAboutAFifth()
    {
        CamProfile cam = CamProfiles.All[2];

        ManifoldSpec right = new()
        {
            IntakeDurationDeg = cam.IntakeDurationDeg,
            ExhaustDurationDeg = cam.ExhaustDurationDeg,
        };

        ManifoldSpec wrong = right with
        {
            IntakeDurationDeg = cam.IntakeAtFiftyDeg,
            ExhaustDurationDeg = cam.ExhaustAtFiftyDeg,
        };

        double correct = ManifoldTuning.Plan(right).Intake.Recommended.EffectiveLengthMm;
        double mistaken = ManifoldTuning.Plan(wrong).Intake.Recommended.EffectiveLengthMm;

        Assert.InRange(1 - (mistaken / correct), 0.12, 0.25);
    }

    /// <summary>Nothing in the list may be blank, and the custom entry must be last.</summary>
    [Fact]
    public void TheListIsWellFormed()
    {
        Assert.All(CamProfiles.All, cam =>
        {
            Assert.False(string.IsNullOrWhiteSpace(cam.Name));
            Assert.False(string.IsNullOrWhiteSpace(cam.Note));
        });

        Assert.True(CamProfiles.All[^1].IsCustom);
        Assert.Single(CamProfiles.All, c => c.IsCustom);
        Assert.True(double.IsNaN(CamProfiles.All[^1].OverlapDeg));
    }
}
