using System.IO.Ports;

namespace OpenLogViewer.Core;

/// <summary>
/// A serial port, whether that is a USB tuning cable or a Bluetooth adapter —
/// both appear as a COM port and neither knows the difference.
/// </summary>
public sealed class SerialEcuTransport(string portName, int baudRate = 115200) : IEcuTransport
{
    private SerialPort? _port;

    public string PortName { get; } = portName;

    public int BaudRate { get; } = baudRate;

    public bool IsOpen => _port is { IsOpen: true };

    /// <summary>COM ports currently present, for the connection picker.</summary>
    public static IReadOnlyList<string> AvailablePorts()
    {
        try
        {
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports, (a, b) => Number(a).CompareTo(Number(b)));
            return ports;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // COM10 sorts before COM9 alphabetically, which reads as a mistake in a
        // list the user is picking from.
        static int Number(string name)
        {
            string digits = new([.. name.Where(char.IsAsciiDigit)]);
            return int.TryParse(digits, out int value) ? value : int.MaxValue;
        }
    }

    /// <summary>
    /// How many times to try opening the port.
    ///
    /// One for a cable, which either works or does not. More for Bluetooth:
    /// establishing an RFCOMM link is reported to fail on the first attempt
    /// after an ECU boots — an SDP discovery failure rather than anything
    /// wrong — and to succeed on the second. Retrying costs a moment; not
    /// retrying costs a connection that would have worked.
    /// </summary>
    public int OpenAttempts { get; init; } = 1;

    public void Open()
    {
        if (IsOpen) return;

        for (int attempt = 1; attempt < Math.Max(1, OpenAttempts); attempt++)
        {
            try
            {
                OpenOnce();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(700);
            }
        }

        try
        {
            OpenOnce();
        }
        catch (IOException e) when (e.Message.Contains("semaphore", StringComparison.OrdinalIgnoreCase))
        {
            // What Windows says when an RFCOMM link cannot be established. The
            // message names a kernel primitive and tells the user nothing; the
            // cause is almost always that the device is switched off or out of
            // range, since a paired port exists whether or not anything answers
            // on it.
            throw new IOException(
                $"{PortName} did not answer. A paired Bluetooth port stays listed whether or not "
                + "the device is switched on, so check the ECU has power and is in range.", e);
        }
    }

    private void OpenOnce()
    {
        if (IsOpen) return;

        _port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,

            // Long, because of what a burn does. A controller writing its flash
            // stops servicing USB for as long as the erase takes, and the bytes
            // still sitting in the driver's buffer cannot be handed over until
            // it comes back — so the write that delivered the burn command is
            // itself what blocks. At 500 ms a rusEFI threw here on every burn,
            // and the burn had in fact succeeded: the board rebooted holding the
            // new value while the application reported a failure.
            //
            // Nothing is lost by waiting. A write to a healthy port returns in
            // microseconds; this bound only decides how long a port with nothing
            // on the other end takes to say so.
            WriteTimeout = 5000,

            // Some adapters hold the line low until these are asserted, and stay
            // silent rather than reporting anything wrong.
            DtrEnable = true,
            RtsEnable = true,
        };

        _port.Open();

        // Anything already buffered predates this session and would be read as
        // the front of the first reply.
        Thread.Sleep(150);

        try
        {
            _port.DiscardInBuffer();
        }
        catch (InvalidOperationException e)
        {
            // The handle went away between opening it and this line, which a USB
            // adapter does while Windows is still attaching it — plug one in and
            // connect straight away and the port lists, opens, and is gone again
            // a moment later. Reported as an IOException because that is what a
            // port that will not stay open is, and what every caller already
            // expects to catch.
            throw new IOException(
                $"{PortName} opened and then closed again. If the adapter was just plugged in, "
                + "give it a moment and try again.", e);
        }
    }

    /// <summary>
    /// Closes the port, tolerating one whose device has already gone. Unplugging
    /// a USB adapter leaves a SerialPort that throws from Close as readily as
    /// from Read, and failing to shut down is not a useful way to report that
    /// something is already shut down.
    /// </summary>
    public void Close()
    {
        try
        {
            _port?.Close();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            _port = null;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_port is not { IsOpen: true }) throw new InvalidOperationException("The port is not open.");

        try
        {
            _port.Write(data.ToArray(), 0, data.Length);
        }
        catch (TimeoutException e)
        {
            // A port that will not even accept bytes. Windows' incoming
            // Bluetooth port does this — it is a listener waiting for something
            // to dial in, so nothing is on the other end of a write and it
            // blocks until the timeout.
            //
            // Reported as an IOException because that is what it is, and because
            // TimeoutException is not something a caller of a transport has any
            // reason to expect — it escaped the connect path and took the
            // application down with it.
            throw new IOException(
                $"{PortName} would not accept the request. If this is a Bluetooth port, "
                + "it may be the incoming one, which waits to be dialled rather than dialling out.", e);
        }
    }

    /// <summary>
    /// Fills the buffer, or returns fewer bytes if the timeout passes first.
    ///
    /// Exactly as many bytes as asked for, rather than "until the line seems
    /// quiet". A 512-byte block takes about 45 ms to arrive at 115200 baud and
    /// the driver hands it over in pieces; treating a gap between pieces as the
    /// end of the reply truncates it, and a truncated frame is indistinguishable
    /// from a corrupt one by the time the checksum sees it.
    /// </summary>
    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (_port is not { IsOpen: true }) throw new InvalidOperationException("The port is not open.");

        int total = 0;
        DateTime deadline = DateTime.UtcNow + timeout;
        var chunk = new byte[buffer.Length];

        while (total < buffer.Length)
        {
            int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) break;

            // The port's own blocking read, rather than polling BytesToRead with
            // a sleep: Windows rounds a 1 ms sleep up to a timer tick of about
            // 15 ms, which was costing more per poll than the transfer itself.
            _port.ReadTimeout = remaining;

            try
            {
                int read = _port.Read(chunk, 0, buffer.Length - total);
                chunk.AsSpan(0, read).CopyTo(buffer[total..]);
                total += read;
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Drops anything buffered. Failing here is not worth reporting: it is done
    /// before a request as hygiene, and a port that cannot be cleared will say
    /// so again on the read that follows.
    /// </summary>
    public void DiscardInput()
    {
        try
        {
            if (_port is { IsOpen: true }) _port.DiscardInBuffer();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void Dispose() => Close();
}
