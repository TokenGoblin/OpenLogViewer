using System.Globalization;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Writing a capture out in the formats the tools for this work already read.
///
/// <para>
/// Worth doing on the first day rather than the tenth. Reading an undocumented
/// bus is long work and nobody should have to do all of it in one program —
/// SavvyCAN has years of analysis in it, and a capture that cannot be opened
/// there is a capture somebody has to take again. A sniffer that only shows you
/// frames is a dead end with a nice view.
/// </para>
/// </summary>
public static class CanExport
{
    /// <summary>
    /// SavvyCAN's own CSV, which is what most of this work ends up being done in.
    ///
    /// Timestamps are microseconds, which is what SavvyCAN expects and what the
    /// MaxxECU happens to give — so frames keep the timing the ECU measured
    /// rather than the timing of when this program got round to asking.
    /// </summary>
    public static string SavvyCan(IEnumerable<CanFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var text = new StringBuilder("Time Stamp,ID,Extended,Dir,Bus,LEN,D1,D2,D3,D4,D5,D6,D7,D8\n");

        foreach (CanFrame frame in frames)
        {
            text.Append(frame.At.ToString(CultureInfo.InvariantCulture))
                .Append(",0x").Append(frame.Id.ToString("X8", CultureInfo.InvariantCulture))
                .Append(frame.IsExtended ? ",true" : ",false")
                .Append(",Rx,0,")
                .Append(frame.Length.ToString(CultureInfo.InvariantCulture));

            // Eight columns whatever the length, because the header promises
            // eight and a short row shifts every column after it in a reader
            // that counts commas.
            for (int i = 0; i < 8; i++)
                text.Append(i < frame.Length
                    ? $",0x{frame.Data[i]:X2}"
                    : ",0x00");

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// The candump log format, as SocketCAN writes it and as most Linux tooling
    /// reads it.
    ///
    /// <c>(seconds.microseconds) bus id#data</c>, with the identifier three hex
    /// digits for a standard frame and eight for an extended one — which is how
    /// a reader tells them apart, there being no other flag in the line.
    /// </summary>
    public static string CanDump(IEnumerable<CanFrame> frames, string bus = "can0")
    {
        ArgumentNullException.ThrowIfNull(frames);

        var text = new StringBuilder();

        foreach (CanFrame frame in frames)
            text.Append('(')
                .Append((frame.At / 1_000_000).ToString(CultureInfo.InvariantCulture))
                .Append('.')
                .Append((frame.At % 1_000_000).ToString("000000", CultureInfo.InvariantCulture))
                .Append(") ")
                .Append(bus)
                .Append(' ')
                .Append(frame.IsExtended
                    ? frame.Id.ToString("X8", CultureInfo.InvariantCulture)
                    : frame.Id.ToString("X3", CultureInfo.InvariantCulture))
                .Append('#')
                .Append(Convert.ToHexString(frame.Data))
                .Append('\n');

        return text.ToString();
    }

    /// <summary>
    /// What was seen at each identifier, for reading rather than for a tool.
    ///
    /// The bits that moved are written as a mask in hex, because that is the
    /// column somebody actually works from: it says where in the payload to look
    /// before anything is known about what the payload means.
    /// </summary>
    public static string Summary(IEnumerable<CanId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var text = new StringBuilder("ID,Extended,Count,Rate Hz,Gap us,Jitter us,Periodic,Data,Bits moved\n");

        foreach (CanId id in ids)
            text.Append(id.Label)
                .Append(id.IsExtended ? ",true," : ",false,")
                .Append(id.Count.ToString(CultureInfo.InvariantCulture))
                .Append(',').Append(id.Rate?.ToString("F1", CultureInfo.InvariantCulture) ?? "")
                .Append(',').Append(id.Gap?.ToString("F0", CultureInfo.InvariantCulture) ?? "")
                .Append(',').Append(id.Jitter.ToString("F0", CultureInfo.InvariantCulture))
                .Append(id.LooksPeriodic ? ",yes," : ",no,")
                .Append(Convert.ToHexString(id.Data))
                .Append(',').Append(Convert.ToHexString(id.Changed))
                .Append('\n');

        return text.ToString();
    }
}
