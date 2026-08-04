using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Volumetric efficiency offered as a kind of engine rather than as a number.
///
/// The figures are conventions and the tests treat them as such — what is checked
/// is that they are ordered the way engines actually are, that they sit inside
/// the range real engines occupy, and that a typed figure still wins.
/// </summary>
public class EngineFamilyTests
{
    [Fact]
    public void TheyAreOrderedFromWorstBreathingToBest()
    {
        // The order is the point: somebody scanning the list should be able to
        // find their engine by how modern its head is.
        EngineFamily[] real = [.. EngineFamilies.All.Where(f => !f.IsCustom)];

        Assert.True(real.Length >= 5);

        for (int i = 1; i < real.Length; i++)
            Assert.True(
                real[i].VolumetricEfficiency > real[i - 1].VolumetricEfficiency,
                $"{real[i].Name} should breathe better than {real[i - 1].Name}");
    }

    [Fact]
    public void EveryFigureIsOneARealEngineHas()
    {
        foreach (EngineFamily family in EngineFamilies.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(family.Name));
            Assert.False(string.IsNullOrWhiteSpace(family.Note));

            if (family.IsCustom) continue;

            // Below seventy is a broken engine; above a hundred and fifteen is
            // not a piston engine breathing through a throttle.
            Assert.InRange(family.VolumetricEfficiency, 70, 115);
        }
    }

    [Fact]
    public void OnlyTheRaceEngineIsAllowedPastAHundred()
    {
        // A tuned inlet length can pack in more than atmospheric at resonance,
        // which is the one honest way past a hundred per cent. Nothing with a
        // plenum and a single throttle should claim it.
        foreach (EngineFamily family in EngineFamilies.All)
            if (family.VolumetricEfficiency > 100)
                Assert.Contains("Race", family.Name);
    }

    [Fact]
    public void ThereIsAWayToSayYouMeasuredItYourself()
    {
        EngineFamily custom = EngineFamilies.All[^1];

        Assert.True(custom.IsCustom);
        Assert.Contains(EngineFamilies.All, f => f.IsCustom);
        Assert.Single(EngineFamilies.All.Where(f => f.IsCustom));
    }

    [Theory]
    [InlineData(75, "Older two-valve")]
    [InlineData(85, "Modern two-valve")]
    [InlineData(92, "Four-valve, fixed cams")]
    [InlineData(97, "Four-valve with cam phasing")]
    [InlineData(105, "Race, tuned intake")]
    public void AFigureFromTheListIsRecognisedAsThatKindOfEngine(double ve, string expected) =>
        Assert.Equal(expected, EngineFamilies.For(ve).Name);

    [Theory]
    // Anything that is not one of the listed figures is somebody's own number.
    [InlineData(88)]
    [InlineData(101)]
    [InlineData(63)]
    [InlineData(0)]
    [InlineData(-5)]
    public void AnythingElseIsTreatedAsMeasured(double ve) =>
        Assert.True(EngineFamilies.For(ve).IsCustom);

    [Fact]
    public void TheIndexAndTheFamilyAgreeWithEachOther()
    {
        // The combo box selects by index, so the two ways of asking must not be
        // able to disagree.
        foreach (EngineFamily family in EngineFamilies.All)
        {
            if (family.IsCustom) continue;

            int at = EngineFamilies.IndexFor(family.VolumetricEfficiency);

            Assert.Equal(family.Name, EngineFamilies.All[at].Name);
        }

        Assert.Equal(EngineFamilies.All.Count - 1, EngineFamilies.IndexFor(88));
    }

    [Fact]
    public void ChoosingAFamilyMovesTheWholeRecipe()
    {
        // The reason this exists: volumetric efficiency multiplies straight into
        // the air, so the kind of engine chosen is worth as much as a fifth of
        // the boost the build needs.
        var spec = new RecipeSpec { Litres = 2.0, TargetHorsepower = 500 };

        Recipe poor = EngineRecipe.Build(spec with { VolumetricEfficiency = 75 });
        Recipe good = EngineRecipe.Build(spec with { VolumetricEfficiency = 105 });

        Assert.Equal(poor.AirAtPeakPower, good.AirAtPeakPower, 6);
        Assert.True(poor.BoostKpa > good.BoostKpa);

        // Forty per cent more filling is forty per cent less manifold pressure
        // needed for the same air.
        Assert.Equal(105.0 / 75, poor.ManifoldKpa / good.ManifoldKpa, 3);
    }
}
