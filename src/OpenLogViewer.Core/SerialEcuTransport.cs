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

    public void Open()
    {
        if (IsOpen) return;

        _port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,

            // Some adapters hold the line low until these are asserted, and stay
            // silent rather than reporting anything wrong.
            DtrEnable = true,
            RtsEnable = true,
        };

        _port.Open();

        // Anything already buffered predates this session and would be read as
        // the front of the first reply.
        Thread.Sleep(150);
        _port.DiscardInBuffer();
    }

    public void Close()
    {
        _port?.Close();
        _port = null;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_port is not { IsOpen: true }) throw new InvalidOperationException("The port is not open.");

        _port.Write(data.ToArray(), 0, data.Length);
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

    public void DiscardInput()
    {
        if (_port is { IsOpen: true }) _port.DiscardInBuffer();
    }

    public void Dispose() => Close();
}
