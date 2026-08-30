namespace OpenLogViewer.App;

/// <summary>What the window is being used for.</summary>
public enum WorkspaceMode
{
    /// <summary>Reading a recording — the plot and the heat table.</summary>
    Log,

    /// <summary>Watching a live connection on the firmware's own dials.</summary>
    Gauges,

    /// <summary>Working on the tune itself.</summary>
    Calibration,

    /// <summary>
    /// How to use the application.
    ///
    /// A mode of its own rather than a menu item behind a link, because of where
    /// this gets used: a laptop plugged into a car in a garage with no internet
    /// is exactly where somebody needs to look something up, and it is the same
    /// reasoning that makes the installer self-contained.
    /// </summary>
    Guide,
}

/// <summary>
/// Which of the three readings of a recording is on screen.
///
/// All three are the same samples. The plot orders them by time, which is the
/// only one that shows a sequence — what led to what. The other two throw time
/// away and order by two channels instead, which is what makes them comparable
/// to a tuning table: the heat table bins that into cells you could edit, and
/// the scatter leaves the samples where they landed.
/// </summary>
public enum LogView
{
    /// <summary>Traces against time.</summary>
    Plot,

    /// <summary>Binned into a table, in the shape of a tuning table.</summary>
    Histogram,

    /// <summary>Every sample at its own X and Y, coloured by a third channel.</summary>
    Scatter,
}
