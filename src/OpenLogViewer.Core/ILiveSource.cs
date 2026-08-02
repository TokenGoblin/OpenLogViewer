namespace OpenLogViewer.Core;

/// <summary>
/// Something a live session can poll for a row of readings.
///
/// The poll loop is the same whatever is on the other end — pace it, buffer it,
/// write it to disk, notice when it stops and try to get it back. Only the way a
/// row is obtained differs, and there are now two very different ways: a
/// MegaSquirt or rusEFI answers a request for a block of memory, while a MaxxECU
/// pushes frames of its own accord once it has been told what to send.
/// </summary>
public interface ILiveSource : IDisposable
{
    /// <summary>Channel names, in the order <see cref="Read"/> returns them.</summary>
    IReadOnlyList<string> Names { get; }

    IReadOnlyList<string> Units { get; }

    /// <summary>Display precision for each channel.</summary>
    IReadOnlyList<int> Digits { get; }

    /// <summary>Opens the link, or throws.</summary>
    void Open();

    /// <summary>
    /// Reads one row of values, one per name.
    ///
    /// Blocks until a reading is available or the attempt fails. Throwing is how
    /// a lost link is reported; the session counts failures and decides when to
    /// give up on it.
    /// </summary>
    double[] Read();

    /// <summary>
    /// Puts a lost link back together, throwing if it cannot.
    ///
    /// Called after a run of failures. Succeeding here is not enough to declare
    /// the link healthy — the session proves that with a read.
    /// </summary>
    void Recover();

    /// <summary>Replies thrown away and asked for again, over the session.</summary>
    int Retries { get; }
}
