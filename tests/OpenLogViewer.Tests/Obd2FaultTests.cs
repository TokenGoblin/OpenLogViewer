using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Reading and clearing fault codes.
///
/// The arithmetic here is unforgiving in a way the mode-01 readings are not. A
/// gauge decoded half a byte out looks wrong immediately — a coolant temperature
/// of 4,000 degrees is not believed by anybody. A fault code decoded half a byte
/// out is five plausible characters that somebody looks up, believes, and spends
/// a Saturday and a hundred pounds acting on. So most of what follows is about
/// the two places a reply can be read wrongly and still look right.
/// </summary>
public class Obd2FaultTests
{
    // ----- what two bytes mean ------------------------------------------------

    [Theory]
    [InlineData(0x03, 0x01, "P0301")] // cylinder 1 misfire, the commonest of all
    [InlineData(0x01, 0x71, "P0171")] // system too lean, bank 1
    [InlineData(0x04, 0x20, "P0420")] // catalyst efficiency
    [InlineData(0xC1, 0x00, "U0100")] // lost communication with the ECM
    [InlineData(0x40, 0x35, "C0035")] // a chassis code
    [InlineData(0x93, 0x42, "B1342")] // a body code, manufacturer range
    public void DecodesTheStandardsBitLayout(byte high, byte low, string expected) =>
        Assert.Equal(expected, Obd2Faults.Decode(high, low));

    /// <summary>
    /// The four characters after the letter are hex digits and not a decimal
    /// number. Hybrid and electrified codes live in the range that proves it, and
    /// a decoder that formatted them as decimal would report P0A0F as P01015.
    /// </summary>
    [Fact]
    public void DecodesTheHexRanges()
    {
        Assert.Equal("P0A0F", Obd2Faults.Decode(0x0A, 0x0F));
        Assert.Equal("P34FF", Obd2Faults.Decode(0x34, 0xFF));
    }

    [Theory]
    [InlineData("P0301")]
    [InlineData("U0100")]
    [InlineData("B1342")]
    [InlineData("P0A0F")]
    [InlineData("C3999")]
    public void EncodesBackToTheSameBytes(string code)
    {
        (byte high, byte low) = Obd2Faults.Encode(code);

        Assert.Equal(code, Obd2Faults.Decode(high, low));
    }

    // ----- the count byte -----------------------------------------------------

    /// <summary>
    /// The trap that makes a fault reply different from every other reply here.
    ///
    /// On CAN the byte after the mode echo is how many codes follow; on the older
    /// serial buses it is the first half of the first code. These four bytes are a
    /// real reply either way and decode to a different fault — a misfire on
    /// cylinder 1 one way round, a mass airflow circuit fault the other. Neither
    /// looks wrong.
    /// </summary>
    [Fact]
    public void TheCountByteChangesWhatTheSameBytesMean()
    {
        const string reply = "43 01 03 01";

        IReadOnlyList<Dtc> can = Obd2Faults.Parse(reply, 0x03, DtcState.Stored, counted: true);
        IReadOnlyList<Dtc> serial = Obd2Faults.Parse(reply, 0x03, DtcState.Stored, counted: false);

        Assert.Equal(["P0301"], can.Select(f => f.Code));
        Assert.Equal(["P0103"], serial.Select(f => f.Code));
    }

    /// <summary>
    /// Which of the two it is comes from the adapter rather than from a guess.
    /// Protocols 1 to 5 are the serial buses and 6 upwards are the CAN variants.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)] // ISO 9141-2, which plenty of pre-2008 cars still use
    [InlineData(5, false)]
    [InlineData(6, true)] // CAN 11 bit, 500 kbaud — the modern default
    [InlineData(9, true)]
    [InlineData(0xB, true)]
    public void TakesTheProtocolFromTheAdapter(int protocol, bool expected)
    {
        var fake = new FakeElm { ProtocolNumber = protocol };
        var elm = new Elm327(fake);
        elm.Reset();

        Assert.Equal(expected, elm.IsCan());
    }

    /// <summary>
    /// Protocol zero is "still searching", not a protocol.
    ///
    /// An adapter that has been told to find the protocol itself answers ATDPN
    /// with "A0" until it succeeds. Caching that as an answer would leave a car
    /// that settles on ISO 9141 a moment later being read as CAN for the rest of
    /// the session — which puts a count byte where a fault code's first half is
    /// and produces codes that are not on the vehicle.
    ///
    /// Found on a live car: the first probe of a running Subaru reported
    /// "is CAN: True" while the search had not finished, and the same link came
    /// back ISO 15765-4 once it had.
    /// </summary>
    [Fact]
    public void AProtocolStillBeingSearchedForIsNotRemembered()
    {
        var fake = new FakeElm { ProtocolNumber = 0 };
        var elm = new Elm327(fake);
        elm.Reset();

        elm.IsCan();
        int asked = fake.Received.Count(c => c.Equals("ATDPN", StringComparison.OrdinalIgnoreCase));

        elm.IsCan();
        int askedAgain = fake.Received.Count(c => c.Equals("ATDPN", StringComparison.OrdinalIgnoreCase));

        Assert.True(askedAgain > asked, "an undecided protocol must be asked about again");

        // And once it settles, the real answer is taken and kept.
        fake.ProtocolNumber = 3;

        Assert.False(elm.IsCan());
    }

    /// <summary>
    /// The adapter prefixes the protocol with how it found it, which is not part
    /// of what it found.
    /// </summary>
    [Fact]
    public void ReportsTheProtocolWithoutTheSearchPrefix()
    {
        var fake = new FakeElm();
        var elm = new Elm327(fake);
        elm.Reset();

        Assert.Equal("ISO 15765-4 (CAN 11/500)", elm.ProtocolName());
    }

    // ----- replies that do not fit in one frame -------------------------------

    /// <summary>
    /// Three codes will not fit in a CAN frame, and the adapter breaks the reply
    /// into numbered pieces. Those numbers are hex digits sitting in front of the
    /// data, so the ordinary reader — which skips anything that is not a hex digit
    /// — does not skip them: the "0" of "0:" becomes half a byte and shifts every
    /// byte after it.
    ///
    /// Both halves are asserted. That the reassembly is right, and that the naive
    /// reading really is wrong rather than merely untidy, because the second is
    /// the reason the first exists.
    /// </summary>
    [Fact]
    public void ReassemblesAFragmentedReply()
    {
        const string reply = "00A\r0:430403010171\r1:04200135000000\r";

        IReadOnlyList<Dtc> faults = Obd2Faults.Parse(reply, 0x03, DtcState.Stored, counted: true);

        Assert.Equal(["P0301", "P0171", "P0420", "P0135"], faults.Select(f => f.Code));

        // What the mode-01 path would have made of the same line.
        Span<byte> naive = stackalloc byte[16];
        int got = Elm327.Unhex("0:430403010171", naive);

        Assert.NotEqual(0x43, naive[0]);
        Assert.Equal(6, got);
    }

    /// <summary>
    /// The declared length is what separates the last frame's real data from the
    /// zeros it is padded out with — and a padding pair decodes to P0000, which is
    /// not a code any car sets.
    /// </summary>
    [Fact]
    public void DropsThePaddingOnTheLastFrame()
    {
        const string reply = "006\r0:430203010171\r";

        IReadOnlyList<Dtc> faults = Obd2Faults.Parse(reply, 0x03, DtcState.Stored, counted: true);

        Assert.Equal(2, faults.Count);
        Assert.DoesNotContain(faults, f => f.Code == "P0000");
    }

    /// <summary>
    /// A fragment that arrives without the piece that started it is half a reply,
    /// and half a reply decodes into codes that were never set. Better to drop it.
    /// </summary>
    [Fact]
    public void IgnoresFragmentsWithNoBeginning()
    {
        const string reply = "1:04200135000000\r";

        Assert.Empty(Obd2Faults.Parse(reply, 0x03, DtcState.Stored, counted: true));
    }

    /// <summary>
    /// Mode 04's entire reply is the two characters "44", which read as a length
    /// header is a declaration of 68 bytes that never arrive. Nothing about the
    /// line itself distinguishes the two — only whether fragments follow it.
    /// </summary>
    [Fact]
    public void DoesNotMistakeAShortReplyForALengthHeader()
    {
        Assert.True(Obd2Faults.Answered("44", 0x04));
        Assert.True(Obd2Faults.Answered("44\r", 0x04));
    }

    // ----- more than one module answering -------------------------------------

    /// <summary>
    /// Most cars have several controllers on the bus and each answers with its
    /// own faults. Taking the first line would lose whatever only the second one
    /// knows about — which on a car with a transmission fault is the transmission
    /// fault.
    /// </summary>
    [Fact]
    public void KeepsEveryModulesAnswer()
    {
        // The engine module with a misfire, and the transmission module with a
        // fault of its own.
        const string reply = "43 01 03 01\r43 01 07 00\r";

        IReadOnlyList<Dtc> faults = Obd2Faults.Parse(reply, 0x03, DtcState.Stored, counted: true);

        Assert.Equal(["P0301", "P0700"], faults.Select(f => f.Code));
    }

    /// <summary>
    /// Two modules reporting the same fault is one fault. Collapsed for that
    /// reason and no other — different codes from different modules are kept.
    /// </summary>
    [Fact]
    public void CollapsesTheSameCodeFromTwoModules()
    {
        const string reply = "43010301\r43010301\r";

        Assert.Single(Obd2Faults.Parse(reply, 0x03, DtcState.Stored, counted: true));
    }

    /// <summary>
    /// A reply that arrives late is answering the previous question. Mode 07's
    /// answer read as mode 03's would report a fault the car has merely noticed
    /// once as one it has confirmed.
    /// </summary>
    [Fact]
    public void IgnoresAnAnswerToADifferentMode()
    {
        Assert.Empty(Obd2Faults.Parse("47010301", 0x03, DtcState.Stored, counted: true));
        Assert.False(Obd2Faults.Answered("47010301", 0x03));
        Assert.True(Obd2Faults.Answered("47010301", 0x07));
    }

    [Theory]
    [InlineData("NO DATA")]
    [InlineData("SEARCHING...")]
    [InlineData("")]
    [InlineData("7F 03 12")]
    public void NoAnswerIsNotAnEmptyAnswer(string reply) =>
        Assert.False(Obd2Faults.Answered(reply, 0x03));

    /// <summary>
    /// A car with nothing wrong still answers the question — count zero. That has
    /// to be distinguishable from a car that never answered, or "no faults found"
    /// comes to mean "the link was down".
    /// </summary>
    [Fact]
    public void AnEmptyListIsStillAnAnswer()
    {
        Assert.True(Obd2Faults.Answered("4300", 0x03));
        Assert.Empty(Obd2Faults.Parse("4300", 0x03, DtcState.Stored, counted: true));
    }

    // ----- what a code means --------------------------------------------------

    [Theory]
    [InlineData("P0301", "Cylinder 1 misfire detected")]
    [InlineData("P0312", "Cylinder 12 misfire detected")]
    [InlineData("P0201", "Injector circuit open — cylinder 1")]
    [InlineData("P0212", "Injector circuit open — cylinder 12")]
    [InlineData("P0261", "Cylinder 1 injector circuit low")]
    [InlineData("P0262", "Cylinder 1 injector circuit high")]
    [InlineData("P0263", "Cylinder 1 contribution or balance fault")]
    [InlineData("P0264", "Cylinder 2 injector circuit low")]
    [InlineData("P0284", "Cylinder 8 contribution or balance fault")]
    public void GeneratesTheFamiliesAtBothEnds(string code, string expected) =>
        Assert.Equal(expected, Obd2Codes.Describe(code));

    /// <summary>
    /// The coil codes are lettered in the standard and numbered on the engine, and
    /// both are worth having: the letter is what the manual prints, the number is
    /// which plug lead to pull.
    /// </summary>
    [Fact]
    public void NamesIgnitionCoilsByLetterAndCylinder()
    {
        Assert.Contains("coil A", Obd2Codes.Describe("P0351"), StringComparison.Ordinal);
        Assert.Contains("cylinder 1", Obd2Codes.Describe("P0351"), StringComparison.Ordinal);
        Assert.Contains("coil L", Obd2Codes.Describe("P0362"), StringComparison.Ordinal);
        Assert.Contains("cylinder 12", Obd2Codes.Describe("P0362"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Six codes per oxygen sensor, three sensors a bank, and bank 2 starting
    /// twenty on rather than eighteen — so there is a real gap at P0148 that has
    /// to fall through rather than be counted over. Counted over, every bank 2
    /// code would come out naming the wrong sensor.
    /// </summary>
    [Theory]
    [InlineData("P0130", "bank 1 sensor 1", "circuit malfunction")]
    [InlineData("P0135", "bank 1 sensor 1", "heater circuit")]
    [InlineData("P0136", "bank 1 sensor 2", "circuit malfunction")]
    [InlineData("P0147", "bank 1 sensor 3", "heater circuit")]
    [InlineData("P0150", "bank 2 sensor 1", "circuit malfunction")]
    [InlineData("P0161", "bank 2 sensor 2", "heater circuit")]
    [InlineData("P0167", "bank 2 sensor 3", "heater circuit")]
    public void WalksTheOxygenSensorRange(string code, string where, string what)
    {
        string text = Obd2Codes.Describe(code);

        Assert.Contains(where, text, StringComparison.Ordinal);
        Assert.Contains(what, text, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesTheGapBetweenTheOxygenSensorBanks()
    {
        Assert.Equal("", Obd2Codes.Describe("P0148"));
        Assert.Equal("", Obd2Codes.Describe("P0149"));
    }

    // ----- whose number is it -------------------------------------------------

    /// <summary>
    /// The ranges are not symmetrical between the four systems, and the split in
    /// the middle of P3 is real. Getting this wrong the wrong way round puts a
    /// confident SAE meaning on a number that never had one.
    /// </summary>
    [Theory]
    [InlineData("P0301", DtcAuthority.Generic)]
    [InlineData("P1131", DtcAuthority.Manufacturer)]
    [InlineData("P2187", DtcAuthority.Generic)]
    [InlineData("P3399", DtcAuthority.Manufacturer)]
    [InlineData("P3400", DtcAuthority.Generic)]
    [InlineData("C0035", DtcAuthority.Generic)]
    [InlineData("C1234", DtcAuthority.Manufacturer)]
    [InlineData("C2100", DtcAuthority.Manufacturer)]
    [InlineData("B1342", DtcAuthority.Manufacturer)]
    [InlineData("U0100", DtcAuthority.Generic)]
    [InlineData("U1000", DtcAuthority.Manufacturer)]
    public void KnowsWhoseNumberItIs(string code, DtcAuthority expected) =>
        Assert.Equal(expected, Obd2Codes.Authority(code));

    /// <summary>
    /// A manufacturer code gets no description rather than a plausible one. P1131
    /// is an oxygen sensor on a Ford and something else entirely on a Toyota, and
    /// a tool that guesses is how somebody buys the wrong part.
    /// </summary>
    [Fact]
    public void RefusesToGuessAtAManufacturersCode()
    {
        Assert.Equal("", Obd2Codes.Describe("P1131"));

        var fault = new Dtc("P1131", DtcState.Stored);

        Assert.Contains("manufacturer", fault.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Powertrain", fault.System);
    }

    /// <summary>
    /// A generic code this does not carry says so too, and differently — the
    /// standard does define it, so it is worth looking up rather than being a
    /// dead end.
    /// </summary>
    [Fact]
    public void SaysWhenAGenericCodeIsSimplyNotListed()
    {
        var fault = new Dtc("P0A0F", DtcState.Stored);

        Assert.Equal("", fault.Description);
        Assert.Contains("looking up", fault.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manufacturer", fault.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("P0301", "Powertrain")]
    [InlineData("C0035", "Chassis")]
    [InlineData("B1342", "Body")]
    [InlineData("U0100", "Network")]
    public void NamesTheSystem(string code, string expected) =>
        Assert.Equal(expected, Obd2Codes.System(code));

    [Theory]
    [InlineData("")]
    [InlineData("P030")]
    [InlineData("P03011")]
    [InlineData("X0301")]
    [InlineData("P03G1")]
    [InlineData(null)]
    public void RejectsWhatIsNotACode(string? code)
    {
        Assert.False(Obd2Codes.IsWellFormed(code));
        Assert.Equal("", Obd2Codes.Describe(code));
    }

    // ----- the whole conversation ---------------------------------------------

    [Fact]
    public void ScansAllThreeListsOnACanVehicle()
    {
        var fake = new FakeElm();
        fake.StoredCodes.AddRange(["P0301", "P0171", "P0420", "P0135"]);
        fake.PendingCodes.Add("P0300");
        fake.PermanentCodes.Add("P0420");
        fake.Answers[0x01] = [0x81, 0x07, 0xE5, 0x00];

        var elm = new Elm327(fake);
        elm.Reset();

        FaultScan scan = Obd2Faults.Scan(elm);

        Assert.Equal(
            ["P0301", "P0171", "P0420", "P0135"], scan.Stored.Select(f => f.Code));
        Assert.Equal(["P0300"], scan.Pending.Select(f => f.Code));
        Assert.Equal(["P0420"], scan.Permanent.Select(f => f.Code));

        Assert.True(scan.MilOn);
        Assert.Equal("", scan.Trouble);
        Assert.False(scan.Clean);
        Assert.Contains("ISO 15765-4", scan.Protocol, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same car on an older bus. The reply is laid out differently and carries
    /// no count, and the codes that come out have to be the same ones.
    /// </summary>
    [Fact]
    public void ScansTheSameFaultsOnASerialBus()
    {
        var fake = new FakeElm { ProtocolNumber = 3 };
        fake.StoredCodes.AddRange(["P0301", "P0171", "P0420", "P0135"]);
        fake.Answers[0x01] = [0x84, 0x07, 0xE5, 0x00];

        var elm = new Elm327(fake);
        elm.Reset();

        FaultScan scan = Obd2Faults.Scan(elm);

        Assert.Equal(["P0301", "P0171", "P0420", "P0135"], scan.Stored.Select(f => f.Code));
        Assert.Equal(4, scan.ReportedCount);
    }

    /// <summary>
    /// A car with nothing wrong. Worth its own test because "no faults" and "no
    /// answer" arrive as very nearly the same thing and only one of them is good
    /// news.
    /// </summary>
    [Fact]
    public void ReportsACleanCarAsClean()
    {
        var fake = new FakeElm();
        fake.Answers[0x01] = [0x00, 0x07, 0xE5, 0x00];

        var elm = new Elm327(fake);
        elm.Reset();

        FaultScan scan = Obd2Faults.Scan(elm);

        Assert.True(scan.Clean);
        Assert.False(scan.MilOn);
        Assert.Equal("", scan.Trouble);
        Assert.Contains("No fault codes", scan.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Older cars have no permanent codes at all and refuse mode 0A. That is not a
    /// fault and must not be reported as one, or every pre-2010 car scans with a
    /// warning on it.
    /// </summary>
    [Fact]
    public void DoesNotComplainWhenACarHasNoPermanentCodes()
    {
        var fake = new FakeElm { ProtocolNumber = 3 };
        fake.StoredCodes.Add("P0301");
        fake.Answers[0x01] = [0x81, 0x07, 0xE5, 0x00];

        var elm = new Elm327(fake);
        elm.Reset();

        Assert.Equal("", Obd2Faults.Scan(elm).Trouble);
    }

    /// <summary>
    /// The car counts two faults and lists none. Something answered PID 01 that
    /// did not answer mode 03, and reporting "no faults found" there would be a
    /// lie by omission.
    /// </summary>
    [Fact]
    public void NoticesWhenTheCountAndTheCodesDisagree()
    {
        var scan = new FaultScan([], MilOn: true, ReportedCount: 2, "CAN");

        Assert.True(scan.CountDisagrees);
        Assert.False(scan.Clean);
    }

    [Fact]
    public void AgreesWhenTheyAgree() =>
        Assert.False(new FaultScan(
            [new Dtc("P0301", DtcState.Stored)], MilOn: true, ReportedCount: 1, "CAN")
            .CountDisagrees);

    // ----- erasing ------------------------------------------------------------

    [Fact]
    public void ClearsTheStoredAndPendingCodes()
    {
        var fake = new FakeElm();
        fake.StoredCodes.AddRange(["P0301", "P0171"]);
        fake.PendingCodes.Add("P0300");
        fake.Answers[0x01] = [0x82, 0x07, 0xE5, 0x00];

        var elm = new Elm327(fake);
        elm.Reset();

        FaultClear cleared = Obd2Faults.Clear(elm);

        Assert.True(cleared.Erased);
        Assert.Empty(fake.StoredCodes);
        Assert.Empty(fake.PendingCodes);
        Assert.Equal(1, fake.ClearRequests);
    }

    /// <summary>
    /// What mode 04 actually costs, which is a great deal more than the codes: the
    /// freeze frame is the one record of what the engine was doing when the fault
    /// happened, and the readiness monitors have to be re-earned over a full drive
    /// cycle before the car can pass a test.
    /// </summary>
    [Fact]
    public void SaysWhatElseWasErased()
    {
        var fake = new FakeElm();
        fake.StoredCodes.Add("P0420");
        fake.Answers[0x01] = [0x81, 0x07, 0xE5, 0x00];

        var elm = new Elm327(fake);
        elm.Reset();

        string message = Obd2Faults.Clear(elm).Message;

        Assert.Contains("readiness", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drive cycle", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Permanent codes survive, which is the entire reason they exist. A person
    /// told the car is clear who then fails a test on a code that was there the
    /// whole time has been misled by the tool.
    /// </summary>
    [Fact]
    public void ReportsPermanentCodesThatSurvivedTheErase()
    {
        var fake = new FakeElm();
        fake.StoredCodes.Add("P0420");
        fake.PermanentCodes.Add("P0420");
        fake.Answers[0x01] = [0x81, 0x07, 0xE5, 0x00];

        var elm = new Elm327(fake);
        elm.Reset();

        FaultClear cleared = Obd2Faults.Clear(elm);

        Assert.True(cleared.Erased);
        Assert.Equal(["P0420"], cleared.Remaining.Select(f => f.Code));
        Assert.Contains("permanent", cleared.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readiness", cleared.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Most cars will not erase with the engine running, and the refusal is worth
    /// translating — "did not answer" would send somebody looking for a loose
    /// connector when the fix is to turn the key back one click.
    /// </summary>
    [Fact]
    public void ExplainsARefusal()
    {
        var fake = new FakeElm { RefuseClear = true };
        fake.StoredCodes.Add("P0301");
        fake.Answers[0x01] = [0x81, 0x07, 0xE5, 0x00];

        var elm = new Elm327(fake);
        elm.Reset();

        FaultClear cleared = Obd2Faults.Clear(elm);

        Assert.False(cleared.Erased);
        Assert.Contains("engine running", cleared.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fake.StoredCodes);
    }

    // ----- two threads, one adapter -------------------------------------------

    /// <summary>
    /// A scan is asked for from the user interface while the session is still
    /// polling, so two threads reach the same adapter. Written into the same port
    /// they interleave into one stream — the scan reads the tail of a coolant
    /// temperature and the poll reads half a fault code, and both parse.
    ///
    /// The transport blocks inside the write, so a second command that was allowed
    /// through would be sitting in there alongside the first. Depth is measured
    /// rather than timed: it can never exceed one while the lock holds, and would
    /// reliably reach two without it.
    /// </summary>
    [Fact]
    public void SendsOneCommandAtATime()
    {
        var transport = new BlockingTransport();
        var elm = new Elm327(transport);

        Task polling = Task.Run(() => elm.Send("010C", TimeSpan.FromSeconds(1)));
        Assert.True(transport.Entered.Wait(TimeSpan.FromSeconds(5)), "the first command never arrived");

        Task scanning = Task.Run(() => elm.Send("03", TimeSpan.FromSeconds(1)));

        // Long enough for the second to have got in if it were going to. A short
        // wait can only make this test pass wrongly, never fail wrongly.
        Thread.Sleep(300);

        Assert.Equal(1, transport.Deepest);

        transport.Release.Set();
        Assert.True(Task.WhenAll(polling, scanning).Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, transport.Deepest);
    }

    /// <summary>A transport that stops inside the write and counts who is in there.</summary>
    private sealed class BlockingTransport : IEcuTransport
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public int Deepest;

        private int _inside;

        public bool IsOpen => true;

        public void Open() { }

        public void Close() { }

        public void Dispose() { }

        public void DiscardInput() { }

        public void Write(ReadOnlySpan<byte> data)
        {
            int depth = Interlocked.Increment(ref _inside);

            for (int was = Volatile.Read(ref Deepest); depth > was; was = Volatile.Read(ref Deepest))
                Interlocked.CompareExchange(ref Deepest, depth, was);

            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(5));

            Interlocked.Decrement(ref _inside);
        }

        /// <summary>The prompt, so the reader finishes as soon as it is let go.</summary>
        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            if (buffer.Length == 0) return 0;

            buffer[0] = (byte)'>';

            return 1;
        }
    }

    /// <summary>
    /// A scan is reachable from the live source, which is the only place the
    /// application can reach an adapter that is already connected and polling.
    /// </summary>
    [Fact]
    public void ScansThroughTheLiveSource()
    {
        var fake = new FakeElm();
        fake.Answers[0x00] = [0x18, 0x3B, 0x80, 0x11];
        fake.Answers[0x01] = [0x81, 0x07, 0xE5, 0x00];
        fake.Answers[0x0C] = [0x1A, 0xF8];
        fake.Answers[0x05] = [0x5A];
        fake.StoredCodes.Add("P0301");

        using Elm327Source source = Elm327Source.Connect(fake);

        FaultScan scan = source.ReadFaults();

        Assert.Equal(["P0301"], scan.Stored.Select(f => f.Code));
        Assert.Equal("Cylinder 1 misfire detected", scan.Stored[0].Description);
    }
}
