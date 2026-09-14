namespace OpenLogViewer.Core;

/// <summary>
/// A MaxxECU's CAN bus, read over the USB cable that is already carrying the
/// telemetry.
///
/// <para>
/// The ECU is the interface. There is no adapter and no second cable: frames are
/// collected by the controller's own driver, put in a ring, and handed over on
/// request — so whatever the ECU is plugged into, this can watch. Its hardware
/// filter accepts everything and the ring is fed before the ECU does any of its
/// own identifier matching, so unknown identifiers arrive too, which is what
/// makes it worth pointing at a bus nobody has documented.
/// </para>
/// <para>
/// The ring holds sixty frames and a read takes fifteen, so a busy bus needs
/// asking often. What stops that being a guess is the drop counter: the ECU
/// counts every frame it could not keep, across both the driver's buffer and the
/// ring, so a capture can say how much it missed instead of looking complete.
/// </para>
/// </summary>
public sealed class MaxxCanSource : ICanSource
{
    private readonly MaxxUsbSource _ecu;
    private readonly bool _ownsEcu;

    private int? _lastCount;

    /// <summary>
    /// Watches the bus of an ECU something else is already talking to.
    ///
    /// The session owns the cable and serialises what goes on it, so this asks it
    /// rather than opening the device again — two programs cannot hold a MaxxECU,
    /// and neither can two parts of this one.
    /// </summary>
    public MaxxCanSource(MaxxUsbSource ecu, bool ownsEcu = false)
    {
        _ecu = ecu ?? throw new ArgumentNullException(nameof(ecu));
        _ownsEcu = ownsEcu;
    }

    /// <inheritdoc/>
    public CanSourceKind Kind { get; } = new(
        "MaxxECU",
        SeesEveryId: true,
        CountsDrops: true,
        Caveat: "Frames arrive at whatever CAN speed the ECU itself is set to, so a bus running "
                + "at another speed reads as silence rather than as an error.");

    /// <summary>
    /// Frames the ECU has counted as lost since this began, or null before it has
    /// been asked.
    ///
    /// Counted across the driver's buffer and the ring together, which is one
    /// number and not two — so nought here really does mean nothing was missed,
    /// short of the controller's own hardware queue overrunning, which needs the
    /// receive interrupt starved rather than merely a busy bus.
    /// </summary>
    public int? Dropped { get; private set; }

    /// <summary>
    /// Arms the analyzer and starts counting from where the ECU is now.
    ///
    /// <para>
    /// Nothing reaches the ring until the tune's <see cref="MaxxCan.EnableSetting"/>
    /// is set, and <b>this does not set it</b>. Turning it on means writing to the
    /// tune, and a MaxxECU write is permanent the moment it lands — which is not
    /// something to do as a side effect of opening a window. It is offered
    /// deliberately, or ticked in MTune, and <see cref="IsArmed"/> says which.
    /// </para>
    /// </summary>
    public void Open()
    {
        // Whatever the ECU had already lost is not this capture's loss, so the
        // count starts from here rather than from whenever the ECU booted — the
        // counter is free-running and nothing resets it, arming the analyzer
        // included.
        _lastCount = DropCount();
        Dropped = _lastCount is null ? null : 0;
    }

    /// <summary>
    /// Whether the ECU is set to put frames in the ring at all.
    ///
    /// Read from the tune rather than assumed, because the ECU clears the flag
    /// whenever the link drops — so an analyzer armed in MTune an hour ago is not
    /// armed now, and a silent bus and an unarmed ECU look identical.
    /// </summary>
    public bool IsArmed(ReadOnlySpan<byte> tune) =>
        MaxxCan.EnableAt < tune.Length && tune[MaxxCan.EnableAt] != 0;

    /// <inheritdoc/>
    public IReadOnlyList<CanFrame> Read()
    {
        var frames = new List<CanFrame>();

        // Until the ring gives back less than a full read, which is how it says
        // it is empty. A busy bus otherwise falls behind one read at a time.
        while (true)
        {
            IReadOnlyList<CanFrame> some = _ecu.ReadCanFrames(MaxxCan.MostPerRead);

            frames.AddRange(some);

            if (some.Count < MaxxCan.MostPerRead) break;
        }

        if (DropCount() is { } now && _lastCount is { } last)
        {
            // Added up rather than subtracted from the start.
            //
            // The counter is sixteen bits and free-running, so on a bus losing
            // frames steadily it comes all the way round in well under a minute.
            // Subtracting the baseline would then report the remainder — a
            // capture that lost seventy thousand frames would claim it lost four
            // and a half thousand, which is worse than admitting ignorance
            // because it looks like a measurement.
            Dropped = (Dropped ?? 0) + MaxxCan.Since(last, now);
            _lastCount = now;
        }

        return frames;
    }

    /// <summary>What the ECU says it has lost, or null when it will not say.</summary>
    private int? DropCount() => _ecu.CanDropCount();

    public void Dispose()
    {
        if (_ownsEcu) _ecu.Dispose();
    }
}
