using System.IO;
using System.Windows;
using System.Windows.Controls;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>One fault, ready to draw.</summary>
/// <param name="Code">The five characters that get typed into a search.</param>
/// <param name="Detail">What it means, or why nothing can be said about it.</param>
/// <param name="Where">Which system, and whose number it is.</param>
public sealed record FaultRow(string Code, string Detail, string Where)
{
    public static FaultRow For(Dtc fault) => new(
        fault.Code,
        fault.Detail,
        fault.Authority == DtcAuthority.Generic
            ? $"{fault.System} · defined by SAE J2012 — the same on every vehicle"
            : $"{fault.System} · the vehicle manufacturer's own number");
}

/// <summary>
/// The car's fault codes, read and cleared.
///
/// The one thing this application can ask a standard vehicle to <em>do</em>
/// rather than merely report, which is why erasing is behind a confirmation that
/// spells out what goes with the codes. Mode 04 does not clear the fault — it
/// clears the evidence of the fault, and along with it the freeze frame and the
/// readiness monitors. A person who clears a light before a test has made the
/// test impossible to pass rather than easier.
///
/// The scan runs on this thread and holds the adapter while it does, so the
/// gauges behind this window visibly stop for a second or two. That is the link
/// being honest about what it is doing: OBD2 has one conversation at a time.
/// </summary>
public partial class FaultsPanel : UserControl
{
    private MainViewModel? _vm;

    public FaultsPanel() => InitializeComponent();

    /// <summary>
    /// Points the panel at a view model, without asking the car anything.
    ///
    /// Separate from scanning because the two hosts want different moments. A
    /// window scans as it opens, since opening it is the request. A tab is built
    /// with the rest of the main window long before anybody switches to it, and
    /// scanning then would take the adapter for a view nobody is looking at.
    /// </summary>
    public void Attach(MainViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        Draw();
    }

    /// <summary>
    /// Gives the panel a way out, for a host that is a window.
    ///
    /// A tab has no such thing, so the button stays hidden unless asked for
    /// rather than being shown and doing something unhelpful.
    /// </summary>
    public void ShowCloseButton(Action close)
    {
        ArgumentNullException.ThrowIfNull(close);

        CloseButton.Visibility = Visibility.Visible;
        CloseButton.Click += (_, _) => close();
    }

    /// <summary>
    /// Scans the first time the panel is actually looked at.
    ///
    /// The calibration tab exists from startup and is switched to much later, so
    /// this is what makes it show the car's codes on arrival rather than an empty
    /// frame with a Scan button. Only the first time — coming back to a tab
    /// should not silently take the adapter off the gauges again.
    /// </summary>
    public void ScanOnFirstSight()
    {
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true || _scanned) return;
            if (_vm is not { IsObd2Live: true }) return;

            _scanned = true;
            Scan();
        };
    }

    private bool _scanned;

    /// <summary>What the last scan found, for the tests and for the erase to check.</summary>
    public FaultScan? Result { get; private set; }

    private void OnScanClick(object sender, RoutedEventArgs e) => Scan();

    /// <summary>
    /// Asks the car, and draws whatever came back.
    ///
    /// Failure is reported in the window rather than thrown. A car that has been
    /// switched off mid-scan is the ordinary way this goes wrong, and an exception
    /// dialog over a diagnostic window says less than the window itself can.
    /// </summary>
    public void Scan()
    {
        Status.Text = "Asking the vehicle…";
        ScanButton.IsEnabled = false;
        ClearButton.IsEnabled = false;

        try
        {
            Result = _vm?.ScanFaults();
        }
        catch (Exception ex) when (ex is EcuProtocolException or IOException or InvalidOperationException)
        {
            Result = null;
            Status.Text = ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }

        Draw();
    }

    private void Draw()
    {
        if (Result is not { } scan)
        {
            Headline.Text = "Nothing was read.";
            Link.Text = _vm is { IsObd2Live: true }
                ? "The adapter is connected but did not answer."
                : "No OBD2 vehicle is connected.";

            // Left alone where the scan set it to the reason it failed. Otherwise
            // this still says "asking the vehicle", which reads as a window that
            // is still working on it rather than one that has finished and found
            // nothing.
            if (Status.Text.EndsWith('…')) Status.Text = Link.Text;

            Fill(StoredHeading, StoredList, []);
            Fill(PendingHeading, PendingList, [], PendingNote);
            Fill(PermanentHeading, PermanentList, [], PermanentNote);
            Warning.Visibility = Visibility.Collapsed;
            ClearButton.IsEnabled = false;

            return;
        }

        // The lamp first, because it is the thing the driver has already seen and
        // the reason they opened this. The count beside it, because a lamp with no
        // codes behind it and codes with no lamp are both worth noticing.
        Headline.Text = scan.Clean
            ? "No fault codes. The warning lamp is off."
            : scan.MilOn
                ? $"The warning lamp is on. {Count(scan)}."
                : $"The lamp is off, but {Count(scan).ToLowerInvariant()}.";

        Link.Text = scan.Protocol.Length > 0
            ? $"{scan.Protocol} · the vehicle counts {scan.ReportedCount} confirmed"
            : $"The vehicle counts {scan.ReportedCount} confirmed";

        // Two ways a scan can be less than it looks, and both are worth saying
        // out loud: a count that outruns the list means something answered PID 01
        // that did not answer mode 03, and a mode that went unanswered means the
        // list is short for a reason nobody can see.
        string trouble = scan.Trouble.Length > 0
            ? scan.Trouble
            : scan.CountDisagrees
                ? $"The vehicle says it has {scan.ReportedCount} confirmed fault"
                  + $"{(scan.ReportedCount == 1 ? "" : "s")} but listed {scan.Stored.Count}. "
                  + "A module that answers the count and not the list has not been fully read."
                : "";

        Warning.Text = trouble;
        Warning.Visibility = trouble.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Fill(StoredHeading, StoredList, scan.Stored);
        Fill(PendingHeading, PendingList, scan.Pending, PendingNote);
        Fill(PermanentHeading, PermanentList, scan.Permanent, PermanentNote);

        // Nothing to erase is not an error, and a button that does nothing is
        // worse than one that is plainly unavailable. Permanent codes alone do
        // not enable it — mode 04 cannot touch those.
        ClearButton.IsEnabled = scan.Stored.Count + scan.Pending.Count > 0;

        Status.Text = scan.Summary;
    }

    private static string Count(FaultScan scan)
    {
        var parts = new List<string>();

        if (scan.Stored.Count > 0)
            parts.Add($"{scan.Stored.Count} confirmed fault{(scan.Stored.Count == 1 ? "" : "s")}");

        if (scan.Pending.Count > 0) parts.Add($"{scan.Pending.Count} pending");
        if (scan.Permanent.Count > 0) parts.Add($"{scan.Permanent.Count} permanent");

        return parts.Count == 0 ? "No codes were listed" : string.Join(", ", parts);
    }

    /// <summary>
    /// Fills one section, and hides the whole thing when it is empty.
    ///
    /// An empty heading over an empty list reads as a section that failed to
    /// load. Most cars have nothing pending and nothing permanent, and three
    /// blank headings would be the usual sight.
    /// </summary>
    private static void Fill(
        TextBlock heading, ItemsControl list, IReadOnlyList<Dtc> faults, TextBlock? note = null)
    {
        list.ItemsSource = faults.Select(FaultRow.For).ToList();

        Visibility visible = faults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        heading.Visibility = visible;
        list.Visibility = visible;
        if (note is not null) note.Visibility = visible;
    }

    /// <summary>
    /// Erases, once the person asking has been told what that means.
    ///
    /// Confirmed at least as firmly as burning a tune, and for the same reason:
    /// what is lost is not recoverable. The freeze frame is the only record of
    /// what the engine was doing at the moment the fault occurred — the single
    /// most useful thing for working out an intermittent — and it goes with the
    /// code. The readiness monitors go too, which is why a car cleared this
    /// morning cannot pass a test this afternoon whatever its condition.
    /// </summary>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (Result is null) return;

        // The confirmation is the view model's, along with the wording, so that
        // the erase cannot be reached without it from anywhere else.
        ClearButton.IsEnabled = false;
        Status.Text = "Erasing…";

        FaultClear? cleared;

        try
        {
            cleared = _vm?.ClearFaults();
        }
        catch (Exception ex) when (ex is EcuProtocolException or IOException or InvalidOperationException)
        {
            Status.Text = ex.Message;
            ClearButton.IsEnabled = true;

            return;
        }

        if (cleared is null)
        {
            // Null is "nothing was attempted", which is either of two things.
            // The button goes back on for both: a declined erase is a decision,
            // not a state to be stuck in.
            Status.Text = _vm is { IsObd2Live: true }
                ? "Nothing was erased."
                : "No OBD2 vehicle is connected.";

            ClearButton.IsEnabled = true;

            return;
        }

        MessageBox.Show(
            Window.GetWindow(this), cleared.Message, "OpenLogViewer", MessageBoxButton.OK,
            cleared.Erased ? MessageBoxImage.Information : MessageBoxImage.Warning);

        // Read back rather than assumed cleared. A code whose fault is still
        // present can be set again before the erase has finished, and a window
        // that shows an empty list it did not verify is telling a story rather
        // than reporting one.
        Scan();
    }

    /// <summary>Wired by the host through <see cref="ShowCloseButton"/>; unused otherwise.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // Deliberately empty. The handler exists because the XAML names it; what
        // closing means belongs to whatever is hosting this.
    }
}
