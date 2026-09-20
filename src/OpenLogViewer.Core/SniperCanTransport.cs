using System.Runtime.InteropServices;

namespace OpenLogViewer.Core;

/// <summary>
/// One CAN frame as the Sniper dongle's own wire record carries it.
///
/// Not <see cref="CanFrame"/>: that type carries an arrival timestamp and an
/// extended/standard flag, neither of which this dongle's 20-byte USB record
/// has room for (offset 0: 4-byte "USBC" magic; offset 4: this 16-byte
/// <c>can_frame</c> — <c>uint32 can_id; uint32 dlc; uint8 data[8];</c> — and
/// nothing else). Every id this protocol uses is 29-bit extended, so there is
/// no flag to carry either. Source: <c>SNIPER_ECU_CLIENT_SPEC.md</c> §1-2.
/// </summary>
/// <param name="CanId">The 29-bit extended identifier, held in the low bits of a uint.</param>
/// <param name="Data">The payload, 0-8 bytes — exactly <c>Dlc</c> bytes, not padded.</param>
public sealed record SniperCanFrame(uint CanId, byte[] Data)
{
    /// <summary>What the wire's <c>dlc</c> field says: how many bytes are real.</summary>
    public int Dlc => Data.Length;
}

/// <summary>
/// A live CAN link to a Sniper dongle: send a frame, receive whatever arrives.
///
/// <para>
/// Deliberately not <see cref="ICanSource"/> — that interface is a read-only
/// sniffer's shape (a MaxxECU relaying a bus it does not itself speak on), and
/// this dongle is the opposite: everything above this talks <i>through</i> it
/// to a specific ECU, so sending is as central as receiving.
/// </para>
/// <para>
/// <b>Never exposes a way to enter the dongle's firmware-flash bootloader.</b>
/// The vendor control request that does that (<c>bRequest</c> 0x03 then 0x04)
/// is real and reachable on the same USB interface as everything here, but is
/// not implemented anywhere in this type or anything built on it — see
/// <c>SNIPER_ECU_CLIENT_SPEC.md</c> §1/§9: it can brick the dongle, and there
/// is no legitimate reason this application would ever need it.
/// </para>
/// </summary>
public interface ISniperCanTransport : IDisposable
{
    bool IsOpen { get; }

    void Open();

    void Close();

    /// <summary>Sends one frame on the bus.</summary>
    void Send(SniperCanFrame frame);

    /// <summary>
    /// The next frame to arrive, or null if none did within <paramref name="timeout"/>.
    /// </summary>
    SniperCanFrame? Receive(TimeSpan timeout);

    /// <summary>
    /// Sets the CAN bus bit rate. 🟡 <c>SNIPER_ECU_CLIENT_SPEC.md</c> §4 says the
    /// firmware-flash path uses 1 Mbit and calls it "the most likely bus rate",
    /// not a confirmed one — <b>[CONFIRM ON HW]</b>.
    /// </summary>
    void SetBitrate(uint bitsPerSecond);

    /// <summary>The dongle's own firmware version, mostly useful as a "did this even open" check.</summary>
    uint GetHardwareVersion();
}

/// <summary>
/// Reaches a Holley Sniper USB→CAN dongle over WinUSB, directly — the same
/// "talk to the real device rather than wrap the vendor's own tool" choice
/// <see cref="FtdiEcuTransport"/> made for a MaxxECU's FTDI chip.
///
/// <para>
/// <b>Why not the vendor's own <c>USBCAN-Driver.dll</c></b> (path A in
/// <c>SNIPER_ECU_CLIENT_SPEC.md</c> §6): that DLL is 32-bit only, and this
/// application is built x64 — <see cref="FtdiEcuTransport"/> already P/Invokes
/// the 64-bit <c>ftd2xx64.dll</c>, confirming the process's own bitness.
/// Loading a 32-bit DLL from a 64-bit process is not possible at all without
/// an out-of-process bridge, which has no precedent anywhere in this codebase
/// and is not worth building when path B — direct WinUSB — needs only
/// <c>winusb.dll</c> and <c>setupapi.dll</c>, both ordinary bitness-agnostic
/// Windows system components.
/// </para>
/// <para>
/// <b>Nothing here has been run against real hardware.</b> This is built
/// entirely from <c>SNIPER_ECU_CLIENT_SPEC.md</c>, itself static analysis of
/// Holley's own software with no hardware used to produce it. Every value and
/// every step below that the spec itself marks 🟡 (inferred) or 🔴
/// (unknown, <b>[CONFIRM ON HW]</b>) is called out at the point it is used —
/// this type does not upgrade a guess into a fact by implementing it
/// confidently. Treat a successful <see cref="Open"/> as "the USB layer
/// answered", not as "this dongle and this code agree on anything above USB".
/// </para>
/// </summary>
public sealed class SniperUsbTransport : ISniperCanTransport
{
    /// <summary>The dongle's USB vendor and product id. <c>SNIPER_ECU_CLIENT_SPEC.md</c> §1. ✅</summary>
    public const int VendorId = 0x2AD0;

    public const int ProductId = 0x1005;

    /// <summary>
    /// The device-interface class GUID WinUSB registers this dongle under —
    /// what <c>SetupDiGetClassDevs</c> is asked for. §1. ✅
    /// </summary>
    private static readonly Guid DeviceInterfaceGuid = new("abe07f2b-a951-4941-94a4-52adaa1fa53e");

    /// <summary>The 4-byte ASCII marker opening every USB record, both directions. §1. ✅</summary>
    private const uint UsbRecordMagic = 0x43425355; // "USBC", little-endian on the wire

    /// <summary>Magic(4) + can_frame(16). §1. ✅</summary>
    private const int RecordSize = 20;

    private IntPtr _deviceHandle = IntPtr.Zero;
    private IntPtr _winUsbHandle = IntPtr.Zero;
    private byte _pipeIn;
    private byte _pipeOut;

    /// <summary>
    /// Bytes read off the IN pipe but not yet resolved into a frame — RX has to
    /// resynchronize on the "USBC" marker because USB packet boundaries do not
    /// align to 20-byte records (§1), so leftover bytes from one read carry
    /// over to the next.
    /// </summary>
    private readonly List<byte> _rxBuffer = [];

    public bool IsOpen => _winUsbHandle != IntPtr.Zero;

    public void Open()
    {
        if (IsOpen) return;

        string? path = FindDevicePath();

        if (path is null)
            throw new IOException(
                $"No Sniper dongle found (USB VID_{VendorId:X4}&PID_{ProductId:X4}). Check it is "
                + "plugged in — this is the USB→CAN dongle, not the ECU itself.");

        _deviceHandle = Native.CreateFile(
            path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

        if (_deviceHandle == Native.InvalidHandle)
            throw new IOException(
                $"The Sniper dongle's device file would not open (Win32 error {Marshal.GetLastWin32Error()}).");

        if (!Native.WinUsb_Initialize(_deviceHandle, out _winUsbHandle))
        {
            int error = Marshal.GetLastWin32Error();
            Native.CloseHandle(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
            throw new IOException($"WinUSB would not initialise on the Sniper dongle (Win32 error {error}).");
        }

        if (!FindPipes())
        {
            Close();
            throw new IOException("The Sniper dongle did not present the two bulk pipes this protocol expects.");
        }

        // §1: RX pipe policies AUTO_FLUSH(6)=TRUE and RAW_IO(7)=TRUE.
        SetPipePolicy(_pipeIn, policyType: 6, value: true);
        SetPipePolicy(_pipeIn, policyType: 7, value: true);

        _rxBuffer.Clear();
    }

    /// <summary>
    /// Walks the device-interface list WinUSB registers this dongle under and
    /// returns the first one whose hardware id matches <see cref="VendorId"/>/
    /// <see cref="ProductId"/>.
    /// </summary>
    private static string? FindDevicePath()
    {
        IntPtr deviceInfoSet = Native.SetupDiGetClassDevs(
            ref DeviceInterfaceGuidMutable, null, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == Native.InvalidHandle) return null;

        try
        {
            var interfaceData = new Native.SP_DEVICE_INTERFACE_DATA();
            interfaceData.cbSize = Marshal.SizeOf(interfaceData);

            for (uint index = 0;
                 Native.SetupDiEnumDeviceInterfaces(
                     deviceInfoSet, IntPtr.Zero, ref DeviceInterfaceGuidMutable, index, ref interfaceData);
                 index++)
            {
                string? path = DetailFor(deviceInfoSet, ref interfaceData);

                // The dongle's hardware id shows up in its device path, the way
                // every WinUSB device's does — e.g. "...vid_2ad0&pid_1005...".
                if (path is { } p
                    && p.Contains($"vid_{VendorId:x4}", StringComparison.OrdinalIgnoreCase)
                    && p.Contains($"pid_{ProductId:x4}", StringComparison.OrdinalIgnoreCase))
                    return p;
            }

            return null;
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    // SetupDiEnumDeviceInterfaces takes the GUID by ref, and .NET will not take
    // the address of a static readonly field directly - mirrored into a mutable
    // one at the call sites that need a ref.
    private static Guid DeviceInterfaceGuidMutable = DeviceInterfaceGuid;

    private static string? DetailFor(IntPtr deviceInfoSet, ref Native.SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        Native.SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out int required, IntPtr.Zero);

        if (required == 0) return null;

        IntPtr detail = Marshal.AllocHGlobal(required);

        try
        {
            // The struct's first field is a DWORD cbSize; on 64-bit this is
            // followed by padding before the inline char array begins.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);

            if (!Native.SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, detail, required, out _, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    /// <summary>Finds the one bulk-in and one bulk-out pipe §1 says this interface presents.</summary>
    private bool FindPipes()
    {
        if (!Native.WinUsb_QueryInterfaceSettings(_winUsbHandle, 0, out Native.USB_INTERFACE_DESCRIPTOR ifaceDesc))
            return false;

        bool foundIn = false, foundOut = false;

        for (byte i = 0; i < ifaceDesc.bNumEndpoints; i++)
        {
            if (!Native.WinUsb_QueryPipe(_winUsbHandle, 0, i, out Native.WINUSB_PIPE_INFORMATION pipe))
                continue;

            // Direction bit (0x80) selects role: bulk IN = RX, bulk OUT = TX. §1.
            bool isIn = (pipe.PipeId & 0x80) != 0;
            bool isBulk = pipe.PipeType == Native.UsbdPipeTypeBulk;

            if (!isBulk) continue;

            if (isIn && !foundIn) { _pipeIn = pipe.PipeId; foundIn = true; }
            else if (!isIn && !foundOut) { _pipeOut = pipe.PipeId; foundOut = true; }
        }

        return foundIn && foundOut;
    }

    private void SetPipePolicy(byte pipeId, uint policyType, bool value)
    {
        byte raw = (byte)(value ? 1 : 0);
        Native.WinUsb_SetPipePolicy(_winUsbHandle, pipeId, policyType, 1, ref raw);
    }

    public void Close()
    {
        if (_winUsbHandle != IntPtr.Zero)
        {
            Native.WinUsb_Free(_winUsbHandle);
            _winUsbHandle = IntPtr.Zero;
        }

        if (_deviceHandle != IntPtr.Zero)
        {
            Native.CloseHandle(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
        }

        _rxBuffer.Clear();
    }

    public void Send(SniperCanFrame frame)
    {
        if (!IsOpen) throw new InvalidOperationException("The Sniper dongle is not open.");
        if (frame.Dlc > 8) throw new ArgumentOutOfRangeException(nameof(frame), "A CAN frame carries at most 8 bytes.");

        byte[] record = new byte[RecordSize];
        BitConverterLittleEndian.WriteUInt32(record, 0, UsbRecordMagic);
        BitConverterLittleEndian.WriteUInt32(record, 4, frame.CanId);
        BitConverterLittleEndian.WriteUInt32(record, 8, (uint)frame.Dlc);
        frame.Data.CopyTo(record, 12);

        if (!Native.WinUsb_WritePipe(_winUsbHandle, _pipeOut, record, (uint)record.Length, out uint written, IntPtr.Zero)
            || written != record.Length)
            throw new IOException($"The Sniper dongle refused a write (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    public SniperCanFrame? Receive(TimeSpan timeout)
    {
        if (!IsOpen) throw new InvalidOperationException("The Sniper dongle is not open.");

        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (TryExtractFrame(out SniperCanFrame? frame)) return frame;
            if (DateTime.UtcNow >= deadline) return null;

            var chunk = new byte[512];

            // WinUsb_ReadPipe blocks until at least something arrives or the
            // pipe's own timeout — there is no partial-timeout parameter here,
            // so the outer deadline is only checked between reads, same
            // tradeoff FtdiEcuTransport.Read makes with its own polling loop.
            if (Native.WinUsb_ReadPipe(_winUsbHandle, _pipeIn, chunk, (uint)chunk.Length, out uint read, IntPtr.Zero)
                && read > 0)
            {
                _rxBuffer.AddRange(chunk.AsSpan(0, (int)read).ToArray());
            }
        }
    }

    /// <summary>Resynchronizes on the "USBC" marker and pulls out one complete record, if there is one. §1.</summary>
    private bool TryExtractFrame(out SniperCanFrame? frame)
    {
        frame = null;

        while (_rxBuffer.Count >= 4)
        {
            uint candidate = BitConverterLittleEndian.ReadUInt32(_rxBuffer, 0);

            if (candidate != UsbRecordMagic)
            {
                _rxBuffer.RemoveAt(0);
                continue;
            }

            if (_rxBuffer.Count < RecordSize) return false;

            uint canId = BitConverterLittleEndian.ReadUInt32(_rxBuffer, 4);
            uint dlc = BitConverterLittleEndian.ReadUInt32(_rxBuffer, 8);
            int dataLength = (int)Math.Min(dlc, 8);

            byte[] data = _rxBuffer.GetRange(12, dataLength).ToArray();
            _rxBuffer.RemoveRange(0, RecordSize);

            frame = new SniperCanFrame(canId, data);
            return true;
        }

        return false;
    }

    public void SetBitrate(uint bitsPerSecond)
    {
        if (!IsOpen) throw new InvalidOperationException("The Sniper dongle is not open.");

        byte[] payload = BitConverter.GetBytes(bitsPerSecond);

        // bmRequestType 0x22 (OUT, class, endpoint recipient), bRequest 0x02. §1.
        if (!ControlTransfer(requestType: 0x22, request: 0x02, value: 0, index: 0, payload))
            throw new IOException($"SetSpeed was refused by the Sniper dongle (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    public uint GetHardwareVersion()
    {
        if (!IsOpen) throw new InvalidOperationException("The Sniper dongle is not open.");

        byte[] buffer = new byte[4];

        // bmRequestType 0xA2 (IN, class, endpoint recipient), bRequest 0x00. §1.
        if (!ControlTransfer(requestType: 0xA2, request: 0x00, value: 0, index: 0, buffer))
            throw new IOException($"GetHardwareVersion was refused by the Sniper dongle (Win32 error {Marshal.GetLastWin32Error()}).");

        return BitConverter.ToUInt32(buffer);
    }

    /// <summary>
    /// One vendor control request. §1 gives every request this dongle answers —
    /// <b>and one it must never be asked</b>: bRequest 0x03 followed by 0x04
    /// enters the dongle's firmware-flash bootloader. That sequence is not a
    /// parameter this method accepts a path to reach; there is no caller of
    /// this method anywhere in this codebase that passes it, and none should
    /// ever be added.
    /// </summary>
    private bool ControlTransfer(byte requestType, byte request, ushort value, ushort index, byte[] buffer)
    {
        var setup = new Native.WINUSB_SETUP_PACKET
        {
            RequestType = requestType,
            Request = request,
            Value = value,
            Index = index,
            Length = (ushort)buffer.Length,
        };

        return Native.WinUsb_ControlTransfer(_winUsbHandle, setup, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
    }

    public void Dispose() => Close();

    private static class Native
    {
        internal const int GENERIC_READ = unchecked((int)0x80000000);
        internal const int GENERIC_WRITE = 0x40000000;
        internal const int FILE_SHARE_READ = 0x1;
        internal const int FILE_SHARE_WRITE = 0x2;
        internal const int OPEN_EXISTING = 3;
        internal const int FILE_ATTRIBUTE_NORMAL = 0x80;

        /// <summary>
        /// WinUsb_Initialize requires a handle opened for overlapped I/O and
        /// fails with ERROR_INVALID_HANDLE (6) without it — found on a real
        /// Holley USBCAN dongle, which enumerated and opened fine and then
        /// refused to initialise. The pipe calls still pass a null OVERLAPPED
        /// and so stay synchronous; WinUSB does the waiting itself.
        /// </summary>
        internal const int FILE_FLAG_OVERLAPPED = 0x40000000;
        internal const uint DIGCF_PRESENT = 0x2;
        internal const uint DIGCF_DEVICEINTERFACE = 0x10;
        internal const int UsbdPipeTypeBulk = 2;

        internal static readonly IntPtr InvalidHandle = new(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(
            string filename, int access, int share, IntPtr securityAttributes,
            int creationDisposition, int flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData,
            IntPtr detailData, int detailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_Initialize(IntPtr deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_QueryInterfaceSettings(
            IntPtr interfaceHandle, byte altSettingNumber, out USB_INTERFACE_DESCRIPTOR descriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_QueryPipe(
            IntPtr interfaceHandle, byte altSettingNumber, byte pipeIndex, out WINUSB_PIPE_INFORMATION pipe);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_SetPipePolicy(
            IntPtr interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref byte value);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_ReadPipe(
            IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_WritePipe(
            IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinUsb_ControlTransfer(
            IntPtr interfaceHandle, WINUSB_SETUP_PACKET setupPacket, byte[] buffer, uint bufferLength,
            out uint lengthTransferred, IntPtr overlapped);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct WINUSB_SETUP_PACKET
        {
            public byte RequestType;
            public byte Request;
            public ushort Value;
            public ushort Index;
            public ushort Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength;
            public byte bDescriptorType;
            public byte bInterfaceNumber;
            public byte bAlternateSetting;
            public byte bNumEndpoints;
            public byte bInterfaceClass;
            public byte bInterfaceSubClass;
            public byte bInterfaceProtocol;
            public byte iInterface;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINUSB_PIPE_INFORMATION
        {
            public int PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }
    }
}

/// <summary>Little-endian word helpers over a growable byte buffer, for the USB record framing.</summary>
internal static class BitConverterLittleEndian
{
    internal static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);

    internal static uint ReadUInt32(List<byte> buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
}
