using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class RealtimeTests
{
    /// <summary>An INI shaped like the real thing, conditionals and all.</summary>
    private const string Ini = """
        [MegaTune]
        signature = "test"

        [OutputChannels]
        ochBlockSize     = 16
        #if CAN_COMMANDS
        ochGetCommand    = "r\$tsCanId\x07%2o%2c"
        #else
        ochGetCommand       = "A"
        #endif

        seconds          = scalar, U16,    0, "s",   1.000, 0.0
        rpm              = scalar, U16,    2, "RPM", 1.000, 0.0
        map              = scalar, S16,    4, "kPa", 0.100, 0.0
        #if CELSIUS
        coolant          = scalar, S16,    6, "°C",  0.100, -40.0
        #else
        coolant          = scalar, S16,    6, "°F",  0.100, -40.0
        #endif
        firing1          = bits,   U08,    8, [0:0]
        mode             = bits,   U08,    8, [1:3]
        secl             = { seconds % 256 }, "s"
        halfRpm          = { rpm / 2 }, "RPM"

        [Datalog]
        entry = rpm,     "RPM",       int,   "%d"
        entry = map,     "MAP",       float, "%.1f"
        entry = coolant, "CLT",       float, "%.2f"
        entry = halfRpm, "Half RPM",  float, "%.1f"
        """;

    private static RealtimeLayout Layout(params string[] symbols) =>
        MsqIni.ReadOutputChannels(Ini, new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase));

    private static byte[] Block()
    {
        var block = new byte[16];
        block[0] = 0x00; block[1] = 0x0A;   // seconds = 10
        block[2] = 0x0B; block[3] = 0xB8;   // rpm = 3000
        block[4] = 0x03; block[5] = 0xE8;   // map raw 1000 → 100.0 kPa
        block[6] = 0x08; block[7] = 0x34;   // coolant raw 2100 → 210.0 − 40 = 170.0
        block[8] = 0b0000_1011;             // firing1 = 1, mode = bits 1..3 = 0b101 = 5
        return block;
    }

    // ----- layout -----------------------------------------------------------

    [Fact]
    public void TheBlockSizeAndCommandAreRead()
    {
        RealtimeLayout layout = Layout("CAN_COMMANDS");

        Assert.Equal(16, layout.BlockSize);
        Assert.False(layout.UsesSimpleCommand);
        Assert.StartsWith("r", layout.GetCommand);
    }

    [Fact]
    public void TheOtherBranchOfAConditionalIsTaken()
    {
        // Without CAN_COMMANDS the plain serial command is the live one.
        RealtimeLayout layout = Layout();

        Assert.True(layout.UsesSimpleCommand);
        Assert.Equal("A", layout.GetCommand);
    }

    [Fact]
    public void OnlyOneBranchOfAConditionalContributesFields()
    {
        // Both branches define "coolant". Taking both would leave two channels
        // of the same name disagreeing about units.
        RealtimeLayout layout = Layout("CAN_COMMANDS");

        RealtimeField coolant = Assert.Single(layout.Fields, f => f.Name == "coolant");
        Assert.Equal("°F", coolant.Units);
    }

    [Fact]
    public void TheSelectedBranchFollowsTheSymbols()
    {
        RealtimeLayout layout = Layout("CELSIUS");

        Assert.Equal("°C", Assert.Single(layout.Fields, f => f.Name == "coolant").Units);
    }

    [Fact]
    public void ScalarsCarryTheirOffsetScaleAndTransform()
    {
        RealtimeField map = Assert.Single(Layout().Fields, f => f.Name == "map");

        Assert.Equal(RealtimeType.S16, map.Type);
        Assert.Equal(4, map.Offset);
        Assert.Equal(0.1, map.Scale, 6);
        Assert.Equal("kPa", map.Units);
        Assert.Equal(1, map.Digits);      // inferred from the scale
    }

    [Fact]
    public void BitFieldsCarryTheirBitRange()
    {
        RealtimeField mode = Assert.Single(Layout().Fields, f => f.Name == "mode");

        Assert.True(mode.IsBitField);
        Assert.Equal(8, mode.Offset);
        Assert.Equal(1, mode.BitLow);
        Assert.Equal(3, mode.BitHigh);
    }

    [Fact]
    public void ExpressionsAreCollectedSeparately()
    {
        RealtimeLayout layout = Layout();

        Assert.Contains(layout.Expressions, e => e.Name == "secl" && e.Expression == "seconds % 256");
        Assert.Contains(layout.Expressions, e => e.Name == "halfRpm");
        Assert.DoesNotContain(layout.Fields, f => f.Name == "secl");
    }

    [Fact]
    public void NothingIsSilentlySkipped() => Assert.Empty(Layout().Skipped);

    [Fact]
    public void ADegreeSignSurvivesALatin1Ini()
    {
        // Firmware INIs are ISO-8859-1 and say nothing about it. Read as UTF-8
        // they mostly work, because almost everything in them is ASCII — and
        // then the one byte that matters in a units string decodes to a
        // replacement character and every temperature reads "?F".
        byte[] latin1 =
        [
            .. "[OutputChannels]\nochBlockSize = 4\ncoolant = scalar, S16, 0, \""u8.ToArray(),
            0xB0, (byte)'F',
            .. "\", 0.1, 0.0\n"u8.ToArray(),
        ];

        RealtimeLayout layout = MsqIni.ReadOutputChannels(TuningText.Decode(latin1));

        Assert.Equal("°F", Assert.Single(layout.Fields).Units);
    }

    [Fact]
    public void AUtf8FileIsStillReadAsUtf8()
    {
        // Only a file that is not valid UTF-8 falls back, so a modern INI keeps
        // whatever it says.
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(
            "[OutputChannels]\nochBlockSize = 4\ncoolant = scalar, S16, 0, \"°C\", 0.1, 0.0\n");

        RealtimeLayout layout = MsqIni.ReadOutputChannels(TuningText.Decode(utf8));

        Assert.Equal("°C", Assert.Single(layout.Fields).Units);
    }

    [Fact]
    public void AByteOrderMarkDoesNotLeadTheFirstLine()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("[OutputChannels]\nochBlockSize = 64\n")];

        Assert.Equal(64, MsqIni.ReadOutputChannels(TuningText.Decode(withBom)).BlockSize);
    }

    // ----- decoding ---------------------------------------------------------

    [Fact]
    public void ScalarsDecodeBigEndianWithScaleAndTransform()
    {
        var decoder = new RealtimeDecoder(Layout());
        double[] values = decoder.Decode(Block());

        Assert.Equal(3000, Value(decoder, values, "rpm"), 4);
        Assert.Equal(100.0, Value(decoder, values, "map"), 4);
        Assert.Equal(170.0, Value(decoder, values, "coolant"), 4);
    }

    [Fact]
    public void BitFieldsDecodeAsTheirOwnValue()
    {
        // 0b0000_1011: bit 0 is 1, bits 1..3 are 0b101 = 5.
        var decoder = new RealtimeDecoder(Layout());
        double[] values = decoder.Decode(Block());

        Assert.Equal(1, Value(decoder, values, "firing1"), 4);
        Assert.Equal(5, Value(decoder, values, "mode"), 4);
    }

    [Fact]
    public void ExpressionsAreEvaluatedFromTheDecodedValues()
    {
        var decoder = new RealtimeDecoder(Layout());
        double[] values = decoder.Decode(Block());

        Assert.Equal(10, Value(decoder, values, "secl"), 4);
        Assert.Equal(1500, Value(decoder, values, "halfRpm"), 4);
    }

    [Fact]
    public void AShortBlockCostsOnlyTheFieldsThatFallOffTheEnd()
    {
        // A truncated read on a flaky link should not lose the whole sample.
        var decoder = new RealtimeDecoder(Layout());
        double[] values = decoder.Decode(Block().AsSpan(0, 4));

        Assert.Equal(3000, Value(decoder, values, "rpm"), 4);
        Assert.True(double.IsNaN(Value(decoder, values, "coolant")));
    }

    [Fact]
    public void AnExpressionNamingSomethingAbsentIsReportedNotThrown()
    {
        const string ini = """
            [OutputChannels]
            ochBlockSize = 4
            rpm     = scalar, U16, 0, "RPM", 1.0, 0.0
            broken  = { nCylinders * 2 }, ""
            """;

        var decoder = new RealtimeDecoder(MsqIni.ReadOutputChannels(ini));

        Assert.Equal("broken", Assert.Single(decoder.UnresolvedExpressions));
        Assert.Contains("rpm", decoder.Names);
        Assert.DoesNotContain("broken", decoder.Names);
    }

    [Fact]
    public void TheTuneSuppliesWhatTheWireDoesNot()
    {
        // Firmware derives channels from tune settings as well as live values;
        // without them a large share cannot be computed at all.
        const string ini = """
            [OutputChannels]
            ochBlockSize = 4
            rpm      = scalar, U16, 0, "RPM", 1.0, 0.0
            perCyl   = { rpm / nCylinders }, "RPM"
            """;

        var settings = new Dictionary<string, double> { ["nCylinders"] = 6 };
        var decoder = new RealtimeDecoder(MsqIni.ReadOutputChannels(ini), settings);

        Assert.Empty(decoder.UnresolvedExpressions);

        double[] values = decoder.Decode([0x0B, 0xB8, 0, 0]);
        Assert.Equal(500, Value(decoder, values, "perCyl"), 4);
    }

    // ----- datalog labels ---------------------------------------------------

    [Fact]
    public void DatalogEntriesMapInternalNamesToLogLabels()
    {
        // This is what lets a preset or a filter written against a recorded log
        // work unchanged against a live connection.
        IReadOnlyList<DatalogEntry> entries = MsqIni.ReadDatalog(Ini);

        Assert.Equal("RPM", Assert.Single(entries, e => e.Channel == "rpm").Label);
        Assert.Equal("CLT", Assert.Single(entries, e => e.Channel == "coolant").Label);
        Assert.Equal("Half RPM", Assert.Single(entries, e => e.Channel == "halfRpm").Label);
    }

    [Fact]
    public void DatalogPrecisionComesFromTheFormat()
    {
        IReadOnlyList<DatalogEntry> entries = MsqIni.ReadDatalog(Ini);

        Assert.Equal(0, Assert.Single(entries, e => e.Channel == "rpm").Digits);
        Assert.Equal(1, Assert.Single(entries, e => e.Channel == "map").Digits);
        Assert.Equal(2, Assert.Single(entries, e => e.Channel == "coolant").Digits);
    }

    // ----- channels that are only another channel renamed --------------------

    /// <summary>
    /// Speeduino's own idiom, verbatim from 202501.7: the throttle position is
    /// published as <c>tps</c>, aliased to <c>throttle</c>, and it is
    /// <c>throttle</c> that the front-page gauge names while the datalog records
    /// <c>tps</c>. Pairing them by name alone lost the gauge entirely.
    /// </summary>
    private const string Aliases = """
        [OutputChannels]
        ochBlockSize = 4
        ochGetCommand = "r\x00\x30%2o%2c"
        rpm      = scalar, U16,  0, "RPM", 1.000, 0.000
        tps      = scalar, U08,  2, "%",   0.500, 0.000
        throttle         = { tps }, "%"
        engineSpeed      = { rpm }
        doubleThrottle   = { tps * 2 }
        revolutionTime   = { rpm ? ( 60000.0 / rpm) : 0 }
        """;

    [Fact]
    public void AChannelThatIsAnotherChannelRenamedIsRecognised()
    {
        IReadOnlyDictionary<string, string> aliases = MsqIni.ReadAliases(Aliases);

        Assert.Equal("tps", aliases["throttle"]);
        Assert.Equal("rpm", aliases["engineSpeed"]);
    }

    [Fact]
    public void AChannelThatComputesSomethingIsNotAnAlias()
    {
        // Following one of these as though it were would put the wrong number on
        // a dial, which is worse than showing no dial at all.
        IReadOnlyDictionary<string, string> aliases = MsqIni.ReadAliases(Aliases);

        Assert.DoesNotContain("doubleThrottle", aliases.Keys);
        Assert.DoesNotContain("revolutionTime", aliases.Keys);
    }

    private static double Value(RealtimeDecoder decoder, double[] values, string name)
    {
        // Backwards, matching the decoder: a later definition of a name wins.
        for (int i = decoder.Names.Count - 1; i >= 0; i--)
            if (decoder.Names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return values[i];

        return double.NaN;
    }
}
