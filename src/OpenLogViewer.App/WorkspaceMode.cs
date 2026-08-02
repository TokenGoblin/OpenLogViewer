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
}
