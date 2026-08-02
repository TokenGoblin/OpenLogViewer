using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Telling Windows' two Bluetooth serial ports apart.
///
/// Pairing a serial-port-profile device produces an outgoing port bound to that
/// device and an incoming one that waits to be dialled. They carry the same
/// name, the same description and the same driver, and sit next to each other in
/// any list — but only one of them can reach an ECU. Choosing the other used to
/// hang for the write timeout and then take the application down.
///
/// The device ids here are the real ones from a paired JDY-33.
/// </summary>
public class SerialPortNameTests
{
    private const string Outgoing =
        @"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&001D\9&3058B9ED&0&01B6EC10F00D_C00000000";

    private const string Incoming =
        @"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\9&3058B9ED&0&000000000000_00000000";

    [Fact]
    public void TheOutgoingPortCarriesTheDevicesAddress() =>
        Assert.False(SerialPortNames.IsIncoming(Outgoing));

    [Fact]
    public void TheIncomingPortCarriesNoAddressAtAll() =>
        Assert.True(SerialPortNames.IsIncoming(Incoming));

    [Fact]
    public void AUsbPortIsNotMistakenForEither()
    {
        // Read as "has no remote address" rather than by matching a shape, so
        // anything that is not a Bluetooth instance id simply is not incoming.
        Assert.False(SerialPortNames.IsIncoming(@"USB\VID_0483&PID_5740&MI_01\7&9DDFB75&0&0001"));
        Assert.False(SerialPortNames.IsIncoming(@"FTDIBUS\VID_0403+PID_6001+A50285BIA\0000"));
    }

    [Fact]
    public void SomethingUnrecognisableIsOffered()
    {
        // A wrong guess must not be able to hide a port; erring towards offering
        // one costs a message, erring the other way costs the only way in.
        Assert.False(SerialPortNames.IsIncoming(""));
        Assert.False(SerialPortNames.IsIncoming("BTHENUM"));
        Assert.False(SerialPortNames.IsIncoming(@"BTHENUM\odd\9&3058B9ED&0&"));
    }

    // ----- naming the port ---------------------------------------------------

    [Fact]
    public void AnEcuThatHasAnsweredIsNamedAheadOfTheChip()
    {
        // Windows calls a Speeduino "Arduino Mega 2560", which names the chip the
        // firmware happens to run on. Nobody hunting for their ECU in a list is
        // looking for that.
        var port = new SerialPortInfo("COM14", "Arduino Mega 2560", IsBluetooth: false)
        {
            KnownEcu = "Speeduino 2025.01.7",
        };

        Assert.Equal("COM14 — Speeduino 2025.01.7", port.Label);
    }

    [Fact]
    public void WindowsDescriptionIsUsedUntilSomethingHasAnswered()
    {
        var port = new SerialPortInfo("COM14", "Arduino Mega 2560", IsBluetooth: false);

        Assert.Equal("COM14 — Arduino Mega 2560", port.Label);
    }

    [Fact]
    public void ABluetoothDeviceStillNamesItselfWhenNoEcuHasAnswered()
    {
        var port = new SerialPortInfo("COM10", "Standard Serial over Bluetooth link", IsBluetooth: true)
        {
            DeviceName = "MaxxECU_12345",
        };

        Assert.Equal("COM10 — MaxxECU_12345 (Bluetooth)", port.Label);
    }

    [Fact]
    public void WhatAnsweredSurvivesBeingSavedAndRestored()
    {
        // Worth remembering between sessions precisely because it is wanted
        // before connecting — having to connect once to find out what is on a
        // port defeats the point of labelling it.
        SerialPortNames.Recall(new Dictionary<string, string>
        {
            [@"USB\VID_2341&PID_0042\95730333837351C01221"] = "Speeduino 2025.01.7",
        });

        Assert.Equal(
            "Speeduino 2025.01.7",
            SerialPortNames.Remembered()[@"usb\vid_2341&pid_0042\95730333837351C01221"]);
    }
}
