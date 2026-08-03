using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// The tuning calculators.
///
/// Every sum lives in <see cref="TuningMath"/> and is tested there. This is
/// only the reading and writing of boxes, which is deliberate: the arithmetic is
/// the part that can be wrong without looking wrong, and it should not be buried
/// in an event handler where nothing can check it.
///
/// Figures update as they are typed, with no button to press. A calculator that
/// needs a button is a calculator that shows a stale answer for as long as
/// nobody presses it.
/// </summary>
public partial class CalculatorsWindow : Window
{
    /// <summary>
    /// True while a box is being filled in by this window rather than by a
    /// person, so the change it raises is not answered by filling in the box
    /// that caused it. Without this, boost in psi rewrites kPa, which rewrites
    /// psi, and the two round each other into the ground.
    /// </summary>
    private bool _updating;

    public CalculatorsWindow()
    {
        InitializeComponent();

        foreach (ComboBox box in (ComboBox[])[InjFuel, PumpFuel, LambdaFuel])
        {
            foreach (Fuel fuel in Enum.GetValues<Fuel>())
                box.Items.Add(TuningMath.Name(fuel));

            box.SelectedIndex = 0;
        }

        _updating = true;

        BoostPsi.Text = "10";
        BoostBar.Text = Round(10 * TuningMath.KpaPerPsi / TuningMath.KpaPerBar, 3);
        BoostKpa.Text = Round(10 * TuningMath.KpaPerPsi, 1);
        MapKpa.Text = Round(TuningMath.AbsoluteFromGauge(10 * TuningMath.KpaPerPsi), 1);
        MapPsi.Text = Round(TuningMath.AbsoluteFromGauge(10 * TuningMath.KpaPerPsi) / TuningMath.KpaPerPsi, 2);

        InjPower.Text = "400";
        InjCylinders.Text = "4";
        InjBsfc.Text = TuningMath.BoostedBsfc.ToString(CultureInfo.CurrentCulture);
        InjDuty.Text = "85";

        PumpPower.Text = "400";
        PumpBsfc.Text = TuningMath.BoostedBsfc.ToString(CultureInfo.CurrentCulture);
        PumpHeadroom.Text = "20";

        LambdaValue.Text = "0.85";
        AfrValue.Text = Round(TuningMath.AfrFromLambda(0.85, Fuel.Petrol), 2);

        AirLitres.Text = "2.0";
        AirRpm.Text = "7000";
        AirVe.Text = "95";
        AirBoost.Text = "10";

        _updating = false;

        Recalculate();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Everything that is not the box being typed into.</summary>
    private void Recalculate()
    {
        ShowPressure();
        ShowInjectors();
        ShowPump();
        ShowLambda();
        ShowAirflow();
    }

    // ----- reading the boxes ---------------------------------------------------

    private static double Value(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
            ? v
            : double.NaN;

    private static string Round(double value, int digits) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? "—"
            : Math.Round(value, digits).ToString(CultureInfo.CurrentCulture);

    private static string Show(double value, int digits) =>
        double.IsNaN(value) || double.IsInfinity(value) || value <= 0
            ? "—"
            : value.ToString("N" + digits, CultureInfo.CurrentCulture);

    private Fuel FuelOf(ComboBox box) =>
        box.SelectedIndex >= 0 ? Enum.GetValues<Fuel>()[box.SelectedIndex] : Fuel.Petrol;

    /// <summary>Sets a box without the change being read back as somebody typing.</summary>
    private void Set(TextBox box, string text)
    {
        _updating = true;
        box.Text = text;
        _updating = false;
    }

    // ----- pressure ------------------------------------------------------------

    private void OnBoostPsiChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        double kpa = Value(BoostPsi) * TuningMath.KpaPerPsi;

        Set(BoostKpa, Round(kpa, 1));
        Set(BoostBar, Round(kpa / TuningMath.KpaPerBar, 3));
        SetAbsolute(kpa);
        ShowPressure();
    }

    private void OnBoostBarChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        double kpa = Value(BoostBar) * TuningMath.KpaPerBar;

        Set(BoostKpa, Round(kpa, 1));
        Set(BoostPsi, Round(kpa / TuningMath.KpaPerPsi, 2));
        SetAbsolute(kpa);
        ShowPressure();
    }

    private void OnBoostKpaChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        double kpa = Value(BoostKpa);

        Set(BoostPsi, Round(kpa / TuningMath.KpaPerPsi, 2));
        Set(BoostBar, Round(kpa / TuningMath.KpaPerBar, 3));
        SetAbsolute(kpa);
        ShowPressure();
    }

    private void OnMapKpaChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        double absolute = Value(MapKpa);

        Set(MapPsi, Round(absolute / TuningMath.KpaPerPsi, 2));
        SetGauge(TuningMath.GaugeFromAbsolute(absolute));
        ShowPressure();
    }

    private void OnMapPsiChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        double absolute = Value(MapPsi) * TuningMath.KpaPerPsi;

        Set(MapKpa, Round(absolute, 1));
        SetGauge(TuningMath.GaugeFromAbsolute(absolute));
        ShowPressure();
    }

    private void SetAbsolute(double gaugeKpa)
    {
        double absolute = TuningMath.AbsoluteFromGauge(gaugeKpa);

        Set(MapKpa, Round(absolute, 1));
        Set(MapPsi, Round(absolute / TuningMath.KpaPerPsi, 2));
    }

    private void SetGauge(double gaugeKpa)
    {
        Set(BoostKpa, Round(gaugeKpa, 1));
        Set(BoostPsi, Round(gaugeKpa / TuningMath.KpaPerPsi, 2));
        Set(BoostBar, Round(gaugeKpa / TuningMath.KpaPerBar, 3));
    }

    private void ShowPressure()
    {
        double absolute = Value(MapKpa);
        double ratio = TuningMath.PressureRatio(absolute);

        PressureRatio.Text = double.IsNaN(ratio) ? "—" : ratio.ToString("N2", CultureInfo.CurrentCulture);
    }

    // ----- injectors -----------------------------------------------------------

    private void OnInjectorChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowInjectors();
    }

    private void OnInjectorFuelChanged(object sender, SelectionChangedEventArgs e) => ShowInjectors();

    private void ShowInjectors()
    {
        if (InjCc is null) return;

        Fuel fuel = FuelOf(InjFuel);

        double power = Value(InjPower);
        double bsfc = Value(InjBsfc);
        double duty = Value(InjDuty);
        int cylinders = (int)Value(InjCylinders);

        double lbHr = TuningMath.InjectorPoundsPerHour(power, cylinders, bsfc, duty);
        double cc = TuningMath.CcPerMinute(lbHr, fuel);

        InjLbHr.Text = Show(lbHr, 1);
        InjCc.Text = Show(cc, 0);
        InjTotal.Text = Show(cc * Math.Max(cylinders, 0), 0);

        InjFuelNote.Text = $"stoichiometric {TuningMath.Stoichiometric(fuel):N2}:1";

        // Said rather than assumed: ethanol needs about a third more fuel by
        // mass for the same power, so a BSFC left at the petrol figure sizes the
        // injector short on E85 — which is the mistake this calculator exists to
        // avoid rather than to make quietly.
        InjNote.Text = fuel is Fuel.Petrol or Fuel.Diesel
            ? "BSFC is a convention rather than a measurement. If you know yours from a "
              + "previous tune, use it — it is the figure everything here rests on."
            : $"{TuningMath.Name(fuel)} needs more fuel by mass than petrol for the same power. "
              + "Raise the BSFC accordingly — around 0.75 to 0.85 on E85 where petrol would be 0.60 "
              + "— or this will size the injector short.";
    }

    // ----- fuel pump -----------------------------------------------------------

    private void OnPumpChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowPump();
    }

    private void OnPumpFuelChanged(object sender, SelectionChangedEventArgs e) => ShowPump();

    private void ShowPump()
    {
        if (PumpBurned is null) return;

        Fuel fuel = FuelOf(PumpFuel);

        double burned = TuningMath.FuelLitresPerHour(Value(PumpPower), Value(PumpBsfc), fuel);
        double needed = TuningMath.PumpLitresPerHour(
            Value(PumpPower), Value(PumpBsfc), fuel, Value(PumpHeadroom));

        PumpBurned.Text = Show(burned, 0);
        PumpNeeded.Text = Show(needed, 0);
        PumpGallons.Text = Show(needed * 0.264172, 1);
    }

    // ----- lambda --------------------------------------------------------------

    private void OnLambdaChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        Set(AfrValue, Round(TuningMath.AfrFromLambda(Value(LambdaValue), FuelOf(LambdaFuel)), 2));
        ShowLambda();
    }

    private void OnAfrChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        Set(LambdaValue, Round(TuningMath.LambdaFromAfr(Value(AfrValue), FuelOf(LambdaFuel)), 3));
        ShowLambda();
    }

    private void OnLambdaFuelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LambdaValue is null) return;

        // The lambda is what stays put when the fuel changes: it is the same
        // richness on any fuel, and the ratio is what has to move.
        Set(AfrValue, Round(TuningMath.AfrFromLambda(Value(LambdaValue), FuelOf(LambdaFuel)), 2));
        ShowLambda();
    }

    private void ShowLambda()
    {
        if (LambdaStoich is null) return;

        Fuel fuel = FuelOf(LambdaFuel);
        double stoich = TuningMath.Stoichiometric(fuel);
        double lambda = Value(LambdaValue);

        LambdaStoich.Text = $"stoichiometric at {stoich:N2}:1";
        AfrNote.Text = $"on {TuningMath.Name(fuel)}";

        LambdaVerdict.Text = lambda switch
        {
            double.NaN => "—",
            < 0.75 => "very rich — safe, and down on power",
            < 0.95 => "rich of stoichiometric — where a boosted engine is run",
            <= 1.02 => "about stoichiometric — cruise and idle",
            <= 1.10 => "lean of stoichiometric — economy, not power",
            _ => "very lean — fine on light throttle, dangerous under load",
        };
    }

    // ----- airflow -------------------------------------------------------------

    private void OnAirflowChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowAirflow();
    }

    private void ShowAirflow()
    {
        if (AirCfm is null) return;

        double litres = Value(AirLitres);
        double boostKpa = Value(AirBoost) * TuningMath.KpaPerPsi;
        double ratio = TuningMath.PressureRatio(TuningMath.AbsoluteFromGauge(boostKpa));

        double cfm = TuningMath.CubicFeetPerMinute(litres, Value(AirRpm), Value(AirVe), ratio);

        AirCid.Text = double.IsNaN(litres)
            ? "litres"
            : $"litres — {litres * TuningMath.CubicInchesPerLitre:N0} cubic inches";

        AirRatio.Text = double.IsNaN(ratio)
            ? "psi"
            : $"psi — a pressure ratio of {ratio:N2}";

        AirCfm.Text = Show(cfm, 0);
        AirLbMin.Text = Show(TuningMath.AirPoundsPerMinute(cfm), 1);
        AirM3h.Text = Show(TuningMath.CubicMetresPerHour(cfm), 0);
    }
}
