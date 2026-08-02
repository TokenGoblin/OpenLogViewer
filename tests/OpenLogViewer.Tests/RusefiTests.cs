using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// A stand-in for a rusEFI board, written to the behaviour a real one showed on
/// a bench rather than to what its INI implies.
///
/// It differs from a MegaSquirt in four ways that all matter, and three of them
/// are silent: it answers 'S' with its signature and refuses 'Q'; it reads the
/// offset and count of a read little-endian; and it will not send more than its
/// blocking factor in one reply. That last one is modelled as the port dying,
/// because that is what happens — asking a real board for 1200 bytes when it
/// declares 1024 takes it off the USB bus until it is replugged.
/// </summary>
internal sealed class FakeRusefi(byte[] block, int blockingFactor = 1024) : IEcuTransport
{
    private byte[] _pending = [];

    public bool IsOpen { get; private set; }

    public bool OffTheBus { get; private set; }

    /// <summary>Every realtime read asked for, as (offset, count).</summary>
    public List<(int Offset, int Count)> Reads { get; } = [];

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (OffTheBus) throw new IOException("The device is not responding.");

        byte[] payload = Unframe(data);
        _pending = payload.Length == 0 ? [] : Answer(payload);
    }

    private byte[] Answer(byte[] payload)
    {
        switch ((char)payload[0])
        {
            case 'S':
                return Reply(Encoding.ASCII.GetBytes("rusEFI master.2024.11.17.uaefi.2834573262"));

            case 'V':
                return Reply(Encoding.ASCII.GetBytes("rusEFI v20241117@1431655765"));

            // MegaSquirt's query command. rusEFI has no such thing and says so.
            case 'Q':
                return Refusal(0x83);

            case 'O':
                return Realtime(payload);

            default:
                return Refusal(0x83);
        }
    }

    private byte[] Realtime(byte[] payload)
    {
        // Little-endian, which is the whole point of this fake.
        int offset = payload[1] | (payload[2] << 8);
        int count = payload[3] | (payload[4] << 8);

        Reads.Add((offset, count));

        if (count > blockingFactor)
        {
            // Not an error reply. The board goes away.
            OffTheBus = true;
            throw new IOException("The device is not responding.");
        }

        if (offset < 0 || count < 1 || offset + count > block.Length) return Refusal(0x84);

        return Reply(block.AsSpan(offset, count).ToArray());
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (OffTheBus) throw new IOException("The device is not responding.");

        int take = Math.Min(buffer.Length, _pending.Length);
        _pending.AsSpan(0, take).CopyTo(buffer);
        _pending = _pending[take..];

        return take;
    }

    public void DiscardInput()
    {
    }

    public void Dispose() => Close();

    private static byte[] Unframe(ReadOnlySpan<byte> framed) =>
        framed.Length < 7 ? [] : framed.Slice(2, (framed[0] << 8) | framed[1]).ToArray();

    private static byte[] Reply(byte[] data) => Frame([0x00, .. data]);

    private static byte[] Refusal(byte status) => Frame([status]);

    private static byte[] Frame(byte[] body)
    {
        uint crc = MsProtocol.Crc32(body);

        return
        [
            (byte)(body.Length >> 8), (byte)body.Length,
            .. body,
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        ];
    }
}

public class RusefiTests
{
    /// <summary>The protocol-relevant lines of a rusEFI INI, in their real sections.</summary>
    private const string Ini = """
        [TunerStudio]
        queryCommand = "S"
        versionInfo  = "V"
        signature    = "rusEFI master.2024.11.17.uaefi.2834573262"

        [Constants]
        endianness     = little
        blockingFactor = 1024

        [OutputChannels]
        ochGetCommand = "O%2o%2c"
        ochBlockSize  = 32

        RPMValue  = scalar, U16, 4,  "RPM",   1,    0
        coolant   = scalar, S16, 12, "deg C", 0.01, 0
        VBatt     = scalar, U16, 16, "V",     0.001, 0
        MAPValue  = scalar, F32, 20, "kPa",   1,    0
        needBurn  = bits,   U32, 0,  [6:6]

        [Datalog]
        entry = RPMValue, "RPM",  int,   "%d"
        entry = coolant,  "CLT",  float, "%.2f"
        entry = MAPValue, "MAP",  float, "%.2f"
        """;

    private static RealtimeLayout Layout() => MsqIni.ReadOutputChannels(Ini);

    /// <summary>A block written the way the board writes it: little-endian throughout.</summary>
    private static byte[] Block()
    {
        var block = new byte[32];

        BitConverter.GetBytes((uint)0b0100_0000).CopyTo(block, 0);   // needBurn, bit 6
        BitConverter.GetBytes((ushort)3000).CopyTo(block, 4);        // RPM
        BitConverter.GetBytes((short)2150).CopyTo(block, 12);        // coolant 21.50 °C
        BitConverter.GetBytes((ushort)13800).CopyTo(block, 16);      // 13.8 V
        BitConverter.GetBytes(101.3f).CopyTo(block, 20);             // MAP

        Assert.True(BitConverter.IsLittleEndian, "The fixture is written on the assumption.");
        return block;
    }

    // ----- what the INI says ------------------------------------------------

    [Fact]
    public void TheByteOrderIsReadFromTheConstantsSection()
    {
        // It governs the channels but is declared nowhere near them, which is
        // exactly why it is easy to miss.
        Assert.True(Layout().LittleEndian);
    }

    [Fact]
    public void AMegasquirtIniIsStillBigEndian()
    {
        const string ms = """
            [Constants]
            endianness = big

            [OutputChannels]
            ochBlockSize = 4
            """;

        Assert.False(MsqIni.ReadOutputChannels(ms).LittleEndian);
    }

    [Fact]
    public void AnIniThatSaysNothingAboutByteOrderIsBigEndian() =>
        Assert.False(MsqIni.ReadOutputChannels("[OutputChannels]\nochBlockSize = 4\n").LittleEndian);

    [Fact]
    public void TheBlockingFactorIsRead() => Assert.Equal(1024, Layout().BlockingFactor);

    [Fact]
    public void FloatChannelsAreUnderstood()
    {
        RealtimeField map = Assert.Single(Layout().Fields, f => f.Name == "MAPValue");

        Assert.Equal(RealtimeType.F32, map.Type);
        Assert.Equal(4, map.Size);
        Assert.Empty(Layout().Skipped);
    }

    // ----- the request ------------------------------------------------------

    [Fact]
    public void TheRusefiReadCommandIsBuiltFromItsTemplate()
    {
        byte[] request = RealtimeCommand.Parse("O%2o%2c").Build(0, 1824, canId: 0, littleEndian: true);

        // 1824 is 0x0720, and little-endian on the wire it is 20 07. Sent the
        // other way round the board reads it as 8199 and refuses the read.
        Assert.Equal<byte[]>([(byte)'O', 0x00, 0x00, 0x20, 0x07], request);
    }

    [Fact]
    public void TheMegasquirtTemplateStillBuildsTheRequestItAlwaysDid()
    {
        byte[] fromTemplate = RealtimeCommand.Parse("r\\$tsCanId\\x07%2o%2c").Build(0, 512);

        Assert.Equal(MsProtocol.RealtimeRequest(512), fromTemplate);
    }

    [Fact]
    public void ACanIdGoesWhereTheTemplateSaysItDoes()
    {
        byte[] request = RealtimeCommand.Parse("r\\$tsCanId\\x07%2o%2c").Build(0, 512, canId: 3);

        Assert.Equal(3, request[1]);
    }

    [Fact]
    public void APlainCommandTakesNoRange()
    {
        RealtimeCommand simple = RealtimeCommand.Parse("A");

        Assert.False(simple.TakesRange);
        Assert.Equal<byte[]>([(byte)'A'], simple.Build(0, 512));
    }

    [Fact]
    public void AnUnknownVariableContributesNoByte()
    {
        // A guessed byte would ask for something else entirely.
        Assert.Equal<byte[]>([(byte)'O'], RealtimeCommand.Parse("O\\$whatIsThis").Build(0, 1));
    }

    // ----- reading it -------------------------------------------------------

    [Fact]
    public void ARusefiBlockDecodesLittleEndian()
    {
        var decoder = new RealtimeDecoder(Layout());
        double[] values = decoder.Decode(Block());

        Assert.Equal(3000, At(decoder, values, "RPMValue"), 4);
        Assert.Equal(21.5, At(decoder, values, "coolant"), 4);
        Assert.Equal(13.8, At(decoder, values, "VBatt"), 4);
        Assert.Equal(101.3, At(decoder, values, "MAPValue"), 3);
        Assert.Equal(1, At(decoder, values, "needBurn"), 4);
    }

    [Fact]
    public void TheSameBytesReadBigEndianAreNotObviouslyWrong()
    {
        // The reason the byte order has to come from the INI rather than be
        // sniffed: read the wrong way round, this block yields 47115 RPM and
        // 15.2 °C, both of which a display renders without complaint.
        var decoder = new RealtimeDecoder(Layout() with { LittleEndian = false });
        double[] values = decoder.Decode(Block());

        Assert.NotEqual(3000, At(decoder, values, "RPMValue"));
        Assert.False(double.IsNaN(At(decoder, values, "RPMValue")));
    }

    // ----- talking to one ---------------------------------------------------

    [Fact]
    public void TheSignatureIsFoundEvenThoughTheQueryCommandIsRefused()
    {
        using var connection = new EcuConnection(new FakeRusefi(Block()), Quick);

        IReadOnlyList<string> identity = connection.ReadIdentity();

        Assert.Contains("rusEFI master.2024.11.17.uaefi.2834573262", identity);
        Assert.Contains("rusEFI v20241117@1431655765", identity);
    }

    [Fact]
    public void TheCatalogueDecidesWhichReplyWasTheSignature()
    {
        // "rusEFI v2024…" comes back too and is not a signature; the INI that
        // recognises one of them is what settles it.
        IniFile[] catalogue = [new("rusefi.ini", "rusEFI master.2024.11.17.uaefi.2834573262")];
        string[] identity = ["rusEFI v20241117@1431655765", "rusEFI master.2024.11.17.uaefi.2834573262"];

        (IniFile ini, string signature) = IniCatalog.MatchAny(identity, catalogue)!.Value;

        Assert.Equal("rusefi.ini", ini.Path);
        Assert.Equal("rusEFI master.2024.11.17.uaefi.2834573262", signature);
    }

    [Fact]
    public void AMegasquirtSignatureIsStillFoundAmongTheSameCandidates()
    {
        IniFile[] catalogue = [new("ms3.ini", "MS3 Format 0592.13P")];
        string[] identity = ["MShift 1.2.3 build 4", "MS3 Format 0592.13P"];

        Assert.Equal("MS3 Format 0592.13P", IniCatalog.MatchAny(identity, catalogue)!.Value.Signature);
    }

    [Fact]
    public void NoMatchIsNoMatchRatherThanTheFirstReply() =>
        Assert.Null(IniCatalog.MatchAny(["something else entirely"], [new("a.ini", "MS3 Format 0592.13P")]));

    [Fact]
    public void ARefusedCommandIsNotRetried()
    {
        // Probing a rusEFI with 'Q' is a refusal by design. Retrying it three
        // times spends three timeouts on an answer that will not change.
        var transport = new CountingRusefi(Block());
        using var connection = new EcuConnection(transport, Quick);

        connection.ReadIdentity();

        Assert.Equal(1, transport.Queries);
    }

    // ----- the blocking factor ----------------------------------------------

    [Fact]
    public void ABlockLargerThanOneReplyIsFetchedInPieces()
    {
        var block = new byte[2500];
        for (int i = 0; i < block.Length; i++) block[i] = (byte)i;

        var transport = new FakeRusefi(block);
        using var connection = new EcuConnection(transport, Quick);
        connection.Use(Layout() with { BlockSize = 2500 });

        byte[] read = connection.ReadRealtime(2500);

        Assert.Equal(block, read);
        Assert.Equal([(0, 1024), (1024, 1024), (2048, 452)], transport.Reads);
    }

    [Fact]
    public void NothingEverAsksForMoreThanTheBlockingFactor()
    {
        // The one that has teeth. A board asked for more does not refuse — it
        // leaves the USB bus, and the only fix is to unplug it.
        var transport = new FakeRusefi(new byte[2500]);
        using var connection = new EcuConnection(transport, Quick);
        connection.Use(Layout() with { BlockSize = 2500 });

        connection.ReadRealtime(2500);

        Assert.False(transport.OffTheBus);
        Assert.All(transport.Reads, read => Assert.True(read.Count <= 1024));
    }

    [Fact]
    public void ABlockThatFitsInOneReplyIsNotSplit()
    {
        var transport = new FakeRusefi(Block());
        using var connection = new EcuConnection(transport, Quick);
        connection.Use(Layout());

        connection.ReadRealtime(32);

        Assert.Equal([(0, 32)], transport.Reads);
    }

    [Fact]
    public void AFirmwareThatDeclaresNoBlockingFactorIsReadWhole()
    {
        var transport = new FakeRusefi(new byte[2500], blockingFactor: 4096);
        using var connection = new EcuConnection(transport, Quick);
        connection.Use(Layout() with { BlockSize = 2500, BlockingFactor = 0 });

        connection.ReadRealtime(2500);

        Assert.Equal([(0, 2500)], transport.Reads);
    }

    /// <summary>End to end: an INI, a board, and the numbers a gauge would show.</summary>
    [Fact]
    public void AReadingComesBackOffTheWireAsTheRightNumber()
    {
        RealtimeLayout layout = Layout();

        using var connection = new EcuConnection(new FakeRusefi(Block()), Quick);
        connection.Use(layout);

        var decoder = new RealtimeDecoder(layout);
        double[] values = decoder.Decode(connection.ReadRealtime(layout.BlockSize));

        Assert.Equal(3000, At(decoder, values, "RPMValue"), 4);
        Assert.Equal(101.3, At(decoder, values, "MAPValue"), 3);
    }

    [Fact]
    public void ALiveSessionOverRusefiTakesItsChannelsFromTheDatalog()
    {
        RealtimeLayout layout = Layout();

        var connection = new EcuConnection(new FakeRusefi(Block()), Quick);
        connection.Use(layout);

        using var session = new LiveSession(
            connection, new RealtimeDecoder(layout), MsqIni.ReadDatalog(Ini),
            new LiveSessionSettings { RecordingPath = null });

        Assert.Equal(["RPM", "CLT", "MAP"], session.Names);
    }

    private static EcuConnectionSettings Quick { get; } = new()
    {
        Retries = 2,
        Timeout = TimeSpan.FromMilliseconds(20),
        RetryPause = TimeSpan.Zero,
    };

    private static double At(RealtimeDecoder decoder, double[] values, string name)
    {
        for (int i = decoder.Names.Count - 1; i >= 0; i--)
            if (decoder.Names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return values[i];

        return double.NaN;
    }
}

/// <summary>A board that counts how often it is asked the MegaSquirt query command.</summary>
internal sealed class CountingRusefi(byte[] block) : IEcuTransport
{
    private readonly FakeRusefi _inner = new(block);

    public int Queries { get; private set; }

    public bool IsOpen => _inner.IsOpen;

    public void Open() => _inner.Open();

    public void Close() => _inner.Close();

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[2] == (byte)'Q') Queries++;
        _inner.Write(data);
    }

    public int Read(Span<byte> buffer, TimeSpan timeout) => _inner.Read(buffer, timeout);

    public void DiscardInput() => _inner.DiscardInput();

    public void Dispose() => _inner.Dispose();
}
