using System.Diagnostics;
using System.IO;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// A Bluetooth Low Energy adapter, presented as a byte stream.
///
/// Many OBD2 dongles — and by now most cheap ones — are BLE rather than
/// Bluetooth Classic. BLE has no serial port profile, so these never become a
/// COM port however long you wait for one, which is a confusing way for a
/// working dongle to fail.
///
/// What they do instead is carry the same ASCII ELM327 conversation over two
/// GATT characteristics: bytes written to one, replies arriving as notifications
/// on the other. That is a byte stream in all but name, so it is presented as
/// one — <see cref="Elm327"/> then neither knows nor cares which radio it is
/// talking over.
///
/// Verified against an "OBDII" dongle on a live vehicle.
/// </summary>
public sealed class BleEcuTransport : IEcuTransport
{
    private readonly ulong _address;
    private readonly Queue<byte> _incoming = new();
    private readonly Lock _gate = new();

    /// <summary>Raised as bytes arrive, so a read waits rather than polls.</summary>
    private readonly SemaphoreSlim _arrived = new(0);

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _write;
    private GattCharacteristic? _notify;

    /// <summary>
    /// How many times to try opening.
    ///
    /// Windows keeps a GATT session alive for a while after the process that
    /// made it has gone, and while it lingers the services still list but their
    /// characteristics come back empty — so a second attempt a moment later
    /// succeeds where the first could not. Reconnecting after a session is the
    /// ordinary case, not the exception.
    /// </summary>
    private const int OpenAttempts = 3;

    public BleEcuTransport(ulong address, string name = "")
    {
        _address = address;
        Name = name;
    }

    /// <summary>What the adapter advertises as, for messages.</summary>
    public string Name { get; }

    public bool IsOpen => _write is not null && _notify is not null;

    /// <summary>
    /// Services known to carry an ELM327 conversation, in the order worth trying.
    ///
    /// There is no standard for this — BLE defines no serial profile, so each
    /// maker picked a vendor service and the clones copied whichever they were
    /// based on. A dongle commonly advertises more than one and answers on only
    /// one of them: the adapter this was written against publishes both, and
    /// 0xAE00 accepts every write and never replies. So the list is tried in
    /// order and the first that actually answers is the one used.
    /// </summary>
    public static IReadOnlyList<Guid> SerialServices { get; } =
    [
        Uuid(0xFFF0),   // Vgate, Viecar, and most of the cheap clones
        Uuid(0xAE00),   // LeLink and Carista
        Uuid(0xFFE0),   // HM-10 modules, and anything built on one
        new("6e400001-b5a3-f393-e0a9-e50e24dcca9e"),   // Nordic UART
    ];

    /// <summary>A 16-bit Bluetooth UUID in its full 128-bit form.</summary>
    private static Guid Uuid(ushort assigned) =>
        new($"0000{assigned:x4}-0000-1000-8000-00805f9b34fb");

    public void Open()
    {
        if (IsOpen) return;

        // On the thread pool, always. Awaiting a WinRT operation from the UI
        // thread resumes on that same thread, and this is called from a click
        // handler — waiting on the result there deadlocks the window rather than
        // connecting.
        Task.Run(OpenWithRetriesAsync).GetAwaiter().GetResult();
    }

    private async Task OpenWithRetriesAsync()
    {
        for (int attempt = 1; attempt < OpenAttempts; attempt++)
        {
            try
            {
                await OpenAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException e)
            {
                Debug.WriteLine($"BLE attempt {attempt} failed: {e.Message}");

                Close();
                await Task.Delay(1500).ConfigureAwait(false);
            }
        }

        await OpenAsync().ConfigureAwait(false);
    }

    private async Task OpenAsync()
    {
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address).AsTask().ConfigureAwait(false)
            ?? throw new IOException(
                $"{Describe()} could not be reached. A paired Bluetooth device stays listed "
                + "whether or not it is powered, so check the adapter is in the socket and the "
                + "ignition is on.");

        // Uncached: the services are read from the device rather than from what
        // Windows saw last time. A cached answer from a previous pairing lists
        // characteristics that cannot be subscribed to.
        GattDeviceServicesResult services =
            await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);

        if (services.Status != GattCommunicationStatus.Success)
            throw new IOException(
                $"{Describe()} did not list its services ({services.Status}). "
                + "If it is already connected to a phone, disconnect it there first — "
                + "these adapters accept one connection at a time.");

        try
        {
            foreach (Guid wanted in SerialServices)
            {
                GattDeviceService? service = services.Services.FirstOrDefault(s => s.Uuid == wanted);
                if (service is null) continue;

                if (await TryUseAsync(service).ConfigureAwait(false))
                {
                    _service = service;
                    return;
                }
            }

            throw new IOException(
                $"{Describe()} is connected, but none of its services answered as an ELM327. "
                + "If a session was open a moment ago, the previous connection may still be "
                + "letting go — wait a few seconds and try again. If it is connected to a "
                + "phone, disconnect it there first; these accept one connection at a time.\n\n"
                + $"It publishes {services.Services.Count} service(s): "
                + string.Join(", ", services.Services.Select(s => s.Uuid)));
        }
        finally
        {
            // Every service not being used is let go of here, and the one that
            // is gets let go of in Close.
            //
            // These are handles on the connection, not descriptions of it.
            // Leaving them undisposed keeps the link open after the process that
            // opened it has exited — and while it lingers the services still
            // list but their characteristics come back empty, so the next
            // connection finds a device that is "connected" and answers nothing.
            // That is not a hypothetical: it cost two dead sessions and a dongle
            // that had to be left alone before it would answer again.
            foreach (GattDeviceService other in services.Services)
                if (!ReferenceEquals(other, _service))
                    other.Dispose();
        }
    }

    /// <summary>
    /// Subscribes to a candidate service, and proves it by asking it something.
    ///
    /// Proving matters. A dongle that publishes two serial services accepts
    /// writes on both and answers on one, so a subscription that succeeds is no
    /// evidence at all — without asking, the silent one gets picked half the time
    /// and the session opens onto a dongle that never says anything.
    /// </summary>
    private async Task<bool> TryUseAsync(GattDeviceService service)
    {
        GattCharacteristicsResult characteristics =
            await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);

        if (characteristics.Status != GattCommunicationStatus.Success) return false;

        GattCharacteristic? write = characteristics.Characteristics.FirstOrDefault(c =>
            c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            || c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse));

        GattCharacteristic? notify = characteristics.Characteristics.FirstOrDefault(c =>
            c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            || c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate));

        if (write is null || notify is null) return false;

        notify.ValueChanged += OnNotified;

        GattCommunicationStatus subscribed = await notify
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : GattClientCharacteristicConfigurationDescriptorValue.Indicate)
            .AsTask().ConfigureAwait(false);

        if (subscribed != GattCommunicationStatus.Success)
        {
            notify.ValueChanged -= OnNotified;
            return false;
        }

        _write = write;
        _notify = notify;

        if (Answers()) return true;

        // Silent. Let go of it cleanly and try the next.
        notify.ValueChanged -= OnNotified;
        _write = null;
        _notify = null;

        return false;
    }

    /// <summary>Whether anything at all comes back from a carriage return.</summary>
    private bool Answers()
    {
        try
        {
            DiscardInput();
            Write("\r"u8);

            Span<byte> one = stackalloc byte[1];
            return Read(one, TimeSpan.FromSeconds(3)) == 1;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"BLE service did not answer: {e.Message}");
            return false;
        }
    }

    private void OnNotified(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[args.CharacteristicValue.Length];
        reader.ReadBytes(bytes);

        lock (_gate)
            foreach (byte b in bytes) _incoming.Enqueue(b);

        // Released once per notification rather than once per byte: a waiting
        // read rechecks the queue anyway, and the count only has to not be lost.
        _arrived.Release();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_write is not { } characteristic)
            throw new InvalidOperationException("The adapter is not open.");

        bool withoutResponse = characteristic.CharacteristicProperties
            .HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

        // In pieces the radio will carry. The default BLE payload is twenty
        // bytes and an ELM327 command is far shorter, but a longer one is
        // silently truncated rather than refused — which reads as the adapter
        // ignoring a command it never received in full.
        byte[] all = data.ToArray();

        for (int at = 0; at < all.Length; at += Chunk)
        {
            var writer = new DataWriter();
            writer.WriteBytes(all[at..Math.Min(at + Chunk, all.Length)]);

            GattCommunicationStatus sent = Task.Run(() => characteristic
                .WriteValueAsync(
                    writer.DetachBuffer(),
                    withoutResponse ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse)
                .AsTask()).GetAwaiter().GetResult();

            if (sent != GattCommunicationStatus.Success)
                throw new IOException($"{Describe()} refused a write ({sent}).");
        }
    }

    /// <summary>The default BLE payload, less the three bytes of ATT header.</summary>
    private const int Chunk = 20;

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int total = 0;

        while (total < buffer.Length)
        {
            lock (_gate)
                while (total < buffer.Length && _incoming.Count > 0)
                    buffer[total++] = _incoming.Dequeue();

            if (total == buffer.Length) break;

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            // Anything at all is enough to return, matching a serial port: the
            // caller is reading one byte at a time looking for the prompt, and
            // waiting for a full buffer that will never fill costs the timeout
            // on every exchange.
            if (!_arrived.Wait(remaining) && total > 0) break;
        }

        return total;
    }

    public void DiscardInput()
    {
        lock (_gate) _incoming.Clear();

        while (_arrived.CurrentCount > 0) _arrived.Wait(0);
    }

    public void Close()
    {
        if (_notify is { } notify) notify.ValueChanged -= OnNotified;

        _notify = null;
        _write = null;

        // The service before the device, and both of them without fail. Either
        // one left undisposed holds the connection open past the life of this
        // object, and the symptom lands on whatever tries to connect next rather
        // than here.
        try
        {
            _service?.Dispose();
            _device?.Dispose();
        }
        catch (Exception e)
        {
            // A device that has gone out of range cannot be closed politely.
            Debug.WriteLine($"Closing the BLE adapter: {e.Message}");
        }

        _service = null;
        _device = null;
        DiscardInput();
    }

    private string Describe() => Name.Length > 0 ? Name : $"The adapter at {_address:X12}";

    public void Dispose() => Close();
}
