using System.Runtime.InteropServices;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>One FTDI device Windows can see, as D2XX describes it.</summary>
/// <param name="Index">Its position in D2XX's list, which is how it is opened.</param>
/// <param name="Serial">
/// The serial in the chip's own EEPROM — <c>MX000000</c> on a MaxxECU. Steadier
/// than the index, which renumbers as other FTDI devices come and go.
/// </param>
/// <param name="Description">The chip's product string: "MaxxECU" on a MaxxECU.</param>
/// <param name="IsOpen">True when another program already has it.</param>
public sealed record FtdiDevice(int Index, string Serial, string Description, bool IsOpen)
{
    /// <summary>True when this is a MaxxECU rather than some other FTDI device.</summary>
    public bool IsMaxxEcu =>
        Description.Contains("MaxxECU", StringComparison.OrdinalIgnoreCase)
        || Serial.StartsWith("MX", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to show in a device list.</summary>
    public string Label =>
        Description.Length > 0 && Serial.Length > 0 ? $"{Description} ({Serial})"
        : Description.Length > 0 ? Description
        : Serial.Length > 0 ? Serial
        : $"FTDI device {Index}";
}

/// <summary>
/// A MaxxECU's USB port, which is an FTDI device and not a COM port.
///
/// This is the whole reason USB needed anything new. Every other cabled ECU here
/// is reached through <see cref="SerialEcuTransport"/>, because Windows gives it
/// a COM number — and a MaxxECU never gets one. Its USB is an FTDI FT-X chip,
/// and the driver Maxxtuning ships for it (<c>oem17.inf</c>, an FTDI bus driver
/// for <c>VID_0403&amp;PID_9728</c>) installs the bus half only: there is no
/// <c>FtdiPort</c> section in the INF, no <c>ftser2k</c>, and so no virtual COM
/// port is ever created. Confirmed on a MaxxECU Race, where the device
/// enumerates as "MaxxECU" under the <c>FTDIBUS</c> service with no child port,
/// and D2XX's own <c>FT_GetComPortNumber</c> answers −1.
///
/// So the only way in is FTDI's D2XX library, which is what MTune uses too —
/// verified by watching MTune hold this device open while connected over USB.
/// That makes this a byte stream like any other, and everything above it —
/// <see cref="MaxxEcuSource"/>, the framing, the subscription — neither knows
/// nor cares which of a MaxxECU's three links it is talking over.
///
/// <c>ftd2xx.dll</c> arrives with the MaxxECU driver and with MTune, and is
/// absent on a machine that has had neither. That is reported as a missing
/// driver rather than as a missing file, because that is what it means to
/// somebody trying to connect.
/// </summary>
public sealed class FtdiEcuTransport : IEcuTransport
{
    private IntPtr _handle = IntPtr.Zero;

    /// <summary>
    /// Opens the MaxxECU by the serial in its own EEPROM where one is given, and
    /// otherwise the first MaxxECU on the machine.
    /// </summary>
    /// <param name="serial">
    /// The chip's serial, or empty for "whichever MaxxECU is plugged in". Worth
    /// naming when two are, because the index D2XX hands out depends on what was
    /// plugged in first and the serial does not.
    /// </param>
    /// <param name="baudRate">
    /// The rate the chip clocks its UART at, which has to match what the
    /// controller behind it expects.
    /// </param>
    public FtdiEcuTransport(string serial = "", int baudRate = DefaultBaudRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(baudRate, 1);

        Serial = serial ?? "";
        BaudRate = baudRate;
    }

    /// <summary>
    /// The rate to drive the FTDI's UART at.
    ///
    /// A MaxxECU's USB is an FTDI chip in front of the controller's serial port,
    /// so unlike a USB CDC device — where the rate is a fiction the host and
    /// device both ignore — this one is real and has to be right. Wrong, the
    /// link behaves exactly as though nothing were there: the bytes go out, the
    /// controller makes nothing of them, and it answers with silence or a byte
    /// or two of noise.
    /// </summary>
    public const int DefaultBaudRate = 115200;

    public string Serial { get; }

    public int BaudRate { get; }

    public bool IsOpen => _handle != IntPtr.Zero;

    /// <summary>What a write is given before the device is called dead.</summary>
    private TimeSpan _writeTimeout = TimeSpan.FromMilliseconds(500);

    public TimeSpan WriteTimeout
    {
        get => _writeTimeout;

        set
        {
            if (value <= TimeSpan.Zero) return;

            _writeTimeout = value;
            if (IsOpen) Native.FT_SetTimeouts(_handle, ReadTimeoutMs, (uint)value.TotalMilliseconds);
        }
    }

    /// <summary>
    /// The device's own read timeout, left short and deliberately not used as
    /// the caller's.
    ///
    /// <see cref="Read"/> does its own waiting against the caller's deadline, so
    /// this only bounds how long a single call into the driver may sit there.
    /// </summary>
    private const uint ReadTimeoutMs = 50;

    /// <summary>
    /// Every FTDI device on the machine.
    ///
    /// Returns nothing at all rather than failing where the library is absent:
    /// a machine with no MaxxECU driver has no MaxxECU to list, and a connect
    /// menu is not the place to explain that.
    /// </summary>
    public static IReadOnlyList<FtdiDevice> Devices()
    {
        try
        {
            if (Native.FT_CreateDeviceInfoList(out uint count) != 0 || count == 0) return [];

            var found = new List<FtdiDevice>((int)count);

            for (uint i = 0; i < count; i++)
            {
                var serial = new byte[16];
                var description = new byte[64];

                if (Native.FT_GetDeviceInfoDetail(
                        i, out uint flags, out _, out _, out _, serial, description, out _) != 0)
                    continue;

                found.Add(new FtdiDevice(
                    (int)i, Text(serial), Text(description), (flags & 1) != 0));
            }

            return found;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException)
        {
            return [];
        }

        static string Text(byte[] raw)
        {
            int end = Array.IndexOf(raw, (byte)0);
            return Encoding.ASCII.GetString(raw, 0, end < 0 ? raw.Length : end);
        }
    }

    /// <summary>Every MaxxECU on the machine, which is the list worth offering.</summary>
    public static IReadOnlyList<FtdiDevice> MaxxEcus() => [.. Devices().Where(d => d.IsMaxxEcu)];

    /// <summary>Whether the FTDI library is installed at all.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                // The answer is discarded: that the call returned at all is what
                // says the library is there to be called.
                Native.FT_CreateDeviceInfoList(out _);
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                          or BadImageFormatException)
            {
                return false;
            }
        }
    }

    public void Open()
    {
        if (IsOpen) return;

        FtdiDevice device = Find();

        uint status;

        try
        {
            status = Native.FT_Open(device.Index, out _handle);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException)
        {
            _handle = IntPtr.Zero;
            throw MissingDriver(e);
        }

        if (status != 0)
        {
            _handle = IntPtr.Zero;

            // Status 3 is D2XX's "device not opened", which on a device that is
            // plainly there almost always means something else has it. MTune
            // holds it for as long as it is connected, and two programs cannot
            // share one FTDI device the way two can share a file.
            throw new IOException(
                status == 3
                    ? $"{device.Label} is already open in another program. MTune holds the USB "
                      + "link for as long as it is connected to the ECU, so disconnect it there "
                      + "first."
                    : $"{device.Label} would not open (FTDI status {status}).");
        }

        // Everything below is the chip's UART, not USB: the FTDI is a serial
        // port in front of the controller and these are its line settings.
        Check(Native.FT_SetBaudRate(_handle, (uint)BaudRate), "set the baud rate");
        Check(Native.FT_SetDataCharacteristics(_handle, 8, 0, 0), "set 8-N-1");
        Check(Native.FT_SetFlowControl(_handle, 0, 0, 0), "turn flow control off");
        Check(Native.FT_SetTimeouts(_handle, ReadTimeoutMs, (uint)_writeTimeout.TotalMilliseconds),
            "set the timeouts");

        // Two milliseconds rather than the default sixteen. The latency timer is
        // how long the chip waits for more bytes before sending a short packet
        // up the USB, so it is dead time on the front of every reply.
        Native.FT_SetLatencyTimer(_handle, 2);

        DiscardInput();
    }

    /// <summary>The device this transport is for, or an exception saying why there is none.</summary>
    private FtdiDevice Find()
    {
        IReadOnlyList<FtdiDevice> all = Devices();

        if (all.Count == 0)
            throw new IOException(
                IsAvailable
                    ? "No FTDI device is plugged in. A MaxxECU's USB port is an FTDI device rather "
                      + "than a COM port, so it appears in no list of serial ports — check the "
                      + "cable, and that the ECU has power."
                    : "The FTDI driver is not installed, so a MaxxECU's USB port cannot be "
                      + "reached. It arrives with MTune and with the driver package MaxxECU "
                      + "publishes.");

        FtdiDevice? wanted = Serial.Length > 0
            ? all.FirstOrDefault(d => d.Serial.Equals(Serial, StringComparison.OrdinalIgnoreCase))
            : all.FirstOrDefault(d => d.IsMaxxEcu) ?? all.FirstOrDefault();

        return wanted ?? throw new IOException(
            Serial.Length > 0
                ? $"No FTDI device with serial {Serial} is plugged in. Present: "
                  + string.Join(", ", all.Select(d => d.Label)) + "."
                : "No MaxxECU is plugged in.");
    }

    private static IOException MissingDriver(Exception inner) =>
        new("ftd2xx.dll could not be loaded, so a MaxxECU's USB port cannot be reached. It is "
            + "installed by MTune and by the MaxxECU USB driver package.", inner);

    private void Check(uint status, string what)
    {
        if (status == 0) return;

        Close();
        throw new IOException($"The MaxxECU's USB device refused to {what} (FTDI status {status}).");
    }

    public void Close()
    {
        if (_handle == IntPtr.Zero) return;

        try
        {
            Native.FT_Close(_handle);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException)
        {
            // A device that has gone cannot be closed politely.
        }
        finally
        {
            _handle = IntPtr.Zero;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!IsOpen) throw new InvalidOperationException("The MaxxECU's USB device is not open.");
        if (data.Length == 0) return;

        byte[] buffer = data.ToArray();
        uint status = Native.FT_Write(_handle, buffer, (uint)buffer.Length, out uint written);

        if (status != 0)
            throw new IOException($"The MaxxECU's USB device refused a write (FTDI status {status}).");

        // A short write is not a slow one. The driver takes the whole buffer or
        // the device has gone, and carrying on with half a frame sent puts the
        // controller's parser out of step with no way back.
        if (written != buffer.Length)
            throw new IOException(
                $"Only {written} of {buffer.Length} bytes reached the MaxxECU's USB device.");
    }

    /// <summary>
    /// Fills the buffer, or returns fewer bytes if the timeout passes first —
    /// the same contract the serial transport keeps.
    ///
    /// The driver's own read timeout is left short and the deadline is kept
    /// here, so a caller asking for a large buffer with a small allowance is not
    /// held for the driver's timeout instead of its own.
    /// </summary>
    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (!IsOpen) throw new InvalidOperationException("The MaxxECU's USB device is not open.");
        if (buffer.Length == 0) return 0;

        int total = 0;
        DateTime deadline = DateTime.UtcNow + timeout;
        var chunk = new byte[buffer.Length];

        while (total < buffer.Length && DateTime.UtcNow < deadline)
        {
            uint status = Native.FT_GetQueueStatus(_handle, out uint waiting);

            if (status != 0)
                throw new IOException(
                    $"The MaxxECU's USB device stopped answering (FTDI status {status}).");

            if (waiting == 0)
            {
                // Nothing yet. A short sleep rather than a spin, and shorter than
                // Windows' timer tick is not worth asking for.
                Thread.Sleep(1);
                continue;
            }

            uint wanted = Math.Min(waiting, (uint)(buffer.Length - total));
            status = Native.FT_Read(_handle, chunk, wanted, out uint read);

            if (status != 0)
                throw new IOException(
                    $"The MaxxECU's USB device failed a read (FTDI status {status}).");

            chunk.AsSpan(0, (int)read).CopyTo(buffer[total..]);
            total += (int)read;
        }

        return total;
    }

    /// <summary>
    /// Drops anything buffered, in both directions.
    ///
    /// Failing is not worth reporting: this is hygiene before a request, and a
    /// device that cannot be cleared will say so again on the read that follows.
    /// </summary>
    public void DiscardInput()
    {
        try
        {
            if (IsOpen) Native.FT_Purge(_handle, 3);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                      or BadImageFormatException)
        {
        }
    }

    public void Dispose() => Close();

    /// <summary>
    /// FTDI's D2XX entry points, as much of them as a byte stream needs.
    ///
    /// Declared here rather than taken from FTDI's own .NET wrapper so that
    /// nothing has to be shipped alongside: the library itself comes with the
    /// driver, and a machine with a MaxxECU plugged into it has one.
    /// </summary>
    private static class Native
    {
        private const string Library = "ftd2xx.dll";

        [DllImport(Library)]
        internal static extern uint FT_CreateDeviceInfoList(out uint count);

        [DllImport(Library)]
        internal static extern uint FT_GetDeviceInfoDetail(
            uint index, out uint flags, out uint type, out uint id, out uint location,
            byte[] serial, byte[] description, out IntPtr handle);

        [DllImport(Library)]
        internal static extern uint FT_Open(int index, out IntPtr handle);

        [DllImport(Library)]
        internal static extern uint FT_Close(IntPtr handle);

        [DllImport(Library)]
        internal static extern uint FT_SetBaudRate(IntPtr handle, uint baudRate);

        [DllImport(Library)]
        internal static extern uint FT_SetDataCharacteristics(
            IntPtr handle, byte bits, byte stopBits, byte parity);

        [DllImport(Library)]
        internal static extern uint FT_SetFlowControl(
            IntPtr handle, ushort flow, byte xon, byte xoff);

        [DllImport(Library)]
        internal static extern uint FT_SetTimeouts(IntPtr handle, uint read, uint write);

        [DllImport(Library)]
        internal static extern uint FT_SetLatencyTimer(IntPtr handle, byte milliseconds);

        [DllImport(Library)]
        internal static extern uint FT_Purge(IntPtr handle, uint mask);

        [DllImport(Library)]
        internal static extern uint FT_Write(
            IntPtr handle, byte[] buffer, uint count, out uint written);

        [DllImport(Library)]
        internal static extern uint FT_Read(
            IntPtr handle, byte[] buffer, uint count, out uint read);

        [DllImport(Library)]
        internal static extern uint FT_GetQueueStatus(IntPtr handle, out uint waiting);
    }
}
