using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Splitting one stream into WCP lines and ELM327 bytes.
///
/// <para>
/// Each case is fed whole, then fed again a byte at a time, then at every split
/// point. A line torn across two reads is the ordinary case on a serial port —
/// the driver hands bytes over in whatever pieces it has — and a splitter that is
/// only right for whole lines is only right on a test bench.
/// </para>
/// </summary>
public class WcpDemuxTests
{
    private static (string Elm, List<string> Wcp) Split(string stream, int[]? cuts = null)
    {
        var elm = new StringBuilder();
        var wcp = new List<string>();
        var demux = new WcpDemux(b => elm.Append(Encoding.ASCII.GetString(b)), wcp.Add);

        byte[] bytes = Encoding.ASCII.GetBytes(stream);
        int at = 0;

        foreach (int cut in cuts ?? [])
        {
            demux.Feed(bytes.AsSpan(at, cut - at));
            at = cut;
        }

        demux.Feed(bytes.AsSpan(at));
        return (elm.ToString(), wcp);
    }

    /// <summary>The same answer however the stream is cut: whole, bytewise, and at every single split.</summary>
    private static void AlwaysSplitsAs(string stream, string elm, params string[] wcp)
    {
        var whole = Split(stream);
        Assert.Equal(elm, whole.Elm);
        Assert.Equal(wcp, whole.Wcp);

        var bytewise = Split(stream, [.. Enumerable.Range(1, Math.Max(0, stream.Length - 1))]);
        Assert.Equal(elm, bytewise.Elm);
        Assert.Equal(wcp, bytewise.Wcp);

        for (int cut = 1; cut < stream.Length; cut++)
        {
            var split = Split(stream, [cut]);
            Assert.True(elm == split.Elm, $"ELM side wrong when cut at {cut}: '{split.Elm}'");
            Assert.Equal(wcp, split.Wcp);
        }
    }

    [Fact]
    public void PlainElmPassesThroughUntouched() =>
        AlwaysSplitsAs("010C\r41 0C 1A F8\r\r>", "010C\r41 0C 1A F8\r\r>");

    [Fact]
    public void AReplyOnItsOwn() =>
        AlwaysSplitsAs("#1:wcp=1*76\r\n#1.OK*7C\r\n", "", "#1:wcp=1*76", "#1.OK*7C");

    [Fact]
    public void AWcpReplyBetweenTwoElmLines()
    {
        // The firmware finished a WCP request between the chip's echo and its
        // answer. The ELM327 side must see exactly what the chip sent, and no
        // stray line feed where the WCP line was.
        AlwaysSplitsAs(
            "010C\r#5.OK 1234*00\r\n41 0C 1A F8\r\r>",
            "010C\r41 0C 1A F8\r\r>",
            "#5.OK 1234*00");
    }

    [Fact]
    public void AnEventStraightAfterThePrompt()
    {
        // The commonest moment for an event to arrive: the chip has said ">" and
        // is waiting, with no line break after it. Only CR and LF counted as line
        // ends, the event would be read as the tail of the prompt and lost.
        AlwaysSplitsAs(
            "41 0C 1A F8\r\r>!CAN 1000 7E8 s 2 0102*00\r\n",
            "41 0C 1A F8\r\r>",
            "!CAN 1000 7E8 s 2 0102*00");
    }

    [Fact]
    public void CanEventsInterleavedThroughAnElmReply()
    {
        AlwaysSplitsAs(
            "!CAN 1 100 s 1 01*00\r\n0100\r!CAN 2 101 s 1 02*00\r\n41 00 BE 3F A8 13\r!DROP 4*00\r\n\r>!CAN 3 102 s 0*00\r\n",
            "0100\r41 00 BE 3F A8 13\r\r>",
            "!CAN 1 100 s 1 01*00", "!CAN 2 101 s 1 02*00", "!DROP 4*00", "!CAN 3 102 s 0*00");
    }

    [Fact]
    public void TheCrLfInFrontOfAWcpLineIsWcps()
    {
        // Contract 8.2: the firmware puts CR LF before every WCP line that does
        // not follow an LF. After a prompt, that CR LF is not the chip's.
        AlwaysSplitsAs(
            "41 0C 1A F8\r\r>\r\n!CAN 1000 7E8 s 2 0102*00\r\n",
            "41 0C 1A F8\r\r>",
            "!CAN 1000 7E8 s 2 0102*00");
    }

    [Fact]
    public void ABlankLineThatIsNotFollowedByWcpIsStillTheChips() =>
        AlwaysSplitsAs("OK\r\r\n\r>", "OK\r\r\n\r>");

    [Fact]
    public void AHashInsideAnElmLineIsElm()
    {
        // Not at a line start, so not WCP — whatever it is.
        AlwaysSplitsAs("AT#1\rOK\r\r>", "AT#1\rOK\r\r>");
    }

    [Fact]
    public void LinesEndedByLfAloneOrCrAlone()
    {
        AlwaysSplitsAs("#1.OK*7C\n#2.OK*00\rELM\r", "ELM\r", "#1.OK*7C", "#2.OK*00");
    }

    [Fact]
    public void ALfAfterAnElmCrIsNotSwallowed()
    {
        // Only the LF that ends a WCP line belongs to WCP. An adapter set to send
        // line feeds (ATL1) sends CR LF itself, and that LF is the chip's.
        AlwaysSplitsAs("OK\r\n\r\n>", "OK\r\n\r\n>");
    }

    [Fact]
    public void ARunawayLineIsDroppedAndTheStreamRecovers()
    {
        var elm = new StringBuilder();
        var wcp = new List<string>();
        var demux = new WcpDemux(b => elm.Append(Encoding.ASCII.GetString(b)), wcp.Add);

        demux.Feed(Encoding.ASCII.GetBytes("#" + new string('a', WcpDemux.MaxLine + 50) + "\r\n"));
        demux.Feed(Encoding.ASCII.GetBytes("#1.OK*7C\r\nOK\r>"));

        Assert.Equal(1, demux.Overflows);
        Assert.Equal(["#1.OK*7C"], wcp);
        Assert.Equal("OK\r>", elm.ToString());
    }

    [Fact]
    public void ResetForgetsAHalfLine()
    {
        var elm = new StringBuilder();
        var wcp = new List<string>();
        var demux = new WcpDemux(b => elm.Append(Encoding.ASCII.GetString(b)), wcp.Add);

        demux.Feed(Encoding.ASCII.GetBytes("#1:half"));
        demux.Reset();
        demux.Feed(Encoding.ASCII.GetBytes("ELM\r#2.OK*00\r\n"));

        Assert.Equal(["#2.OK*00"], wcp);
        Assert.Equal("ELM\r", elm.ToString());
    }
}
