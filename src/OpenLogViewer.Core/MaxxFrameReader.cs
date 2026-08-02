using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>
/// Reassembles MaxxECU frames from a byte stream.
///
/// Needed because Bluetooth serial does not preserve message boundaries: a
/// 34-byte frame can arrive as any number of reads of any size, and two frames
/// can arrive in one. Nothing may assume that one read is one message.
///
/// The frame is awkward to parse forwards, because it states its payload length
/// <em>after</em> the payload:
///
/// <code>type(1) | 77 AA 77 | payload(N) | N as u16 LE | checksum(2) | CC 77 AA 44</code>
///
/// So the reader finds a trailer, reads the length from just before it, and
/// checks that the frame that implies starts where a header actually is. The
/// trailer alone is not enough — a payload may legitimately contain those four
/// bytes — and it is the length agreeing with the header position that makes
/// the framing unambiguous.
/// </summary>
public sealed class MaxxFrameReader
{
    private readonly List<byte> _buffer = [];

    /// <summary>Bytes thrown away as unparseable, for reporting a link that is out of step.</summary>
    public int Discarded { get; private set; }

    /// <summary>
    /// Adds received bytes to whatever is already waiting.
    ///
    /// Appends and nothing else. Capping the buffer here looks like prudent
    /// housekeeping and silently destroys data: one large read then discards
    /// everything but its tail before a single frame has been taken out of it.
    /// Bounding belongs where the reader gives up on finding a frame, which is
    /// the only place it can tell the difference.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data) => _buffer.AddRange(data);

    /// <summary>
    /// Takes the next complete frame, or returns false when more bytes are
    /// needed. Call until it returns false.
    /// </summary>
    public bool TryTake(out MaxxFrame? frame)
    {
        frame = null;

        while (true)
        {
            // Anchor on a header rather than on a trailer. Searching backwards
            // from the first trailer looks simpler and is wrong: a payload may
            // contain those four bytes, and reading a length from in front of a
            // false trailer occasionally lands on something that passes for a
            // header — which swallows real frames rather than failing. Against a
            // recording of a real ECU that cost two thirds of them.
            int start = IndexOfHeader();

            if (start < 0)
            {
                // Nothing that could begin a frame. Keep only the tail a header
                // might straddle, so a stream of noise cannot grow without bound.
                if (_buffer.Count > 3)
                {
                    Discarded += _buffer.Count - 3;
                    _buffer.RemoveRange(0, _buffer.Count - 3);
                }

                return false;
            }

            if (start > 0)
            {
                Discarded += start;
                _buffer.RemoveRange(0, start);
            }

            // The trailer of a frame starting here sits where the length field
            // agrees: for a payload of N the frame is N + 12 long, its trailer
            // begins at N + 8, and the length is the two bytes before that.
            int found = -1;

            for (int t = MaxxProtocol.Overhead - 4; t + 4 <= _buffer.Count; t++)
            {
                if (!IsTrailerAt(t)) continue;

                int declared = _buffer[t - 4] | (_buffer[t - 3] << 8);
                if (declared != t - 8) continue;

                found = t;
                break;
            }

            if (found < 0)
            {
                // Either the rest has not arrived, or this header was a false
                // one inside a payload. Only give up on it once there is more
                // than a whole frame of evidence.
                if (_buffer.Count <= MaxxProtocol.MaximumFrame + MaxxProtocol.Overhead) return false;

                Discarded++;
                _buffer.RemoveRange(0, 1);
                continue;
            }

            int length = found - 8;
            var payload = new byte[length];
            for (int i = 0; i < length; i++) payload[i] = _buffer[4 + i];

            frame = new MaxxFrame(_buffer[0], payload);
            _buffer.RemoveRange(0, found + 4);

            return true;
        }
    }

    /// <summary>The first position whose next three bytes are the magic.</summary>
    private int IndexOfHeader()
    {
        ReadOnlySpan<byte> magic = MaxxProtocol.Magic;

        for (int i = 0; i + 4 <= _buffer.Count; i++)
        {
            bool match = true;

            for (int j = 0; j < magic.Length; j++)
            {
                if (_buffer[i + 1 + j] == magic[j]) continue;

                match = false;
                break;
            }

            if (match) return i;
        }

        return -1;
    }

    private bool IsTrailerAt(int at)
    {
        ReadOnlySpan<byte> trailer = MaxxProtocol.Trailer;

        if (at < 0 || at + trailer.Length > _buffer.Count) return false;

        for (int j = 0; j < trailer.Length; j++)
            if (_buffer[at + j] != trailer[j])
                return false;

        return true;
    }

    /// <summary>Reads a single frame from a complete buffer, for a fixed message.</summary>
    public static bool? Read(ReadOnlySpan<byte> data, out MaxxFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;

        if (data.Length < MaxxProtocol.Overhead) return false;

        int declared = BinaryPrimitives.ReadUInt16LittleEndian(data[^8..]);
        if (declared + MaxxProtocol.Overhead != data.Length) return false;

        if (!data[1..4].SequenceEqual(MaxxProtocol.Magic)) return false;
        if (!data[^4..].SequenceEqual(MaxxProtocol.Trailer)) return false;

        frame = new MaxxFrame(data[0], data.Slice(4, declared).ToArray());
        consumed = data.Length;

        return true;
    }
}
