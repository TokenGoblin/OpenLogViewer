using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// Setting up the power estimate and adding its channels to the log.
///
/// The window only collects what the log cannot tell us — the displacement, the
/// injectors, the fuel consumption to assume. Everything it can tell us it is
/// asked for instead: the mixture, the volumetric efficiency, the rail pressure
/// and the manifold are read from the log where they are there, and the fields
/// here are only the fallbacks.
///
/// What each method can and cannot do on this particular log is shown before
/// anything is added, because "add channels" on a log that cannot feed any of
/// them would otherwise be a button that does nothing without saying why.
/// </summary>
public partial class PowerWindow : Window
{
    private readonly MainViewModel _vm;

    public PowerWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _vm = viewModel;

        InitializeComponent();

        foreach (Fuel fuel in Enum.GetValues<Fuel>()) FuelBox.Items.Add(TuningMath.Name(fuel));
        FuelBox.SelectedIndex = 0;

        var defaults = new EngineSpec();

        Litres.Text = defaults.Litres.ToString("N1", CultureInfo.CurrentCulture);
        Cylinders.Text = defaults.Cylinders.ToString(CultureInfo.CurrentCulture);
        Bsfc.Text = defaults.Bsfc.ToString("N2", CultureInfo.CurrentCulture);
        Ve.Text = defaults.VolumetricEfficiency.ToString("N0", CultureInfo.CurrentCulture);
        Loss.Text = "0";
        InjectorCc.Text = defaults.InjectorCcPerMinute.ToString("N0", CultureInfo.CurrentCulture);
        RatedKpa.Text = defaults.InjectorRatedKpa.ToString("N0", CultureInfo.CurrentCulture);
        DeadTime.Text = defaults.InjectorDeadTimeMs.ToString("N2", CultureInfo.CurrentCulture);

        Refresh();
        Show();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void OnToggled(object sender, RoutedEventArgs e) => Refresh();

    private void OnFuelChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private static double Value(TextBox box, double fallback) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
            ? v
            : fallback;

    /// <summary>What the fields currently describe.</summary>
    private EngineSpec Spec()
    {
        var defaults = new EngineSpec();

        return new EngineSpec
        {
            Litres = Value(Litres, defaults.Litres),
            Cylinders = (int)Value(Cylinders, defaults.Cylinders),
            Fuel = FuelBox.SelectedIndex >= 0
                ? Enum.GetValues<Fuel>()[FuelBox.SelectedIndex]
                : Fuel.Petrol,
            Bsfc = Value(Bsfc, defaults.Bsfc),
            VolumetricEfficiency = Value(Ve, defaults.VolumetricEfficiency),
            InjectorCcPerMinute = Value(InjectorCc, defaults.InjectorCcPerMinute),
            InjectorRatedKpa = Value(RatedKpa, defaults.InjectorRatedKpa),
            InjectorDeadTimeMs = Value(DeadTime, defaults.InjectorDeadTimeMs),
            BatchInjection = Batch.IsChecked == true,
            FuelPressureIsDifferential = Differential.IsChecked == true,
            DrivetrainLossPercent = Value(Loss, 0),
        };
    }

    private void Refresh()
    {
        if (Available is null) return;

        EngineSpec spec = Spec();

        FuelNote.Text = $"stoichiometric {TuningMath.Stoichiometric(spec.Fuel):N2}:1, "
                      + $"{TuningMath.Density(spec.Fuel):N3} kg/L";

        // The same distinction the calculators draw: the sizing figure is
        // deliberately pessimistic and would understate the engine here.
        BsfcNote.Text = $"lb/hp/hr — {TuningMath.SuggestedBsfc(spec.Fuel, TuningMath.FullThrottleBsfc):N2} "
                      + $"on {TuningMath.ShortName(spec.Fuel)} at full throttle";

        DeadTimeNote.Text = $"ms — {spec.InjectorDeadTimeMs:N2} ms is "
                          + $"{spec.InjectorDeadTimeMs * 7000 / 1200:N1}% of the cycle at 7,000 rpm";

        PowerEstimateResult? estimate = _vm.EstimatePower(spec);

        if (estimate is null)
        {
            Available.Text = "No log is open.";
            Missing.Text = "";
            Advice.Text = "";
            VeNote.Text = "%";
            AddButton.IsEnabled = false;
            Status.Text = "Open a log first.";

            return;
        }

        VeNote.Text = estimate.Methods.Any(m => m.Basis.Contains("VE from"))
            ? "% — ignored, the log reports its own"
            : "% — used, since the log does not report it";

        Available.Text = estimate.Methods.Count > 0
            ? string.Join(
                Environment.NewLine,
                estimate.Methods.Select(m => $"✓  {m.Name} — {m.Basis}"))
            : "Nothing here can be estimated from this log.";

        Missing.Text = estimate.Unavailable.Count > 0
            ? string.Join(
                Environment.NewLine,
                estimate.Unavailable.Select(u => $"—  {u.Name} needs {u.Needs}"))
            : "";

        int count = estimate.Channels.Count;

        AddButton.IsEnabled = count > 0;
        Status.Text = count > 0
            ? $"{count} calculated channel(s) to add."
            : "Nothing to add from this log.";

        // Said only when there are two figures to compare, since that is the one
        // piece of advice here that is worth more than the numbers themselves.
        Advice.Text = estimate.Methods.Any(m => m.Name == "Agreement")
            ? "Both routes are available on this log, so watch the spread channel. A few per cent "
            + "apart is noise. A steady gap means one of the two inputs is wrong — an optimistic VE "
            + "table pushes the air estimate up, and injector data that is not what the box claimed "
            + "pushes the fuel estimate about. The fuel consumption you assumed cancels out of the "
            + "comparison, so the spread is telling you about the engine rather than about the guess."
            : "";
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        int added = _vm.AddPowerChannels(Spec());

        Status.Text = added > 0
            ? $"Added {added} channel(s). Tick them in the channel list to plot them."
            : "Nothing was added.";
    }
}
