//! Ported from `WorkspaceMode.cs`. Owned by package `w6-ui-base`.

/// What the window is being used for.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub enum WorkspaceMode {
    /// Reading a recording: the plot and the heat table.
    #[default]
    Log,

    /// Watching a live connection on the firmware's own dials.
    Gauges,

    /// Working on the tune itself.
    Calibration,

    /// How to use the application.
    ///
    /// A mode of its own rather than a menu item behind a link, because of where this gets used:
    /// a laptop plugged into a car in a garage with no internet is exactly where somebody needs to
    /// look something up, and it is the same reasoning that makes the installer self-contained.
    Guide,
}

impl WorkspaceMode {
    /// In the order the C# enum declares them, which is the order the mode switcher shows.
    pub const ALL: [WorkspaceMode; 4] = [
        WorkspaceMode::Log,
        WorkspaceMode::Gauges,
        WorkspaceMode::Calibration,
        WorkspaceMode::Guide,
    ];
}

/// Which of the three readings of a recording is on screen.
///
/// All three are the same samples. The plot orders them by time, which is the only one that shows
/// a sequence: what led to what. The other two throw time away and order by two channels instead,
/// which is what makes them comparable to a tuning table: the heat table bins that into cells you
/// could edit, and the scatter leaves the samples where they landed.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub enum LogView {
    /// Traces against time.
    #[default]
    Plot,

    /// Binned into a table, in the shape of a tuning table.
    Histogram,

    /// Every sample at its own X and Y, coloured by a third channel.
    Scatter,
}

impl LogView {
    pub const ALL: [LogView; 3] = [LogView::Plot, LogView::Histogram, LogView::Scatter];
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_modes_are_the_four_the_window_switches_between_and_start_in_log() {
        assert_eq!(4, WorkspaceMode::ALL.len());
        assert_eq!(WorkspaceMode::Log, WorkspaceMode::default());
        assert_eq!(WorkspaceMode::Guide, *WorkspaceMode::ALL.last().unwrap());
    }

    #[test]
    fn a_recording_opens_on_the_plot() {
        assert_eq!(LogView::Plot, LogView::default());
        assert_eq!(3, LogView::ALL.len());
    }
}
