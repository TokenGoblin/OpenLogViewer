using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.PlotInvalidated += OnPlotInvalidated;
        _vm.HistogramInvalidated += RebuildHistogram;
        Plot.CursorSampleChanged += _vm.UpdateCursor;
        Plot.HoverChannelChanged += _vm.HighlightChannel;

        InputBindings.Add(new KeyBinding(new RelayCommand(Open), Key.O, ModifierKeys.Control));
    }

    public void LoadFile(string path)
    {
        try
        {
            _vm.Load(path);
            Plot.SetDocument(_vm.Document, _vm.Channels);
        }
        catch (Exception ex) when (ex is LogFormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"Could not open {Path.GetFileName(path)}.\n\n{ex.Message}",
                "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Switches to the binned table view, optionally selecting one of the
    /// breakpoint sources by position (0 is "from the log data").
    /// </summary>
    public void ShowHistogram(int axisSource = 0, bool colourByCount = false, bool countStatistic = false)
    {
        _vm.ShowHistogram = true;

        if (axisSource > 0 && axisSource < _vm.AxisSources.Count)
            _vm.AxisSource = _vm.AxisSources[axisSource];

        if (colourByCount) _vm.ColorByCount = true;
        if (countStatistic) _vm.StatCount = true;
    }

    /// <summary>
    /// Positions the cursor from fractions of the plot area. Used to capture a
    /// deterministic screenshot with the hover readout showing.
    /// </summary>
    public void PreviewPointer(double fractionX, double fractionY) =>
        Plot.MoveCursorTo(new Point(Plot.ActualWidth * fractionX, Plot.ActualHeight * fractionY));

    private void Open()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open datalog",
            Filter = LogReaderFactory.OpenFileFilter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
            LoadFile(dialog.FileName);
    }

    /// <summary>
    /// Rebuilds the table. The sample window comes from the plot's current zoom
    /// when the user has asked to restrict it, so a table can be built from one
    /// pull rather than the whole log.
    /// </summary>
    private void RebuildHistogram()
    {
        if (_vm.Document is not { SampleCount: > 0 } doc)
        {
            Histogram.SetTable(null, _vm.ColorByCount);
            return;
        }

        int first = 0, last = doc.SampleCount - 1;
        if (_vm.HistogramZoomOnly && Plot.ViewEnd > Plot.ViewStart)
        {
            first = doc.IndexAtTime(Plot.ViewStart);
            last = doc.IndexAtTime(Plot.ViewEnd);
        }

        _vm.RebuildHistogram(first, last);
        Histogram.SetTable(_vm.Table, _vm.ColorByCount);
    }

    private void OnPlotInvalidated()
    {
        // Channel visibility changed but the document did not, so keep the
        // current zoom window and just redraw.
        if (Plot.ViewEnd > Plot.ViewStart) Plot.Refresh();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => Open();

    private void OnResetZoomClick(object sender, RoutedEventArgs e) => Plot.ResetView();

    private void OnPlotCommonClick(object sender, RoutedEventArgs e) => _vm.PlotCommon();

    // ----- data filters -----------------------------------------------------

    private void OnAddFilterClick(object sender, RoutedEventArgs e) =>
        FilterEditor.Visibility = Visibility.Visible;

    private void OnConfirmFilterClick(object sender, RoutedEventArgs e)
    {
        if (_vm.AddFilter()) FilterEditor.Visibility = Visibility.Collapsed;
    }

    private void OnCancelFilterClick(object sender, RoutedEventArgs e) =>
        FilterEditor.Visibility = Visibility.Collapsed;

    private void OnFiltersNoneClick(object sender, RoutedEventArgs e) => _vm.SetAllFilters(false);

    private void OnDeleteFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        FilterItem? item = element.DataContext as FilterItem
            ?? ((element.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as FilterItem;

        if (item is not null) _vm.DeleteFilter(item);
    }

    // ----- presets ----------------------------------------------------------

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        PresetNameRow.Visibility = Visibility.Visible;
        PresetNameBox.Text = "";
        PresetNameBox.Focus();
    }

    private void OnConfirmPresetClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SavePreset(PresetNameBox.Text))
            PresetNameRow.Visibility = Visibility.Collapsed;
    }

    private void OnCancelPresetClick(object sender, RoutedEventArgs e)
    {
        PresetNameRow.Visibility = Visibility.Collapsed;
        _vm.ResetHint();
    }

    private void OnPresetNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnConfirmPresetClick(sender, e);
        else if (e.Key == Key.Escape) OnCancelPresetClick(sender, e);
        else return;

        e.Handled = true;
    }

    private void OnApplyPresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetFrom(sender) is { } preset) _vm.ApplyPreset(preset);
    }

    private void OnOverwritePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetFrom(sender) is { } preset) _vm.SavePreset(preset.Name);
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetFrom(sender) is { } preset) _vm.DeletePreset(preset);
    }

    private static ChannelPreset? PresetFrom(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        if (element.DataContext is ChannelPreset preset) return preset;

        return (element.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
            ? target.DataContext as ChannelPreset
            : null;
    }

    private void OnJumpToMaxClick(object sender, RoutedEventArgs e) =>
        JumpTo(ChannelFrom(sender), max: true);

    private void OnJumpToMinClick(object sender, RoutedEventArgs e) =>
        JumpTo(ChannelFrom(sender), max: false);

    private void OnPlotOnlyClick(object sender, RoutedEventArgs e)
    {
        if (ChannelFrom(sender) is not { } item) return;

        _vm.SetAllVisible(false);
        item.IsVisible = true;
    }

    /// <summary>
    /// Resolves the channel a click belongs to. A menu item inside a context menu
    /// is outside the visual tree, so its data context is taken from the row the
    /// menu was opened on.
    /// </summary>
    private static ChannelItem? ChannelFrom(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        if (element.DataContext is ChannelItem item) return item;

        return (element.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
            ? target.DataContext as ChannelItem
            : null;
    }

    /// <summary>
    /// Moves the cursor to where a channel peaks or bottoms out. The channel is
    /// plotted first if it was not already, so the jump has something to land on.
    /// </summary>
    private void JumpTo(ChannelItem? item, bool max)
    {
        if (item is null || _vm.Document is not { } doc) return;

        int index = max ? item.Channel.MaxIndex : item.Channel.MinIndex;
        if (index < 0) return;

        item.IsVisible = true;
        Plot.FocusTime(doc.Time.At(index));
    }

    private void OnShowAllClick(object sender, RoutedEventArgs e) => _vm.SetAllVisible(true);

    private void OnShowNoneClick(object sender, RoutedEventArgs e) => _vm.SetAllVisible(false);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            LoadFile(files[0]);
    }
}
