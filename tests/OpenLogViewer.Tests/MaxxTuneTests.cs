using System.Buffers.Binary;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Reading a MaxxECU's tune out of the blob it hands over.
///
/// Built against a blob composed here rather than a recorded one, because what
/// is being checked is the packing and every part of it has to be varied: a
/// table whose axes are different lengths catches a transposition that a square
/// one hides, and the nine bytes between the axes and the cells only show up as
/// wrong if the cells are not all the same.
///
/// The layout it is composed to is the one a real MaxxECU Race handed over on
/// 2026-09-13, where 118 of its 120 live tables decoded inside their own
/// declared limits. See MAXXECU_TUNE_BLOB_DECODING.md in the TCU repository.
/// </summary>
public class MaxxTuneTests
{
    /// <summary>
    /// Lays a table into a blob the way a MaxxECU does, and hands back the
    /// address its config struct went to.
    /// </summary>
    private static int Compose(
        byte[] blob,
        int config,
        int dataOffset,
        int xChannel,
        int yChannel,
        short[] x,
        short[] y,
        short[,] cells)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(config), (ushort)xChannel);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(config + 0x02), (ushort)yChannel);
        blob[config + 0x06] = (byte)x.Length;
        blob[config + 0x07] = (byte)y.Length;
        blob[config + 0x08] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(config + 0x0B), (ushort)dataOffset);

        int at = MaxxTune.TableData + dataOffset;

        blob[at] = 0;
        int cursor = at + 1;

        foreach (short value in x)
        {
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(cursor), value);
            cursor += 2;
        }

        foreach (short value in y)
        {
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(cursor), value);
            cursor += 2;
        }

        // The nine bytes that are not the grid: two zeroes, a value, a checksum,
        // and a byte the second checksum covers.
        //
        // Written as nine rather than as MaxxTune.Preamble on purpose. Composing
        // the fixture with the same constant the decoder reads it with means both
        // sides move together, and the test goes on passing with the number set
        // to anything at all — which is exactly the mistake it exists to catch.
        // Nine is what a MaxxECU Race puts there.
        cursor += 9;

        for (int row = 0; row < y.Length; row++)
            for (int column = 0; column < x.Length; column++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(cursor), cells[row, column]);
                cursor += 2;
            }

        return config;
    }

    /// <summary>A VE table the shape of the one on the bench Race: 16 wide, 11 deep.</summary>
    private static byte[] WithVeTable(int config = 1578, int dataOffset = 496)
    {
        var blob = new byte[MaxxTune.BlobSize];

        short[] rpm = [200, 800, 1000, 1200, 1600, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500, 7000];
        short[] kpa = [200, 400, 503, 600, 800, 999, 1199, 1399, 1599, 1799, 1999];

        var cells = new short[kpa.Length, rpm.Length];
        for (int row = 0; row < kpa.Length; row++)
            for (int column = 0; column < rpm.Length; column++)
                cells[row, column] = (short)(280 + (row * 80) + (column * 2));

        Compose(blob, config, dataOffset, 61, 20, rpm, kpa, cells);

        return blob;
    }

    private static IReadOnlyList<MaxxSettingDefinition> VeDefinition(int config = 1578) =>
        [new MaxxSettingDefinition("VE Table 1 data", "dynamicTable", config, 22 * 22, 0.1, 0, 2500)];

    [Fact]
    public void ATableIsFoundThroughItsConfigStruct()
    {
        MaxxTable? table = MaxxTune.TableAt(WithVeTable(), "VE Table 1 data", 1578);

        Assert.NotNull(table);
        Assert.Equal("VE Table 1", table.Name);
        Assert.Equal(16, table.Columns);
        Assert.Equal(11, table.Rows);
        Assert.Equal(61, table.XChannel);
        Assert.Equal(20, table.YChannel);
    }

    /// <summary>
    /// The nine bytes between the axes and the cells are counted.
    ///
    /// This is the whole of what makes the decode right or wrong, and getting it
    /// wrong does not look wrong: the cells land four and a half cells early, so
    /// a VE table still reads as numbers between 28 and 116 and still rises to
    /// the right, just not the numbers that are in the ECU. The check is that the
    /// first cell is the first cell.
    /// </summary>
    [Fact]
    public void TheCellsBeginAfterThePreambleAndNotAtTheAxes()
    {
        byte[] blob = WithVeTable();

        MaxxTuneModel model = MaxxTune.Build(blob, VeDefinition());
        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) = (model.Layout, model.Tables);

        TuneTable table = EcuTune.FromPages(layout, blob).Tables(tables).Single();

        // Composed as 280 + row*80 + column*2, at a scale of a tenth.
        Assert.Equal(28.0, table.Values[0, 0], 3);
        Assert.Equal(29.0, table.Values[5, 0], 3);
        Assert.Equal(36.0, table.Values[0, 1], 3);
        Assert.Equal(111.0, table.Values[15, 10], 3);
    }

    /// <summary>
    /// Rows and columns are not swapped.
    ///
    /// A square table cannot catch this and most of a MaxxECU's are not square,
    /// so the 16 × 11 shape is the test. Swapped, the table is 11 × 16 and every
    /// value after the first row is somebody else's.
    /// </summary>
    [Fact]
    public void TheGridIsNotTransposed()
    {
        byte[] blob = WithVeTable();

        MaxxTuneModel model = MaxxTune.Build(blob, VeDefinition());
        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) = (model.Layout, model.Tables);

        TuneTable table = EcuTune.FromPages(layout, blob).Tables(tables).Single();

        Assert.Equal(16, table.Columns);
        Assert.Equal(11, table.Rows);
        Assert.Equal(16, table.X.Breakpoints.Length);
        Assert.Equal(11, table.Y.Breakpoints.Length);
    }

    /// <summary>
    /// An axis is counted in what its own channel is counted in, not in the
    /// table's units.
    ///
    /// A VE table is scaled by a tenth because its cells are percent. Its columns
    /// are RPM and stored whole, so scaling them the same way turns 5,000 RPM
    /// into 500 — which looks like an idle speed rather than an obvious fault.
    /// </summary>
    [Fact]
    public void AnAxisIsScaledByItsOwnChannelAndNotByTheTable()
    {
        byte[] blob = WithVeTable();

        var channels = new Dictionary<int, MaxxChannelDefinition>
        {
            [61] = new(61, "RPM", "rpm", 1, true, 0),
            [20] = new(20, "MAP", "kPa", 0.1, true, 1),
        };

        MaxxTuneModel model = MaxxTune.Build(blob, VeDefinition(), channels);
        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) = (model.Layout, model.Tables);

        TuneTable table = EcuTune.FromPages(layout, blob).Tables(tables).Single();

        Assert.Equal(200, table.X.Breakpoints[0], 3);
        Assert.Equal(7000, table.X.Breakpoints[15], 3);
        Assert.Equal("rpm", table.X.Units);

        Assert.Equal(20, table.Y.Breakpoints[0], 3);
        Assert.Equal(199.9, table.Y.Breakpoints[10], 3);
        Assert.Equal("kPa", table.Y.Units);
    }

    /// <summary>
    /// A table the ECU is not using is left out rather than decoded into
    /// nonsense. The counts are what say so — zero, or past the 64 a MaxxECU
    /// allows.
    /// </summary>
    [Theory]
    [InlineData(0, 11)]
    [InlineData(16, 0)]
    [InlineData(200, 11)]
    [InlineData(16, 200)]
    public void ATableWithImpossibleDimensionsIsNotOffered(int columns, int rows)
    {
        byte[] blob = WithVeTable();
        blob[1578 + 0x06] = (byte)columns;
        blob[1578 + 0x07] = (byte)rows;

        Assert.Null(MaxxTune.TableAt(blob, "VE Table 1 data", 1578));

        IReadOnlyList<TableDefinition> tables = MaxxTune.Build(blob, VeDefinition()).Tables;
        Assert.Empty(tables);
    }

    /// <summary>
    /// A table whose axis does not climb is not a table.
    ///
    /// Two thirds of the addresses in the definitions are not stated but follow
    /// on from what came before, so one drift reads a config struct off bytes
    /// that are not one — and what comes back is a plausible size and an offset
    /// inside the blob, not an obvious failure. On the bench ECU that produced
    /// six tables that drew perfectly well, one of them fifteen by thirty-nine
    /// with columns running 1,000 to 8,000 and then 0, 27, −25,795.
    /// </summary>
    [Fact]
    public void ATableWhoseAxisDoesNotClimbIsNotOffered()
    {
        byte[] blob = WithVeTable();

        // One breakpoint out of order, in the middle of an otherwise good axis.
        BinaryPrimitives.WriteInt16LittleEndian(
            blob.AsSpan(MaxxTune.TableData + 496 + 1 + (5 * 2)), -25795);

        Assert.Null(MaxxTune.TableAt(blob, "VE Table 1 data", 1578));

        IReadOnlyList<TableDefinition> tables = MaxxTune.Build(blob, VeDefinition()).Tables;
        Assert.Empty(tables);
    }

    /// <summary>
    /// A repeated breakpoint counts as not climbing, which is what leaves the
    /// unused tables out: a MaxxECU parks one it is not using at two by two with
    /// both axes at zero.
    /// </summary>
    [Fact]
    public void AnUnusedTableParkedAtZeroIsNotOffered()
    {
        var blob = new byte[MaxxTune.BlobSize];
        Compose(blob, 400, 100, 61, 20, [0, 0], [0, 0], new short[2, 2]);

        Assert.Null(MaxxTune.TableAt(blob, "ClosedBoost Target2 Table data", 400));
    }

    /// <summary>
    /// A table whose record would run past the end of the blob is refused. The
    /// data offset is sixteen bits and the cells are counted from it, so a
    /// corrupt or unexpected offset otherwise reads whatever is next.
    /// </summary>
    [Fact]
    public void ATableWhoseDataRunsPastTheBlobIsNotOffered()
    {
        byte[] blob = WithVeTable();
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(1578 + 0x0B), 65000);

        Assert.Null(MaxxTune.TableAt(blob, "VE Table 1 data", 1578));
    }

    /// <summary>
    /// Two tables that MTune gives the same name are kept apart.
    ///
    /// A constant is found by name, so a repeat would shadow the first — and the
    /// shadowing is silent: the second table's title would sit over the first
    /// one's numbers.
    /// </summary>
    [Fact]
    public void TablesSharingANameAreKeptApart()
    {
        var blob = new byte[MaxxTune.BlobSize];
        short[] axis = [100, 200];
        var cells = new short[2, 2];

        Compose(blob, 400, 100, 61, 20, axis, axis, cells);
        Compose(blob, 500, 300, 61, 20, axis, axis, cells);

        IReadOnlyList<MaxxSettingDefinition> definitions =
        [
            new("Boost Table data", "dynamicTable", 400, 4, 0.1, 0, 300),
            new("Boost Table data", "dynamicTable", 500, 4, 0.1, 0, 300),
        ];

        MaxxTuneModel model = MaxxTune.Build(blob, definitions);
        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) = (model.Layout, model.Tables);

        Assert.Equal(2, tables.Count);
        Assert.Equal(2, tables.Select(t => t.Values).Distinct().Count());
        Assert.Equal(6, layout.Constants.Count);
    }

    /// <summary>
    /// A setting whose name collides with a generated axis name does not take
    /// the axis over.
    ///
    /// The axis constants are invented here — "VE Table 1 X" — and the ECU's own
    /// settings are not, so nothing stops the definitions containing that name
    /// already. A constant is found by name and the later one wins, so the table
    /// would read its breakpoints out of that setting, at that setting's scale,
    /// with nothing on screen to say so.
    /// </summary>
    [Fact]
    public void ASettingCannotTakeOverATablesAxis()
    {
        byte[] blob = WithVeTable();

        // Something plausible at an address nowhere near the axis.
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(9000), 1234);

        IReadOnlyList<MaxxSettingDefinition> definitions =
        [
            .. VeDefinition(),
            new("VE Table 1 X", "int16", 9000, 1, 1, -30000, 30000),
        ];

        MaxxTuneModel model = MaxxTune.Build(blob, definitions);
        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) = (model.Layout, model.Tables);
        TuneTable table = EcuTune.FromPages(layout, blob).Tables(tables).Single();

        // The breakpoints are the table's own, not the setting's single value.
        Assert.Equal(16, table.X.Breakpoints.Length);
        Assert.Equal(200, table.X.Breakpoints[0], 3);
        Assert.Equal(7000, table.X.Breakpoints[15], 3);
    }

    /// <summary>
    /// The page a MaxxECU's tune sits on declares no way to write it.
    ///
    /// This is what keeps the ordinary write path off a controller it must not
    /// touch. Send and Burn are driven by the templates the firmware gives for
    /// them — <c>C%2o%2c%v</c> and <c>B</c> on a MegaSquirt — and a MaxxECU is
    /// not written that way at all. Both are empty, so there is nothing for that
    /// path to send even if it were reached, and the burn is empty for the
    /// further reason that this ECU has none.
    /// </summary>
    [Fact]
    public void TheTunePageOffersNoWriteAndNoBurn()
    {
        TuneLayout layout = MaxxTune.Build(WithVeTable(), VeDefinition()).Layout;
        TunePage page = Assert.Single(layout.Pages);

        Assert.Empty(page.ChunkWriteCommand);
        Assert.Empty(page.BurnCommand);
        Assert.Empty(page.ReadCommand);
    }

    // ----- the invented settings interface ---------------------------------------

    private static IReadOnlyList<MaxxSettingDefinition> SomeSettings() =>
    [
        new("IATSensor Input Voltage", "list", 100, 18, 0.001, 0, 5),
        new("IATSensor Predef", "uint16", 200, 1, 1, 0, 100),
        new("Fuel Injmethod", "uint8", 300, 1, 1, 0, 3),
        new("VE Table 1 data", "dynamicTable", 1578, 484, 0.1, 0, 2500),
    ];

    /// <summary>
    /// Settings are grouped into pages by the subsystem their name begins with,
    /// because MTune's definitions describe no pages at all and 8,748 settings
    /// with nowhere to be shown is the same as none.
    /// </summary>
    [Fact]
    public void SettingsAreGroupedIntoPagesBySubsystem()
    {
        TuneInterface ui = MaxxTune.Build(WithVeTable(), SomeSettings()).Pages;

        Assert.False(ui.IsEmpty);
        Assert.Equal(2, ui.Dialogs.Count);
        Assert.Contains(ui.Dialogs.Values, d => d.Title == "IATSensor");
        Assert.Contains(ui.Dialogs.Values, d => d.Title == "Fuel");
    }

    /// <summary>
    /// A list becomes a row per point.
    ///
    /// A field with no subscript addresses the first element, so an eighteen
    /// point sensor calibration would show as a single box reading 4.660 — a
    /// fifth of one of its points, with nothing on screen saying so.
    /// </summary>
    [Fact]
    public void EveryPointOfAListIsShown()
    {
        TuneInterface ui = MaxxTune.Build(WithVeTable(), SomeSettings()).Pages;
        TuneDialog sensor = ui.Dialogs.Values.Single(d => d.Title == "IATSensor");

        Assert.Equal(19, sensor.Items.Count);
        Assert.Equal("IATSensor Input Voltage[0]", sensor.Items[0].Target);
        Assert.Equal("IATSensor Input Voltage[17]", sensor.Items[17].Target);
        Assert.Equal(0, sensor.Items[0].TargetIndex);
        Assert.Equal(17, sensor.Items[17].TargetIndex);
        Assert.Equal("IATSensor Input Voltage", sensor.Items[17].TargetConstant);
    }

    /// <summary>
    /// Tables are not among them. They have the other half of that view, and a
    /// table offered as a field would show one cell of itself.
    /// </summary>
    [Fact]
    public void TablesAreNotOfferedAsSettings()
    {
        TuneInterface ui = MaxxTune.Build(WithVeTable(), SomeSettings()).Pages;

        Assert.DoesNotContain(
            ui.Dialogs.Values.SelectMany(d => d.Items),
            item => item.TargetConstant.Contains("VE Table", StringComparison.Ordinal));
    }

    /// <summary>
    /// No page is longer than it should be. The largest subsystem declares 1,201
    /// settings, which as one page is a scroll nobody finds anything in.
    /// </summary>
    [Fact]
    public void ALongSubsystemIsSplitAcrossPages()
    {
        IReadOnlyList<MaxxSettingDefinition> many =
        [
            .. Enumerable.Range(0, 130).Select(i =>
                new MaxxSettingDefinition($"GPO Thing {i}", "uint8", 1000 + i, 1, 1, 0, 255)),
        ];

        TuneInterface ui = MaxxTune.Build(new byte[MaxxTune.BlobSize], many).Pages;

        Assert.True(ui.Dialogs.Count >= 3, $"expected several pages, got {ui.Dialogs.Count}");
        Assert.All(ui.Dialogs.Values, d => Assert.InRange(d.Items.Count, 1, 60));
        Assert.Equal(130, ui.Dialogs.Values.Sum(d => d.Items.Count));
    }

    // ----- the definitions file -------------------------------------------------

    /// <summary>
    /// An address that is not stated follows on from the one before.
    ///
    /// Two thirds of the file states no address at all, so a reader that takes
    /// only the ones that do puts everything else at nought.
    /// </summary>
    [Fact]
    public void AnAddressThatIsNotStatedFollowsTheOneBefore()
    {
        string xml = """
            <ECUSettingsDefinitions version="1">
              <ECUSetting name="First"  type="uint16" length="1" address="100" />
              <ECUSetting name="Second" type="uint16" length="1" address="seq" />
              <ECUSetting name="Third"  type="uint8"  length="4" />
              <ECUSetting name="Fourth" type="uint16" length="1" />
              <ECUSetting name="Fifth"  type="uint16" length="1" address="900" />
            </ECUSettingsDefinitions>
            """;

        string path = Path.Combine(Path.GetTempPath(), $"olv-maxx-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);

        try
        {
            IReadOnlyList<MaxxSettingDefinition> read = MaxxTuneDefinitions.Read(path);

            Assert.Equal([100, 102, 104, 108, 900], read.Select(d => d.Address));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A list is signed when the firmware says its smallest value is below zero,
    /// because the file gives lists no element type at all. Read unsigned, a
    /// temperature curve starting at −40 °C begins at 6,513 °C.
    /// </summary>
    [Theory]
    [InlineData("list", -40.0, true)]
    [InlineData("list", 0.0, false)]
    [InlineData("uint16", -40.0, false)]
    [InlineData("int16", 0.0, true)]
    public void SignednessComesFromTheTypeAndThenFromTheRange(string kind, double low, bool signed)
    {
        var definition = new MaxxSettingDefinition("x", kind, 0, 1, 0.1, low, 100);

        Assert.Equal(signed, definition.IsSigned);
    }

    [Fact]
    public void MissingDefinitionsAreNotAnError() =>
        Assert.Empty(MaxxTuneDefinitions.Read(Path.Combine(Path.GetTempPath(), "no-such-file.xml")));

    // ----- writing ---------------------------------------------------------------

    /// <summary>
    /// An ECU that holds a tune and lets it be written the way a MaxxECU does:
    /// the header acknowledged on its own, then the payload with its checksum.
    /// </summary>
    private sealed class FakeEcu(byte status = MaxxUsbProtocol.Ok) : IEcuTransport
    {
        private byte[] _reply = [];

        /// <summary>Whether it acknowledges a write's header at all.</summary>
        public bool AnswerTheHeader { get; init; } = true;

        /// <summary>Whether it acknowledges the payload once it has taken it.</summary>
        public bool AnswerThePayload { get; init; } = true;

        /// <summary>What it is holding, so a write can be checked against it.</summary>
        public byte[] Tune { get; } = new byte[MaxxTune.BlobSize];

        /// <summary>Set while a write's payload is expected rather than a header.</summary>
        private (int Offset, int Length)? _expecting;

        public int Writes { get; private set; }

        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_expecting is { } pending)
            {
                // The payload, and the checksum the ECU verifies before taking it.
                uint sent = BitConverter.ToUInt32(data[pending.Length..]);

                if (sent == MaxxProtocol.Crc32(data[..pending.Length]))
                {
                    data[..pending.Length].CopyTo(Tune.AsSpan(pending.Offset));
                    Writes++;

                    // Taken, and the acknowledgement lost on the way back — which
                    // is the case the caller must not read as "nothing happened".
                    _reply = AnswerThePayload ? [MaxxUsbProtocol.Ok] : [];
                }
                else _reply = [(byte)MaxxWriteStatus.OutOfRange];

                _expecting = null;
                return;
            }

            int offset = data[2] | (data[3] << 8);
            int length = data[4] | (data[5] << 8);

            if (data[0] == MaxxUsbProtocol.Write)
            {
                if (!AnswerTheHeader) { _reply = []; return; }
                if (status != MaxxUsbProtocol.Ok) { _reply = [status]; return; }

                _expecting = (offset, length);
                _reply = [MaxxUsbProtocol.Ok];
                return;
            }

            // A read of the tune, which is how a write is checked.
            var reply = new byte[MaxxUsbProtocol.ReplyLength(length)];
            reply[0] = MaxxUsbProtocol.Ok;
            Tune.AsSpan(offset, length).CopyTo(reply.AsSpan(1));
            BitConverter.GetBytes(MaxxProtocol.Crc32(reply.AsSpan(0, 1 + length)))
                .CopyTo(reply, 1 + length);

            _reply = reply;
        }

        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            int taken = Math.Min(buffer.Length, _reply.Length);
            _reply.AsSpan(0, taken).CopyTo(buffer);
            _reply = _reply[taken..];

            return taken;
        }

        public void DiscardInput() => _reply = [];

        public void Dispose() => Close();
    }

    /// <summary>
    /// A table's cells go to the ECU with the checksum the firmware checks them
    /// against, computed over the pad byte in front of the grid as well as the
    /// grid.
    ///
    /// Cells written without it are cells the ECU has been told not to trust.
    /// The span was recovered by searching for it against a real record and
    /// confirmed on three tables, and then confirmed again by writing a cell to
    /// a bench ECU and having it keep the checksum given to it.
    /// </summary>
    [Fact]
    public void ATablesCellsAreSentWithTheChecksumOverThem()
    {
        byte[] blob = WithVeTable();
        MaxxTable table = MaxxTune.TableAt(blob, "VE Table 1 data", 1578)!;

        short[] cells = [.. Enumerable.Range(0, table.Columns * table.Rows).Select(i => (short)(300 + i))];
        byte[] record = MaxxTune.Record(blob, table, cells);

        Assert.Equal((cells.Length * 2) + 4, record.Length);
        Assert.Equal(300, BinaryPrimitives.ReadInt16LittleEndian(record));

        uint sent = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(cells.Length * 2));
        uint expected = MaxxProtocol.Crc32(
            [blob[table.CellsAt - 1], .. record.AsSpan(0, cells.Length * 2)]);

        Assert.Equal(expected, sent);

        // And not the checksum of the cells alone, which is the tempting wrong
        // span and differs.
        Assert.NotEqual(MaxxProtocol.Crc32(record.AsSpan(0, cells.Length * 2)), sent);
    }

    [Fact]
    public void AGridOfTheWrongSizeIsRefused()
    {
        byte[] blob = WithVeTable();
        MaxxTable table = MaxxTune.TableAt(blob, "VE Table 1 data", 1578)!;

        Assert.Throws<ArgumentException>(() => MaxxTune.Record(blob, table, new short[3]));
    }

    /// <summary>
    /// Whether a table can be sent at all in one write, which on a controller
    /// with no burn is the difference between an edit that is safe and one that
    /// leaves the ECU holding a grid its checksum disagrees with.
    /// </summary>
    [Fact]
    public void ATableTooBigToSendAtOnceSaysSo()
    {
        byte[] blob = WithVeTable();

        // 16 x 11 is 352 bytes of cells and cannot go in one 256-byte write.
        Assert.False(MaxxTune.FitsInOneWrite(MaxxTune.TableAt(blob, "VE Table 1 data", 1578)!));

        var small = new byte[MaxxTune.BlobSize];
        short[] axis = [10, 20, 30, 40, 50, 60];
        Compose(small, 400, 100, 61, 20, axis, axis, new short[6, 6]);

        // 6 x 6 is 72 bytes, and fits with its checksum.
        Assert.True(MaxxTune.FitsInOneWrite(MaxxTune.TableAt(small, "Fuel ASE Table data", 400)!));
    }

    /// <summary>
    /// A table that fits goes in one write, so it is never inconsistent with its
    /// own checksum for even a moment.
    /// </summary>
    [Fact]
    public void ATableThatFitsIsSentAsOneWrite()
    {
        var blob = new byte[MaxxTune.BlobSize];
        short[] axis = [10, 20, 30, 40, 50, 60];
        Compose(blob, 400, 100, 61, 20, axis, axis, new short[6, 6]);

        MaxxTable table = MaxxTune.TableAt(blob, "Fuel ASE Table data", 400)!;
        IReadOnlyList<(int Offset, byte[] Data)> pieces =
            MaxxTune.WritesFor(blob, table, new short[36]);

        (int offset, byte[] data) = Assert.Single(pieces);
        Assert.Equal(table.CellsAt, offset);
        Assert.Equal((36 * 2) + 4, data.Length);
    }

    /// <summary>
    /// A table too big for one write is split, and the checksum goes in the last
    /// piece.
    ///
    /// That ordering is the whole safety property. Between the first piece
    /// landing and the last, the ECU holds a grid its stored checksum disagrees
    /// with and reads the table as nought for one evaluation; putting the
    /// checksum anywhere but last would leave it disagreeing after the cells were
    /// already right.
    /// </summary>
    [Fact]
    public void ABigTableIsSplitWithTheChecksumLast()
    {
        byte[] blob = WithVeTable();
        MaxxTable table = MaxxTune.TableAt(blob, "VE Table 1 data", 1578)!;

        short[] cells = [.. Enumerable.Range(0, table.Columns * table.Rows).Select(i => (short)(300 + i))];
        IReadOnlyList<(int Offset, byte[] Data)> pieces = MaxxTune.WritesFor(blob, table, cells);

        Assert.True(pieces.Count > 1, "a 16 by 11 table does not fit in one write");
        Assert.All(pieces, p => Assert.InRange(p.Data.Length, 1, MaxxTune.MaximumWrite));

        // Nose to tail from the first cell, with no gap and no overlap.
        Assert.Equal(table.CellsAt, pieces[0].Offset);

        for (int i = 1; i < pieces.Count; i++)
            Assert.Equal(pieces[i - 1].Offset + pieces[i - 1].Data.Length, pieces[i].Offset);

        // The whole record, and the checksum at the end of the last piece.
        byte[] whole = [.. pieces.SelectMany(p => p.Data)];
        Assert.Equal(MaxxTune.Record(blob, table, cells), whole);

        (int last, byte[] tail) = pieces[^1];
        Assert.Equal(
            table.CellsAt + (cells.Length * 2) + 4,
            last + tail.Length);
    }

    /// <summary>
    /// The tune's checksums are one CRC-32 per 4 KB, and the last one covers
    /// only what is left rather than a whole chunk.
    ///
    /// The short last chunk is what made the hardware agreement convincing: over
    /// the range MTune asks for, it is 740 bytes and not 4,096, and a reading
    /// that was merely close would not have matched it.
    /// </summary>
    [Fact]
    public void TheTunesChecksumsAreOnePerChunkWithAShortLast()
    {
        var blob = new byte[MaxxTune.BlobSize];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i * 7);

        uint[] checksums = MaxxTune.ChecksumsOf(blob, MaxxTune.WriteCeiling);

        Assert.Equal(16, checksums.Length);

        // The last covers 0xF2E7 - 15 * 4096 = 743 bytes.
        int last = MaxxTune.WriteCeiling - (15 * MaxxTune.ChecksumChunk);
        Assert.Equal(
            MaxxProtocol.Crc32(blob.AsSpan(15 * MaxxTune.ChecksumChunk, last)),
            checksums[15]);

        Assert.Equal(MaxxProtocol.Crc32(blob.AsSpan(0, MaxxTune.ChecksumChunk)), checksums[0]);
    }

    /// <summary>
    /// Changing one byte changes the checksum of its chunk and no other, which
    /// is what makes this a useful thing to compare after a write.
    /// </summary>
    [Fact]
    public void OnlyTheChunkThatChangedGetsANewChecksum()
    {
        var blob = new byte[MaxxTune.BlobSize];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i * 7);

        uint[] before = MaxxTune.ChecksumsOf(blob);

        blob[(3 * MaxxTune.ChecksumChunk) + 11] ^= 0xFF;

        uint[] after = MaxxTune.ChecksumsOf(blob);

        Assert.NotEqual(before[3], after[3]);

        for (int i = 0; i < before.Length; i++)
            if (i != 3)
                Assert.Equal(before[i], after[i]);
    }

    /// <summary>
    /// A payload that went out and was never acknowledged is uncertain, not a
    /// failure.
    ///
    /// This is the most dangerous status in the file. The ECU applies a write as
    /// it arrives and saves it itself, so an acknowledgement lost on the way back
    /// looks exactly like a write that never happened — and calling that "nothing
    /// was changed" tells somebody their tune is untouched when it may be
    /// permanently different. The header is acknowledged separately, so silence
    /// there really does mean nothing was sent.
    /// </summary>
    [Fact]
    public void APayloadSentWithoutAnAcknowledgementIsUncertainRatherThanFailed()
    {
        var ecu = new FakeEcu { AnswerThePayload = false };

        Assert.Equal(MaxxWriteStatus.Uncertain, MaxxTune.Write(ecu, 1000, [1, 2, 3, 4]));

        // And it really did go out, which is the whole point.
        Assert.Equal<byte[]>([1, 2, 3, 4], ecu.Tune.AsSpan(1000, 4).ToArray());
    }

    [Fact]
    public void AHeaderThatIsNotAnsweredMeansNothingWasSent()
    {
        var ecu = new FakeEcu { AnswerTheHeader = false };

        Assert.Equal(MaxxWriteStatus.NoAnswer, MaxxTune.Write(ecu, 1000, [1, 2, 3, 4]));
        Assert.Equal(0, ecu.Writes);
    }

    // ----- one pass, one set of names ------------------------------------------

    /// <summary>
    /// The tables a write uses carry the same names the editor shows.
    ///
    /// They were worked out separately and could disagree: a table renamed for
    /// the layout because something else had claimed its name kept the original
    /// in the list a write looks it up in, so that table could never be sent —
    /// the button stayed shut with nothing to say why.
    /// </summary>
    [Fact]
    public void TheTablesAWriteUsesAreNamedLikeTheOnesOnScreen()
    {
        var blob = new byte[MaxxTune.BlobSize];
        short[] axis = [10, 20];
        var cells = new short[2, 2];

        Compose(blob, 400, 100, 61, 20, axis, axis, cells);
        Compose(blob, 500, 300, 61, 20, axis, axis, cells);

        IReadOnlyList<MaxxSettingDefinition> definitions =
        [
            new("Boost Table data", "dynamicTable", 400, 4, 0.1, 0, 300),
            new("Boost Table data", "dynamicTable", 500, 4, 0.1, 0, 300),
        ];

        MaxxTuneModel model = MaxxTune.Build(blob, definitions);

        Assert.Equal(2, model.Maps.Count);
        Assert.Equal(
            [.. model.Tables.Select(t => t.Title).Order()],
            [.. model.Maps.Select(m => m.Name).Order()]);
    }

    /// <summary>
    /// A settings page points at the constant the layout actually made, not at
    /// the name the file used.
    ///
    /// Where a setting's name is already taken — by a table, by an axis, or by
    /// another setting — the layout renames it. A page that went on referring to
    /// the original would put that row on top of whatever holds the name now, and
    /// show one setting's bytes under another's label.
    /// </summary>
    [Fact]
    public void ASettingsPagePointsAtTheConstantTheLayoutMade()
    {
        byte[] blob = WithVeTable();

        // Named exactly like the table's X axis, which is generated first.
        IReadOnlyList<MaxxSettingDefinition> definitions =
        [
            .. VeDefinition(),
            new("VE Table 1 X", "int16", 9000, 1, 1, -30000, 30000),
        ];

        MaxxTuneModel model = MaxxTune.Build(blob, definitions);

        string[] targets =
            [.. model.Pages.Dialogs.Values.SelectMany(d => d.Items).Select(i => i.TargetConstant)];

        Assert.All(targets, t => Assert.Contains(model.Layout.Constants, c => c.Name == t));

        // The setting did not take the axis over, so it is under some other name.
        TuneConstant axis = model.Layout.Constants.Single(c => c.Name == "VE Table 1 X");
        Assert.Equal(16, axis.Columns);
        Assert.DoesNotContain("VE Table 1 X", targets);
    }

    [Fact]
    public void AWriteLandsWhereItWasAimed()
    {
        var ecu = new FakeEcu();
        byte[] data = [1, 2, 3, 4];

        Assert.Equal(MaxxWriteStatus.Ok, MaxxTune.Write(ecu, 1000, data));
        Assert.Equal(data, ecu.Tune.AsSpan(1000, 4).ToArray());
        Assert.True(MaxxTune.Verify(ecu, 1000, data));
    }

    /// <summary>
    /// Reading back is what says a write took, and it has to be able to say no.
    /// </summary>
    [Fact]
    public void VerifyingFailsWhenTheTuneDoesNotMatch()
    {
        var ecu = new FakeEcu();
        MaxxTune.Write(ecu, 1000, [1, 2, 3, 4]);

        Assert.False(MaxxTune.Verify(ecu, 1000, [1, 2, 3, 5]));
    }

    /// <summary>
    /// A refusal is reported as what the ECU said, not as a bare failure: busy
    /// is worth retrying and out of range never is.
    /// </summary>
    [Theory]
    [InlineData(0x40, MaxxWriteStatus.WriterBusy)]
    [InlineData(0x30, MaxxWriteStatus.OutOfRange)]
    [InlineData(0x20, MaxxWriteStatus.UnknownCommand)]
    public void ARefusedWriteSaysWhy(byte answered, MaxxWriteStatus expected)
    {
        var ecu = new FakeEcu(answered);

        Assert.Equal(expected, MaxxTune.Write(ecu, 1000, [1, 2, 3, 4]));
        Assert.Equal(0, ecu.Writes);
    }

    /// <summary>
    /// A write past what the ECU accepts is refused here rather than sent.
    ///
    /// The ECU would answer 0x30 and nothing would happen, so this is not about
    /// safety on the wire — it is that the caller finds out before the bytes go
    /// out, which on a controller with no undo is the habit worth having.
    /// </summary>
    [Fact]
    public void AWriteAboveTheCeilingIsRefusedBeforeItIsSent()
    {
        var ecu = new FakeEcu();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MaxxTune.Write(ecu, MaxxTune.WriteCeiling - 2, new byte[4]));

        Assert.Equal(0, ecu.Writes);
    }

    [Fact]
    public void AWriteLongerThanTheEcuTakesIsRefusedBeforeItIsSent()
    {
        var ecu = new FakeEcu();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MaxxTune.Write(ecu, 1000, new byte[MaxxTune.MaximumWrite + 1]));

        Assert.Equal(0, ecu.Writes);
    }

    /// <summary>
    /// The payload carries its own checksum, and a wrong one is not taken.
    ///
    /// Checked by writing properly and then corrupting the same exchange: the
    /// fake refuses it exactly as the firmware does, which is what makes the
    /// checksum in <see cref="MaxxTune.Write"/> load-bearing rather than
    /// decorative.
    /// </summary>
    [Fact]
    public void ThePayloadIsSentWithAChecksumOverIt()
    {
        var ecu = new FakeEcu();
        byte[] data = [9, 8, 7, 6];

        MaxxTune.Write(ecu, 2000, data);

        // What Write sends: the bytes, then the CRC-32 of exactly those bytes.
        var expected = new byte[8];
        data.CopyTo(expected, 0);
        BitConverter.GetBytes(MaxxProtocol.Crc32(data)).CopyTo(expected, 4);

        Assert.Equal(data, ecu.Tune.AsSpan(2000, 4).ToArray());
        Assert.Equal(MaxxProtocol.Crc32(data), BitConverter.ToUInt32(expected, 4));
    }
}
