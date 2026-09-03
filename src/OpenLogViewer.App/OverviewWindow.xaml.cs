using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;

namespace OpenLogViewer.App;

/// <summary>
/// What a connected AI agent found in the open tune and log, published through
/// <c>push_overview_report</c>.
///
/// <para>
/// Unlike <see cref="InsightsWindow"/>, this window does not compute anything itself
/// — everything but <see cref="Tally"/> is bound straight to <see cref="MainViewModel"/>,
/// which the MCP tool mutates directly, so a new revision appears the moment it is
/// published with no refresh button needed. <see cref="Tally"/> exists because it
/// aggregates <see cref="OverviewFinding.Accepted"/> across every finding, which the
/// view model has no reason to track itself — it is purely what the checkboxes on
/// screen add up to.
/// </para>
/// </summary>
public partial class OverviewWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm;

    public OverviewWindow(MainViewModel vm)
    {
        _vm = vm;

        InitializeComponent();
        DataContext = vm;

        Subscribe(vm.OverviewFindings);
        vm.OverviewFindings.CollectionChanged += OnFindingsChanged;
        Closed += (_, _) =>
        {
            vm.OverviewFindings.CollectionChanged -= OnFindingsChanged;
            Unsubscribe(vm.OverviewFindings);
        };

        RaiseTally();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>How many of the proposed changes are ticked, said in one line.</summary>
    public string Tally { get; private set; } = "";

    private void OnFindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) Unsubscribe(e.OldItems.Cast<OverviewFinding>());
        if (e.NewItems is not null) Subscribe(e.NewItems.Cast<OverviewFinding>());

        RaiseTally();
    }

    private void OnFindingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OverviewFinding.Accepted)) RaiseTally();
    }

    private void Subscribe(IEnumerable<OverviewFinding> findings)
    {
        foreach (OverviewFinding finding in findings) finding.PropertyChanged += OnFindingChanged;
    }

    private void Unsubscribe(IEnumerable<OverviewFinding> findings)
    {
        foreach (OverviewFinding finding in findings) finding.PropertyChanged -= OnFindingChanged;
    }

    private void RaiseTally()
    {
        int changeable = _vm.OverviewFindings.Count(f => f.HasChange);
        int accepted = _vm.OverviewFindings.Count(f => f.Accepted);
        int notes = _vm.OverviewFindings.Count - changeable;

        Tally = changeable == 0
            ? notes == 0 ? "" : $"{notes} note{(notes == 1 ? "" : "s")}"
            : $"{accepted} of {changeable} change{(changeable == 1 ? "" : "s")} selected"
              + (notes > 0 ? $" · {notes} note{(notes == 1 ? "" : "s")}" : "");

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tally)));
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _vm.ClearOverview();

    /// <summary>
    /// A ready-made analysis request, for pasting into whichever chat the connected
    /// agent is on the other end of.
    ///
    /// <para>
    /// MCP has no channel for this window to make an agent start working — only the
    /// human side of a chat can begin a turn, and Claude Code does not implement the
    /// one protocol feature (sampling) that could act as a workaround. Putting the
    /// prompt on the clipboard is the honest version of "one click": it removes the
    /// work of writing the request, not the work of pasting it.
    /// </para>
    /// </summary>
    private void OnAskAiClick(object sender, RoutedEventArgs e)
    {
        const string prompt =
            "Using the OpenLogViewer MCP tools, fully analyze whatever tune and/or log is "
            + "currently open: review the tune's key settings, check VE/ignition/AFR trends in "
            + "the log, scan for faults, and note anything that looks off. Give concrete tuning "
            + "suggestions. When you have findings, call push_overview_report so I can review "
            + "and accept the ones I want.";

        try
        {
            Clipboard.SetText(prompt);
            App.Report("Analysis request copied — paste it into your connected AI agent's chat.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            App.Report("The clipboard was busy — nothing was copied.");
        }
    }

    /// <summary>
    /// The accepted changes as text, ready to paste back into the chat with whichever
    /// agent published them.
    /// </summary>
    private void OnCopySelectedClick(object sender, RoutedEventArgs e)
    {
        OverviewFinding[] accepted = [.. _vm.OverviewFindings.Where(f => f.Accepted)];

        if (accepted.Length == 0)
        {
            App.Report("Nothing is ticked — nothing to copy.");
            return;
        }

        var text = new StringBuilder();
        text.AppendLine("Apply the following accepted changes, then publish the next revision:");
        text.AppendLine();

        foreach (OverviewFinding finding in accepted)
        {
            text.AppendLine($"- {finding.Title}: {finding.Change?.Description}");
        }

        try
        {
            Clipboard.SetText(text.ToString());
            App.Report($"{accepted.Length} change{(accepted.Length == 1 ? "" : "s")} copied.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            App.Report("The clipboard was busy — nothing was copied.");
        }
    }
}
