using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Reading a Subaru's own parameters over SSM.
///
/// The protocol here was not taken from a specification or from another
/// project's source. It was established by asking a running 2014 Crosstrek and
/// writing down what it said, then checked by reading values over SSM that could
/// also be read over OBD2 — engine speed landed between two OBD2 readings taken
/// either side of it, and coolant returned the identical raw byte to PID 05.
/// Those two cross-checks are reproduced here against a fake so the decode cannot
/// drift away from what the car actually returned.
/// </summary>
public class SsmTests
{
    // ----- the request ---------------------------------------------------------

    /// <summary>
    /// The padding byte after the command is not decoration. Sent without it a
    /// real ECU parses the request and refuses it for length, which is how it was
    /// found out.
    /// </summary>
    [Fact]
    public void AReadRequestCarriesTheCommandThePaddingAndThreeBytesOfAddress()
    {
        Assert.Equal("A80000000E", Ssm.ReadRequest(0x00000E));
        Assert.Equal("A800000008", Ssm.ReadRequest(0x000008));
    }

    [Fact]
    public void AnAddressBeyondThreeBytesIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Ssm.ReadRequest(0x1000000));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ssm.ReadRequest(-1));
    }

    // ----- the reply -----------------------------------------------------------

    [Fact]
    public void APositiveReplyIsTheEchoAndOneByteEach()
    {
        Assert.Equal([0x0E], Ssm.ReadReply("E80E", 1));
        Assert.Equal([0x86], Ssm.ReadReply("E886", 1));
    }

    /// <summary>
    /// A refusal is told apart from silence, because a refusal proves the vehicle
    /// read the command — which is the difference between "this car does not
    /// speak SSM" and "that address was wrong".
    /// </summary>
    [Theory]
    [InlineData("7FA813")] // length the ECU did not expect
    [InlineData("7FA812")] // sub-function it does not support
    public void ARefusalIsRecognisedAsOne(string reply)
    {
        Assert.Empty(Ssm.ReadReply(reply, 1));
        Assert.True(Ssm.Refused(reply));
    }

    [Theory]
    [InlineData("NO DATA")]
    [InlineData("?")]
    [InlineData("")]
    public void SilenceIsNotARefusal(string reply)
    {
        Assert.Empty(Ssm.ReadReply(reply, 1));
        Assert.False(Ssm.Refused(reply));
    }

    /// <summary>An answer to something else is not an answer to this.</summary>
    [Fact]
    public void AMode01ReplyIsNotMistakenForAnSsmOne() =>
        Assert.Empty(Ssm.ReadReply("410C0E2B", 1));

    // ----- what the bytes mean -------------------------------------------------

    /// <summary>
    /// The exact reading taken off the car: 0x0E36 over SSM, against OBD2
    /// readings of 906.75 and 912.25 rpm taken either side of it.
    /// </summary>
    [Fact]
    public void EngineSpeedDecodesToWhatTheCarWasDoing()
    {
        var rpm = new SsmParameter("Engine Speed", 0x00000E, Bytes: 2, Scale: 0.25);

        double reading = rpm.Read([0x0E, 0x36]);

        Assert.Equal(909.5, reading, 3);
        Assert.InRange(reading, 906.75, 912.25);
    }

    /// <summary>
    /// Big-endian, and it matters. Read the other way round the same idle comes
    /// out at 13,830 rpm — obvious on a rev counter and quietly plausible on a
    /// two-byte temperature.
    /// </summary>
    [Fact]
    public void TheHighByteIsTheOneAtTheLowerAddress()
    {
        var rpm = new SsmParameter("Engine Speed", 0x00000E, Bytes: 2, Scale: 0.25);

        Assert.Equal(909.5, rpm.Read([0x0E, 0x36]), 3);
        Assert.NotEqual(909.5, rpm.Read([0x36, 0x0E]), 3);
    }

    /// <summary>
    /// Coolant at address 0x000008 returned 0x86, the identical raw byte to OBD2
    /// PID 05 read at the same moment — so the same arithmetic must give the same
    /// answer, 94 °C.
    /// </summary>
    [Fact]
    public void CoolantMatchesWhatObd2SaidAtTheSameMoment()
    {
        var coolant = new SsmParameter("Coolant", 0x000008, Units: "°C", Offset: -40);

        const byte raw = 0x86;

        Assert.Equal(94, coolant.Read([raw]));
        Assert.Equal(raw - 40, coolant.Read([raw]));
    }

    [Fact]
    public void AParameterCoversEveryByteItSpans()
    {
        Assert.Equal([0x0E], new SsmParameter("a", 0x0E).Addresses);
        Assert.Equal([0x0E, 0x0F], new SsmParameter("a", 0x0E, Bytes: 2).Addresses);
    }

    // ----- the file the addresses come from ------------------------------------

    [Fact]
    public void TheTemplateIsAFileThisCanRead()
    {
        IReadOnlyList<SsmParameter> parameters = SsmParameterFile.Read(SsmParameterFile.Template);

        Assert.Equal(2, parameters.Count);

        SsmParameter rpm = parameters.Single(p => p.Name == "Engine Speed");

        Assert.Equal(0x00000E, rpm.Address);
        Assert.Equal(2, rpm.Bytes);
        Assert.Equal(909.5, rpm.Read([0x0E, 0x36]), 3);

        SsmParameter coolant = parameters.Single(p => p.Name == "Coolant");

        Assert.Equal(94, coolant.Read([0x86]));
    }

    [Theory]
    [InlineData("0x00000E", 0x0E)]
    [InlineData("00000E", 0x0E)]
    [InlineData("0X8", 8)]
    [InlineData("FFFFFF", 0xFFFFFF)]
    public void AddressesAreReadAsHexHoweverTheyAreWritten(string text, int expected) =>
        Assert.Equal(expected, SsmParameterFile.ParseAddress(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("1000000")] // beyond three bytes
    public void AnUnusableAddressIsRefused(string? text) =>
        Assert.Null(SsmParameterFile.ParseAddress(text));

    /// <summary>
    /// One bad entry costs that entry and not the file. Somebody filling this in
    /// by hand will get one wrong, and losing every parameter because the fourth
    /// has a typo would be a poor trade.
    /// </summary>
    [Fact]
    public void ABadEntryDoesNotCostTheGoodOnes()
    {
        const string json = """
            {
              "version": 1,
              "parameters": [
                { "name": "Good", "address": "0x000008" },
                { "name": "No address" },
                { "address": "0x00000E" },
                { "name": "Silly bytes", "address": "0x000010", "bytes": 99 },
                { "name": "Also good", "address": "0x000046" }
              ]
            }
            """;

        Assert.Equal(["Good", "Also good"], SsmParameterFile.Read(json).Select(p => p.Name));
    }

    /// <summary>
    /// Two channels with one name would share a column, and every preset and
    /// filter matching on it would find whichever came first.
    /// </summary>
    [Fact]
    public void ADuplicateNameIsDropped()
    {
        const string json = """
            {
              "parameters": [
                { "name": "Coolant", "address": "0x000008" },
                { "name": "coolant", "address": "0x000009" }
              ]
            }
            """;

        Assert.Single(SsmParameterFile.Read(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"parameters":[]}""")]
    public void AnEmptyOrBrokenFileIsNoParametersRatherThanAThrow(string json) =>
        Assert.Empty(SsmParameterFile.Read(json));

    // ----- the whole conversation ----------------------------------------------

    [Fact]
    public void ReadsTheParametersItWasGiven()
    {
        IReadOnlyList<SsmParameter> parameters = SsmParameterFile.Read(SsmParameterFile.Template);

        using SsmSource source = SsmSource.Connect(new FakeSubaru(), parameters);

        Assert.Equal(["Engine Speed", "Coolant"], source.Names);

        double[] values = source.Read();

        Assert.Equal(909.5, values[0], 3);
        Assert.Equal(94, values[1]);
    }

    /// <summary>
    /// A two-byte value is read one address at a time, because the hardware this
    /// was proven against will not carry two in one request.
    /// </summary>
    [Fact]
    public void AMultiByteValueIsAskedForAByteAtATime()
    {
        var car = new FakeSubaru();
        IReadOnlyList<SsmParameter> parameters = SsmParameterFile.Read(SsmParameterFile.Template);

        using SsmSource source = SsmSource.Connect(car, parameters);
        source.Read();

        Assert.Contains("A80000000E", car.Received);
        Assert.Contains("A80000000F", car.Received);
    }

    /// <summary>
    /// With no addresses there is nothing to do, and it says why rather than
    /// opening a session that shows an empty screen.
    /// </summary>
    [Fact]
    public void RefusesToStartWithNoParameters()
    {
        EcuProtocolException e = Assert.Throws<EcuProtocolException>(
            () => SsmSource.Connect(new FakeSubaru(), []));

        Assert.Contains(SsmParameterFile.Name, e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every address in the file may be wrong. A session that starts happily and
    /// shows a screen of dashes is worse than one that refuses and says so.
    /// </summary>
    [Fact]
    public void RefusesToStartWhenTheCarWillNotAnswerTheFirstAddress()
    {
        var car = new FakeSubaru();
        car.Memory.Clear();

        EcuProtocolException e = Assert.Throws<EcuProtocolException>(
            () => SsmSource.Connect(car, [new SsmParameter("Invented", 0x123456)]));

        Assert.Contains("did not answer", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The adapter is put back to ordinary addressing on the way out, or whatever
    /// connects next is talking to the engine module by mistake.
    /// </summary>
    [Fact]
    public void PutsTheAdapterBackWhenItIsDoneWith()
    {
        var car = new FakeSubaru();
        IReadOnlyList<SsmParameter> parameters = SsmParameterFile.Read(SsmParameterFile.Template);

        SsmSource source = SsmSource.Connect(car, parameters);
        source.Dispose();

        Assert.Contains("ATSH7DF", car.Received);
    }
}
