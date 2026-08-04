using System.Windows;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// The fault panel in a window of its own, for reaching it from the menu without
/// leaving whatever view is open.
///
/// All the behaviour is in <see cref="FaultsPanel"/>. This decides only the two
/// things a window decides: that opening it is itself the request to scan, and
/// that it needs a way out.
/// </summary>
public partial class FaultsWindow : Window
{
    public FaultsWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        Panel.Attach(viewModel);
        Panel.ShowCloseButton(Close);

        Show();
        Panel.Scan();
    }

    /// <summary>What the last scan found, for the tests.</summary>
    public FaultScan? Result => Panel.Result;

    /// <summary>Asks the car again.</summary>
    public void Scan() => Panel.Scan();
}
