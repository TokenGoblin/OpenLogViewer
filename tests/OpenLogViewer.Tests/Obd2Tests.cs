using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class Obd2Tests
{
    /// <summary>A car answering the parameters most of them do.</summary>
    private static FakeElm Car()
    {
        var car = new FakeElm();

        // Supported [01-20]: 0x01, 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11, with
        // the last bit set to say there is a further range.
        car.Answers[0x00] = [0b1001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0001];

        // Supported [21-40]: none of them, but another range follows.
        car.Answers[0x20] = [0b0000_0000, 0b0000_0000, 0b0000_0000, 0b0000_0001];

        // Supported [41-60]: 0x42 alone, and nothing after it.
        car.Answers[0x40] = [0b0100_0000, 0b0000_0000, 0b0000_0000, 0b0000_0000];

        car.Answers[0x01] = [0x81, 0x07, 0x65, 0x04];   // MIL on, 1 code
        car.Answers[0x04] = [0x7F];                      // 49.8 % load
        car.Answers[0x05] = [0x5A];                      // 50 °C
        car.Answers[0x0B] = [0x64];                      // 100 kPa
        car.Answers[0x0C] = [0x1A, 0xF8];                // 1726 rpm
        car.Answers[0x0D] = [0x40];                      // 64 km/h
        car.Answers[0x0F] = [0x46];                      // 30 °C
        car.Answers[0x11] = [0x33];                      // 20 %
        car.Answers[0x42] = [0x37, 0x1A];                // 14.106 V

        return car;
    }

    // ----- the adapter -------------------------------------------------------

    [Fact]
    public void TheAdapterIsPutIntoTheModeThisCanParse()
    {
        // Echo, linefeeds, spaces and headers all off, and auto-protocol on.
        // Every one of them changes the shape of a reply.
        var car = Car();
        Elm327Source.Connect(car);

        Assert.Equal(
            ["ATZ", "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0"],
            car.Received.Take(6));
    }

    [Fact]
    public void TheAdapterSaysWhatItIs()
    {
        Assert.Equal("ELM327 v1.5", Elm327Source.Connect(Car()).Adapter);
    }

    [Fact]
    public void TheEchoedCommandIsNotMistakenForTheVersion()
    {
        // ATZ is answered while echo is still on, so what comes back is the
        // command and then the version.
        Assert.StartsWith("ELM327", Elm327Source.Connect(Car()).Adapter, StringComparison.Ordinal);
    }

    // ----- what the car supports ---------------------------------------------

    [Fact]
    public void OnlyTheParametersTheCarReportsAreAskedFor()
    {
        // The alternative is trying all ninety-six and waiting out a NO DATA for
        // each — a round trip apiece on the slowest link in this application.
        Elm327Source source = Elm327Source.Connect(Car());

        Assert.Equal<byte>(
            [0x01, 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11, 0x42],
            [.. source.Parameters.Select(p => p.Pid)]);
    }

    [Fact]
    public void ABitmaskNamesThePidsThatFollowIt()
    {
        // Bit 31 of the first byte stands for the parameter after the one asked
        // about, so 0100 answering 0x80… means PID 0x01 is supported.
        Assert.Equal<byte>([0x01], Obd2Pids.SupportedBy(0x00, [0x80, 0, 0, 0]));
        Assert.Equal<byte>([0x20], Obd2Pids.SupportedBy(0x00, [0, 0, 0, 0x01]));
        Assert.Equal<byte>([0x41], Obd2Pids.SupportedBy(0x40, [0x80, 0, 0, 0]));
    }

    [Fact]
    public void TheCarIsNotAskedAboutARangeItSaidNothingFollowedIn()
    {
        // The last bit of each mask means "and there is another range after
        // this". Asking anyway costs a round trip and answers NO DATA, which
        // reads like a fault.
        var car = Car();
        Elm327Source.Connect(car);

        Assert.Contains("0100", car.Received);
        Assert.Contains("0140", car.Received);

        // 0x60 would be the next range, and the 0140 mask does not claim one.
        Assert.DoesNotContain("0160", car.Received);
    }

    [Fact]
    public void ACarThatSupportsNothingIsSaidToRatherThanConnectedTo()
    {
        var car = new FakeElm();
        car.Answers[0x00] = [0, 0, 0, 0];

        EcuProtocolException e =
            Assert.Throws<EcuProtocolException>(() => Elm327Source.Connect(car));

        Assert.Contains("ignition", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAdapterAtTheWrongSpeedIsNotMistakenForAKeyLeftOut()
    {
        // The two failures need telling apart. A key turned off is fixed in a
        // second; a wrong baud rate is not, and waiting for an ignition that is
        // already on would be waiting for nothing. What separates them is that an
        // adapter being sent noise answers with noise — and noise is never its
        // own name.
        var noise = new FakeElm { Garble = true };

        EcuProtocolException e =
            Assert.Throws<EcuProtocolException>(() => Elm327Source.Connect(noise));

        Assert.DoesNotContain("ignition", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("answered as an OBD2 adapter", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verbatim from the test vehicle. Two modules answer 0100, the engine one
    /// reporting twenty-four usable parameters and the other almost none, and
    /// which arrives first is not fixed — so taking one of them gave 24 channels
    /// on one connection and 3 on the next, on the same car a minute apart.
    /// </summary>
    private const string TwoModules = "SEARCHING...\r4100BE3FA813\r410080000001\r\r";

    [Fact]
    public void EveryModulesCapabilitiesAreCombinedRatherThanRaced()
    {
        IReadOnlyList<byte[]> masks = Elm327.ParseAll(TwoModules, 0x00, 4);

        Assert.Equal(2, masks.Count);

        byte[] rich = [0xBE, 0x3F, 0xA8, 0x13];
        byte[] sparse = [0x80, 0x00, 0x00, 0x01];

        Assert.Equal(rich, masks[0]);
        Assert.Equal(sparse, masks[1]);

        // The sparse module alone would leave the car reporting one parameter.
        Assert.Single(Obd2Pids.SupportedBy(0x00, sparse).Where(p => p != 0x20));
        Assert.True(Obd2Pids.SupportedBy(0x00, rich).Count > 10);
    }

    [Fact]
    public void ACarWhoseSecondModuleAnswersFirstStillFindsEverything()
    {
        // The failure as it actually happened: three channels instead of
        // twenty-four, because the uninteresting module got a word in first.
        var car = Car();
        car.ExtraAnswers[0x00] = [[0x80, 0x00, 0x00, 0x01]];

        Elm327Source source = Elm327Source.Connect(car);

        Assert.Contains("RPM", source.Names);
        Assert.Contains("Coolant", source.Names);
        Assert.True(source.Parameters.Count > 5, $"only found {source.Parameters.Count}");
    }

    [Fact]
    public void TheSpeedsTriedStartWithTheOneAGenuineAdapterShipsAt()
    {
        // 38,400 is the ELM327's factory setting. Clones vary, which is why there
        // is a list at all.
        Assert.Equal(38400, Elm327Source.BaudRates[0]);
        Assert.Contains(115200, Elm327Source.BaudRates);
        Assert.Contains(9600, Elm327Source.BaudRates);
    }

    // ----- decoding ----------------------------------------------------------

    [Fact]
    public void TheStandardFormulasAreApplied()
    {
        Elm327Source source = Elm327Source.Connect(Car());
        double[] row = source.Read();

        Assert.Equal(1726, At(source, row, "RPM"));
        Assert.Equal(64, At(source, row, "Speed"));
        Assert.Equal(100, At(source, row, "MAP"));
        Assert.Equal(20, At(source, row, "Throttle Position"), 1);
        Assert.Equal(49.8, At(source, row, "Engine Load"), 1);
    }

    [Fact]
    public void SearchingIsNotReadAsAReading()
    {
        // Its letters are mostly hex digits — SEARCHING gives E, A and C — so run
        // together with the reply behind it they decode as a different number
        // entirely. Only its own line saves it.
        var car = Car();
        car.Searching = true;

        Elm327Source source = Elm327Source.Connect(car);

        Assert.Equal(1726, At(source, source.Read(), "RPM"));
    }

    [Fact]
    public void AReplyToADifferentQuestionIsRefused()
    {
        // A reply that arrives after its timeout is answering the previous
        // request. Taken at face value it puts one channel's number on another
        // channel's gauge, which is worse than no reading.
        Span<byte> data = stackalloc byte[4];

        Assert.False(Elm327.TryParse("410D40", 0x0C, 2, data, out _));
        Assert.True(Elm327.TryParse("410C1AF8", 0x0C, 2, data, out _));
    }

    [Fact]
    public void ATruncatedReplyIsRefusedRatherThanPaddedWithZero()
    {
        // Half of an RPM reply is not an RPM of half the value.
        Span<byte> data = stackalloc byte[4];

        Assert.False(Elm327.TryParse("410C1A", 0x0C, 2, data, out _));
    }

    [Fact]
    public void SpacesInAReplyAreToleratedWhicheverWayTheAdapterSendsThem()
    {
        // ATS0 turns them off, but a clone that ignores it should not cost every
        // reading in the session.
        Span<byte> a = stackalloc byte[2];
        Span<byte> b = stackalloc byte[2];

        Assert.True(Elm327.TryParse("41 0C 1A F8", 0x0C, 2, a, out _));
        Assert.True(Elm327.TryParse("410C1AF8", 0x0C, 2, b, out _));
        Assert.True(a.SequenceEqual(b));
    }

    [Fact]
    public void NoDataIsNotAReading()
    {
        // "NO DATA" contains D, A and A, which are hex digits.
        Span<byte> data = stackalloc byte[4];

        Assert.False(Elm327.TryParse("NO DATA", 0x0C, 2, data, out _));
    }

    [Fact]
    public void OneRequestAnsweredBySeveralModulesTakesTheFirstCompleteReply()
    {
        // A car with more than one responding module answers once per module,
        // one to a line.
        Span<byte> data = stackalloc byte[2];

        Assert.True(Elm327.TryParse("410C1AF8\r410C1AF9", 0x0C, 2, data, out _));
        Assert.Equal(0xF8, data[1]);
    }

    [Fact]
    public void OnePidCanCarryTwoChannels()
    {
        // 0x01 is the check-engine lamp and the number of stored codes at once.
        Elm327Source source = Elm327Source.Connect(Car());

        // Rotated in, so it takes a few rounds to come round.
        double[] row = [];
        for (int i = 0; i < source.Parameters.Count; i++) row = source.Read();

        Assert.Equal(1, At(source, row, "MIL"));
        Assert.Equal(1, At(source, row, "DTC Count"));
    }

    // ----- the shape of a session --------------------------------------------

    [Fact]
    public void AChannelReadsAsNothingUntilItHasAnswered()
    {
        // Not zero, which is a reading. A rotated channel has not been asked yet
        // on the first round, and a dial showing zero volts would be a fault
        // report rather than an absence.
        Elm327Source source = Elm327Source.Connect(Car());
        double[] first = source.Read();

        Assert.True(double.IsNaN(At(source, first, "Battery")));
        Assert.False(double.IsNaN(At(source, first, "RPM")));
    }

    [Fact]
    public void TheFastChannelsAreAskedForEveryRound()
    {
        // Otherwise the rev counter updates at the speed of the fuel level.
        var car = Car();
        Elm327Source source = Elm327Source.Connect(car);

        source.Read();
        car.Received.Clear();
        source.Read();

        Assert.Contains("010C", car.Received);
        Assert.Contains("010D", car.Received);
        Assert.Contains("0111", car.Received);
    }

    [Fact]
    public void ASlowChannelHoldsItsLastReadingBetweenTurns()
    {
        Elm327Source source = Elm327Source.Connect(Car());

        double[] row = [];
        for (int i = 0; i < source.Parameters.Count * 2; i++) row = source.Read();

        Assert.Equal(14.106, At(source, row, "Battery"), 3);
    }

    [Fact]
    public void ACarThatStopsAnsweringAltogetherIsReported()
    {
        // One parameter falling silent is ordinary. Every one of them doing so is
        // the key having been turned off, and the session should say so rather
        // than record a screenful of unchanging numbers.
        var car = Car();
        Elm327Source source = Elm327Source.Connect(car);
        source.Read();

        car.Answers.Clear();

        Assert.Throws<EcuProtocolException>(() => source.Read());
    }

    // ----- gauges ------------------------------------------------------------

    [Fact]
    public void EveryReportedParameterGetsAGauge()
    {
        Elm327Source source = Elm327Source.Connect(Car());
        IReadOnlyList<GaugeSpec> gauges = Obd2Gauges.For(source.Parameters);

        Assert.Equal(source.Names.Count, gauges.Count);
        Assert.All(gauges, g => Assert.True(g.HasScale, $"{g.Title} has no range"));
    }

    [Fact]
    public void OnlyTheParametersWithAConventionWorthHavingAreBanded()
    {
        // The standard supplies no limits, so these are a workshop manual's
        // figures rather than the car's. Which makes it the more important that
        // they stop where the convention does: nobody can say what a wrong road
        // speed or a wrong manifold pressure is, and colouring those would be
        // asserting something no one has said.
        IReadOnlyList<GaugeSpec> gauges = Obd2Gauges.For(Obd2Pids.All);

        Assert.All(
            (string[])["Speed", "RPM", "MAP", "Engine Load", "Throttle Position",
                       "Timing Advance", "Run Time", "Barometric Pressure", "Lambda"],
            name => Assert.False(
                Assert.Single(gauges, g => g.Title == name).HasBands,
                $"{name} claims to know what a safe reading is"));

        Assert.All(
            (string[])["Coolant", "Battery", "Engine Oil Temp", "Fuel Level",
                       "Long Fuel Trim B1", "MIL"],
            name => Assert.True(
                Assert.Single(gauges, g => g.Title == name).HasBands,
                $"{name} has no limits at all"));
    }

    [Fact]
    public void ALitMalfunctionLampIsRed()
    {
        // The one limit the standard does state. Its own word for the lamp being
        // commanded on is "malfunction", so this is the car talking rather than
        // a convention about what is probably bad.
        GaugeSpec mil = Assert.Single(Obd2Gauges.For(Obd2Pids.All), g => g.Title == "MIL");

        Assert.Equal(GaugeBand.Normal, mil.BandFor(0));
        Assert.Equal(GaugeBand.Danger, mil.BandFor(1));
    }

    [Fact]
    public void AStoredCodeShowsAsAWarningAndNoneAsNormal()
    {
        GaugeSpec codes = Assert.Single(Obd2Gauges.For(Obd2Pids.All), g => g.Title == "DTC Count");

        Assert.Equal(GaugeBand.Normal, codes.BandFor(0));
        Assert.Equal(GaugeBand.Warning, codes.BandFor(1));
        Assert.Equal(GaugeBand.Warning, codes.BandFor(6));
    }

    [Theory]
    [InlineData("Coolant", 90, GaugeBand.Normal)]
    [InlineData("Coolant", 108, GaugeBand.Warning)]
    [InlineData("Coolant", 120, GaugeBand.Danger)]
    [InlineData("Battery", 14.1, GaugeBand.Normal)]
    [InlineData("Battery", 12.0, GaugeBand.Warning)]
    [InlineData("Battery", 11.0, GaugeBand.Danger)]
    [InlineData("Battery", 15.6, GaugeBand.Danger)]
    [InlineData("Fuel Level", 50, GaugeBand.Normal)]
    [InlineData("Fuel Level", 10, GaugeBand.Warning)]
    [InlineData("Fuel Level", 3, GaugeBand.Danger)]
    [InlineData("Long Fuel Trim B1", 2, GaugeBand.Normal)]
    [InlineData("Long Fuel Trim B1", -14, GaugeBand.Warning)]
    [InlineData("Long Fuel Trim B1", 30, GaugeBand.Danger)]
    public void AReadingFallsWhereAWorkshopManualWouldPutIt(
        string title, double reading, GaugeBand expected)
    {
        GaugeSpec gauge = Assert.Single(Obd2Gauges.For(Obd2Pids.All), g => g.Title == title);

        Assert.Equal(expected, gauge.BandFor(reading));
    }

    [Fact]
    public void TheLimitsSurviveBeingShownInOtherUnits()
    {
        // A coolant gauge in Fahrenheit keeping a Celsius redline would call 105
        // degrees an emergency at 40 °C.
        GaugeSpec metric = Assert.Single(Obd2Gauges.For(Obd2Pids.All), g => g.Title == "Coolant");
        GaugeSpec imperial = metric.In(UnitSystem.Imperial);

        Assert.Equal(GaugeBand.Normal, imperial.BandFor(194));    // 90 °C
        Assert.Equal(GaugeBand.Danger, imperial.BandFor(248));    // 120 °C
    }

    [Fact]
    public void TheTachometerIsDrawnToSomethingAnEngineCouldReach()
    {
        // The standard encodes RPM to 16,383.75, which is the counter's ceiling
        // and no engine's. A dial drawn to it leaves every real reading in the
        // first quarter.
        GaugeSpec tacho = Assert.Single(Obd2Gauges.For(Obd2Pids.All), g => g.Title == "RPM");

        Assert.Equal(Obd2Pids.TachometerTop, tacho.High);
        Assert.True(tacho.High is >= 6000 and <= 12000);
    }

    [Fact]
    public void TheFrontPageIsADashboardRatherThanTheWholeCatalogue()
    {
        IReadOnlyList<string> named = [.. Obd2Gauges.For(Obd2Pids.All).Select(g => g.Title)];

        Assert.All(Obd2Gauges.FrontPage, name => Assert.Contains(name, named));
        Assert.True(Obd2Gauges.FrontPage.Count < named.Count);
    }

    private static double At(Elm327Source source, double[] row, string name)
    {
        int at = source.Names.ToList().IndexOf(name);
        Assert.True(at >= 0, $"no channel called {name}");

        return row[at];
    }
}
