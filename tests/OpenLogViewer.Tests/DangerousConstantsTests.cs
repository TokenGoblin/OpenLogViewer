using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Recognising the handful of settings that exist specifically to stop an
/// engine hurting itself, across the wildly different spellings real firmware
/// gives them.
///
/// This is a heuristic and the tests say so: the point is not that every real
/// INI's rev limiter is caught (nobody has catalogued that), it is that the
/// small set of spellings this declares are matched, that an unrelated setting
/// is left alone, and that a name the firmware does not even declare is never
/// flagged.
/// </summary>
public class DangerousConstantsTests
{
    private static TuneLayout LayoutWith(params string[] names) => new()
    {
        Pages = [new TunePage { Index = 0, Size = 64, Identifier = "", ReadCommand = "" }],
        Constants =
        [
            .. names.Select(n => new TuneConstant
            {
                Name = n, Page = 0, Offset = 0, Type = RealtimeType.U16, Low = 0, High = 10000,
            }),
        ],
    };

    [Theory]
    [InlineData("revLimit", DangerousRole.RevLimiter)]
    [InlineData("SoftLimit", DangerousRole.RevLimiter)]
    [InlineData("HardRevLim", DangerousRole.RevLimiter)]
    [InlineData("rpmHardLimit", DangerousRole.RevLimiter)]
    [InlineData("launchRpm", DangerousRole.LaunchControlRpm)]
    [InlineData("launchTiming", DangerousRole.LaunchControlRpm)]
    [InlineData("flatShiftRpm", DangerousRole.LaunchControlRpm)]
    [InlineData("boostCtrlMax", DangerousRole.BoostLimit)]
    [InlineData("overBoost", DangerousRole.BoostLimit)]
    [InlineData("ignitionCutTime", DangerousRole.IgnitionCut)]
    [InlineData("sparkCutRpm", DangerousRole.IgnitionCut)]
    [InlineData("fuelCutRpm", DangerousRole.FuelCut)]
    [InlineData("decelFuelCutOff", DangerousRole.FuelCut)]
    public void RecognisedSpellingsMatchTheRoleTheyPlay(string name, DangerousRole role)
    {
        TuneLayout layout = LayoutWith(name);

        Assert.Equal(role, DangerousConstants.Find(layout, name));
        Assert.True(DangerousConstants.IsDangerous(layout, name));
    }

    [Theory]
    [InlineData("crankingRPM")]
    [InlineData("veTable1")]
    [InlineData("injectorLatency")]
    [InlineData("vehicleName")]
    public void AnOrdinarySettingMatchesNothing(string name)
    {
        TuneLayout layout = LayoutWith(name);

        Assert.Null(DangerousConstants.Find(layout, name));
        Assert.False(DangerousConstants.IsDangerous(layout, name));
    }

    [Fact]
    public void ASpellingTheFirmwareDoesNotDeclareIsNeverFlagged()
    {
        // Matching by name alone, with no such constant in the tune, would
        // flag a typo just as readily as a real setting, and would refuse a
        // write for the wrong reason -- "no such setting" is the honest one.
        TuneLayout layout = LayoutWith("crankingRPM");

        Assert.Null(DangerousConstants.Find(layout, "revLimit"));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveLikeTheRestOfAnIni()
    {
        TuneLayout layout = LayoutWith("REVLIMIT");

        Assert.Equal(DangerousRole.RevLimiter, DangerousConstants.Find(layout, "REVLIMIT"));
    }

    [Fact]
    public void AnEmptyOrBlankNameIsNeverFlagged()
    {
        TuneLayout layout = LayoutWith("revLimit");

        Assert.Null(DangerousConstants.Find(layout, ""));
        Assert.Null(DangerousConstants.Find(layout, "   "));
    }
}
