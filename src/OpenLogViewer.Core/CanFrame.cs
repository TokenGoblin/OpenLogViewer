namespace OpenLogViewer.Core;

/// <summary>
/// One frame off a CAN bus.
///
/// Deliberately not a channel. Everything else live here is a named value
/// sampled over time — a frame is an identifier, some bytes whose meaning is
/// unknown until somebody works it out, and the moment it arrived. Putting one
/// through the channel machinery would mean inventing a name and a scale for
/// something that has neither yet, which is the whole difficulty of reading a
/// bus nobody has documented.
/// </summary>
/// <param name="Id">
/// The identifier, 11-bit or 29-bit. Held as an int rather than masked to eleven
/// bits because an extended id does not fit in eleven and truncating one silently
/// merges it with whatever shares its low bits.
/// </param>
/// <param name="IsExtended">Whether the id is a 29-bit one.</param>
/// <param name="Data">The payload, nought to eight bytes.</param>
/// <param name="At">
/// When it arrived, as the source reports it — microseconds since whatever the
/// source counts from, which is not wall-clock time and is not comparable
/// between sources.
/// </param>
public sealed record CanFrame(int Id, bool IsExtended, byte[] Data, long At)
{
    /// <summary>How many bytes the frame declares, which is what is in <see cref="Data"/>.</summary>
    public int Length => Data.Length;

    /// <summary>The id as it is written down and searched for: hex, and wide enough to sort.</summary>
    public string Label => IsExtended ? $"{Id:X8}" : $"{Id:X3}";

    /// <summary>The payload as a person reads it.</summary>
    public string Hex => Convert.ToHexString(Data);
}

/// <summary>
/// Somewhere frames come from — an ECU relaying its bus, or an adapter that is
/// nothing but a bus.
///
/// <para>
/// The same shape as <see cref="ILiveSource"/> and for the same reason: what sits
/// above it should not know which device is on the other end. What it does add is
/// <see cref="Dropped"/>, because a CAN source can lose frames in a way a channel
/// source cannot, and a capture that quietly loses them is worse than no capture
/// at all — somebody reading an unknown bus concludes an identifier does not
/// exist when in truth it was missed.
/// </para>
/// </summary>
public interface ICanSource : IDisposable
{
    /// <summary>Starts the source, and whatever the device needs to begin listening.</summary>
    void Open();

    /// <summary>
    /// Whatever has arrived since the last call, oldest first. Empty is the
    /// ordinary case on a quiet bus rather than a fault.
    /// </summary>
    IReadOnlyList<CanFrame> Read();

    /// <summary>
    /// Frames the device is known to have lost since the capture began, or null
    /// where it cannot say.
    ///
    /// Null and nought are different answers and must not be shown the same way:
    /// nought is "none lost", null is "this device does not count them", and only
    /// one of those licenses anybody to call a capture complete.
    /// </summary>
    int? Dropped { get; }

    /// <summary>
    /// What this source can honestly be said to do, for the interface to report
    /// rather than imply.
    /// </summary>
    CanSourceKind Kind { get; }
}

/// <summary>
/// What a CAN source is, in the terms that decide whether a capture can be
/// trusted for the job in hand.
/// </summary>
/// <param name="Name">What to call it on screen.</param>
/// <param name="SeesEveryId">
/// Whether it reports identifiers it was not configured for. False makes a source
/// useless for discovering an unknown bus while leaving it perfectly good for
/// watching traffic somebody already knows about.
/// </param>
/// <param name="CountsDrops">Whether <see cref="ICanSource.Dropped"/> means anything.</param>
/// <param name="Caveat">
/// What is true about this source that the numbers do not say — the sentence
/// somebody needs before they believe a capture.
/// </param>
public sealed record CanSourceKind(
    string Name,
    bool SeesEveryId,
    bool CountsDrops,
    string Caveat = "");
