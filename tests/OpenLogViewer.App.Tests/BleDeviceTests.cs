namespace OpenLogViewer.App.Tests;

/// <summary>
/// Finding a Bluetooth LE adapter in the Windows device tree.
///
/// The instance ids here are the real ones from a paired OBDII dongle, which is
/// the hardware this was written against.
/// </summary>
public class BleDeviceTests
{
    private const string Obdii = @"BTHLE\DEV_B96975AA93F6\9&306AB8F&0&B96975AA93F6";

    [Fact]
    public void TheRadioAddressIsReadFromTheInstanceId()
    {
        // It is what the Bluetooth APIs want in order to reach the device, and
        // the device tree is the only place it is written down.
        Assert.Equal(0xB96975AA93F6UL, BleDevices.AddressIn(Obdii));
    }

    [Fact]
    public void SomethingThatIsNotABleDeviceHasNoAddress()
    {
        Assert.Null(BleDevices.AddressIn(@"USB\VID_2341&PID_0042\95730333837351C01221"));
        Assert.Null(BleDevices.AddressIn(
            @"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&001D\9&3058B9ED&0&01B6EC10F00D_C00000000"));
        Assert.Null(BleDevices.AddressIn(""));
        Assert.Null(BleDevices.AddressIn(@"BTHLE\DEV_TOOSHORT"));
    }

    [Fact]
    public void AnAdapterIsRecognisedByWhatItAdvertisesAs()
    {
        // A guess, and the only one available: BLE publishes no profile that
        // says "I am an ELM327" — the serial services these use are vendor
        // numbers meaning whatever the maker decided.
        Assert.True(new BleDevice("OBDII", 1).IsObd2);
        Assert.True(new BleDevice("Vgate iCar Pro", 1).IsObd2);
        Assert.True(new BleDevice("vLinker MC+", 1).IsObd2);

        Assert.False(new BleDevice("MaxxECU_28xf7p", 1).IsObd2);
        Assert.False(new BleDevice("HP 720 Wireless Mouse", 1).IsObd2);
    }

    [Fact]
    public void TheRadioIsNamedSoTheEntryIsNotMistakenForAPort()
    {
        // It sits in the same menu as the COM ports and behaves the same way, so
        // the one thing worth saying is why it never appeared as one.
        Assert.Equal("OBDII (Bluetooth LE)", new BleDevice("OBDII", 1).Label);
    }

    [Fact]
    public void TheServiceThatWorkedInPracticeIsTriedFirst()
    {
        // No standard exists for a serial bridge over BLE, so each maker picked a
        // vendor service. 0xFFF0 is what the clones overwhelmingly use, and what
        // the dongle this was verified against answers on.
        Assert.Equal(
            new Guid("0000fff0-0000-1000-8000-00805f9b34fb"),
            BleEcuTransport.SerialServices[0]);

        // 0xAE00 is second because the same dongle publishes it and never
        // answers on it — which is why the code proves a service before using it.
        Assert.Contains(new Guid("0000ae00-0000-1000-8000-00805f9b34fb"), BleEcuTransport.SerialServices);
    }
}
