using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Asking for six parameters in one request.
///
/// The cost of OBD2 is round trips rather than bytes — every other ECU here
/// hands over its whole realtime block in one exchange and this one does not —
/// so a request that carries six parameters is the single biggest thing
/// available to it: a round that was six exchanges becomes two.
///
/// It is also the part with the most ways to be quietly wrong, because the reply
/// is multi-frame, has nothing between its groups, and can contain more than one
/// module's answer. Each test here is a shape that has actually arrived off a
/// car.
/// </summary>
public class Obd2BatchTests
{
    private static int Length(byte pid) => Obd2Pids.DataBytesOf(pid);

    /// <summary>A car answering the parameters most of them do, in one request.</summary>
    private static FakeElm Car(
        FakeElm.BatchReply batches = FakeElm.BatchReply.All, int protocol = 6)
    {
        var car = new FakeElm { Batches = batches, ProtocolNumber = protocol };

        // Supported [01-20]: 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11.
        car.Answers[0x00] = [0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000];

        car.Answers[0x04] = [0x7F];         // 49.8 % load
        car.Answers[0x05] = [0x5A];         // 50 °C
        car.Answers[0x0B] = [0x64];         // 100 kPa
        car.Answers[0x0C] = [0x1A, 0xF8];   // 1726 rpm
        car.Answers[0x0D] = [0x40];         // 64 km/h
        car.Answers[0x0F] = [0x46];         // 30 °C
        car.Answers[0x11] = [0x33];         // 20 %

        return car;
    }

    private static double At(Elm327Source source, double[] row, string name) =>
        row[source.Names.ToList().IndexOf(name)];

    // ----- reading the reply -------------------------------------------------

    [Fact]
    public void GroupsAreReadByTheLengthEachParameterIsDefinedToCarry()
    {
        // There is nothing between the groups: 41, then (pid, data) over and
        // over. Knowing where one ends is knowing how long that parameter is.
        IReadOnlyList<Elm327.BatchAnswer> answers =
            Elm327.ParseBatch("410C1AF80B640D40\r\r>", [0x0C, 0x0B, 0x0D], Length);

        Assert.Equal([0x0C, 0x0B, 0x0D], answers.Select(a => a.Pid));
        Assert.Equal([0x1A, 0xF8], answers[0].Data);
        Assert.Equal([0x64], answers[1].Data);
        Assert.Equal([0x40], answers[2].Data);
    }

    [Fact]
    public void ASegmentMarkerDoesNotShiftTheBytesAfterIt()
    {
        // A multi-frame reply is printed as a total length and numbered
        // segments. "0:" and "1:" are not decoration — their digits are valid
        // hex, so stripping non-hex characters and pairing the rest turns the
        // "0" into half a byte and moves everything after it by a nibble.
        const string reply = """
            00D
            0:410C1AF80B64
            1:0D4011330441

            >
            """;

        IReadOnlyList<Elm327.BatchAnswer> answers =
            Elm327.ParseBatch(reply, [0x0C, 0x0B, 0x0D, 0x11, 0x04], Length);

        Assert.Equal([0x0C, 0x0B, 0x0D, 0x11, 0x04], answers.Select(a => a.Pid));
        Assert.Equal([0x1A, 0xF8], answers[0].Data);
        Assert.Equal([0x33], answers[3].Data);
    }

    [Fact]
    public void TheSameReplyIsReadWhenTheLineBreaksHaveBeenLost()
    {
        // Some clients strip carriage returns as they read, which runs the whole
        // reply together with the markers still embedded. The same rule carries
        // it: a marker's digit is discarded by the colon in front of the next
        // byte rather than paired with it.
        IReadOnlyList<Elm327.BatchAnswer> answers = Elm327.ParseBatch(
            "00D0:410C1AF80B641:0D4011330441", [0x0C, 0x0B, 0x0D, 0x11, 0x04], Length);

        Assert.Equal(5, answers.Count);
        Assert.Equal([0x1A, 0xF8], answers[0].Data);
    }

    [Fact]
    public void TheAnchorThatExplainsMostOfTheReplyWins()
    {
        // Two modules answering, run together — measured on a live Crosstrek.
        // A leading fragment carries a parameter that was asked for, so the
        // first plausible start latches onto it, emits one group, meets the
        // padding and stops. One parameter of four reads as "this car does not
        // do batching", and the whole feature is switched off on a car that
        // does: a throughput collapse with no parse error anywhere.
        // A fragment carrying 0x01, then the real answer with its own length
        // header: 41 5C 60 | 0C 1A F8 | 0D 40.
        const string reply = "410100040000\r008\r0:415C600C1AF8\r1:0D40\r\r>";

        IReadOnlyList<Elm327.BatchAnswer> answers =
            Elm327.ParseBatch(reply, [0x01, 0x5C, 0x0C, 0x0D], Length);

        Assert.Equal([0x5C, 0x0C, 0x0D], answers.Select(a => a.Pid));
    }

    [Fact]
    public void TheWalkStopsAtSomethingNobodyAskedFor()
    {
        // Frame padding, or a second module's answer beginning. Either way the
        // groups have run out, and reading on takes a length out of another
        // message's data.
        IReadOnlyList<Elm327.BatchAnswer> answers =
            Elm327.ParseBatch("410C1AF80B6455AAAA", [0x0C, 0x0B], Length);

        Assert.Equal([0x0C, 0x0B], answers.Select(a => a.Pid));
    }

    [Fact]
    public void TheWalkStopsWhenTheAnswerStartsRepeatingItself()
    {
        IReadOnlyList<Elm327.BatchAnswer> answers =
            Elm327.ParseBatch("410C1AF80C1AF8", [0x0C], Length);

        Assert.Single(answers);
    }

    [Fact]
    public void ATruncatedReplyKeepsWhatArrived()
    {
        // The last group is a parameter number with nothing behind it. What came
        // before it is still good, and is worth more than nothing.
        IReadOnlyList<Elm327.BatchAnswer> answers =
            Elm327.ParseBatch("410C1AF80B640D", [0x0C, 0x0B, 0x0D], Length);

        Assert.Equal([0x0C, 0x0B], answers.Select(a => a.Pid));
    }

    [Fact]
    public void AParameterOfUnknownLengthIsNeverAskedForInABatch()
    {
        // Its group could not be located in the reply, so it would not merely be
        // missing — it would lose every group after it.
        var car = Car();
        var elm = new Elm327(car);
        elm.Reset();

        elm.ReadMany([0x0C, 0xFE, 0x0D], Length);

        Assert.Contains("010C0D", car.Received);
    }

    // ----- deciding whether to do it at all ----------------------------------

    [Fact]
    public void ACarThatAnswersSeveralAtOnceIsAskedThatWayFromThenOn()
    {
        using Elm327Source source = Elm327Source.Connect(Car());

        Assert.True(source.Batching);
    }

    [Fact]
    public void OneParameterComingBackIsNotEvidenceOfBatching()
    {
        // The trap in probing this: an ordinary single-parameter reply to the
        // first parameter listed is indistinguishable from a batched reply that
        // carried one. Two is the smallest answer that can only have come from a
        // car that understood the question.
        using Elm327Source source = Elm327Source.Connect(Car(FakeElm.BatchReply.First));

        Assert.False(source.Batching);
    }

    [Fact]
    public void ALegacyBusIsNeverAskedForSeveralAtOnce()
    {
        // J1850 and the ISO serial buses have no such request. What arrives is
        // not refused, it is malformed.
        using Elm327Source source = Elm327Source.Connect(Car(protocol: 2));

        Assert.False(source.Batching);
    }

    [Fact]
    public void AnUndeterminedProtocolIsNotTreatedAsCan()
    {
        // "Not slow" is not "is CAN": an unknown protocol is neither, and both
        // answers have to be no. An asleep ECU answering STOPPED is how a link
        // comes to be unidentified at exactly the wrong moment, and a CAN-only
        // request sent on the strength of that reaches a truck as a malformed
        // one.
        using Elm327Source source = Elm327Source.Connect(Car(protocol: 0));

        Assert.False(source.Batching);
    }

    // ----- what it does to a round -------------------------------------------

    [Fact]
    public void ARoundBecomesTwoRequestsInsteadOfSix()
    {
        var car = Car();
        using Elm327Source source = Elm327Source.Connect(car);

        car.Received.Clear();
        source.Read();

        // One for the parameters a needle follows, one for as many of the rest
        // as fit. Every one of them carries several.
        Assert.Equal(2, car.Received.Count);
        Assert.All(car.Received, c => Assert.True(c.Length > 4, $"{c} asked for one parameter"));
    }

    [Fact]
    public void TheReadingsAreTheSameOnesAsAskingSingly()
    {
        var car = Car();
        using Elm327Source source = Elm327Source.Connect(car);

        double[] row = source.Read();

        Assert.Equal(1726, At(source, row, "RPM"), 0);
        Assert.Equal(100, At(source, row, "MAP"), 0);
        Assert.Equal(64, At(source, row, "Speed"), 0);
        Assert.Equal(50, At(source, row, "Coolant"), 0);
    }

    [Fact]
    public void TheSlowParametersStopBeingAQueue()
    {
        // One per round was what a round trip each forced. Six per round costs
        // exactly the same one request, so a fuel level or an oil temperature
        // comes round in a fraction of the time.
        var car = Car();
        using Elm327Source source = Elm327Source.Connect(car);

        double[] row = source.Read();

        // Coolant and IAT both rotate, and both are here after a single round.
        Assert.Equal(50, At(source, row, "Coolant"), 0);
        Assert.Equal(30, At(source, row, "IAT"), 0);
    }

    // ----- when it stops working ---------------------------------------------

    [Fact]
    public void ABatchThatAnswersNothingIsRetriedOneAtATime()
    {
        // The request failed, which says nothing about whether the car has these
        // sensors — so the round is asked again singly rather than concluded
        // from, and the gauges do not blink while batching is on probation.
        var car = Car();
        using Elm327Source source = Elm327Source.Connect(car);

        car.Batches = FakeElm.BatchReply.Refuse;
        double[] row = source.Read();

        Assert.Equal(1726, At(source, row, "RPM"), 0);
    }

    [Fact]
    public void ThreeUnansweredBatchesGiveUpOnBatchingAndNotOnTheChannels()
    {
        var car = Car();
        using Elm327Source source = Elm327Source.Connect(car);

        car.Batches = FakeElm.BatchReply.Refuse;

        for (int round = 0; round < 3; round++) source.Read();

        Assert.False(source.Batching);

        // The channels are all still being read, one at a time.
        double[] row = source.Read();

        Assert.Equal(1726, At(source, row, "RPM"), 0);
        Assert.Equal(100, At(source, row, "MAP"), 0);
    }

    [Fact]
    public void AParameterThatHasAnsweredIsNeverGivenUpOn()
    {
        // The retirement is never undone, so a run of transient silence — a busy
        // module, a moment of interference — would cost the channel for the rest
        // of the drive. Measured elsewhere on this protocol: a coolant reading
        // 97 °C dropped thirty seconds after it was on screen.
        var car = Car(FakeElm.BatchReply.Refuse);
        using Elm327Source source = Elm327Source.Connect(car);

        Assert.Equal(50, At(source, source.Read(), "Coolant"), 0);

        // The module goes quiet for a while — a good deal longer than the run
        // that used to retire a parameter — and then comes back.
        car.Answers.Remove(0x05);
        for (int round = 0; round < 16; round++) source.Read();

        car.Answers[0x05] = [0x5F];   // 55 °C

        double coolant = double.NaN;

        for (int round = 0; round < 16 && coolant != 55; round++)
            coolant = At(source, source.Read(), "Coolant");

        Assert.Equal(55, coolant, 0);
    }

    [Fact]
    public void AParameterMissingFromTheReplyIsAskedForOnItsOwn()
    {
        // A reply that carried some of what was asked for is not a round that
        // worked. The rest have to be asked again singly, because nothing else
        // here will ever come back to them: a parameter that has answered before
        // is deliberately never retired, so one that stops appearing in the
        // batched reply simply keeps the gauge it had — showing a reading from
        // whenever the reply last carried it, and looking live the whole time.
        var car = Car();
        using Elm327Source source = Elm327Source.Connect(car);

        Assert.True(source.Batching);
        Assert.Equal(50, At(source, source.Read(), "Coolant"), 0);

        // The car goes on answering for coolant. Its answer just stops arriving
        // as part of the batch.
        car.OmitFromBatch.Add(0x05);
        car.Answers[0x05] = [0x5F];   // 55 °C

        double coolant = double.NaN;

        for (int round = 0; round < 8 && coolant != 55; round++)
            coolant = At(source, source.Read(), "Coolant");

        Assert.Equal(55, coolant, 0);
        Assert.True(source.Batching, "batching was given up over a reply that mostly worked");
    }
}
