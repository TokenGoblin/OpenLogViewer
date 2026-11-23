using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The MaxxECU USB transport.
///
/// Deliberately hardware-free. Whether an ECU is plugged in is not something a
/// test run can arrange, so what is checked here is everything that does not
/// need one: how a device is recognised and labelled, and that the transport
/// refuses clearly rather than throwing something unhelpful from the driver.
///
/// The enumeration tests assert only that asking is safe. A machine that has
/// never had a MaxxECU has no <c>ftd2xx.dll</c> at all, and a connect menu that
/// crashed on such a machine would be a worse bug than the one it was listing.
/// </summary>
public class FtdiTransportTests
{
    [Theory]
    [InlineData("MaxxECU", "MX000000", true)]     // what a real one reports
    [InlineData("maxxecu", "MX123456", true)]     // the description is not case-sensitive
    [InlineData("", "MX000000", true)]            // serial alone is enough
    [InlineData("MaxxECU Race", "", true)]        // so is the description
    [InlineData("USB Serial Converter", "A50285BIA", false)]
    [InlineData("FT232R USB UART", "", false)]
    [InlineData("", "", false)]
    public void AMaxxEcuIsToldFromAnyOtherFtdiDevice(string description, string serial, bool expected) =>
        Assert.Equal(expected, new FtdiDevice(0, serial, description, false).IsMaxxEcu);

    [Theory]
    [InlineData("MaxxECU", "MX000000", "MaxxECU (MX000000)")]
    [InlineData("MaxxECU", "", "MaxxECU")]
    [InlineData("", "MX000000", "MX000000")]
    [InlineData("", "", "FTDI device 3")]
    public void ADeviceIsLabelledWithWhateverItReports(
        string description, string serial, string expected) =>
        Assert.Equal(expected, new FtdiDevice(3, serial, description, false).Label);

    [Fact]
    public void ListingDevicesIsSafeWithOrWithoutTheDriver()
    {
        // Either a list or an empty one, and never an exception: this runs while
        // a menu is being built.
        IReadOnlyList<FtdiDevice> all = FtdiEcuTransport.Devices();

        Assert.NotNull(all);
        Assert.All(all, d => Assert.True(d.Index >= 0));

        // And the MaxxECU list is a subset of it, by the same rule.
        Assert.All(FtdiEcuTransport.MaxxEcus(), d => Assert.True(d.IsMaxxEcu));
        Assert.All(FtdiEcuTransport.MaxxEcus(), d => Assert.Contains(d, all));
    }

    [Fact]
    public void ANonsenseBaudRateIsRefusedBeforeAnythingIsOpened() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FtdiEcuTransport(baudRate: 0));

    [Fact]
    public void ReadingOrWritingBeforeOpeningSaysSoRatherThanCrashing()
    {
        using var transport = new FtdiEcuTransport("no-such-device");

        Assert.False(transport.IsOpen);
        Assert.Throws<InvalidOperationException>(() => transport.Write([1, 2, 3]));
        Assert.Throws<InvalidOperationException>(() => transport.Read(new byte[8], TimeSpan.Zero));

        // And closing one that was never open is not an error, because that is
        // what disposing a failed connection does.
        transport.Close();
        transport.DiscardInput();
    }

    [Fact]
    public void OpeningADeviceThatIsNotThereExplainsItself()
    {
        using var transport = new FtdiEcuTransport("definitely-not-a-real-serial");

        // An IOException either way, and one that names the situation: no
        // driver, nothing plugged in, or nothing with that serial. Which of the
        // three depends on the machine, so the assertion is on the type and on
        // there being something to read.
        IOException raised = Assert.Throws<IOException>(transport.Open);

        Assert.NotEmpty(raised.Message);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public void AMaxxEcuUsbTransportIsAnOrdinaryTransport()
    {
        // It has to be, or none of the protocol above it could be shared with
        // the Bluetooth path. This is the whole design in one assertion.
        using var transport = new FtdiEcuTransport();

        Assert.IsAssignableFrom<IEcuTransport>(transport);
        Assert.Equal(FtdiEcuTransport.DefaultBaudRate, transport.BaudRate);
    }
}
