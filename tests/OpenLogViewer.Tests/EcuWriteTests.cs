using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// A board whose page can be read and written, and which can be told to take a
/// write badly in each of the ways that matter.
/// </summary>
internal sealed class WritableEcu(byte[] page, int blockingFactor = 64) : IEcuTransport
{
    private byte[] _pending = [];

    public bool IsOpen { get; private set; }

    public byte[] Page { get; } = page;

    public int Burns { get; private set; }

    public List<(int Offset, int Count)> Writes { get; } = [];

    /// <summary>Writes land at this many bytes off, the way a byte-order slip would.</summary>
    public int Skew { get; set; }

    /// <summary>When set, writes are acknowledged and thrown away.</summary>
    public bool SilentlyDiscardWrites { get; set; }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Write(ReadOnlySpan<byte> data)
    {
        byte[] payload = data.Length < 7 ? [] : data.Slice(2, (data[0] << 8) | data[1]).ToArray();
        _pending = payload.Length == 0 ? [] : Answer(payload);
    }

    private byte[] Answer(byte[] payload)
    {
        switch ((char)payload[0])
        {
            // R <offset:2 LE> <count:2 LE>
            case 'R':
            {
                int offset = payload[1] | (payload[2] << 8);
                int count = payload[3] | (payload[4] << 8);

                if (count > blockingFactor) return Refusal(0x84);
                if (offset < 0 || count < 1 || offset + count > Page.Length) return Refusal(0x84);

                return Reply(Page.AsSpan(offset, count).ToArray());
            }

            // C <offset:2 LE> <count:2 LE> <data>
            case 'C':
            {
                int offset = payload[1] | (payload[2] << 8);
                int count = payload[3] | (payload[4] << 8);

                if (payload.Length < 5 + count) return Refusal(0x80);
                if (offset + count > Page.Length) return Refusal(0x84);

                Writes.Add((offset, count));

                if (!SilentlyDiscardWrites)
                {
                    int at = offset + Skew;
                    if (at >= 0 && at + count <= Page.Length)
                        payload.AsSpan(5, count).CopyTo(Page.AsSpan(at));
                }

                return Reply([]);
            }

            case 'B':
                Burns++;
                return Reply([]);

            default:
                return Refusal(0x83);
        }
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int take = Math.Min(buffer.Length, _pending.Length);
        _pending.AsSpan(0, take).CopyTo(buffer);
        _pending = _pending[take..];

        return take;
    }

    public void DiscardInput()
    {
    }

    public void Dispose() => Close();

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

/// <summary>
/// A link that answers late, the way a Bluetooth one does.
///
/// The reply to a request that timed out arrives while the next is in flight.
/// Nothing in the protocol says which request a reply answers — there is no echo
/// of the offset or count — so a straggler is otherwise decoded as the answer to
/// whatever was asked next.
/// </summary>
internal sealed class LateTransport(byte[] first, byte[] second) : IEcuTransport
{
    private readonly Queue<byte[]> _late = new();
    private byte[] _pending = [];
    private int _writes;

    public bool IsOpen { get; private set; }

    public int Discards { get; private set; }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Write(ReadOnlySpan<byte> data)
    {
        _writes++;

        // The first request is answered too late to be read, so its reply is
        // still queued when the second goes out.
        if (_writes == 1) _late.Enqueue(first);
        else _pending = _late.Count > 0 ? _late.Dequeue() : second;
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int take = Math.Min(buffer.Length, _pending.Length);
        _pending.AsSpan(0, take).CopyTo(buffer);
        _pending = _pending[take..];

        return take;
    }

    public void DiscardInput() => Discards++;

    public void Dispose() => Close();
}

/// <summary>Never answers, and records whether a drain happened between writes.</summary>
internal sealed class CountingSettle : IEcuTransport
{
    private int _writes;
    private int _readsSinceWrite;

    public bool IsOpen { get; private set; }

    public bool DrainedBeforeSecondWrite { get; private set; }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Write(ReadOnlySpan<byte> data)
    {
        _writes++;

        // Reads after the first write and before the second are the header read
        // that timed out plus the drain that followed it.
        if (_writes == 2 && _readsSinceWrite > 1) DrainedBeforeSecondWrite = true;

        _readsSinceWrite = 0;
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        _readsSinceWrite++;
        return 0;
    }

    public void DiscardInput()
    {
    }

    public void Dispose() => Close();
}

/// <summary>
/// A big-endian board that remembers every request it was asked, so a test can
/// check the bytes rather than only the effect. Speaks the MegaSquirt commands
/// the real firmware declares: 'w' to write, 'r' to read, 'b' to burn.
/// </summary>
internal sealed class RecordingEcu(byte[] page, bool bigEndian) : IEcuTransport
{
    private byte[] _pending = [];

    public bool IsOpen { get; private set; }

    public byte[] Page { get; } = page;

    /// <summary>Each request's payload, without the length and checksum around it.</summary>
    public List<byte[]> Requests { get; } = [];

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Dispose() => Close();

    public void DiscardInput() { }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) { _pending = []; return; }

        byte[] payload = data.Slice(2, (data[0] << 8) | data[1]).ToArray();
        Requests.Add(payload);
        _pending = Answer(payload);
    }

    private int Number(byte[] p, int at) => bigEndian ? (p[at] << 8) | p[at + 1] : p[at] | (p[at + 1] << 8);

    private byte[] Answer(byte[] p)
    {
        // Every command here carries the CAN id and the page byte before its
        // arguments, exactly as the firmware's templates write them.
        switch ((char)p[0])
        {
            case 'w':
            {
                int offset = Number(p, 3);
                int count = Number(p, 5);

                if (offset + count <= Page.Length && p.Length >= 7 + count)
                    p.AsSpan(7, count).CopyTo(Page.AsSpan(offset));

                return Framed([]);
            }

            case 'r':
            {
                int offset = Number(p, 3);
                int count = Number(p, 5);

                return offset + count <= Page.Length
                    ? Framed(Page.AsSpan(offset, count).ToArray())
                    : Framed([], status: 0x84);
            }

            case 'b': return Framed([]);

            default: return Framed([], status: 0x83);
        }
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int take = Math.Min(buffer.Length, _pending.Length);
        _pending.AsSpan(0, take).CopyTo(buffer);
        _pending = _pending[take..];

        return take;
    }

    private static byte[] Framed(byte[] data, byte status = 0x00)
    {
        byte[] body = [status, .. data];
        uint crc = MsProtocol.Crc32(body);

        return
        [
            (byte)(body.Length >> 8), (byte)body.Length,
            .. body,
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        ];
    }
}

public class EcuWriteTests
{
    private const string Ini = """
        [Constants]
        endianness      = little
        nPages          = 1
        pageSize        = 256
        pageIdentifier  = "\x00\x00"
        pageReadCommand = "R%2o%2c"
        pageChunkWrite  = "C%2o%2c%v"
        burnCommand     = "B"
        blockingFactor  = 64

        page = 1
        veTable    = array, U16, 0,  [4x2], "%", 0.1, 0, 0, 999, 1
        veRpmBins  = array, U16, 16, [4],   "RPM", 1, 0, 0, 18000, 0
        veLoadBins = array, U16, 24, [2],   "kPa", 1, 0, 0, 255, 0
        idleTrim   = scalar, S16, 40, "%", 0.1, 0, -100, 100, 1
        """;

    private static TuneLayout Layout() => TuneLayoutReader.Read(Ini);

    private static TunePage Page() => Layout().Pages[0];

    private static EcuConnectionSettings Quick { get; } = new()
    {
        Retries = 1,
        Timeout = TimeSpan.FromMilliseconds(20),
        RetryPause = TimeSpan.Zero,
    };

    // ----- encoding ---------------------------------------------------------

    [Fact]
    public void ValuesAreScaledBackIntoBytes()
    {
        EcuTune tune = EcuTune.FromPages(Layout(), new byte[256]);

        TuneWrite write = Assert.IsType<TuneWrite>(
            tune.EncodeArray("veTable", [10, 20, 30, 40, 50, 60, 70, 80]));

        Assert.Equal(0, write.Page);
        Assert.Equal(0, write.Offset);
        Assert.Equal(16, write.Data.Length);
        Assert.Equal(100, BitConverter.ToUInt16(write.Data, 0));   // 10 % at a scale of 0.1
        Assert.Equal(800, BitConverter.ToUInt16(write.Data, 14));
    }

    [Fact]
    public void ScalingRoundsRatherThanTruncates()
    {
        // 84.7 over a scale of 0.1 is 846.99999... in binary floating point.
        // Truncating drops a tenth off every cell of a table.
        EcuTune tune = EcuTune.FromPages(Layout(), new byte[256]);

        TuneWrite write = Assert.IsType<TuneWrite>(tune.EncodeArray("idleTrim", [84.7]));

        Assert.Equal(847, BitConverter.ToInt16(write.Data, 0));
    }

    [Fact]
    public void AValueTheTypeCannotHoldIsRefused()
    {
        // Wrapping it into whatever it becomes is the worst available response.
        EcuTune tune = EcuTune.FromPages(Layout(), new byte[256]);

        Assert.Null(tune.EncodeArray("veTable", [10, 20, 30, 40, 50, 60, 70, 99999]));
    }

    [Fact]
    public void TheWrongNumberOfValuesIsRefused()
    {
        EcuTune tune = EcuTune.FromPages(Layout(), new byte[256]);

        Assert.Null(tune.EncodeArray("veTable", [1, 2, 3]));
    }

    [Fact]
    public void ATableEncodesRowMajorTheWayThePageHoldsIt()
    {
        EcuTune tune = EcuTune.FromPages(Layout(), new byte[256]);

        var cells = new double[4, 2];
        for (int column = 0; column < 4; column++)
            for (int row = 0; row < 2; row++)
                cells[column, row] = (row * 4 + column + 1) * 10;

        TuneWrite write = Assert.IsType<TuneWrite>(tune.EncodeTable("veTable", cells));

        Assert.Equal(100, BitConverter.ToUInt16(write.Data, 0));    // row 0, column 0
        Assert.Equal(400, BitConverter.ToUInt16(write.Data, 6));    // row 0, column 3
        Assert.Equal(500, BitConverter.ToUInt16(write.Data, 8));    // row 1, column 0
    }

    // ----- the round trip ---------------------------------------------------

    [Fact]
    public void WhatIsWrittenCanBeReadBack()
    {
        var board = new WritableEcu(new byte[256]);
        using var connection = new EcuConnection(board, Quick);

        byte[] data = [.. Enumerable.Range(0, 16).Select(i => (byte)(i * 3))];
        connection.WriteTunePage(Page(), 64, littleEndian: true, offset: 0, data);

        Assert.Equal(data, board.Page.AsSpan(0, 16).ToArray());
    }

    [Fact]
    public void AWriteThatLandsInTheWrongPlaceIsCaught()
    {
        // The failure that matters. A board that acknowledges the command and
        // puts the bytes somewhere else leaves an engine running on numbers
        // nobody chose, and the acknowledgement alone cannot tell you.
        var board = new WritableEcu(new byte[256]) { Skew = 4 };
        using var connection = new EcuConnection(board, Quick);

        EcuProtocolException thrown = Assert.Throws<EcuProtocolException>(
            () => connection.WriteTunePage(Page(), 64, true, 0, [1, 2, 3, 4, 5, 6, 7, 8]));

        Assert.Contains("did not take that write", thrown.Message);
    }

    [Fact]
    public void AWriteThatIsAcknowledgedAndDiscardedIsCaught()
    {
        var board = new WritableEcu(new byte[256]) { SilentlyDiscardWrites = true };
        using var connection = new EcuConnection(board, Quick);

        Assert.Throws<EcuProtocolException>(
            () => connection.WriteTunePage(Page(), 64, true, 0, [9, 9, 9, 9]));
    }

    [Fact]
    public void TheFailureSaysNothingHasBeenBurned()
    {
        // Which is the thing to know: a power cycle undoes it.
        var board = new WritableEcu(new byte[256]) { SilentlyDiscardWrites = true };
        using var connection = new EcuConnection(board, Quick);

        EcuProtocolException thrown = Assert.Throws<EcuProtocolException>(
            () => connection.WriteTunePage(Page(), 64, true, 0, [9, 9, 9, 9]));

        Assert.Contains("burned", thrown.Message);
        Assert.Equal(0, board.Burns);
    }

    [Fact]
    public void AWriteIsSplitToFitTheBlockingFactor()
    {
        var board = new WritableEcu(new byte[256]);
        using var connection = new EcuConnection(board, Quick);

        connection.WriteTunePage(Page(), 64, true, 0, new byte[200]);

        Assert.All(board.Writes, w => Assert.True(w.Count <= 64 - 8));
        Assert.Equal(200, board.Writes.Sum(w => w.Count));
    }

    [Fact]
    public void AWriteRunningPastTheEndOfThePageIsRefusedBeforeItIsSent()
    {
        var board = new WritableEcu(new byte[256]);
        using var connection = new EcuConnection(board, Quick);

        Assert.Throws<EcuProtocolException>(
            () => connection.WriteTunePage(Page(), 64, true, 250, new byte[16]));

        Assert.Empty(board.Writes);
    }

    // ----- burning ----------------------------------------------------------

    [Fact]
    public void WritingDoesNotBurn()
    {
        // Everything up to a burn is undone by turning the key off, and that is
        // worth keeping true.
        var board = new WritableEcu(new byte[256]);
        using var connection = new EcuConnection(board, Quick);

        connection.WriteTunePage(Page(), 64, true, 0, [1, 2, 3, 4]);

        Assert.Equal(0, board.Burns);
    }

    [Fact]
    public void BurningIsItsOwnCall()
    {
        var board = new WritableEcu(new byte[256]);
        using var connection = new EcuConnection(board, Quick);

        connection.BurnPage(Page(), littleEndian: true);

        Assert.Equal(1, board.Burns);
    }

    [Fact]
    public void AFirmwareDeclaringNoWriteCommandCannotBeWrittenTo()
    {
        const string readOnly = """
            [Constants]
            nPages = 1
            pageSize = 64
            pageReadCommand = "R%2o%2c"
            """;

        TunePage page = TuneLayoutReader.Read(readOnly).Pages[0];

        var board = new WritableEcu(new byte[256]);
        using var connection = new EcuConnection(board, Quick);

        EcuProtocolException thrown = Assert.Throws<EcuProtocolException>(
            () => connection.WriteTunePage(page, 64, true, 0, [1, 2]));

        Assert.Contains("no write command", thrown.Message);
        Assert.Empty(board.Writes);
    }

    // ----- late replies -----------------------------------------------------

    [Fact]
    public void AReplyOfTheWrongLengthIsNotTakenAsTheAnswer()
    {
        // The failure this guards against was reported from a Bluetooth rig: a
        // straggler matched to the next request decoded at the wrong offsets and
        // showed a stationary car doing 230 km/h. Well-formed, checksummed, and
        // entirely wrong.
        var transport = new LateTransport(Reply(new byte[200]), Reply(new byte[16]));

        // Three attempts: the first times out, the second collects its late
        // reply and refuses it, the third is answered properly.
        using var connection = new EcuConnection(transport, new EcuConnectionSettings
        {
            Retries = 2,
            Timeout = TimeSpan.FromMilliseconds(20),
            SettleFor = TimeSpan.FromMilliseconds(20),
            QuietFor = TimeSpan.FromMilliseconds(1),
        });

        connection.Use(MsqIni.ReadOutputChannels("""
            [Constants]
            endianness = little

            [OutputChannels]
            ochGetCommand = "R%2o%2c"
            ochBlockSize  = 16
            """));

        // The 200-byte straggler is refused, and the 16 that follows is right.
        Assert.Equal(16, connection.ReadRealtime(16).Length);
        Assert.Equal(2, connection.Retries);
    }

    [Fact]
    public void TheLinkIsDrainedBeforeAnythingIsSentAgain()
    {
        // rusEFI's command handler has no queue: a request arriving while a reply
        // is still being written desynchronises it permanently, and only a power
        // cycle brings the ECU back. Never more than one request outstanding.
        var transport = new CountingSettle();

        using var connection = new EcuConnection(transport, new EcuConnectionSettings
        {
            Retries = 1,
            Timeout = TimeSpan.FromMilliseconds(5),
            SettleFor = TimeSpan.FromMilliseconds(60),
            QuietFor = TimeSpan.FromMilliseconds(5),
        });

        Assert.Throws<EcuProtocolException>(() => connection.ReadSignature());

        // Drained after the failure, before the retry went out.
        Assert.True(transport.DrainedBeforeSecondWrite, "the retry was sent without draining first");
    }

    private static byte[] Reply(byte[] data)
    {
        byte[] body = [0x00, .. data];
        uint crc = MsProtocol.Crc32(body);

        return
        [
            (byte)(body.Length >> 8), (byte)body.Length,
            .. body,
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        ];
    }

    // ----- the command ------------------------------------------------------

    [Fact]
    public void TheWriteCommandCarriesItsPayload()
    {
        byte[] request = RealtimeCommand.Parse("C%2o%2c%v")
            .Build(16, 3, littleEndian: true, payload: [0xAA, 0xBB, 0xCC]);

        Assert.Equal<byte[]>([(byte)'C', 16, 0, 3, 0, 0xAA, 0xBB, 0xCC], request);
    }

    [Fact]
    public void AMegasquirtWriteNamesItsPageToo()
    {
        byte[] identifier = RealtimeCommand.Parse(@"\$tsCanId\x06").Build(0, 1, canId: 1);

        byte[] request = RealtimeCommand.Parse("w%2i%2o%2c%v")
            .Build(2, 2, canId: 1, page: identifier, payload: [0x12, 0x34]);

        Assert.Equal<byte[]>([(byte)'w', 1, 0x06, 0, 2, 0, 2, 0x12, 0x34], request);
    }

    [Fact]
    public void TheWriteAndBurnCommandsAreReadFromTheIni()
    {
        TunePage page = Page();

        Assert.Equal("C%2o%2c%v", page.ChunkWriteCommand);
        Assert.Equal("B", page.BurnCommand);
    }

    /// <summary>End to end: change a cell, write it, read the tune back.</summary>
    [Fact]
    public void ATableChangeSurvivesAWriteAndAReread()
    {
        var board = new WritableEcu(new byte[256]);
        using var connection = new EcuConnection(board, Quick);

        TuneLayout layout = Layout();
        EcuTune before = EcuTune.Read(connection, layout);

        var cells = new double[4, 2];
        for (int column = 0; column < 4; column++)
            for (int row = 0; row < 2; row++)
                cells[column, row] = 50 + column + row * 10;

        TuneWrite write = Assert.IsType<TuneWrite>(before.EncodeTable("veTable", cells));
        connection.WriteTunePage(layout.Pages[write.Page], layout.BlockingFactor, layout.LittleEndian,
            write.Offset, write.Data);

        EcuTune after = EcuTune.Read(connection, layout);
        double[] read = Assert.IsType<double[]>(after.Array("veTable"));

        Assert.Equal([50, 51, 52, 53, 60, 61, 62, 63], read);
    }

    // ----- the same method TunerStudio uses -----------------------------------

    /// <summary>
    /// MS2Extra's own declarations, copied from the file the MicroSquirt matched.
    /// Big-endian, the CAN id and the page byte as literals in every command, and
    /// the two timings the firmware asks for.
    /// </summary>
    private const string MegaSquirt = """
        [Constants]
        endianness          = big
        nPages              = 1
        pageSize            = 1024
        pageIdentifier      = "\$tsCanId"
        pageReadCommand     = "r\$tsCanId%2o%2c"
        pageChunkWrite      = "w\$tsCanId%2o%2c%v"
        burnCommand         = "b\$tsCanId"
        blockingFactor      = 256
        interWriteDelay     = 1
        pageActivationDelay = 10

        page = 1
        veTable = array, U08, 0, [4x4], "%", 1, 0, 0, 255, 0
        """;

    [Fact]
    public void TheFirmwaresOwnTimingsAreRead()
    {
        // Declared by the file and observed by TunerStudio, so ignoring them is
        // not "the same method".
        TuneLayout layout = TuneLayoutReader.Read(MegaSquirt);

        Assert.Equal(1, layout.InterWriteDelay);
        Assert.Equal(10, layout.AfterBurnDelay);
    }

    [Fact]
    public void AWriteIsTheBytesTheFirmwareDeclares()
    {
        // The whole of the question "is this the same method": the request on
        // the wire has to be what the .ini says it is, byte for byte.
        //
        //   w  <canId>  0x04  <offset:2 big-endian>  <count:2 big-endian>  <data>
        TuneLayout layout = TuneLayoutReader.Read(MegaSquirt);
        var ecu = new RecordingEcu(new byte[1024], bigEndian: true);

        using var connection = new EcuConnection(ecu, Quick);
        connection.Open();

        connection.WriteTunePage(
            layout.Pages[0], layout.BlockingFactor, layout.LittleEndian, 0x0102, [0x2A, 0x2B],
            layout.InterWriteDelay);

        byte[] sent = ecu.Requests[0];

        Assert.Equal(
            [(byte)'w', 0x00, 0x04, 0x01, 0x02, 0x00, 0x02, 0x2A, 0x2B],
            sent);
    }

    [Fact]
    public void ABurnIsTheBytesTheFirmwareDeclares()
    {
        TuneLayout layout = TuneLayoutReader.Read(MegaSquirt);
        var ecu = new RecordingEcu(new byte[1024], bigEndian: true);

        using var connection = new EcuConnection(ecu, Quick);
        connection.Open();

        connection.BurnPage(layout.Pages[0], layout.LittleEndian, afterBurnDelay: 0);

        Assert.Equal([(byte)'b', 0x00, 0x04], ecu.Requests[^1]);
    }

    [Fact]
    public void AWriteLargerThanTheBlockingFactorIsSplitAndStillLands()
    {
        // Never more than the firmware allows in one message — a rusEFI asked
        // for more leaves the bus until it is replugged.
        TuneLayout layout = TuneLayoutReader.Read(MegaSquirt);
        var ecu = new RecordingEcu(new byte[1024], bigEndian: true);

        byte[] data = [.. Enumerable.Range(0, 600).Select(i => (byte)(i & 0xFF))];

        using var connection = new EcuConnection(ecu, Quick);
        connection.Open();
        connection.WriteTunePage(layout.Pages[0], layout.BlockingFactor, layout.LittleEndian, 0, data);

        Assert.True(ecu.Requests.Count > 1, "a 600-byte write went out in one message");
        Assert.All(ecu.Requests.Where(r => r[0] == (byte)'w'),
            r => Assert.True(r.Length <= layout.BlockingFactor, $"a message was {r.Length} bytes"));

        Assert.Equal(data, ecu.Page.AsSpan(0, data.Length).ToArray());
    }
}

// ----- what a real controller answers -------------------------------------

/// <summary>
/// The burn path, as a rusEFI actually behaves rather than as a fake was
/// written to behave.
///
/// Every test in this file passed while all three of these were broken, because
/// the fake answered a burn with 0x00 and never stalled. A real board answers
/// 0x04 and stops servicing USB while it writes flash.
/// </summary>
public class EcuBurnReplyTests
{
    /// <summary>A transport answering a burn the way the hardware does.</summary>
    private sealed class Board(byte burnStatus) : IEcuTransport
    {
        private byte[] _pending = [];

        public int Burns { get; private set; }

        /// <summary>Set to make the port refuse the bytes, as one does mid-erase.</summary>
        public bool RefuseWrites { get; set; }

        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (RefuseWrites)
                throw new IOException("The semaphore timeout period has expired. : 'COM8'.");

            byte[] payload = data.Length < 7 ? [] : data.Slice(2, (data[0] << 8) | data[1]).ToArray();

            if (payload.Length > 0 && payload[0] == (byte)'B')
            {
                Burns++;
                _pending = Framed(burnStatus);
                return;
            }

            _pending = Framed(0x00);
        }

        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            int take = Math.Min(buffer.Length, _pending.Length);
            _pending.AsSpan(0, take).CopyTo(buffer);
            _pending = _pending[take..];

            return take;
        }

        public void DiscardInput() => _pending = [];

        public void Dispose() => Close();

        private static byte[] Framed(byte status)
        {
            byte[] body = [status];
            uint crc = MsProtocol.Crc32(body);

            return
            [
                0, (byte)body.Length, .. body,
                (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
            ];
        }
    }

    private static readonly TunePage Page = new()
    {
        Index = 0,
        Size = 64,
        Identifier = "\"\x01\"",
        ReadCommand = "\"R%2o%2c\"",
        ChunkWriteCommand = "\"C%2o%2c%v\"",
        BurnCommand = "\"B\"",
    };

    [Theory]
    [InlineData(0x00)]  // a plain acknowledgement, which is what a page read gets
    [InlineData(0x04)]  // TS_RESPONSE_BURN_OK — measured off both a rusEFI and a Speeduino
    [InlineData(0x07)]  // and what a controller command is acknowledged with
    public void EveryWayOfSayingYesIsTakenForAYes(byte status)
    {
        // Insisting on 0x00 made a successful burn read as a refusal: the flash
        // was written, the board answered 0x04 to say so, and the application
        // reported that the burn had been declined.
        //
        // Not one firmware's quirk. Asked the same question directly, a
        // Speeduino 2025.01.7 answers 0x00 to a page read and 0x04 to a burn,
        // exactly as a rusEFI does — so every burn either of them was ever asked
        // for came back looking refused.
        var board = new Board(status);
        using var connection = new EcuConnection(board);
        connection.Open();

        connection.BurnPage(Page, littleEndian: true);

        Assert.Equal(1, board.Burns);
    }

    [Theory]
    [InlineData(0x80)]  // underrun
    [InlineData(0x82)]  // CRC failure
    [InlineData(0x83)]  // unrecognised command
    [InlineData(0x84)]  // out of range
    [InlineData(0x8D)]  // framing error
    public void AndEveryWayOfSayingNoStillMeansNo(byte status)
    {
        using var connection = new EcuConnection(new Board(status));
        connection.Open();

        EcuProtocolException e = Assert.Throws<EcuProtocolException>(
            () => connection.BurnPage(Page, littleEndian: true));

        Assert.Contains($"0x{status:X2}", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APortThatWillNotTakeTheBytesIsReportedRatherThanThrownRaw()
    {
        // The transport raises an IOException where the port misbehaves rather
        // than the ECU. Catching only EcuProtocolException let it escape the
        // retry loop and reach the application as an unhandled "The semaphore
        // timeout period has expired" — a crash, and a misleading one, since the
        // burn it was raised for had gone through.
        var board = new Board(0x04) { RefuseWrites = true };
        using var connection = new EcuConnection(board);
        board.RefuseWrites = false;
        connection.Open();
        board.RefuseWrites = true;

        Assert.Throws<EcuProtocolException>(() => connection.BurnPage(Page, littleEndian: true));
    }

    [Fact]
    public void ABurnThatCannotBeConfirmedSaysItMayStillHaveHappened()
    {
        // The one message worth getting right. A burn reported as failed sends
        // somebody to burn again, and a controller stops answering while it
        // writes flash — which looks identical to one that never heard. Silence
        // is the case this is about; a refusal is not, and is tested separately.
        var board = new SilentBoard();
        using var connection = new EcuConnection(board);
        connection.Open();

        EcuProtocolException e = Assert.Throws<EcuProtocolException>(
            () => connection.BurnPage(Page, littleEndian: true));

        Assert.False(e.Refused);
        Assert.Contains("may still have completed", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABurnIsNeverSentTwice()
    {
        // Every other request may be repeated freely, because a lost reply is
        // just a lost reply. A burn is the one where the ECU may well have done
        // the work and gone quiet doing it, and re-sending spends a flash erase
        // to learn nothing.
        var board = new SilentBoard();
        using var connection = new EcuConnection(board);
        connection.Open();

        Assert.Throws<EcuProtocolException>(() => connection.BurnPage(Page, littleEndian: true));
        Assert.Equal(1, board.Burns);
    }

    /// <summary>A board that takes a burn and never answers it.</summary>
    private sealed class SilentBoard : IEcuTransport
    {
        public int Burns { get; private set; }

        public bool IsOpen { get; private set; }

        public TimeSpan WriteTimeout { get; set; } = SerialEcuTransport.DefaultWriteTimeout;

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data)
        {
            byte[] payload = data.Length < 7 ? [] : data.Slice(2, (data[0] << 8) | data[1]).ToArray();
            if (payload.Length > 0 && payload[0] == (byte)'B') Burns++;
        }

        public int Read(Span<byte> buffer, TimeSpan timeout) => 0;

        public void DiscardInput()
        {
        }

        public void Dispose() => Close();
    }

    // ----- the allowance a burn gets ------------------------------------------

    /// <summary>A transport that answers only after the erase, and stalls writes meanwhile.</summary>
    private sealed class SlowBoard(TimeSpan erase) : IEcuTransport
    {
        private byte[] _pending = [];
        private DateTime _answersAt = DateTime.MaxValue;

        public bool IsOpen { get; private set; }

        public TimeSpan WriteTimeout { get; set; } = SerialEcuTransport.DefaultWriteTimeout;

        /// <summary>The longest a write was ever allowed while one was outstanding.</summary>
        public TimeSpan WidestSeen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (WriteTimeout > WidestSeen) WidestSeen = WriteTimeout;

            // A port that will not take the bytes for as long as the erase runs.
            if (WriteTimeout < erase) throw new IOException("The semaphore timeout period has expired.");

            byte[] body = [0x04];
            uint crc = MsProtocol.Crc32(body);
            _pending = [0, 1, 0x04, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
            _answersAt = DateTime.UtcNow + erase;
        }

        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            // Nothing comes back until the erase is done, and only if the caller
            // is prepared to wait that long.
            if (DateTime.UtcNow + timeout < _answersAt) return 0;

            while (DateTime.UtcNow < _answersAt) Thread.Sleep(5);

            int take = Math.Min(buffer.Length, _pending.Length);
            _pending.AsSpan(0, take).CopyTo(buffer);
            _pending = _pending[take..];

            return take;
        }

        public void DiscardInput() => _pending = [];

        public void Dispose() => Close();
    }

    private static readonly TunePage SlowPage = new()
    {
        Index = 0,
        Size = 64,
        Identifier = "\"\x01\"",
        ReadCommand = "\"R%2o%2c\"",
        ChunkWriteCommand = "\"C%2o%2c%v\"",
        BurnCommand = "\"B\"",
    };

    [Fact]
    public void ABurnIsGivenLongerThanEverythingElseIs()
    {
        // Sending a burn once rather than retrying took away the time the retry
        // loop used to buy. A controller that erases before it answers would
        // then miss the ordinary 500 ms window every single time, and a burn
        // that worked would be reported as unconfirmed on every attempt.
        var board = new SlowBoard(TimeSpan.FromMilliseconds(900));
        using var connection = new EcuConnection(board);
        connection.Open();

        connection.BurnPage(SlowPage, littleEndian: true);
    }

    [Fact]
    public void AndTheLongAllowanceIsPutBackAfterwards()
    {
        // The port is shared with everything else on the link, and everything
        // else should fail fast: Windows' incoming Bluetooth port never accepts
        // a write at all, and waiting five seconds on each one hangs the window
        // through the whole identify sequence.
        var board = new SlowBoard(TimeSpan.FromMilliseconds(50));
        using var connection = new EcuConnection(board);
        connection.Open();

        TimeSpan before = board.WriteTimeout;
        connection.BurnPage(SlowPage, littleEndian: true);

        Assert.Equal(before, board.WriteTimeout);
        Assert.True(board.WidestSeen >= TimeSpan.FromSeconds(1),
                    $"the burn should have widened it, but the widest seen was {board.WidestSeen}");
    }

    [Fact]
    public void ARefusalIsLeftToSpeakForItself()
    {
        // "It may still have completed — turn the ignition off and on" is the
        // right thing to say about silence and the wrong thing to say about a
        // controller that answered. 0x83 means it did not recognise the burn
        // command, so nothing was burned and nobody should be sent to the car.
        var board = new Board(0x83);
        using var connection = new EcuConnection(board);
        connection.Open();

        EcuProtocolException e = Assert.Throws<EcuProtocolException>(
            () => connection.BurnPage(SlowPage, littleEndian: true));

        Assert.True(e.Refused);
        Assert.DoesNotContain("may still have completed", e.Message, StringComparison.Ordinal);
    }
}
