using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Recognising a dongle by its name, and asking it what it really is.
///
/// The names are checked against what real adapters actually advertise rather
/// than against what they are called in a catalogue, because those are not the
/// same thing and the difference is what broke this.
/// </summary>
public class Obd2AdapterTests
{
    [Theory]
    // The one that caught this out. An OBDLink r2.6 advertises after the company
    // rather than the product, so every name being looked for missed it — and a
    // working dongle got probed as a MegaSquirt and reported as an unknown ECU.
    [InlineData("ScanTool.net-5487")]
    [InlineData("OBDLink MX+")]
    [InlineData("OBDLink LX")]
    [InlineData("OBDLink CX")]
    [InlineData("OBDII")]
    [InlineData("OBD2 Adapter")]
    [InlineData("ELM327 v1.5")]
    [InlineData("Vgate iCar Pro")]
    [InlineData("vLinker MC+")]
    [InlineData("VEEPEAK OBDCheck")]
    [InlineData("Konnwei KW902")]
    [InlineData("obdlink mx")]
    public void TheAdaptersAreRecognised(string name) =>
        Assert.True(Obd2Adapters.LooksLikeOne(name), $"{name} should be recognised");

    [Theory]
    // Everything else paired to the same machine. Guessing wrong here is the same
    // mistake in the other direction: a MaxxECU routed into the OBD2 path finds
    // nothing, having never spoken OBD2 in its life.
    [InlineData("MaxxECU_28xf7p")]
    [InlineData("HP 720/725 Multi-Device Rechargeable Wireless Mouse")]
    [InlineData("uaefi")]
    [InlineData("Standard Serial over Bluetooth link")]
    [InlineData("soundcore")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EverythingElseIsNot(string? name) =>
        Assert.False(Obd2Adapters.LooksLikeOne(name), $"{name} should not be taken for an adapter");

    [Fact]
    public void AnyOfSeveralNamesWillDo()
    {
        // A serial port has both a device name and a Windows description, and
        // either may be the one carrying the clue.
        Assert.True(Obd2Adapters.LooksLikeOne("Standard Serial over Bluetooth link", "ScanTool.net-5487"));
        Assert.False(Obd2Adapters.LooksLikeOne("Standard Serial over Bluetooth link", "MaxxECU_28xf7p"));
        Assert.False(Obd2Adapters.LooksLikeOne(null, null));
    }

    // ----- what the adapter says it is -----------------------------------------

    [Fact]
    public void AnObdLinkIsNamedByItsProductRatherThanItsClaim()
    {
        // Measured against the real one: an OBDLink r2.6 answers ATI with
        // "ELM327 v1.3a" — a version superseded a decade before it was built —
        // and only STDI says what it actually is.
        var fake = new FakeElm
        {
            Elm327Name = "ELM327 v1.3a",
            Product = "OBDLink r2.6",
            Firmware = "STN1100 v2.2.2",
        };

        var elm = new Elm327(fake);
        elm.Reset();

        Assert.Equal("OBDLink r2.6 (STN1100 v2.2.2)", elm.Identify());
    }

    [Fact]
    public void ACloneIsStillNamedByWhateverItDoesAnswer()
    {
        // No ST commands, so there is nothing but the ELM327 claim to go on —
        // and that has to keep working, because it is most of what is out there.
        var fake = new FakeElm { Elm327Name = "ELM327 v2.1" };

        var elm = new Elm327(fake);
        elm.Reset();

        Assert.Equal("ELM327 v2.1", elm.Identify());
    }

    [Fact]
    public void RefusingTheExtendedCommandsIsNotMistakenForAnAnswer()
    {
        // An ELM327 answers anything it does not know with "?", and taking that
        // for a product name would put a question mark in the connect menu.
        var fake = new FakeElm { Elm327Name = "ELM327 v1.5" };

        var elm = new Elm327(fake);
        elm.Reset();

        Assert.DoesNotContain("?", elm.Identify());
        Assert.Contains("STDI", fake.Received);
        Assert.Contains("STI", fake.Received);
    }

    [Fact]
    public void APartlyExtendedAdapterUsesWhatItHas()
    {
        // Firmware but no product name: worth reporting alongside the ELM327
        // claim rather than throwing away.
        var fake = new FakeElm { Elm327Name = "ELM327 v1.4", Firmware = "STN2255 v5.6.4" };

        var elm = new Elm327(fake);
        elm.Reset();

        Assert.Equal("ELM327 v1.4 (STN2255 v5.6.4)", elm.Identify());
    }

    [Fact]
    public void ASessionReportsTheProductAndNotTheClaim()
    {
        // The whole point of the exercise: what the connect menu and the status
        // bar end up showing.
        var fake = new FakeElm
        {
            Elm327Name = "ELM327 v1.3a",
            Product = "OBDLink r2.6",
            Firmware = "STN1100 v2.2.2",
            Answers =
            {
                [0x00] = [0xBE, 0x3F, 0xA8, 0x13],
                [0x0C] = [0x1A, 0xF8],
            },
        };

        using Elm327Source source = Elm327Source.Connect(fake);

        Assert.Equal("OBDLink r2.6 (STN1100 v2.2.2)", source.Adapter);
        Assert.NotEmpty(source.Parameters);
    }

    [Fact]
    public void AnAdapterBeingSentNoiseIsStillNotNamed()
    {
        // The check that this did not become a way to invent a name for a port
        // that has nothing on it: a garbled reply names nothing, and the failure
        // has to stay recognisable as "wrong port or wrong speed".
        var fake = new FakeElm { Garble = true };

        var elm = new Elm327(fake);

        Assert.Equal("", elm.Reset());
    }
}
