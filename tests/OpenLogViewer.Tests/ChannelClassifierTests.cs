using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class ChannelClassifierTests
{
    [Theory]
    [InlineData("DutyCycle1", "duty cycle 1")]
    [InlineData("Duty Cycle1", "duty cycle 1")]
    [InlineData("duty_cycle_1", "duty cycle 1")]
    [InlineData("VE1", "ve 1")]
    [InlineData("AFR2", "afr 2")]
    [InlineData("Batt V", "batt v")]
    [InlineData("Fuel: Warmup cor", "fuel warmup cor")]
    [InlineData("gpioadc4", "gpioadc 4")]
    [InlineData("MAP", "map")]
    public void NormaliseSplitsHumpsAndSeparators(string input, string expected) =>
        Assert.Equal(expected, ChannelClassifier.Normalise(input));

    [Theory]
    [InlineData("RPM")]
    [InlineData("MAP")]
    [InlineData("AFR")]
    [InlineData("CLT")]
    [InlineData("TPS")]
    [InlineData("Batt V")]
    [InlineData("VE1")]
    public void EverydayChannelsArePromotedToCommon(string name) =>
        Assert.Equal(ChannelCategory.Common, ChannelClassifier.Classify(name));

    [Theory]
    [InlineData("EGO cor2", ChannelCategory.Fuel)]
    [InlineData("Fuel: Accel enrich", ChannelCategory.Fuel)]
    [InlineData("MAFload", ChannelCategory.Fuel)]
    [InlineData("Boost psi", ChannelCategory.Common)]
    [InlineData("MAP dot", ChannelCategory.Air)]
    [InlineData("Barometer", ChannelCategory.Air)]
    [InlineData("EGT 6 temp", ChannelCategory.Temperature)]
    [InlineData("Dwell", ChannelCategory.Common)]
    [InlineData("Spark Advance", ChannelCategory.Common)]
    [InlineData("Closed loop idle target RPM", ChannelCategory.Idle)]
    [InlineData("ADC6", ChannelCategory.Electrical)]
    [InlineData("CAN error count", ChannelCategory.Diagnostics)]
    [InlineData("Mainloop time", ChannelCategory.Diagnostics)]
    [InlineData("SecL", ChannelCategory.Diagnostics)]
    [InlineData("Torque", ChannelCategory.Engine)]
    public void ChannelsLandInTheExpectedCategory(string name, ChannelCategory expected) =>
        Assert.Equal(expected, ChannelClassifier.Classify(name));

    [Fact]
    public void ShortKeysDoNotMatchInsideLongerWords()
    {
        // "ve" must not match "Valve", nor "pw" match "Power".
        Assert.NotEqual(ChannelCategory.Fuel, ChannelClassifier.Classify("Valve position", "%"));
        Assert.NotEqual(ChannelCategory.Fuel, ChannelClassifier.Classify("Power level", "%"));
    }

    [Fact]
    public void RunTogetherCapsFallBackToPrefixMatching()
    {
        // "TPSADC" normalises to a single token that matches no whole key.
        Assert.Equal(ChannelCategory.Air, ChannelClassifier.Classify("TPSADC"));
        Assert.Equal(ChannelCategory.Diagnostics, ChannelClassifier.Classify("canin1_8"));
    }

    [Fact]
    public void PrefixFallbackDoesNotMatchMidWord()
    {
        // "ase" (after-start enrichment) must not match inside "ph-ase".
        Assert.Equal(ChannelCategory.Diagnostics, ChannelClassifier.Classify("SDcard phase"));
    }

    [Theory]
    // Channels that touch two systems; rule order decides the owner.
    [InlineData("SPK: Fuel cut retard", ChannelCategory.Ignition)]
    [InlineData("SPK: Idle Correction Advance", ChannelCategory.Ignition)]
    [InlineData("Injector timing pri", ChannelCategory.Fuel)]
    [InlineData("Timing err", ChannelCategory.Ignition)]
    [InlineData("PWM Idle duty", ChannelCategory.Idle)]
    [InlineData("VVT duty 1", ChannelCategory.Air)]
    [InlineData("Duty Cycle2", ChannelCategory.Fuel)]
    [InlineData("Fuel temperature cor", ChannelCategory.Fuel)]
    [InlineData("ECU Temperature", ChannelCategory.Temperature)]
    public void OverlappingChannelsResolveByRuleOrder(string name, ChannelCategory expected) =>
        Assert.Equal(expected, ChannelClassifier.Classify(name));

    [Theory]
    // The leading namespace token wins over keywords later in the name.
    [InlineData("Fuel: Baro cor", ChannelCategory.Fuel)]
    [InlineData("Fuel: Air cor", ChannelCategory.Fuel)]
    [InlineData("Seq PW1", ChannelCategory.Fuel)]
    [InlineData("SPK: MAT Retard", ChannelCategory.Ignition)]
    [InlineData("SPK: Launch VSS Retard", ChannelCategory.Ignition)]
    [InlineData("Ign load", ChannelCategory.Ignition)]
    public void LeadingNamespaceTokenClaimsTheChannel(string name, ChannelCategory expected) =>
        Assert.Equal(expected, ChannelClassifier.Classify(name));

    [Theory]
    [InlineData("Widget", "°F", ChannelCategory.Temperature)]
    [InlineData("Widget", "kPa", ChannelCategory.Air)]
    [InlineData("Widget", "bits", ChannelCategory.Diagnostics)]
    [InlineData("Widget", "", ChannelCategory.Other)]
    public void UnitsAreUsedWhenTheNameSaysNothing(string name, string units, ChannelCategory expected) =>
        Assert.Equal(expected, ChannelClassifier.Classify(name, units));

    [Fact]
    public void EveryCategoryHasADisplayName()
    {
        foreach (ChannelCategory c in Enum.GetValues<ChannelCategory>())
            Assert.False(string.IsNullOrWhiteSpace(ChannelClassifier.DisplayName(c)));
    }
}
