using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Tests;

/// <summary>
/// An adapter whose bytes arrive when they are told to.
///
/// <see cref="FakeElm"/> answers instantly and completely, which is the right
/// model for the decode and the wrong one for the framing: every defect this
/// exists to pin is about <em>when</em> a byte turns up. A payload that arrives
/// whole with the prompt three hundred milliseconds behind it, an echo that
/// arrives long before the data it precedes, a prompt left over from the last
/// exchange — none of those can be expressed by a queue that is already full.
///
/// So a reply is scripted as pieces with delays, and a read only sees a piece
/// once its time has come.
///
/// One modelling rule is load-bearing: <see cref="DiscardInput"/> drops only
/// what has already arrived, never what is still in flight. A real link cannot
/// throw away a byte the adapter has not sent yet, and a fake that can will
/// happily pass every test with the fix taken back out.
/// </summary>
internal sealed class ScriptedElm : IEcuTransport
{
    /// <summary>One instalment of a reply: how long after the request, and what.</summary>
    internal readonly record struct Piece(TimeSpan After, string Text);

    private readonly Func<string, IReadOnlyList<Piece>> _script;
    private readonly Lock _gate = new();
    private readonly List<(DateTime At, byte Value)> _wire = [];
    private readonly StringBuilder _pending = new();

    public ScriptedElm(Func<string, IReadOnlyList<Piece>> script) => _script = script;

    /// <summary>Every command the adapter was sent, in order.</summary>
    public List<string> Received { get; } = [];

    public bool IsOpen { get; private set; } = true;

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Dispose() => Close();

    /// <summary>
    /// Puts bytes on the wire that belong to no request — the tail of an earlier
    /// exchange, still on its way.
    ///
    /// <paramref name="after"/> is what makes it a leftover rather than nothing
    /// at all. Bytes that have already landed are removed by the drain before
    /// the next request, exactly as on a real link; the ones that matter are the
    /// ones still in flight while that drain runs, because those cannot be
    /// thrown away and arrive at the front of the next read.
    /// </summary>
    public void Interject(string text, TimeSpan after)
    {
        DateTime at = DateTime.UtcNow + after;

        lock (_gate)
            foreach (char c in text) _wire.Add((at, (byte)c));
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b != (byte)'\r')
            {
                _pending.Append((char)b);
                continue;
            }

            string command = _pending.ToString().Trim();
            _pending.Clear();

            Received.Add(command);

            DateTime sent = DateTime.UtcNow;

            lock (_gate)
                foreach (Piece piece in _script(command))
                    foreach (char c in piece.Text)
                        _wire.Add((sent + piece.After, (byte)c));
        }
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            lock (_gate)
            {
                int taken = 0;

                while (taken < buffer.Length && _wire.Count > 0 && _wire[0].At <= DateTime.UtcNow)
                {
                    buffer[taken++] = _wire[0].Value;
                    _wire.RemoveAt(0);
                }

                // Anything at all is enough to return, as a radio transport does:
                // the caller is reading a byte at a time looking for the prompt.
                if (taken > 0) return taken;
            }

            if (DateTime.UtcNow >= deadline) return 0;

            Thread.Sleep(2);
        }
    }

    public void DiscardInput()
    {
        lock (_gate) _wire.RemoveAll(b => b.At <= DateTime.UtcNow);
    }
}
