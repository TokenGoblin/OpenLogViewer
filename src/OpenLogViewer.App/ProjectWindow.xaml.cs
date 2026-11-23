using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// The vehicle's project: what is wrong with the tune, what has been tried, and
/// what happened.
///
/// <para>
/// Shown as prose rather than as a grid of fields, because that is how it is
/// read — and because it is the same text an assistant is handed, so what a
/// person sees here and what a model sees are the same thing. A window where
/// those two differ is a window that will eventually mislead one of them.
/// </para>
/// </summary>
public partial class ProjectWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm;

    public ProjectWindow(MainViewModel viewModel)
    {
        _vm = viewModel;

        InitializeComponent();
        DataContext = this;

        _vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnViewModelChanged;

        Reload();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Project) or nameof(MainViewModel.ProjectSummary))
            Show(_vm.Project);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Summary => _vm.ProjectSummary;

    /// <summary>Fills the picker, and offers the open project or a sensible new name.</summary>
    private void Reload()
    {
        Vehicles.Items.Clear();

        foreach (string vehicle in _vm.Projects.Vehicles()) Vehicles.Items.Add(vehicle);

        // The open project selected, so the box agrees with the line under it.
        if (_vm.Project is { } open && Vehicles.Items.Contains(open.Vehicle))
            Vehicles.SelectedItem = open.Vehicle;
        else if (Vehicles.Items.Count > 0) Vehicles.SelectedIndex = 0;

        // And a name to start from, where the firmware offers one.
        if (NewVehicle.Text.Length == 0 && _vm.Project is null) NewVehicle.Text = Suggested();

        Show(_vm.Project);
    }

    /// <summary>
    /// A name to start from: the firmware on the other end, where there is one.
    ///
    /// Better than an empty box. Somebody with an ECU connected has a vehicle in
    /// front of them, and the signature is the one thing about it the
    /// application already knows.
    /// </summary>
    private string Suggested() =>
        _vm.LiveSignature.Length > 0 ? _vm.LiveSignature : "";

    /// <summary>
    /// Fills the two version pickers, and hides them until there is a
    /// comparison to make.
    ///
    /// Newest first, and defaulting to the last two — which is the comparison
    /// somebody almost always wants: what did the change I just made do.
    /// </summary>
    private void FillVersions(TuningProject? project)
    {
        FromVersion.Items.Clear();
        ToVersion.Items.Clear();

        TuneVersion[] versions = project is null ? [] : [.. project.Versions.AsEnumerable().Reverse()];

        CompareRow.Visibility = versions.Length >= 2 ? Visibility.Visible : Visibility.Collapsed;

        if (versions.Length < 2) return;

        foreach (TuneVersion version in versions)
        {
            FromVersion.Items.Add(version.Id);
            ToVersion.Items.Add(version.Id);
        }

        // Newest against the one before it.
        ToVersion.SelectedIndex = 0;
        FromVersion.SelectedIndex = 1;
    }

    private void OnCompareClick(object sender, RoutedEventArgs e)
    {
        string from = FromVersion.SelectedItem as string ?? "";
        string to = ToVersion.SelectedItem as string ?? "";

        if (from.Length == 0 || to.Length == 0) return;

        Brief.Text = _vm.CompareVersions(from, to);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Show(_vm.Project);

    private void Show(TuningProject? project)
    {
        FillVersions(project);

        Brief.Text = project is null
            ? "No project open.\n\n"
              + "A project keeps what the insights found on each log, and what you are trying to "
              + "fix — what is wrong, what you changed, and whether it worked. That is the part no "
              + "single log can tell you, and the part an assistant needs in order to be useful "
              + "rather than start over every time.\n\n"
              + "Name a vehicle above and press Open."
            : TuningProjectStore.Brief(project, sessions: 10);

        Raise(nameof(Summary));
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        string vehicle = NewVehicle.Text?.Trim() ?? "";

        if (vehicle.Length == 0)
        {
            MessageBox.Show(this, "Give the vehicle a name first.", "OpenLogViewer",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _vm.OpenProject(vehicle);
        NewVehicle.Text = "";
        Reload();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        string vehicle = Vehicles.SelectedItem as string ?? "";

        if (vehicle.Length == 0)
        {
            MessageBox.Show(this, "Pick a project first, or start a new one below.", "OpenLogViewer",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _vm.OpenProject(vehicle);
        Reload();
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Project is null)
        {
            MessageBox.Show(this, "Open a project first, so there is somewhere to record it.",
                            "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Asked for, because it is the note that makes a sitting worth having:
        // "after VE +4% above 150 kPa" is what turns a row of findings into
        // evidence about a change.
        string said = _vm.RecordSitting(Note.Text?.Trim() ?? "");

        Note.Text = "";
        Show(_vm.Project);

        MessageBox.Show(this, said, "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnKeepTuneClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Project is null)
        {
            MessageBox.Show(this, "Open a project first.", "OpenLogViewer",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string said = _vm.KeepTune(Note.Text?.Trim() ?? "");

        Show(_vm.Project);

        MessageBox.Show(this, said, "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Brief.Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
            // Another program is holding the clipboard. Not worth a dialog.
        }
    }

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        string folder = _vm.Project is { } project
            ? Path.GetDirectoryName(_vm.Projects.PathFor(project.Vehicle)) ?? _vm.Projects.Root
            : _vm.Projects.Root;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
                                         or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "OpenLogViewer",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
