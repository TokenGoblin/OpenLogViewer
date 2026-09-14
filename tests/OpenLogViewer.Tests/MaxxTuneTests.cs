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

        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) =
            MaxxTune.Build(blob, VeDefinition());

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

        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) =
            MaxxTune.Build(blob, VeDefinition());

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

        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) =
            MaxxTune.Build(blob, VeDefinition(), channels);

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

        (_, IReadOnlyList<TableDefinition> tables) = MaxxTune.Build(blob, VeDefinition());
        Assert.Empty(tables);
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

        (TuneLayout layout, IReadOnlyList<TableDefinition> tables) = MaxxTune.Build(blob, definitions);

        Assert.Equal(2, tables.Count);
        Assert.Equal(2, tables.Select(t => t.Values).Distinct().Count());
        Assert.Equal(6, layout.Constants.Count);
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
}
