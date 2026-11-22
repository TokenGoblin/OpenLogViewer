using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Raw CAN capture over slcan — a MeatPi WiCAN with its protocol set to slcan.
/// The frames are the Lawicel text format; the adapter is a loopback socket
/// standing in for the dongle, so the transport is the real one.
/// </summary>
public class CanCaptureTests
{
    // ----- parsing ----------------------------------------------------------

    [Fact]
    public void AStandardFrameIsRead()
    {
        Assert.True(Slcan.TryParse("t2C48 1AF8000000000012".Replace(" ", ""), 1.5, out CanFrame f));

        Assert.Equal(0x2C4u, f.Id);
        Assert.False(f.Extended);
        Assert.False(f.Remote);
        Assert.Equal([0x1A, 0xF8, 0, 0, 0, 0, 0, 0x12], f.Data);
        Assert.Equal(1.5, f.Seconds);
        Assert.Equal("0x2C4", f.IdText);
    }

    [Fact]
    public void AnExtendedFrameIsRead()
    {
        Assert.True(Slcan.TryParse("T18DAF1102AABB", 0, out CanFrame f));

        Assert.Equal(0x18DAF110u, f.Id);
        Assert.True(f.Extended);
        Assert.Equal([0xAA, 0xBB], f.Data);
    }

    [Fact]
    public void ATimestampAfterTheDataIsTolerated()
    {
        Assert.True(Slcan.TryParse("t1232AABB1F40", 0, out CanFrame f));
        Assert.Equal([0xAA, 0xBB], f.Data);
    }

    [Fact]
    public void ARemoteFrameCarriesNoData()
    {
        Assert.True(Slcan.TryParse("r7DF8", 0, out CanFrame f));
        Assert.True(f.Remote);
        Assert.Empty(f.Data);
    }

    [Theory]
    [InlineData("")]                    // the bare reply to a command
    [InlineData("V1013")]               // a version string
    [InlineData("t2C48 1AF8")]          // torn: says 8 bytes, carries 2
    [InlineData("t2C481AF80000000000")] // one digit too many
    [InlineData("t8001AA")]             // a standard id past 0x7FF
    [InlineData("t1239")]               // length past 8
    [InlineData("t12G1AA")]             // not hex
    public void AnythingElseIsNotAFrame(string line)
    {
        Assert.False(Slcan.TryParse(line, 0, out _));
    }

    [Fact]
    public void ListeningIsCloseBitrateListenOnly()
    {
        // Listen-only first: the adapter never drives the car's bus, so a wrong
        // bitrate costs silence rather than error frames.
        Assert.Equal(["C", "S6", "L"], Slcan.ListenCommands(500));
        Assert.Throws<ArgumentOutOfRangeException>(() => Slcan.ListenCommands(333));
    }

    // ----- a capture against a stand-in dongle ------------------------------

    [Fact]
    public void ACaptureRecordsEveryFrameAndWhichBytesMoved()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string dir = Directory.CreateTempSubdirectory("olv-can").FullName;
        string path = Path.Combine(dir, "can.csv");

        var received = new StringBuilder();

        var dongle = Task.Run(() =>
        {
            using TcpClient client = listener.AcceptTcpClient();
            using NetworkStream stream = client.GetStream();

            // Wait for the open, as a real one would.
            var buffer = new byte[256];
            while (!received.ToString().Contains("L\r"))
                received.Append(Encoding.ASCII.GetString(buffer, 0, stream.Read(buffer)));

            // Revs rising in bytes 0-1 of 0x2C4; a constant id beside it; a reply
            // and a bell mixed in, as an adapter sends them.
            var text = new StringBuilder("\r\a");
            for (int rpm = 800; rpm < 3000; rpm += 100)
            {
                text.Append($"t2C48{rpm >> 8:X2}{rpm & 0xFF:X2}000000000000\r");
                text.Append("t3B44DEADBEEF\r");
            }

            byte[] bytes = Encoding.ASCII.GetBytes(text.ToString());

            // In two pieces split mid-frame, which is how a socket delivers them.
            stream.Write(bytes, 0, 37);
            Thread.Sleep(50);
            stream.Write(bytes, 37, bytes.Length - 37);
            Thread.Sleep(500);
        });

        using var capture = new CanCapture(new WifiEcuTransport("127.0.0.1", port), path);
        capture.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (capture.FrameCount < 44 && DateTime.UtcNow < deadline) Thread.Sleep(20);

        capture.Stop();
        dongle.Wait(TimeSpan.FromSeconds(5));

        Assert.StartsWith("C\rS6\rL\r", received.ToString());
        Assert.Equal(44, capture.FrameCount);
        Assert.False(capture.FellBackToNormalOpen);

        CanIdSummary revs = capture.Ids.Single(i => i.Id == 0x2C4);
        Assert.Equal(22, revs.Count);
        Assert.True(revs.Changes[0]);
        Assert.True(revs.Changes[1]);
        Assert.False(revs.Changes[2]);

        CanIdSummary still = capture.Ids.Single(i => i.Id == 0x3B4);
        Assert.DoesNotContain(true, still.Changes);

        string[] lines = File.ReadAllLines(path);
        Assert.Equal("Time,Id,Extended,Length,Data", lines[0]);
        Assert.Equal(45, lines.Length);
        Assert.EndsWith(",0x2C4,0,8,0320000000000000", lines[1]);

        Assert.Contains("0x2C4", capture.Summary());
    }

    // ----- the byte log -----------------------------------------------------

    [Fact]
    public void TheByteLogHasAChannelForEachMovingByteAndPairAndOpensInTheViewer()
    {
        var frames = new List<CanFrame>();
        for (int i = 0; i < 40; i++)
        {
            int rpm = 800 + i * 50;
            frames.Add(new CanFrame(i * 0.05, 0x2C4, false, false, [(byte)(rpm >> 8), (byte)rpm, 0, 0]));
            frames.Add(new CanFrame(i * 0.05 + 0.01, 0x3B4, false, false, [1, 2]));
        }

        string path = Path.Combine(Directory.CreateTempSubdirectory("olv-can").FullName, "bytes.csv");
        CanByteLog.Write(frames, path);

        string header = File.ReadLines(path).First();
        Assert.Equal("Time,2C4 b0,2C4 b1,2C4 w0,2C4 w1", header);

        LogDocument doc = new DelimitedLogReader().Read(path);
        LogChannel word = doc.Channels.Single(c => c.Name == "2C4 w0");

        // Big-endian, high byte first, as Toyota sends its numbers.
        Assert.Equal(800, word.At(0));
        Assert.Equal(800 + 39 * 50, word.At(word.Length - 1));
    }

    [Fact]
    public void ACaptureWhereNothingMovedSaysSo()
    {
        CanFrame[] frames = [new(0, 0x100, false, false, [1]), new(1, 0x100, false, false, [1])];
        string path = Path.Combine(Directory.CreateTempSubdirectory("olv-can").FullName, "bytes.csv");

        Assert.Throws<InvalidOperationException>(() => CanByteLog.Write(frames, path));
    }

    // ----- the WiCAN in the Wi-Fi list --------------------------------------

    [Fact]
    public void TheWiCanIsOfferedAfterTheVgate()
    {
        Assert.Equal("192.168.0.10:35000", WifiEcuTransport.KnownAddresses[0]);
        Assert.Contains("192.168.80.1:3333", WifiEcuTransport.KnownAddresses);
        Assert.Contains("WiCAN", WifiEcuTransport.Describe(WifiEcuTransport.WiCanAddress));
        Assert.True(Obd2Adapters.LooksLikeOne("WiCAN_A1B2C3"));
    }
}

/// <summary>
/// An adapter whose ATSP0 searches nothing — the original MeatPi WiCAN, whose
/// firmware implements CAN protocols 6 to 9 and no search.
/// </summary>
public class NoSearchAdapterTests
{
    [Fact]
    public void AnAdapterThatCannotSearchIsPutOnCanAndReadsTheCar()
    {
        var car = new FakeElm { CannotSearch = true };
        car.Answers[0x00] = [0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000];
        car.Answers[0x0C] = [0x1A, 0xF8];

        using Elm327Source source = Elm327Source.Connect(car);

        Assert.Contains("ATSP6", car.Received);
        Assert.NotEmpty(source.Supports);
    }

    [Fact]
    public void AnAdapterThatCanSearchIsLeftToSearch()
    {
        var car = new FakeElm();
        car.Answers[0x00] = [0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000];
        car.Answers[0x0C] = [0x1A, 0xF8];

        using Elm327Source source = Elm327Source.Connect(car);

        Assert.DoesNotContain("ATSP6", car.Received);
    }
}
