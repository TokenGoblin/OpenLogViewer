using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The dangerous-constant guard against the names real firmware actually uses.
///
/// <para>
/// The alias list was written from what MegaSquirt, Speeduino and rusEFI INIs
/// were understood to call things, and was documented as a heuristic rather
/// than a catalogue. These names are not from that understanding — they were
/// read off a Speeduino running 202501 over the agent API, out of the 682
/// settings it actually declares. Four of them were being missed.
/// </para>
/// <para>
/// <c>launchEnable</c> is the one that mattered most: the bench board this was
/// read from has launch control switched on with a 2,700 rpm soft limit, so
/// its limiter is not inert, and turning that on or off was reaching the ECU
/// without ever asking.
/// </para>
/// </summary>
public class DangerousConstantsRealFirmwareTests
{
    /// <summary>A layout declaring exactly the names given, so Find has something to match against.</summary>
    private static TuneLayout Declaring(params string[] names) =>
        new()
        {
            Pages = [],
            Constants =
            [
                .. names.Select(n => new TuneConstant
                {
                    Name = n, Page = 0, Offset = 0, Type = RealtimeType.U08,
                }),
            ],
        };

    [Theory]
    // Rev limiting, as Speeduino 202501 spells it.
    [InlineData("hardRevLim")]
    [InlineData("SoftRevLim")]
    [InlineData("SoftLimMax")]
    [InlineData("SoftLimitMode")]
    [InlineData("SoftLimRetard")]
    // Launch control — the board on the bench has this enabled.
    [InlineData("launchEnable")]
    [InlineData("lnchHardLim")]
    [InlineData("lnchSoftLim")]
    [InlineData("launchHiLo")]
    // Boost.
    [InlineData("boostLimit")]
    [InlineData("boostCutEnabled")]
    [InlineData("boostByGear1")]
    // How the limiter cuts, and what protection cuts fuel.
    [InlineData("hardCutType")]
    [InlineData("kindOfLimiting0")]
    [InlineData("afrProtectCutTime")]
    public void ARealSafetyCriticalSpeeduinoSettingIsRecognised(string name) =>
        Assert.True(
            DangerousConstants.IsDangerous(Declaring(name), name),
            $"{name} is a setting whose job is to stop the engine hurting itself, and it was not recognised");

    [Theory]
    // Ordinary settings from the same 682, which must not all become dangerous
    // — a guard that fires on everything is one people learn to pass blindly.
    [InlineData("crankingRPM")]
    [InlineData("aseTaperTime")]
    [InlineData("aeColdPct")]
    [InlineData("battVCorMode")]
    [InlineData("boostFreq")]
    [InlineData("boostKP")]
    [InlineData("iacTPSlimit")]
    public void AnOrdinarySettingIsNotTreatedAsDangerous(string name) =>
        Assert.False(
            DangerousConstants.IsDangerous(Declaring(name), name),
            $"{name} is an ordinary setting and should not need confirming");

    [Fact]
    public void ASettingThisFirmwareDoesNotDeclareIsNotJudgedAtAll()
    {
        // Matching on a name the layout has never heard of would refuse a write
        // that is about to be rejected for a better reason.
        Assert.False(DangerousConstants.IsDangerous(Declaring("crankingRPM"), "hardRevLim"));
    }
}
