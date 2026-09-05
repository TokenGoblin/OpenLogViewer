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

    // ----- a clone whose banner never says "ELM" -----------------------------

    /// <summary>
    /// Plenty of clones answer ATZ with a version and no maker's name at all.
    /// Reset finds a name by looking for "ELM", so it returns nothing for these —
    /// which is a name it could not find, not a link that said nothing.
    /// </summary>
    [Fact]
    public void AnAdapterThatDoesNotCallItselfElmStillAnswered()
    {
        var elm = new Elm327(new FakeElm { Elm327Name = "OBDII v2.1" });

        Assert.Equal("", elm.Reset());
        Assert.True(elm.AnsweredReset, "a banner came back and this says otherwise");
    }

    /// <summary>A link that is open and says nothing whatever.</summary>
    private sealed class Mute : IEcuTransport
    {
        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data)
        {
            // Swallowed. Answering is the one thing this does not do.
        }

        public int Read(Span<byte> buffer, TimeSpan timeout) => 0;

        public void DiscardInput()
        {
        }

        public void Dispose() => Close();
    }

    [Fact]
    public void SilenceIsToldApartFromAnUnfamiliarBanner()
    {
        var elm = new Elm327(new Mute());

        Assert.Equal("", elm.Reset());
        Assert.False(elm.AnsweredReset, "nothing came back and this says something did");
    }

    /// <summary>
    /// A clone that gives no name is still warmed up.
    ///
    /// The warm-up spends the protocol search on a request nobody reads. Skipped,
    /// that search lands on the capability query instead, and the narration an
    /// adapter emits during it ("SEARCHING...") is hex-parseable — so the car
    /// comes back with a short parameter list and a protocol still reading as
    /// undetermined, which also silently rules out batching.
    /// </summary>
    [Fact]
    public void AnAdapterWithNoNameIsStillWarmedUp()
    {
        var fake = new FakeElm { Elm327Name = "OBDII v2.1", Product = "OBDII v2.1" };

        fake.Answers[0x00] = [0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000];
        fake.Answers[0x04] = [0x7F];
        fake.Answers[0x05] = [0x5A];
        fake.Answers[0x0B] = [0x64];
        fake.Answers[0x0C] = [0x1A, 0xF8];
        fake.Answers[0x0D] = [0x40];
        fake.Answers[0x0F] = [0x46];
        fake.Answers[0x11] = [0x33];

        using Elm327Source source = Elm327Source.Connect(fake);

        // Seven is what this car declares. A skipped warm-up loses some of them
        // to the protocol search that then happens mid-query.
        Assert.Equal(7, source.Parameters.Count);
    }

    /// <summary>
    /// And the session that rests on it comes back from a key-off.
    ///
    /// Recover reads "nothing came back" as a dead session and ends it. Reading
    /// an unfamiliar banner that way threw on every attempt against a perfectly
    /// live link, so the whole reconnect window was spent and the session ended
    /// on a car that had simply been switched off and on again.
    /// </summary>
    [Fact]
    public void ASessionOnSuchAnAdapterRecovers()
    {
        // Product is what ATI answers, and it is what names an adapter whose ATZ
        // banner does not. Connecting still needs a name — noise must not get
        // one — and this clone has one to give; what it does not have is the
        // word "ELM" in its banner, which is what used to end the session.
        var fake = new FakeElm { Elm327Name = "OBDII v2.1", Product = "OBDII v2.1" };

        // Supported [01-20]: 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11.
        fake.Answers[0x00] = [0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000];
        fake.Answers[0x04] = [0x7F];
        fake.Answers[0x05] = [0x5A];
        fake.Answers[0x0B] = [0x64];
        fake.Answers[0x0C] = [0x1A, 0xF8];
        fake.Answers[0x0D] = [0x40];
        fake.Answers[0x0F] = [0x46];
        fake.Answers[0x11] = [0x33];

        using Elm327Source source = Elm327Source.Connect(fake);

        Assert.NotEmpty(source.Parameters);

        // The exception is the failure being tested for; anything else would be
        // the fake, and should not be swallowed.
        source.Recover();
    }
}
