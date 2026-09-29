using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>
/// The log a MaxxECU keeps in its own memory, downloaded over the USB cable.
///
/// <para>
/// Not <see cref="MaxxLogReader"/>, which opens the zip MTune saves to disk.
/// This is the step before that file exists: the ECU's internal logger, read
/// straight off the controller the way MTune's "Download logs" button does.
/// </para>
/// <para>
/// Recovered from a real MTune session on 2026-09-19 — see
/// <c>MAXXECU_LOG_PROTOCOL_RECOVERED.md</c>. The transfer is command
/// <c>0x32</c>, and it is an ordinary read in every other respect: the same
/// ten-byte request, the same status-data-checksum reply, 512 bytes at a time
/// at rising offsets. Two downloads in that capture each read 8,194 bytes —
/// sixteen full pieces and a two-byte tail — and that is the whole of what is
/// known about how long a stored log is.
/// </para>
/// <para>
/// <b>What this deliberately does not send.</b> MTune preceded each download
/// with a <c>0x3B</c> write and a <c>0x07</c> write carrying <c>FF FF FF FF</c>.
/// The first is not a command at all — the ECU answers it
/// <see cref="MaxxWriteStatus.UnknownCommand"/>. The second's meaning is not
/// established: "select a file" is a guess, and on a controller where a write
/// is permanent the moment it lands, an unknown write could as easily be
/// "clear the log after reading". This sends reads and nothing else, so the
/// worst a wrong guess here costs is a download that fails.
/// </para>
/// <para>
/// <b>Not yet run against hardware</b> — built from the capture alone.
/// </para>
/// </summary>
public static class MaxxStoredLog
{
    /// <summary>The command that reads the stored log.</summary>
    public const byte ReadLog = 0x32;

    /// <summary>
    /// How much one request asks for — what MTune asked for, and the
    /// protocol's own ceiling (<see cref="MaxxUsbProtocol.MaximumData"/>).
    /// </summary>
    public const int Piece = MaxxUsbProtocol.MaximumData;

    /// <summary>
    /// How long both downloads in the capture were: 16 × 512 and then two bytes.
    ///
    /// Observed rather than known. How MTune decided to stop there was not in
    /// the capture — nothing it read beforehand carried the number — so this
    /// is the default and not a rule, and a caller that learns better can ask
    /// for another length.
    /// </summary>
    public const int ObservedLength = (16 * Piece) + 2;

    /// <summary>
    /// Reads the stored log, <see cref="Piece"/> bytes at a time, finishing
    /// with whatever short piece <paramref name="length"/> leaves over — which
    /// is exactly how MTune's own download ended.
    /// </summary>
    /// <param name="transport">An open link to the ECU.</param>
    /// <param name="length">How many bytes to read.</param>
    /// <param name="progress">Called with bytes read and bytes total.</param>
    /// <param name="onRetry">Called before each attempt past the first.</param>
    public static byte[] Read(
        IEcuTransport transport, int length = ObservedLength,
        Action<int, int>? progress = null, Action? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        // Offsets travel as sixteen bits, so the last piece has to start at or
        // below 65,535. Checked here rather than left to Request, which would
        // otherwise refuse partway through with the earlier pieces already read.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, ushort.MaxValue + 1);

        var log = new byte[length];
        var timeout = TimeSpan.FromMilliseconds(600);

        for (int at = 0; at < length; at += Piece)
        {
            int piece = Math.Min(Piece, length - at);
            var reply = new byte[MaxxUsbProtocol.ReplyLength(piece)];

            // Retried, because every request names its own offset — asking
            // again asks for the same bytes, not the next ones. That is how
            // the capture shows it addressed; if the ECU turns out to keep a
            // read pointer of its own instead, a retry here would skip a
            // piece, and that is the first thing to look at if a download
            // comes back shifted.
            byte[] request = MaxxUsbProtocol.Request(MaxxUsbProtocol.Read, ReadLog, at, piece);

            if (!MaxxUsbProtocol.TryAsk(transport, request, reply, piece, timeout, attempts: 3, out byte[] data, onRetry))
                throw new EcuProtocolException(
                    $"The MaxxECU stopped answering {at:N0} bytes into its stored log. "
                    + "Nothing has been changed on the ECU — this is a read.");

            data.CopyTo(log, at);
            progress?.Invoke(at + piece, length);
        }

        return log;
    }

    /// <summary>
    /// The log as a run of little-endian sixteen-bit values — which is what it
    /// looked like in the capture: tight bands of neighbouring numbers, the
    /// shape of channels sampled at a steady rate rather than of a file
    /// format. <b>Which channel each value belongs to is not known</b>, so
    /// these are raw numbers and not a log anything can be plotted from yet.
    /// A trailing odd byte is left out.
    /// </summary>
    public static ushort[] Samples(ReadOnlySpan<byte> log)
    {
        var samples = new ushort[log.Length / 2];

        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadUInt16LittleEndian(log[(i * 2)..]);

        return samples;
    }
}
