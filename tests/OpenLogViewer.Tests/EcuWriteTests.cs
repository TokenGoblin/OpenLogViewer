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
}
