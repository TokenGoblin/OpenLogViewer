using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Splits one byte stream from a WiCAN Pro into the two conversations sharing it:
/// WCP lines on one side, and every other byte, untouched, on the ELM327 side.
///
/// <para>
/// <b>The rule is the first byte of a line.</b> A line that starts with <c>#</c>
/// or <c>!</c> is WCP; everything else is ELM327, which never begins a line with
/// either. "Starts a line" means: the first byte of the stream, or the first after
/// a CR, an LF, or an ELM327 prompt (<c>&gt;</c>).
/// </para>
///
/// <para>
/// <b>Why the prompt counts as a line end.</b> An ELM327 exchange finishes with
/// <c>\r\r&gt;</c> and nothing after it: the chip sits on the prompt waiting for the
/// next command. A WCP event that arrives then — a <c>!CAN</c> frame, a
/// <c>!DISARM</c> — is the first thing after the <c>&gt;</c>, not the first thing after
/// a line break. Treat only CR and LF as boundaries and that event is read as the
/// tail of the prompt's "line", handed to the ELM327 parser as noise, and lost.
/// The contract does not say this in so many words; it is what its rule has to
/// mean for the commonest moment an event can arrive.
/// </para>
///
/// <para>
/// <b>What this cannot fix.</b> A WCP line the firmware writes into the
/// <em>middle</em> of an ELM327 line — "<c>41 0C</c>", a whole <c>!CAN</c> line, then
/// "<c> 1A F8</c>" — is not at a line start, so it goes to the ELM327 side and is
/// lost from WCP, and the ELM327 reply is corrupted too. Only the firmware can
/// prevent that, by never interleaving inside a line of the chip's output.
/// </para>
///
/// <para>
/// Pure: it owns no thread and no transport, so every interleaving can be fed to
/// it a byte at a time in a test. <see cref="WcpLink"/> drives it from the port.
/// </para>
/// </summary>
public sealed class WcpDemux
{
    private readonly Action<ReadOnlySpan<byte>> _elm;
    private readonly Action<string> _wcp;
    private readonly StringBuilder _line = new();

    private bool _atLineStart = true;
    private bool _inWcp;
    private bool _overflowing;
    private bool _swallowLf;

    /// <summary>
    /// The longest WCP line kept, in characters. Generous: a data line is at most
    /// 200 characters of text and its framing. Anything longer is not a line the
    /// firmware wrote, and is thrown away up to its end rather than held without
    /// limit.
    /// </summary>
    public const int MaxLine = 1024;

    /// <param name="elm">Receives every byte that is not part of a WCP line, in order, in runs.</param>
    /// <param name="wcp">Receives each WCP line, without its terminator, unchecked.</param>
    public WcpDemux(Action<ReadOnlySpan<byte>> elm, Action<string> wcp)
    {
        _elm = elm ?? throw new ArgumentNullException(nameof(elm));
        _wcp = wcp ?? throw new ArgumentNullException(nameof(wcp));
    }

    /// <summary>WCP lines that ran past <see cref="MaxLine"/> and were discarded.</summary>
    public long Overflows { get; private set; }

    /// <summary>
    /// Takes the next bytes off the wire. A line may be split across any number of
    /// calls, at any byte.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        int runStart = 0;

        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];

            if (_inWcp)
            {
                if (b is (byte)'\r' or (byte)'\n')
                {
                    if (!_overflowing) _wcp(_line.ToString());

                    _line.Clear();
                    _inWcp = false;
                    _overflowing = false;
                    _atLineStart = true;

                    // The firmware ends its lines CR LF. Without this the LF would
                    // be the first byte of the next "line" and go to the ELM327
                    // side, which would then see a bare line feed every time an
                    // event arrived — harmless to most parsers, and still not a
                    // byte the chip ever sent.
                    _swallowLf = b == (byte)'\r';
                    runStart = i + 1;
                    continue;
                }

                if (_line.Length >= MaxLine)
                {
                    if (!_overflowing) Overflows++;
                    _overflowing = true;
                }
                else
                {
                    _line.Append((char)b);
                }

                runStart = i + 1;
                continue;
            }

            if (_swallowLf)
            {
                _swallowLf = false;

                if (b == (byte)'\n')
                {
                    runStart = i + 1;
                    continue;
                }
            }

            if (_atLineStart && b is (byte)'#' or (byte)'!')
            {
                if (i > runStart) _elm(bytes[runStart..i]);

                _inWcp = true;
                _line.Clear();
                _line.Append((char)b);
                runStart = i + 1;
                continue;
            }

            _atLineStart = b is (byte)'\r' or (byte)'\n' or (byte)'>';
        }

        if (!_inWcp && bytes.Length > runStart) _elm(bytes[runStart..]);
    }

    /// <summary>
    /// Forgets any half-received line, as after the port has been closed and
    /// reopened: what arrives next starts a fresh stream.
    /// </summary>
    public void Reset()
    {
        _line.Clear();
        _inWcp = false;
        _overflowing = false;
        _swallowLf = false;
        _atLineStart = true;
    }
}
