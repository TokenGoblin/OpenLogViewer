namespace OpenLogViewer.Core;

/// <summary>
/// Implemented by an <see cref="ILiveSource"/> that decodes more than it
/// normally reports.
///
/// <see cref="TunerStudioSource"/> decodes a firmware's entire realtime block
/// but only ever hands <see cref="ILiveSource.Read"/> the channels the
/// firmware's own datalog definition names — a human-curated subset. Nothing
/// about that changes here: this is a second, optional view onto the same
/// poll, for whatever wants everything the firmware actually publishes,
/// including a field the datalog author never thought to log.
/// </summary>
public interface IRawTelemetrySource
{
    /// <summary>Every channel the firmware's <c>[OutputChannels]</c> section defines.</summary>
    IReadOnlyList<string> RawNames { get; }

    IReadOnlyList<string> RawUnits { get; }

    /// <summary>
    /// The full decoded block from the most recent <see cref="ILiveSource.Read"/>,
    /// or null before the first one. Replaced wholesale each poll rather than
    /// mutated, so a reader on another thread never sees a half-written frame.
    /// </summary>
    double[]? LastRawFrame { get; }
}
