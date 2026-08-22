using System.Diagnostics;
using OpenLogViewer.Core;
using Xunit;
using Piece = OpenLogViewer.Tests.ScriptedElm.Piece;

namespace OpenLogViewer.Tests;

/// <summary>
/// The two things a Vgate iCar Pro does that no amount of correct protocol
/// survives, and the one that makes single-parameter polling bearable.
///
/// Both were settled on a running car, and the second reverses something that
/// looked like a straightforward win: asking for six parameters at once is three
/// times fewer round trips, and on this dongle it kills the TCP session — not by
/// refusing the request but by answering it, completely and on time, and then
/// never speaking again. So the dongle has to be able to be given up on, and the
/// giving up has to be remembered, because finding it out costs a dropped link
/// every time.
/// </summary>
public class Obd2WedgeTests
{
    private static int Length(byte pid) => Obd2Pids.DataBytesOf(pid);

    private static TimeSpan TimeOf(Action work)
    {
        var clock = Stopwatch.StartNew();
        work();

        return clock.Elapsed;
    }

    /// <summary>A car answering the parameters most of them do.</summary>
    private static FakeElm Car(FakeElm.BatchReply batches = FakeElm.BatchReply.All)
    {
        var car = new FakeElm { Batches = batches };

        car.Answers[0x00] = [0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000];
        car.Answers[0x04] = [0x7F];
        car.Answers[0x05] = [0x5A];
        car.Answers[0x0B] = [0x64];
        car.Answers[0x0C] = [0x1A, 0xF8];
        car.Answers[0x0D] = [0x40];
        car.Answers[0x0F] = [0x46];
        car.Answers[0x11] = [0x33];

        return car;
    }

    // ----- the reply finishing itself ----------------------------------------

    [Fact]
    public void AnAnswerOfTheLengthItHadToBeDoesNotWaitForThePrompt()
    {
        // The prompt trails the payload by about 210 ms on this adapter, and
        // with batching dead that gap is the poll cycle: every read holding a
        // complete answer sat waiting for a terminator.
        var wire = new ScriptedElm(_ =>
        [
            new Piece(TimeSpan.FromMilliseconds(20), "410C1AF8"),
            new Piece(TimeSpan.FromMilliseconds(900), "\r\r>"),
        ]);

        var adapter = new Elm327(wire) { Timeout = TimeSpan.FromSeconds(3) };

        var data = new byte[8];
        bool got = false;
        TimeSpan took = TimeOf(() => got = adapter.TryRead(0x0C, 2, data, out _));

        Assert.True(got);
        Assert.True(took < TimeSpan.FromMilliseconds(250), $"the read took {took.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void NoDataIsNotMistakenForAnAnswerOfTheRightLength()
    {
        // Worth its own test: "NODATA" is six characters, exactly as many as a
        // one-byte reply, so counting alone would take it for one. What rules it
        // out is that an answer begins 41 and this does not.
        var wire = new ScriptedElm(_ => [new Piece(TimeSpan.FromMilliseconds(20), "NO DATA\r\r>")]);
        var adapter = new Elm327(wire) { Timeout = TimeSpan.FromSeconds(2) };

        Span<byte> data = stackalloc byte[8];

        Assert.False(adapter.TryRead(0x05, 1, data, out _));
    }

    [Fact]
    public void ARefusalFromOneModuleDoesNotEndTheReadBeforeAnotherAnswers()
    {
        // Where the 41 test earns its place, and it is not the one it looks
        // like. "NO DATA" is already ruled out by having letters in it; a
        // negative response is not — 7F 01 12 is pure hex and exactly as long as
        // a one-byte answer, so counting alone takes it for one.
        //
        // A request is a broadcast, so that refusal can be one module's while
        // the answer is another's, arriving behind it. Ending the read on the
        // refusal loses the reading and leaves the real answer to turn up in the
        // next read, one command out of step.
        var wire = new ScriptedElm(_ =>
        [
            new Piece(TimeSpan.FromMilliseconds(10), "7F0112\r"),
            new Piece(TimeSpan.FromMilliseconds(120), "41051E\r\r>"),
        ]);

        var adapter = new Elm327(wire) { Timeout = TimeSpan.FromSeconds(2) };
        var data = new byte[8];

        Assert.True(adapter.TryRead(0x05, 1, data, out _), "the refusal was taken for the answer");
        Assert.Equal(0x1E, data[0]);
    }

    [Fact]
    public void ASegmentedReplyIsNeverCutShortByTheCount()
    {
        // The guard that earns its place. A second module's fragment ahead of a
        // segmented answer shifts the count, so an equality test that kept
        // counting would strike mid-reply and truncate it. Any character that is
        // not hex — the colon of a marker — says this is not the reply the
        // length was computed for.
        var wire = new ScriptedElm(_ =>
        [
            new Piece(TimeSpan.FromMilliseconds(10), "410100040000\r"),
            new Piece(TimeSpan.FromMilliseconds(40), "008\r0:415C600C1AF8\r"),
            new Piece(TimeSpan.FromMilliseconds(70), "1:0D40\r\r>"),
        ]);

        var adapter = new Elm327(wire) { Timeout = TimeSpan.FromSeconds(2) };

        IReadOnlyList<Elm327.BatchAnswer> answers =
            adapter.ReadMany([0x01, 0x5C, 0x0C, 0x0D], Length);

        Assert.Equal([0x5C, 0x0C, 0x0D], answers.Select(a => a.Pid));
    }

    [Fact]
    public void ACapabilityBitmapWaitsForEveryModuleThatAnswers()
    {
        // Never finished by length, by construction: a mode-01 request is a
        // broadcast, the transmission's sparse map can arrive tens of
        // milliseconds ahead of the engine's, and both are wanted. Stopping at
        // the first would make discovery a coin toss — and the map that wins is
        // the one without a rev counter in it.
        var wire = new ScriptedElm(_ =>
        [
            new Piece(TimeSpan.FromMilliseconds(10), "410080000001\r"),
            new Piece(TimeSpan.FromMilliseconds(120), "4100BE3FA813\r\r>"),
        ]);

        var adapter = new Elm327(wire) { Timeout = TimeSpan.FromSeconds(2) };

        Assert.Equal(2, adapter.ReadAll(0x00, 4).Count);
    }

    // ----- the dongle that dies of being asked -------------------------------

    /// <summary>What a session remembers, without a settings file behind it.</summary>
    private sealed class Remembered : IObd2BatchMemory
    {
        private readonly Dictionary<string, int> _deaths = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Recorded { get; } = [];

        public int DeathsOn(string adapter) => _deaths.GetValueOrDefault(adapter);

        public void Died(string adapter)
        {
            _deaths[adapter] = DeathsOn(adapter) + 1;
            Recorded.Add(adapter);
        }
    }

    [Fact]
    public void BatchingThatStopsBeingAnsweredIsWrittenDownAgainstTheAdapter()
    {
        var car = Car();
        var memory = new Remembered();

        using Elm327Source source = Elm327Source.Connect(car, "192.168.0.10:35000", memory);
        Assert.True(source.Batching);

        // The session dies the way this dongle's does: it answered the batch,
        // and now it answers nothing.
        car.Batches = FakeElm.BatchReply.Refuse;
        for (int round = 0; round < 3; round++) source.Read();

        Assert.False(source.Batching);
        Assert.Equal(["192.168.0.10:35000"], memory.Recorded);
    }

    [Fact]
    public void ALinkThatDiesAltogetherIsNotBlamedOnBatching()
    {
        // Measured, by connecting: this dongle's session dies during ordinary
        // single-parameter traffic too, and when it does every read returns
        // nothing while the socket still reports itself connected. A silent
        // batch is therefore not evidence about the batch — it is what a dead
        // link looks like from here, and writing a permanent verdict on it
        // would condemn a capable adapter for something it did not do.
        //
        // The fallback is what tells them apart: singles answering where the
        // batch did not is the request being refused; singles falling silent
        // too is the link, and batching is a bystander.
        var car = Car();
        var memory = new Remembered();

        using Elm327Source source = Elm327Source.Connect(car, "192.168.0.10:35000", memory);
        Assert.True(source.Batching);

        // Everything stops answering, batch and single alike.
        car.Answers.Clear();

        for (int round = 0; round < 4; round++)
        {
            try { source.Read(); }
            catch (EcuProtocolException) { }
        }

        Assert.Empty(memory.Recorded);
    }

    [Fact]
    public void AnAdapterWithFormIsNotEvenAsked()
    {
        // The probe is itself a batched request, so probing something already
        // known to fail is not a cheap check — it is the entire cost of the
        // thing being checked for, paid again on every connection.
        var memory = new Remembered();

        for (int drive = 0; drive < Elm327Source.BatchDeathsBeforeGivingUp; drive++)
            memory.Died("192.168.0.10:35000");

        var car = Car();
        using Elm327Source source = Elm327Source.Connect(car, "192.168.0.10:35000", memory);

        Assert.False(source.Batching);
        Assert.DoesNotContain(car.Received, c => c.StartsWith("01", StringComparison.Ordinal) && c.Length > 4);
    }

    [Fact]
    public void OneBadLinkIsNotEnoughToCondemnAnAdapter()
    {
        // A link can die for reasons that have nothing to do with the request in
        // flight — out of range, key off, a cable pulled. Giving up on the first
        // would cost the whole advantage on a capable adapter, permanently and
        // invisibly.
        var memory = new Remembered();
        memory.Died("192.168.0.10:35000");

        using Elm327Source source = Elm327Source.Connect(Car(), "192.168.0.10:35000", memory);

        Assert.True(source.Batching);
    }

    [Fact]
    public void WhatIsRememberedIsTheAdapterAndNotTheCar()
    {
        // A different dongle starts clean. The verdict is about hardware that
        // cannot survive a request, and carrying it to another device would hide
        // a working feature for no reason.
        var memory = new Remembered();

        for (int drive = 0; drive < Elm327Source.BatchDeathsBeforeGivingUp; drive++)
            memory.Died("192.168.0.10:35000");

        using Elm327Source source = Elm327Source.Connect(Car(), "192.168.4.1:35000", memory);

        Assert.True(source.Batching);
    }

    [Fact]
    public void WithNothingRememberingItTheSessionStillLearnsWithin()
    {
        // No memory at all is the old behaviour and must still work: it costs
        // one link per session rather than one ever.
        var car = Car();
        using Elm327Source source = Elm327Source.Connect(car);

        car.Batches = FakeElm.BatchReply.Refuse;
        for (int round = 0; round < 3; round++) source.Read();

        Assert.False(source.Batching);
    }

    // ----- the corpse --------------------------------------------------------

    [Fact]
    public void ASessionThatAnswersNothingToAResetIsNotWalkedIntoAsIfItWereSlow()
    {
        // ATZ never reaches the vehicle — the adapter answers it out of its own
        // firmware — so silence is a dead session rather than a sleeping car.
        // Walking on spends the warm-up, the protocol question and a whole poll
        // round proving what the first reply already said, which is most of ten
        // seconds of blank gauges before anything reconnects.
        var wire = new Fragile(Car(FakeElm.BatchReply.Refuse));
        using Elm327Source source = Elm327Source.Connect(wire);

        wire.Wedged = true;

        TimeSpan took = TimeOf(() => Assert.Throws<EcuProtocolException>(source.Recover));

        // One generous reset, because this adapter's banner really can be a
        // second late, and then a short second opinion — against the fifteen or
        // twenty seconds of handshake and polling that a corpse used to absorb
        // before anything concluded it was one.
        Assert.True(took < TimeSpan.FromSeconds(8), $"a corpse took {took.TotalSeconds:0.0} s to report itself");
    }

    /// <summary>
    /// A link that works, and then does not — while still reporting itself open,
    /// accepting every write, and answering none of them. The shape of the
    /// wedge: not a refused connection, a session that has stopped existing
    /// without saying so.
    /// </summary>
    private sealed class Fragile(FakeElm inner) : IEcuTransport
    {
        public bool Wedged { get; set; }

        public bool IsOpen => inner.IsOpen;

        public void Open() => inner.Open();

        public void Close() => inner.Close();

        public void Dispose() => inner.Dispose();

        public void DiscardInput() => inner.DiscardInput();

        public void Write(ReadOnlySpan<byte> data)
        {
            if (!Wedged) inner.Write(data);
        }

        public int Read(Span<byte> buffer, TimeSpan timeout) =>
            Wedged ? 0 : inner.Read(buffer, timeout);
    }

    /// <summary>
    /// The dongle this whole file is about: a link that answers a request for
    /// several parameters, completely and on time, and has stopped existing by
    /// the time the next one is sent.
    ///
    /// A fresh connection brings it back, which is the part that matters. The
    /// socket is what dies, not the adapter and not the car, so reopening gets a
    /// working link — and one batched request kills that one too.
    /// </summary>
    private sealed class KilledByBatching(FakeElm inner) : IEcuTransport
    {
        private bool _dead;

        public bool IsOpen => inner.IsOpen;

        public void Open()
        {
            _dead = false;
            inner.Open();
        }

        public void Close() => inner.Close();

        public void Dispose() => inner.Dispose();

        public void DiscardInput() => inner.DiscardInput();

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_dead) return;

            inner.Write(data);

            // Mode 01 carrying more than one parameter — "010C0B" and longer.
            // The reply to it has already been queued, and is allowed through:
            // answering is exactly what this adapter does before it goes.
            string command = System.Text.Encoding.ASCII.GetString(data).Trim();

            if (command.StartsWith("01", StringComparison.OrdinalIgnoreCase) && command.Length > 4)
                _dead = true;
        }

        public int Read(Span<byte> buffer, TimeSpan timeout) =>
            inner.Read(buffer, timeout);
    }

    [Fact]
    public void ALinkKilledByBatchingComesBackWithoutItRatherThanBeingKilledAgain()
    {
        // The failure the memory exists for, and the one path that never reached
        // it. The probe is a batched request, so a recovery that probes kills the
        // link it has just rebuilt — connect, probe, die, reconnect, for as long
        // as the reconnection window lasts, with nothing concluded and nothing
        // written down.
        var wire = new KilledByBatching(Car());
        var memory = new Remembered();

        using Elm327Source source = Elm327Source.Connect(wire, "192.168.0.10:35000", memory);

        // The probe itself was the fatal request: it was answered, and the link
        // is already gone.
        Assert.True(source.Batching);
        Assert.Throws<EcuProtocolException>(() => source.Read());

        source.Recover();

        Assert.False(source.Batching, "the recovery went back to batching on a link batching had killed");
        Assert.Equal(["192.168.0.10:35000"], memory.Recorded);

        // And the session is a working one, on single requests.
        Assert.Equal(1726, source.Read()[source.Names.ToList().IndexOf("RPM")], 0);
    }

    [Fact]
    public void AReconnectionThatFollowedNoDeathBlamesNothing()
    {
        // Recover is public and is not only reached from a link that has died —
        // a reconnection can be asked for. Nothing was in flight, so there is
        // nothing to draw a conclusion from, and a verdict written here would be
        // written against an adapter that had done nothing at all.
        var car = Car();
        var memory = new Remembered();

        using Elm327Source source = Elm327Source.Connect(car, "192.168.0.10:35000", memory);
        Assert.True(source.Batching);

        source.Recover();

        Assert.Empty(memory.Recorded);
        Assert.True(source.Batching, "a reconnection gave up batching on its own");
    }

    [Fact]
    public void ALinkThatDiedWithBatchingAlreadyOffIsNotBlamedForIt()
    {
        // The same rule from the other side. Once batching has been given up,
        // every later death happened without it — so nothing that follows is
        // evidence about it, and the count must not go on climbing towards a
        // verdict the adapter has already had.
        var car = Car(FakeElm.BatchReply.Refuse);
        var memory = new Remembered();

        using Elm327Source source = Elm327Source.Connect(car, "192.168.0.10:35000", memory);
        Assert.False(source.Batching, "the refused probe turned batching on");

        // Everything stops answering, and then comes back.
        car.Answers.Clear();

        try { source.Read(); }
        catch (EcuProtocolException) { }

        car.Answers[0x0C] = [0x1A, 0xF8];
        source.Recover();

        Assert.Empty(memory.Recorded);
    }
}
