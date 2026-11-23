using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Where a <see cref="WcpClient"/> sends its lines and hears the device's.
///
/// An interface so the client can be driven by a test without a byte stream at
/// all; <see cref="WcpLink"/> is the real one.
/// </summary>
public interface IWcpPort
{
    /// <summary>Whether lines can be sent now.</summary>
    bool IsOpen { get; }

    /// <summary>Sends one framed line. The port adds the terminator.</summary>
    void SendLine(string line);

    /// <summary>
    /// Raised for every WCP line the device sends, without its terminator and
    /// before its CRC is checked. Raised on the port's reading thread, so a
    /// handler must be quick and must not block on a reply.
    /// </summary>
    event Action<string>? LineReceived;

    /// <summary>Raised when the link closes, which is the host disconnecting as the firmware sees it.</summary>
    event Action? Closed;
}

/// <summary>
/// A transport that reads bytes as soon as any are there, rather than waiting to
/// fill a buffer.
///
/// <see cref="IEcuTransport.Read"/> is "until this many bytes, or the timeout",
/// which is right for a framed reply of known length and wrong for a pump that
/// does not know how much is coming. Asked for 256 bytes by a reader that only
/// needs whatever has arrived, a serial port waits out its whole timeout on every
/// call; asked for one, it costs a system call per byte, and a CAN stream is tens
/// of thousands of bytes a second.
/// </summary>
public interface IReadsAvailable
{
    /// <summary>
    /// Waits up to <paramref name="timeout"/> for at least one byte, then returns
    /// everything already buffered, up to the size of <paramref name="buffer"/>.
    /// Zero when the timeout passed with nothing.
    /// </summary>
    int ReadAvailable(Span<byte> buffer, TimeSpan timeout);
}

/// <summary>
/// One port, two conversations: the WiCAN Pro's ELM327 and its WCP control lane,
/// over the same USB serial line.
///
/// <para>
/// <b>What it is to each side.</b> To <see cref="Elm327"/> and everything built on
/// it, this <em>is</em> the transport: an <see cref="IEcuTransport"/> whose reads
/// return the ELM327 bytes and nothing else, with every WCP line taken out. To a
/// <see cref="WcpClient"/> it is an <see cref="IWcpPort"/> that sends lines and
/// raises the ones that came back. So OBD2 logging goes on exactly as it does on a
/// plain adapter while settings, logs and the CAN stream travel alongside it.
/// </para>
///
/// <para>
/// <b>Why a reading thread.</b> The obvious design has no thread: whoever wants
/// bytes reads the port, and sorts what they get. It fails on the one thing WCP
/// adds that ELM327 never had — bytes nobody asked for. A CAN stream arrives
/// continuously whether or not an OBD2 request is in flight, and a serial port
/// with nobody reading it fills its 4 KB driver buffer in a fraction of a second
/// at 2 Mbaud and drops the rest. So a thread drains the port all the time, and
/// both sides read from what it has sorted.
/// </para>
///
/// <para>
/// <b>Writes are whole lines, one at a time.</b> An ELM327 command and a WCP
/// request written at the same moment from two threads could interleave byte by
/// byte, and the firmware would see neither. Each write here is under one lock, so
/// they reach the wire one after the other.
/// </para>
///
/// <para>
/// <b>No WCP request while an ELM327 command waits for its prompt</b> (review item
/// F17). The firmware holds WCP back from the moment an ELM327 command line
/// arrives until the chip's <c>&gt;</c> goes out (contract 8.2), for up to 1.5 s, so
/// a request written in that window only waits on the device instead of here —
/// and eats its own timeout doing it. So a WCP line waits here for the prompt,
/// for at most <see cref="ElmPromptWait"/>, and an ELM327 command written while a
/// WCP line is waiting lets it go first.
/// </para>
///
/// <para>
/// <b>Closing it is a disconnect.</b> <see cref="Elm327Source.Recover"/> closes and
/// reopens its transport to put a dead link back together; when that transport is
/// this, the WCP side loses its link for the same moment, and the firmware treats
/// that as the host going away — it disarms CAN transmit. That is the safe
/// direction for a link that has just failed.
/// </para>
/// </summary>
public sealed class WcpLink : IEcuTransport, IWcpPort
{
    private readonly IEcuTransport _inner;
    private readonly WcpDemux _demux;
    private readonly Lock _writeGate = new();
    private readonly object _elmGate = new();
    private readonly Queue<byte> _elm = new();

    /// <summary>Guards the turn-taking between an ELM327 command awaiting its prompt and a WCP line waiting to go.</summary>
    private readonly object _turn = new();
    private DateTime? _elmAwaitingSince;
    private int _wcpWaiting;

    /// <summary>
    /// The longest a WCP line waits for an ELM327 prompt before it goes anyway: a
    /// little over the firmware's own 1.5 s hold, so a prompt that never comes —
    /// a command the chip dropped — costs this once rather than stalling WCP.
    /// </summary>
    public static readonly TimeSpan ElmPromptWait = TimeSpan.FromMilliseconds(1700);

    /// <summary>The longest an ELM327 command waits for a WCP line queued ahead of it.</summary>
    private static readonly TimeSpan WcpTurnWait = TimeSpan.FromMilliseconds(500);

    /// <summary>WCP lines that had to wait for an ELM327 prompt before going out.</summary>
    public long HeldForElm { get; private set; }

    /// <summary>Whether an ELM327 command written here is still waiting for its <c>&gt;</c>.</summary>
    public bool ElmAwaitingPrompt
    {
        get
        {
            lock (_turn) return _elmAwaitingSince is { } since && DateTime.UtcNow - since < ElmPromptWait;
        }
    }

    private Thread? _pump;
    private volatile bool _stop;
    private Exception? _fault;

    /// <summary>
    /// The most ELM327 bytes held for a reader that is not reading. Far more than
    /// any reply; past it the oldest are dropped, because an ELM327 conversation
    /// discards what is buffered before every command anyway.
    /// </summary>
    public const int MaxElmBuffered = 64 * 1024;

    public WcpLink(IEcuTransport inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _demux = new WcpDemux(TakeElm, line => LineReceived?.Invoke(line));
    }

    /// <summary>The port underneath, for saying where this was reached.</summary>
    public IEcuTransport Inner => _inner;

    public event Action<string>? LineReceived;

    public event Action? Closed;

    public bool IsOpen => _inner.IsOpen && _pump is { IsAlive: true };

    /// <summary>WCP lines discarded for running past <see cref="WcpDemux.MaxLine"/>.</summary>
    public long Overflows => _demux.Overflows;

    public TimeSpan WriteTimeout
    {
        get => _inner.WriteTimeout;
        set => _inner.WriteTimeout = value;
    }

    public void Open()
    {
        if (IsOpen) return;

        if (!_inner.IsOpen) _inner.Open();

        _fault = null;
        _stop = false;
        _demux.Reset();

        lock (_elmGate) _elm.Clear();

        lock (_turn)
        {
            _elmAwaitingSince = null;
            Monitor.PulseAll(_turn);
        }

        _pump = new Thread(Pump) { IsBackground = true, Name = "WiCAN link" };
        _pump.Start();
    }

    public void Close()
    {
        bool wasOpen = _pump is not null;

        _stop = true;

        if (_pump is { } pump && pump != Thread.CurrentThread) pump.Join(TimeSpan.FromSeconds(1));

        _pump = null;
        _inner.Close();

        lock (_elmGate) Monitor.PulseAll(_elmGate);

        lock (_turn)
        {
            _elmAwaitingSince = null;
            Monitor.PulseAll(_turn);
        }

        if (wasOpen) Closed?.Invoke();
    }

    private void Pump()
    {
        byte[] buffer = new byte[4096];

        try
        {
            while (!_stop)
            {
                DateTime asked = DateTime.UtcNow;

                int got = _inner is IReadsAvailable available
                    ? available.ReadAvailable(buffer, ReadSlice)
                    : _inner.Read(buffer.AsSpan(0, 1), ReadSlice);

                if (got > 0)
                {
                    _demux.Feed(buffer.AsSpan(0, got));
                    continue;
                }

                if (!_inner.IsOpen) throw new IOException("The adapter closed the connection.");

                // A transport that returns at once with nothing — a test double, or
                // a socket whose far end has gone — would otherwise turn this into a
                // loop spinning a core flat out.
                if (DateTime.UtcNow - asked < TimeSpan.FromMilliseconds(2)) Thread.Sleep(1);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException
                                      or UnauthorizedAccessException or TimeoutException)
        {
            _fault = e;

            lock (_elmGate) Monitor.PulseAll(_elmGate);
        }
    }

    /// <summary>
    /// How long one read of the port waits before looking round to see whether it
    /// has been asked to stop. Short, because closing waits for it.
    /// </summary>
    private static readonly TimeSpan ReadSlice = TimeSpan.FromMilliseconds(50);

    private void TakeElm(ReadOnlySpan<byte> bytes)
    {
        // The prompt ends the ELM327 exchange, and with it the firmware's hold on WCP.
        if (bytes.Contains((byte)'>'))
        {
            lock (_turn)
            {
                _elmAwaitingSince = null;
                Monitor.PulseAll(_turn);
            }
        }

        lock (_elmGate)
        {
            foreach (byte b in bytes) _elm.Enqueue(b);
            while (_elm.Count > MaxElmBuffered) _elm.Dequeue();

            Monitor.PulseAll(_elmGate);
        }
    }

    /// <summary>
    /// ELM327 bytes, with every WCP line already taken out. Waits for as many as
    /// asked for, or until the timeout — the same contract as any other transport,
    /// and it has to be: <see cref="Elm327"/> reads a byte at a time and treats a
    /// read that came back early and empty as a link that has closed.
    /// </summary>
    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int count = 0;

        lock (_elmGate)
        {
            while (count < buffer.Length)
            {
                while (count < buffer.Length && _elm.Count > 0) buffer[count++] = _elm.Dequeue();

                if (count == buffer.Length) break;

                if (_fault is { } fault)
                {
                    if (count > 0) break;
                    throw new IOException(fault.Message, fault);
                }

                if (_pump is null) throw new InvalidOperationException("The port is not open.");

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                Monitor.Wait(_elmGate, remaining);
            }
        }

        return count;
    }

    /// <summary>
    /// Drops the ELM327 bytes waiting to be read. Only those: the port's own
    /// buffer is not touched, because what is in it may be the WCP reply a
    /// request on another thread is waiting for.
    /// </summary>
    public void DiscardInput()
    {
        lock (_elmGate) _elm.Clear();
    }

    /// <summary>
    /// An ELM327 command, or anything else that is not WCP. Sent as it is given,
    /// after any WCP line already waiting for its turn; a carriage return in it
    /// marks a command awaiting its prompt.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_fault is { } fault) throw new IOException(fault.Message, fault);

        bool command = data.Contains((byte)'\r');

        lock (_turn)
        {
            // A WCP line that waited out the last prompt goes before this command,
            // or a poll loop writing its next command the moment the prompt
            // arrives would keep WCP waiting for ever.
            DateTime until = DateTime.UtcNow + WcpTurnWait;

            while (command && _wcpWaiting > 0 && _elmAwaitingSince is null && DateTime.UtcNow < until)
                Monitor.Wait(_turn, until - DateTime.UtcNow);

            lock (_writeGate) _inner.Write(data);

            if (command) _elmAwaitingSince = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// One WCP line, with a carriage return to end it. Waits first, for at most
    /// <see cref="ElmPromptWait"/>, while an ELM327 command written here has not had
    /// its prompt: see the class remarks.
    /// </summary>
    public void SendLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (_pump is null) throw new InvalidOperationException("The WiCAN link is not open.");
        if (_fault is { } fault) throw new IOException(fault.Message, fault);

        byte[] bytes = Encoding.ASCII.GetBytes(line + "\r");

        lock (_turn)
        {
            _wcpWaiting++;

            try
            {
                bool held = false;

                while (_elmAwaitingSince is { } since && _pump is not null)
                {
                    TimeSpan left = since + ElmPromptWait - DateTime.UtcNow;

                    // No prompt in time: the command was dropped, or its answer
                    // lost. Go anyway; the firmware's own hold has lapsed by now.
                    if (left <= TimeSpan.Zero)
                    {
                        _elmAwaitingSince = null;
                        break;
                    }

                    held = true;
                    Monitor.Wait(_turn, left);
                }

                if (held) HeldForElm++;

                lock (_writeGate) _inner.Write(bytes);
            }
            finally
            {
                _wcpWaiting--;
                Monitor.PulseAll(_turn);
            }
        }
    }

    public void Dispose()
    {
        Close();
        _inner.Dispose();
    }
}
