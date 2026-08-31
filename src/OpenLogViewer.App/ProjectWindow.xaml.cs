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

        Vehicles.Text = _vm.Project?.Vehicle ?? Suggested();

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

    private void Show(TuningProject? project)
    {
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

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        string vehicle = Vehicles.Text?.Trim() ?? "";

        if (vehicle.Length == 0)
        {
            MessageBox.Show(this, "Name the vehicle first.", "OpenLogViewer",
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
