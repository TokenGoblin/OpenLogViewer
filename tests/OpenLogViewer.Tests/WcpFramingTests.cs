using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// WCP's framing: the CRC, building requests, and reading every kind of line back.
///
/// <para>
/// The expected CRCs are not computed by the code under test. The check value is
/// the published one for CRC-8/SMBUS, the single-byte case was worked by hand
/// (below), and the rest were computed by an independent few lines of Python
/// written from the polynomial, not from this implementation.
/// </para>
/// </summary>
public class WcpFramingTests
{
    // ----- the CRC -------------------------------------------------------------

    [Fact]
    public void TheCheckValueIsThePublishedOne() =>
        Assert.Equal(0xF4, Wcp.Crc8("123456789"));

    [Fact]
    public void OneByteWorkedByHand()
    {
        // '#' = 0x23 = 0010_0011. Shift left eight times, XOR 0x07 in whenever
        // the bit falling off the top was a one:
        //   0x46, 0x8C, (0x18^07)=0x1F, 0x3E, 0x7C, 0xF8, (0xF0^07)=0xF7, (0xEE^07)=0xE9
        Assert.Equal(0xE9, Wcp.Crc8("#"));
    }

    [Theory]
    [InlineData("#1 hello", 0x5F)]
    [InlineData("#1.OK", 0x7C)]
    [InlineData("#1:wcp=1", 0x76)]
    [InlineData("!DROP 3", 0xC8)]
    [InlineData("#2 ping", 0x88)]
    [InlineData("#7 can arm 30 7E0,7DF", 0x4F)]
    [InlineData("#3:sta_pass=****", 0x2B)]
    [InlineData("#0.ERR CRC", 0xF9)]
    public void KnownLines(string body, int crc) => Assert.Equal(crc, Wcp.Crc8(body));

    [Fact]
    public void TheEmptyStringIsTheInitialValue() => Assert.Equal(0, Wcp.Crc8(""));

    // ----- requests ------------------------------------------------------------

    [Fact]
    public void ARequestIsHashSeqSpaceCommandStarCrc() =>
        Assert.Equal("#1 hello*5F", Wcp.Request(1, "hello"));

    [Fact]
    public void ArgumentsAreCoveredByTheCrc() =>
        Assert.Equal("#7 can arm 30 7E0,7DF*4F", Wcp.Request(7, "can arm 30 7E0,7DF"));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void ASeqOutsideOneTo65535IsRefused(int seq) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Wcp.Request(seq, "ping"));

    [Theory]
    [InlineData("config set a=b\rATZ")]
    [InlineData("config set a=b\nATZ")]
    public void ALineBreakInACommandIsRefused(string command)
    {
        // The half after the break would reach the ELM327 chip as a command of
        // its own — ATZ here, which resets it under a running session.
        Assert.Throws<ArgumentException>(() => Wcp.Request(1, command));
    }

    [Fact]
    public void ARequestPastTheFirmwaresLimitIsRefusedBeforeItIsSent() =>
        Assert.Throws<ArgumentException>(() => Wcp.Request(1, "config set note=" + new string('a', 600)));

    // ----- reading lines -------------------------------------------------------

    [Fact]
    public void ADataLine()
    {
        Assert.True(WcpLine.TryParse("#1:wcp=1*76", out WcpLine line, out _));

        Assert.Equal(WcpLineKind.Data, line.Kind);
        Assert.Equal(1, line.Seq);
        Assert.Equal("wcp=1", line.Text);
    }

    [Fact]
    public void AnOk()
    {
        Assert.True(WcpLine.TryParse("#1.OK*7C", out WcpLine line, out _));

        Assert.Equal(WcpLineKind.Ok, line.Kind);
        Assert.True(line.IsFinal);
        Assert.Equal("", line.Text);
    }

    [Fact]
    public void AnOkWithASummary()
    {
        Assert.True(WcpLine.TryParse(Wcp.Frame("#42.OK 123456"), out WcpLine line, out _));

        Assert.Equal(42, line.Seq);
        Assert.Equal("123456", line.Text);
    }

    [Fact]
    public void AnErrorWithItsCodeAndMessage()
    {
        Assert.True(WcpLine.TryParse(Wcp.Frame("#9.ERR DENIED rate"), out WcpLine line, out _));

        Assert.Equal(WcpLineKind.Error, line.Kind);
        Assert.True(line.IsFinal);
        Assert.Equal("DENIED", line.Code);
        Assert.Equal("rate", line.Text);
    }

    [Fact]
    public void TheCrcErrorThatCouldNotReadTheSeq()
    {
        Assert.True(WcpLine.TryParse("#0.ERR CRC*F9", out WcpLine line, out _));

        Assert.Equal(0, line.Seq);
        Assert.Equal("CRC", line.Code);
    }

    [Fact]
    public void AnEvent()
    {
        Assert.True(WcpLine.TryParse("!DROP 3*C8", out WcpLine line, out _));

        Assert.Equal(WcpLineKind.Event, line.Kind);
        Assert.Equal("DROP", line.Code);
        Assert.Equal("3", line.Text);
    }

    [Fact]
    public void AMaskedPasswordIsNotMistakenForTheCrc()
    {
        // Five stars on the line: four of the mask and one of the framing. Split
        // on the first, and the CRC is checked over "#3:sta_pass=" and fails a
        // line that is perfectly good.
        Assert.True(WcpLine.TryParse("#3:sta_pass=*****2B", out WcpLine line, out bool damaged));

        Assert.False(damaged);
        Assert.Equal("sta_pass=****", line.Text);
    }

    [Fact]
    public void ABadCrcIsRefusedAndSaidToBeDamaged()
    {
        Assert.False(WcpLine.TryParse("#1:wcp=1*77", out _, out bool damaged));
        Assert.True(damaged);
    }

    [Fact]
    public void ADroppedByteFailsTheCrc()
    {
        // The failure the CRC is for: a byte lost from the middle of a line that
        // still reads as a plausible line. 7E0 has become 70.
        Assert.False(WcpLine.TryParse("#7 can arm 30 70,7DF*4F", out _, out bool damaged));
        Assert.True(damaged);
    }

    [Theory]
    [InlineData("41 0C 1A F8")]
    [InlineData("NO DATA")]
    [InlineData(">")]
    [InlineData("")]
    [InlineData("#1:wcp=1")]
    [InlineData("#1:wcp=1*7")]
    [InlineData("#1:wcp=1*ZZ")]
    public void WhatIsNotWcpIsNotCalledDamaged(string line)
    {
        Assert.False(WcpLine.TryParse(line, out _, out bool damaged));
        Assert.False(damaged);
    }

    [Theory]
    [InlineData("#x.OK")]
    [InlineData("#.OK")]
    [InlineData("#1234567.OK")]
    [InlineData("#70000.OK")]
    [InlineData("#1?OK")]
    [InlineData("!")]
    public void AWellFramedLineThatIsNotAReplyShapeIsRefused(string body) =>
        Assert.False(WcpLine.TryParse(Wcp.Frame(body), out _, out _));

    [Fact]
    public void AnEchoedRequestIsRecognisedAsOne()
    {
        // What a stock unit sends back: its ELM327 chip echoing the request.
        Assert.True(WcpLine.TryParse("#1 hello*5F", out WcpLine line, out _));

        Assert.Equal(WcpLineKind.Request, line.Kind);
        Assert.Equal("hello", line.Text);
    }

    [Fact]
    public void FramingRoundTrips()
    {
        string framed = Wcp.Frame("#12:volt=12.6");

        Assert.True(Wcp.TryUnframe(framed, out string body, out bool ok));
        Assert.True(ok);
        Assert.Equal("#12:volt=12.6", body);
    }

    // ----- sequence numbers ----------------------------------------------------

    [Fact]
    public void SeqStartsAtOneWhenToldTo() => Assert.Equal(1, new WcpSequence(0).Next());

    [Fact]
    public void SeqStartsSomewhereRandomByDefault()
    {
        // F17: a firmware that answers a repeated seq from its reply store must
        // not be handed the last session's seqs again by a new one.
        int[] starts = [.. Enumerable.Range(0, 8).Select(_ => new WcpSequence().Next())];

        Assert.All(starts, s => Assert.InRange(s, 1, 65535));
        Assert.True(starts.Distinct().Count() > 1, string.Join(",", starts));
    }

    [Fact]
    public void SeqWrapsPastZero()
    {
        // Zero is the firmware's "your seq was unreadable", so it is never used.
        var seq = new WcpSequence(65534);

        Assert.Equal(65535, seq.Next());
        Assert.Equal(1, seq.Next());
    }
}
