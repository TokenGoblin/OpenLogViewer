using System.Diagnostics;
using OpenLogViewer.Core;
using Xunit;
using Piece = OpenLogViewer.Tests.ScriptedElm.Piece;

namespace OpenLogViewer.Tests;

/// <summary>
/// When a reply is finished — the framing rule, which is not what the datasheet
/// says it is.
///
/// An ELM327 is supposed to end every reply with a "&gt;" prompt, and a good one
/// does. A Vgate iCar Pro sends it on most reads and not on the rest: measured
/// against a live car, 20–40 % of reads ran out the whole window with a complete
/// payload already in the buffer and only the terminator missing. Waiting longer
/// cannot fix a character that is not coming, so quiet has to be able to finish a
/// reply — and every one of these tests exists because doing that naively breaks
/// something else.
///
/// Timing is real here rather than injected, so the margins are wide: what is
/// being asserted is "well inside the window" against "waited the window out",
/// never a precise duration.
/// </summary>
public class Elm327FramingTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    /// <summary>An adapter with the timings these tests are about.</summary>
    private static Elm327 Adapter(ScriptedElm wire) =>
        new(wire) { Timeout = Window, IdleGap = TimeSpan.FromMilliseconds(300) };

    private static TimeSpan TimeOf(Action work)
    {
        var clock = Stopwatch.StartNew();
        work();

        return clock.Elapsed;
    }

    // ----- the prompt --------------------------------------------------------

    [Fact]
    public void APromptStillFinishesAReplyAtOnce()
    {
        // The healthy case, which must not be made slower by any of the rest.
        var wire = new ScriptedElm(_ => [new Piece(TimeSpan.FromMilliseconds(20), "410C1AF8\r\r>")]);

        string reply = "";
        TimeSpan took = TimeOf(() => reply = Adapter(wire).Send("010C", Window));

        Assert.Contains("410C1AF8", reply, StringComparison.Ordinal);
        Assert.True(took < TimeSpan.FromMilliseconds(250), $"a prompt-terminated read took {took.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void AReplyWithNoPromptIsFinishedByTheQuietAfterIt()
    {
        // The Vgate's own failure: the payload arrives and the terminator never
        // does. Before this, the read burned the whole window and the poll rate
        // collapsed on a car that was answering perfectly.
        var wire = new ScriptedElm(_ => [new Piece(TimeSpan.FromMilliseconds(20), "410C1AF8\r")]);

        string reply = "";
        TimeSpan took = TimeOf(() => reply = Adapter(wire).Send("010C", Window));

        Assert.Contains("410C1AF8", reply, StringComparison.Ordinal);
        Assert.True(took < TimeSpan.FromMilliseconds(900), $"a promptless read took {took.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void SilenceIsNotAShortReply()
    {
        // Nothing arriving must keep waiting. An adapter that is merely slow is
        // told apart from one that has answered by whether anything came back at
        // all — so a read with an empty buffer waits out the window rather than
        // reporting an empty answer after the gap.
        var wire = new ScriptedElm(_ => []);

        TimeSpan took = TimeOf(() => Adapter(wire).Send("010C", TimeSpan.FromMilliseconds(900)));

        Assert.True(took > TimeSpan.FromMilliseconds(700), $"silence was given up on after {took.TotalMilliseconds:0} ms");
    }

    // ----- the echo ----------------------------------------------------------

    [Fact]
    public void TheEchoAloneDoesNotFinishAReply()
    {
        // THE trap the quiet rule creates. This adapter ignores ATE0: it echoes
        // the command, pauses, and only then sends the data — and for a longer
        // request the pause is longer than the gap. An echo accepted as an answer
        // completes the read before the answer exists, and the answer lands in
        // the next command's window: every reply one behind, for ever.
        var wire = new ScriptedElm(command =>
        [
            new Piece(TimeSpan.FromMilliseconds(10), command + "\r"),
            new Piece(TimeSpan.FromMilliseconds(700), "410C1AF8\r\r>"),
        ]);

        string reply = Adapter(wire).Send("010C", Window);

        Assert.Contains("410C1AF8", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEchoFollowedByARealAnswerIsStillFinishedByQuiet()
    {
        // The two rules together: the echo does not count, the data does, and
        // there is no prompt to end it.
        var wire = new ScriptedElm(command =>
        [
            new Piece(TimeSpan.FromMilliseconds(10), command + "\r"),
            new Piece(TimeSpan.FromMilliseconds(120), "410C1AF8\r"),
        ]);

        string reply = "";
        TimeSpan took = TimeOf(() => reply = Adapter(wire).Send("010C", Window));

        Assert.Contains("410C1AF8", reply, StringComparison.Ordinal);
        Assert.True(took < TimeSpan.FromMilliseconds(900), $"took {took.TotalMilliseconds:0} ms");
    }

    // ----- the leftover ------------------------------------------------------

    [Fact]
    public void APromptWithNothingBeforeItIsALeftoverAndNotAnAnswer()
    {
        // The consequence of finishing on quiet: the prompt that belonged to the
        // previous reply is still in flight, so it cannot be drained — it had not
        // been sent. It arrives at the very start of the next read, which by the
        // rule that a prompt ends a reply accepts it as a complete empty answer.
        // The real one then turns up a command late, and it sustains itself.
        var wire = new ScriptedElm(_ => [new Piece(TimeSpan.FromMilliseconds(90), "410C1AF8\r\r>")]);

        // Still in flight while the request goes out, which is the whole point:
        // a byte that has not been sent yet cannot be drained, so it arrives at
        // the front of the next read however carefully the buffer was cleared.
        wire.Interject(">", TimeSpan.FromMilliseconds(30));

        string reply = Adapter(wire).Send("010C", Window);

        Assert.Contains("410C1AF8", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdapterThatSendsNothingButLeftoversStillGivesUp()
    {
        // Discarding empty prompts must not become a loop. The timeout is what
        // bounds it, and it still has to.
        var wire = new ScriptedElm(_ =>
        [
            new Piece(TimeSpan.FromMilliseconds(10), ">"),
            new Piece(TimeSpan.FromMilliseconds(200), ">"),
            new Piece(TimeSpan.FromMilliseconds(400), ">"),
        ]);

        var adapter = new Elm327(wire) { Timeout = TimeSpan.FromMilliseconds(700) };

        string reply = "";
        TimeSpan took = TimeOf(() => reply = adapter.Send("010C", TimeSpan.FromMilliseconds(700)));

        Assert.Equal("", reply.Trim());
        Assert.True(took < TimeSpan.FromSeconds(2), $"gave up after {took.TotalMilliseconds:0} ms");
    }

    // ----- getting back into step --------------------------------------------

    [Fact]
    public void AnAnswerThatArrivesAfterItsTimeoutDoesNotBecomeTheNextOne()
    {
        // A timeout means the adapter was late, not absent. What has not arrived
        // yet cannot be discarded, so without waiting for it the late reply
        // satisfies the next read — and a reading lands on the wrong channel,
        // which is worse than a reading that never came.
        var wire = new ScriptedElm(command => command == "010C"
            ? [new Piece(TimeSpan.FromMilliseconds(550), "410C1AF8\r\r>")]
            : [new Piece(TimeSpan.FromMilliseconds(40), "41051E\r\r>")]);

        var adapter = new Elm327(wire) { Timeout = TimeSpan.FromMilliseconds(300) };

        // Times out: the rev counter's answer is still 250 ms away.
        adapter.Send("010C", TimeSpan.FromMilliseconds(300));

        string coolant = adapter.Send("0105", TimeSpan.FromSeconds(2));

        Assert.DoesNotContain("410C", coolant, StringComparison.Ordinal);
        Assert.Contains("41051E", coolant, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAnswerLaterThanAnyWaitStillCannotBeReadAsAnotherChannel()
    {
        // The tail wait is bounded, so an answer late enough will outlive it and
        // land in somebody else's read. What must never happen is that it is
        // believed: a reply carries the number of the parameter it answers, and
        // that check is the thing standing between a late rev counter and a
        // coolant gauge reading 1,726.
        var wire = new ScriptedElm(command => command == "010C"
            ? [new Piece(TimeSpan.FromSeconds(2), "410C1AF8\r\r>")]
            : []);

        var adapter = new Elm327(wire) { Timeout = TimeSpan.FromMilliseconds(200), TailWait = TimeSpan.FromMilliseconds(50) };

        adapter.Send("010C", TimeSpan.FromMilliseconds(200));

        Span<byte> data = stackalloc byte[8];
        bool answered = adapter.TryRead(0x05, 1, data, out _, TimeSpan.FromSeconds(3));

        Assert.False(answered, "the rev counter's answer was accepted as a coolant reading");
    }

    [Fact]
    public void ADroppedInstalmentDoesNotCostTheOnesAfterIt()
    {
        // A car with more than one responding module answers on several lines,
        // and the gap has to be wide enough to hold them together. Two modules
        // 90 ms apart is inside it; the quiet rule must not cut the second one
        // off, because what is lost is every parameter only that module reports.
        var wire = new ScriptedElm(_ =>
        [
            new Piece(TimeSpan.FromMilliseconds(20), "4100BE3FA813\r"),
            new Piece(TimeSpan.FromMilliseconds(110), "410080000001\r"),
        ]);

        string reply = Adapter(wire).Send("0100", Window);

        Assert.Equal(2, Elm327.ParseAll(reply, 0x00, 4).Count);
    }

    // ----- a link that has gone ----------------------------------------------

    /// <summary>
    /// A link whose far end has closed: every read comes back empty, at once and
    /// for ever.
    ///
    /// What <see cref="WifiEcuTransport"/> does once its socket has seen the
    /// FIN. There is nothing left to wait for, so it does not wait — which is
    /// the whole point, and also what makes the caller's "keep reading until the
    /// deadline" into a spin rather than a sleep.
    /// </summary>
    private sealed class ClosedLink : IEcuTransport
    {
        public bool IsOpen => true;

        public void Open() { }

        public void Close() { }

        public void Dispose() { }

        public void Write(ReadOnlySpan<byte> data) { }

        public int Read(Span<byte> buffer, TimeSpan timeout) => 0;

        public void DiscardInput() { }
    }

    [Fact]
    public void AClosedLinkIsGivenUpOnRatherThanSpunOn()
    {
        // A transport that returns empty without waiting means the link is gone,
        // and the read has to say so instead of asking again. Going round again
        // costs nothing to ask and gets the same answer, so the loop runs flat
        // out until the deadline: a core at 100 % for two seconds a command and
        // five on a reset, which is precisely when the dongle has dropped the
        // session and the recovery wants to be quick.
        TimeSpan took = TimeOf(() => new Elm327(new ClosedLink()).Send("010C", Window));

        Assert.True(took < TimeSpan.FromMilliseconds(250), $"a dead link took {took.TotalMilliseconds:0} ms to give up");
    }
}
