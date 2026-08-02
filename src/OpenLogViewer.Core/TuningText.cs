using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Reads the text files the tuning world is made of.
///
/// INIs and MSQ tunes are ISO-8859-1: an MSQ says so in its XML declaration,
/// and an INI does not say anything but is written that way regardless. Read as
/// UTF-8 they mostly work, because most of the content is ASCII — and then a
/// degree sign, the one byte that matters in a units string, decodes to a
/// replacement character and every temperature channel is labelled "?F".
///
/// UTF-8 is tried first all the same, since a modern file may well be, and only
/// a file that is not valid UTF-8 falls back.
/// </summary>
public static class TuningText
{
    public static string Read(string path) => Decode(File.ReadAllBytes(path));

    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Strip(bytes));
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Drops a UTF-8 byte order mark, which would otherwise lead the first line.</summary>
    private static byte[] Strip(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
}
