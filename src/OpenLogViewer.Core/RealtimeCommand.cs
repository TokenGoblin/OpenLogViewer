using System.Globalization;

namespace OpenLogViewer.Core;

/// <summary>
/// The INI's <c>ochGetCommand</c>, turned into the bytes that ask for a block.
///
/// Every firmware asks differently. MegaSquirt reads a page with
/// <c>r\$tsCanId\x07%2o%2c</c>; rusEFI has a command of its own,
/// <c>O%2o%2c</c>; an older MS2 on plain serial sends <c>A</c> and gets the lot.
/// The template is in the INI precisely so that a reader does not have to know
/// which — building the request from it, rather than from a hard-coded guess, is
/// the whole of what makes one program able to talk to both.
/// </summary>
public sealed class RealtimeCommand
{
    private enum Part
    {
        /// <summary>A byte written as it stands.</summary>
        Literal,

        /// <summary>The controller's CAN id: <c>\$tsCanId</c>.</summary>
        CanId,

        /// <summary>Two bytes: where in the block to start — <c>%2o</c>.</summary>
        Offset,

        /// <summary>Two bytes: how much of it to send — <c>%2c</c>.</summary>
        Count,

        /// <summary>Two bytes: which page — <c>%2i</c>. Always the realtime one.</summary>
        Page,
    }

    private readonly (Part Kind, byte Value)[] _parts;

    private RealtimeCommand((Part, byte)[] parts)
    {
        _parts = parts;
        TakesRange = parts.Any(p => p.Item1 is Part.Offset or Part.Count);
    }

    /// <summary>
    /// Whether the command can ask for part of the block.
    ///
    /// False for a template like <c>A</c>, which returns everything and has
    /// nowhere to put an offset — such a firmware cannot have its block read in
    /// pieces, however large it is.
    /// </summary>
    public bool TakesRange { get; }

    /// <summary>The plain MegaSquirt page read, for a layout that declares no template.</summary>
    public static RealtimeCommand Default { get; } = Parse("r\\$tsCanId\\x07%2o%2c");

    public static RealtimeCommand Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        string text = template.Trim().Trim('"');
        var parts = new List<(Part, byte)>();

        for (int i = 0; i < text.Length;)
        {
            char c = text[i];

            if (c == '%' && TryField(text, i, out Part field, out int fieldLength))
            {
                parts.Add((field, 0));
                i += fieldLength;
                continue;
            }

            if (c == '\\' && TryEscape(text, i, out (Part, byte)? escape, out int escapeLength))
            {
                // An escape may resolve to nothing: an unrecognised variable is
                // consumed and skipped rather than guessed at, since a wrong byte
                // in the request asks for something else entirely.
                if (escape is { } resolved) parts.Add(resolved);
                i += escapeLength;
                continue;
            }

            parts.Add((Part.Literal, (byte)c));
            i++;
        }

        return new RealtimeCommand([.. parts]);
    }

    /// <summary>
    /// Builds one request.
    ///
    /// <paramref name="littleEndian"/> comes from the INI's <c>endianness</c>,
    /// and applies to the offset and the count as much as to the data coming
    /// back: rusEFI reads both ends the same way round. Sending them the wrong
    /// way round does not produce an error you can recognise — a count of 1824
    /// arrives as 8199, which the firmware refuses as out of range, and a count
    /// of 1024 arrives as 4, which it honours.
    /// </summary>
    /// <summary>
    /// Builds one request.
    ///
    /// <paramref name="page"/> supplies the bytes for a <c>%2i</c>, which is not
    /// a number but the page's own identifier string — <c>\x00\x00</c> on a
    /// rusEFI, <c>\$tsCanId\x04</c> on a MegaSquirt. A realtime template names
    /// its page with a literal instead and needs none.
    /// </summary>
    public byte[] Build(
        int offset, int count, byte canId = 0, bool littleEndian = false, ReadOnlySpan<byte> page = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var request = new List<byte>(_parts.Length + 6);

        foreach ((Part kind, byte value) in _parts)
        {
            switch (kind)
            {
                case Part.Literal: request.Add(value); break;
                case Part.CanId: request.Add(canId); break;
                case Part.Offset: Write(request, offset, littleEndian); break;
                case Part.Count: Write(request, count, littleEndian); break;

                case Part.Page:
                    if (page.Length > 0) request.AddRange(page);
                    else Write(request, 0, littleEndian);
                    break;
            }
        }

        return [.. request];
    }

    private static void Write(List<byte> request, int value, bool littleEndian)
    {
        byte high = (byte)(value >> 8);
        byte low = (byte)value;

        if (littleEndian)
        {
            request.Add(low);
            request.Add(high);
        }
        else
        {
            request.Add(high);
            request.Add(low);
        }
    }

    private static bool TryField(string text, int at, out Part field, out int length)
    {
        field = default;
        length = 3;

        if (at + 2 >= text.Length || text[at + 1] != '2') return false;

        switch (text[at + 2])
        {
            case 'o': field = Part.Offset; return true;
            case 'c': field = Part.Count; return true;
            case 'i': field = Part.Page; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Reads one backslash escape. Returns false when it is not one, in which
    /// case the backslash is a literal; returns true with a null part when the
    /// escape is understood but contributes no byte.
    /// </summary>
    private static bool TryEscape(string text, int at, out (Part, byte)? part, out int length)
    {
        part = null;
        length = 0;

        if (at + 1 >= text.Length) return false;

        // \xNN — a byte in hex, which is how a page number is written.
        if (text[at + 1] is 'x' or 'X' && at + 3 < text.Length
            && byte.TryParse(text.AsSpan(at + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
        {
            part = (Part.Literal, value);
            length = 4;
            return true;
        }

        // \$name — a variable. tsCanId is the only one these templates use.
        if (text[at + 1] == '$')
        {
            int end = at + 2;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;

            length = end - at;
            if (text.AsSpan(at + 2, end - at - 2).Equals("tsCanId", StringComparison.OrdinalIgnoreCase))
                part = (Part.CanId, 0);

            return true;
        }

        return false;
    }
}
