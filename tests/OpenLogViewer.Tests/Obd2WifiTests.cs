using System.Net.Sockets;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The Wi-Fi OBD2 link — a Vgate iCar Pro and the dongles built like it.
///
/// The adapter is its own access point with a socket behind it, so there is no
/// port to open, nothing to pair with and nothing to enumerate: an address is
/// the whole of what it is reached by. These check the parts that are specific
/// to that, against a real loopback socket, and leave the ELM327 conversation
/// itself to <see cref="Obd2Tests"/> — it is the same conversation.
/// </summary>
public class Obd2WifiTests
{
    /// <summary>A car answering the parameters most of them do.</summary>
    private static FakeElm Car(bool stickyEcho = false)
    {
        var car = new FakeElm { StickyEcho = stickyEcho };

        // Supported [01-20]: 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11, and the
        // last bit clear, so nothing is asked for above this range.
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

    // ----- the address -------------------------------------------------------

    [Fact]
    public void TheFirstAddressTriedIsWhereAVgateAnswers()
    {
        // Fixed in the dongle's own firmware rather than handed out by a router,
        // because the dongle is the network.
        Assert.Equal("192.168.0.10:35000", WifiEcuTransport.KnownAddresses[0]);
    }

    [Fact]
    public void AnAddressWithoutAPortGetsTheOneTheseListenOn()
    {
        using var transport = WifiEcuTransport.At("192.168.0.10");

        Assert.Equal("192.168.0.10", transport.Host);
        Assert.Equal(35000, transport.Port);
    }

    [Fact]
    public void AnAddressWithAPortKeepsIt()
    {
        using var transport = WifiEcuTransport.At("192.168.4.1:35001");

        Assert.Equal("192.168.4.1", transport.Host);
        Assert.Equal(35001, transport.Port);
    }

    [Fact]
    public void SomethingThatIsNotAPortAfterTheColonIsPartOfTheName()
    {
        // A host is not truncated to something that does not resolve just because
        // it has a colon in it.
        using var transport = WifiEcuTransport.At("obd:link");

        Assert.Equal("obd:link", transport.Host);
        Assert.Equal(WifiEcuTransport.DefaultPort, transport.Port);
    }

    // ----- connecting --------------------------------------------------------

    [Fact]
    public void AnAdapterOnTheNetworkAnswersAndTheCarReportsItsParameters()
    {
        var car = Car();
        using var dongle = new FakeElmOverTcp(car);

        using Elm327Source source = Elm327Source.ConnectOverWifi(dongle.Address);

        Assert.Equal("ELM327 v1.5", source.Adapter);
        Assert.Equal(7, source.Parameters.Count);
    }

    [Fact]
    public void TheAddressThatAnsweredIsRemembered()
    {
        // The only way back to the same device: nothing lists a Wi-Fi dongle, so
        // unless the session records which address answered there is nowhere to
        // read it off afterwards.
        var car = Car();
        using var dongle = new FakeElmOverTcp(car);

        using Elm327Source source = Elm327Source.ConnectOverWifi(dongle.Address);

        Assert.Equal(dongle.Address, source.Link);
    }

    [Fact]
    public void APortOrARadioHasNoAddressToRecord()
    {
        using Elm327Source source = Elm327Source.Connect(Car());

        Assert.Equal("", source.Link);
    }

    [Fact]
    public void ReadingsComeBackOverTheSocket()
    {
        var car = Car();
        using var dongle = new FakeElmOverTcp(car);

        using Elm327Source source = Elm327Source.ConnectOverWifi(dongle.Address);

        double[] row = source.Read();
        int rpm = source.Names.ToList().IndexOf("RPM");

        Assert.True(rpm >= 0, "the car reported no rev counter");
        Assert.Equal(1726, row[rpm], 0);
    }

    [Fact]
    public void AnAdapterThatEchoesDespiteBeingToldNotToStillWorks()
    {
        // What a Vgate iCar Pro does: ATE0 is acknowledged and ignored, so every
        // reply arrives behind the command that caused it. Read as one run of hex
        // that is a car supporting nothing.
        var car = Car(stickyEcho: true);
        using var dongle = new FakeElmOverTcp(car);

        using Elm327Source source = Elm327Source.ConnectOverWifi(dongle.Address);

        Assert.True(car.Echo, "the fake stopped echoing, so this proves nothing");
        Assert.Equal(7, source.Parameters.Count);
    }

    // ----- when it is not there ----------------------------------------------

    [Fact]
    public void NothingAtTheAddressSaysWhatToDoAboutIt()
    {
        IOException failure = Assert.Throws<IOException>(
            () => Elm327Source.ConnectOverWifi(FakeElmOverTcp.ClosedAddress()));

        // The address, and the thing that is actually wrong when a Wi-Fi dongle
        // does not answer — which is nearly always which network this computer
        // is on. "The target machine actively refused it" sends nobody anywhere.
        Assert.Contains("127.0.0.1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("V-LINK", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdapterThatHangsUpIsNotWaitedOnForever()
    {
        // A closed stream socket reports itself by returning nothing, at once and
        // for ever. Read as "no data yet" it spins the read loop flat out until
        // the timeout, on every request, for the rest of the session.
        using var dongle = new FakeElmOverTcp(Car(), closeImmediately: true);
        using var transport = WifiEcuTransport.At(dongle.Address);

        transport.Open();

        var buffer = new byte[16];
        int got = -1;

        TimeSpan took = FakeElmOverTcp.TimeOf(
            () => got = transport.Read(buffer, TimeSpan.FromSeconds(10)));

        Assert.Equal(0, got);
        Assert.True(took < TimeSpan.FromSeconds(2), $"a closed link took {took.TotalSeconds:0.0} s to report itself");
    }

    [Fact]
    public void AReadOnAnAdapterThatWasNeverOpenedSaysSo()
    {
        using var transport = new WifiEcuTransport("192.168.0.10");

        Assert.Throws<InvalidOperationException>(() => transport.Read(new byte[4], TimeSpan.Zero));
    }

    // ----- the stream itself -------------------------------------------------

    [Fact]
    public void AReplyArrivingInPiecesIsReadWhole()
    {
        var car = Car();
        using var dongle = new FakeElmOverTcp(car);
        using var transport = WifiEcuTransport.At(dongle.Address);

        transport.Open();
        transport.Write("ATI\r"u8);

        // The echoed command, "ELM327 v1.5" and the prompt — eighteen bytes,
        // which the fake sends in seven-byte pieces. This only passes if a short
        // read comes back for the rest of them.
        var buffer = new byte[18];
        int got = transport.Read(buffer, TimeSpan.FromSeconds(2));

        Assert.Equal(18, got);
        Assert.Contains("ELM327 v1.5", System.Text.Encoding.ASCII.GetString(buffer, 0, got), StringComparison.Ordinal);
    }

    [Fact]
    public void WhatArrivedBeforeARequestIsDropped()
    {
        // The tail of a previous exchange, read as the front of the next answer,
        // decodes as a different reading altogether.
        var car = Car();
        using var dongle = new FakeElmOverTcp(car);
        using var transport = WifiEcuTransport.At(dongle.Address);

        transport.Open();
        transport.Write("ATI\r"u8);

        // Long enough for the whole reply to have arrived and be waiting.
        Thread.Sleep(300);
        transport.DiscardInput();

        Assert.Equal(0, transport.Read(new byte[8], TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void ClosingAndOpeningAgainGetsAFreshSocket()
    {
        // What a recovery does after the link has gone. A socket that reported
        // itself open on a connection that had ended would be reopened into
        // nothing, and every read after it would fail the same way.
        var car = Car();
        using var dongle = new FakeElmOverTcp(car);
        using var transport = WifiEcuTransport.At(dongle.Address);

        transport.Open();
        Assert.True(transport.IsOpen);

        transport.Close();
        Assert.False(transport.IsOpen);
    }
}
