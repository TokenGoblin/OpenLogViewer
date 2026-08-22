using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
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

    /// <summary>
    /// The air the engine is breathing, set once on the Pressure ratio tab and
    /// used by every tab that needs it.
    ///
    /// One value rather than one per tab, because two tabs disagreeing about
    /// what an atmosphere is would be worse than either of them being wrong on
    /// its own — the window would give two absolute pressures for one boost
    /// reading and no way to tell which was meant.
    /// </summary>
    private double _barometricKpa = TuningMath.AtmosphericKpa;

    /// <summary>One calculator, and where it lives in the list.</summary>
    /// <param name="Category">The heading it sits under.</param>
    /// <param name="Name">What the list calls it, and what a screenshot asks for by.</param>
    /// <param name="Content">The panel to show when it is picked.</param>
    private sealed record Calculator(string Category, string Name, FrameworkElement Content);

    private Calculator[] _calculators = [];

    /// <summary>
    /// The calculators, grouped.
    ///
    /// Order matters twice over: the categories appear in the order their first
    /// member does, and within a category so do the calculators. Grouped rather
    /// than listed flat because the list is going to keep growing, and nine
    /// unsorted entries is already more than anyone wants to read through.
    /// </summary>
    private void BuildNavigation()
    {
        _calculators =
        [
            new("Plan a build", "Engine recipe", PageRecipe),

            new("Air & boost", "Boost", PageBoost),
            new("Air & boost", "Pressure ratio", PagePressureRatio),
            new("Air & boost", "Turbo sizing", PageTurbo),
            new("Air & boost", "Airflow", PageAirflow),
            new("Air & boost", "Intercooling", PageIntercooler),

            new("Fuel", "Injectors", PageInjectors),
            new("Fuel", "Fuel pump", PageFuelPump),
            new("Fuel", "Lambda", PageLambda),
            new("Fuel", "Octane", PageOctane),

            new("Engine", "Engine", PageEngine),
            new("Engine", "Runners & headers", PageManifold),

            new("Drivetrain", "Gearing", PageGearing),
            new("Drivetrain", "Drag strip", PageDragStrip),

            new("Running costs", "Fuel economy", PageRunningCosts),
        ];

        CollectionViewSource grouped = new() { Source = _calculators };
        grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Calculator.Category)));

        Nav.ItemsSource = grouped.View;
        Nav.SelectedIndex = 0;
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not Calculator chosen) return;

        foreach (Calculator calculator in _calculators)
            calculator.Content.Visibility = calculator == chosen
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows a calculator by the name the list gives it, for scripted runs.
    /// </summary>
    public bool Show(string name)
    {
        Calculator? match = _calculators.FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        if (match is null) return false;

        Nav.SelectedItem = match;

        return true;
    }

    public CalculatorsWindow()
    {
        InitializeComponent();

        foreach (ComboBox box in (ComboBox[])[InjFuel, PumpFuel, LambdaFuel])
        {
            foreach (Fuel fuel in Enum.GetValues<Fuel>())
                box.Items.Add(TuningMath.Name(fuel));

            box.SelectedIndex = 0;
        }

        foreach (ComboBox box in (ComboBox[])[TurboFuel, RecFuel])
        {
            foreach (Fuel fuel in Enum.GetValues<Fuel>())
                box.Items.Add(TuningMath.Name(fuel));

            box.SelectedIndex = 0;
        }

        foreach (ComboBox box in (ComboBox[])[TurboVeFamily, RecVeFamily])
        {
            foreach (EngineFamily family in EngineFamilies.All) box.Items.Add(family.Name);

            box.SelectedIndex = EngineFamilies.All.Count - 1;
        }

        foreach (ComboBox box in (ComboBox[])[TurboTempUnit, RecTempUnit])
        {
            foreach (string scale in (string[])["°C", "°F"]) box.Items.Add(scale);

            box.SelectedIndex = 0;
        }

        foreach (DragFormula formula in DragStrip.Formulas)
            DragFormula.Items.Add(formula.Name);

        DragFormula.SelectedIndex = DragStrip.Formulas.ToList().IndexOf(DragStrip.Default);

        foreach (Blendstock stock in Enum.GetValues<Blendstock>())
            OctStock.Items.Add(OctaneBlend.Name(stock));

        OctStock.SelectedIndex = 0;

        _updating = true;

        GearTyre.Text = "245/40R18";
        GearDeflection.Text = Gearing.RollingDeflectionPercent.ToString(CultureInfo.CurrentCulture);
        GearFinal.Text = "3.90";
        GearRedline.Text = "7000";
        GearCruise.Text = "70";
        GearRatios.Text = "3.545, 2.048, 1.416, 1.059, 0.848, 0.756";

        foreach (ComboBox box in (ComboBox[])[CostFuelA, CostFuelB, CostFuelC])
        {
            foreach (FuelSpec fuel in Fuels.All) box.Items.Add(fuel.Name);
        }

        // Three different fuels to start with, because a page that opens showing
        // the same car three times does not look like a comparison.
        CostFuelA.SelectedIndex = 0;
        CostFuelB.SelectedIndex = Fuels.All.ToList().FindIndex(f => f.Kind == FuelKind.Hybrid);
        CostFuelC.SelectedIndex = Fuels.All.ToList().FindIndex(f => f.Kind == FuelKind.Electricity);

        CostMiles.Text = RunningCosts.TypicalAnnualMiles.ToString("F0", CultureInfo.CurrentCulture);

        CostEconomyA.Text = "26";
        CostEconomyB.Text = "48";
        CostEconomyC.Text = "3.5";

        foreach (var column in CostColumns)
        {
            column.Price.Text = FuelPrices.For(KindOf(column.Fuel))
                .ToString("0.####", CultureInfo.CurrentCulture);
        }

        foreach (Sprayable fluid in Sprayables.All) IcFluid.Items.Add(fluid.Name);

        IcFluid.SelectedIndex = Sprayables.All.ToList().FindIndex(f => f.Name.StartsWith("50/50"));

        foreach (CoreMaterial material in CoreMaterials.All) IcMaterial.Items.Add(material.Name);

        IcMaterial.SelectedIndex = 0;

        IcAir.Text = "40";
        IcBoost.Text = "20";
        IcAmbient.Text = "85";
        IcCompEff.Text = "70";
        IcEffect.Text = "70";
        IcW.Text = "24";
        IcH.Text = "12";
        IcT.Text = "3";
        IcDrop.Text = "60";
        IcEvap.Text = (ChemicalIntercooling.TypicalEvaporated * 100).ToString("F0", CultureInfo.CurrentCulture);
        IcFuel.Text = "4.0";

        DragWeight.Text = "3200";
        DragPower.Text = "400";
        DragTrap.Text = "115";
        DragEt.Text = "13.0";

        RecLitres.Text = "2.0";
        RecCylinders.Text = "4";
        RecPower.Text = "500";
        RecTorqueRpm.Text = "3500";
        RecPowerRpm.Text = "7000";
        RecLambda.Text = "0.80";
        RecVe.Text = "97";
        RecChargeTemp.Text = "45";
        RecDuty.Text = "85";
        RecPumpHeadroom.Text = "20";

        TurboPower.Text = "650";
        TurboLitres.Text = "5.7";
        TurboRpm.Text = "6000";
        TurboVe.Text = "80";
        TurboAfr.Text = "11.5";
        TurboBsfc.Text = TurboSizing.RatedBsfc.ToString(CultureInfo.CurrentCulture);
        TurboChargeTemp.Text = "55";
        TurboInletLoss.Text = "1";
        TurboHeadroom.Text = "10";

        OctBase.Text = "91";
        OctSensitivity.Text = OctaneBlend.TypicalSensitivity.ToString(CultureInfo.CurrentCulture);
        OctPercent.Text = "30";

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
        PumpRailPsi.Text = "43.5";
        PumpBoost.Text = "0";

        LambdaValue.Text = "0.85";
        AfrValue.Text = Round(TuningMath.AfrFromLambda(0.85, Fuel.Petrol), 2);

        AirLitres.Text = "2.0";
        AirRpm.Text = "7000";
        AirVe.Text = "95";
        AirBoost.Text = "10";
        AirLambda.Text = "0.85";
        AirBsfc.Text = TuningMath.FullThrottleBsfc.ToString(CultureInfo.CurrentCulture);

        // Sea level, which is what the rest of the window assumed before this
        // was an input at all.
        PrBoost.Text = "12";
        PrAltitude.Text = "0";
        PrBarometric.Text = Round(TuningMath.AtmosphericKpa, 1);
        PrInletLoss.Text = Round(TuningMath.TypicalInletLossKpa / TuningMath.KpaPerPsi, 1);
        PrChargeLoss.Text = Round(TuningMath.TypicalChargeLossKpa / TuningMath.KpaPerPsi, 1);

        // The induction and goal lists are filled from the enums in their declared
        // order, because that is the order they are read back in.
        foreach (Induction induction in Enum.GetValues<Induction>())
            ManInduction.Items.Add(induction == Induction.Turbocharged ? "Turbocharged" : "Naturally aspirated");

        ManInduction.SelectedIndex = 0;

        foreach (ManifoldGoal goal in Enum.GetValues<ManifoldGoal>())
        {
            ManGoal.Items.Add(goal switch
            {
                ManifoldGoal.QuickSpool => "Quick spool",
                ManifoldGoal.HighRpmRace => "High-rpm race",
                _ => "Street and strip",
            });
        }

        ManGoal.SelectedIndex = (int)ManifoldGoal.Balanced;

        foreach (CamProfile cam in CamProfiles.All) ManCam.Items.Add(cam.Name);

        foreach (EngineFamily family in EngineFamilies.All) ManVeFamily.Items.Add(family.Name);

        // A four-valve head with phasing, which is what most engines somebody is
        // planning a manifold for actually are.
        ManVeFamily.SelectedIndex = 3;

        ManLitres.Text = "2.0";
        ManCylinders.Text = "4";
        ManTorqueRpm.Text = "4500";
        ManPowerRpm.Text = "7000";
        ManCompression.Text = "10.5";

        // Opens on the middle of the five, so the page starts on an engine
        // somebody might actually be building rather than on a bare default.
        CamProfile opening = CamProfiles.All[2];

        ManIntakeDuration.Text = Round(opening.IntakeDurationDeg, 0);
        ManExhaustDuration.Text = Round(opening.ExhaustDurationDeg, 0);
        ManIntakeTemp.Text = Round(opening.ChargeCelsius, 0);
        ManExhaustTemp.Text = Round(opening.ExhaustCelsius, 0);

        // From the head and the cam together, rather than a bare 100 that agreed
        // with neither of them.
        ManVe.Text = Round(
            CamProfiles.VolumetricEfficiency(EngineFamilies.All[3], opening), 0);
        ManBoost.Text = Round(TuningMath.AtmosphericKpa, 1);
        ManBackPressure.Text = Round(TuningMath.AtmosphericKpa, 1);

        EngBore.Text = "86";
        EngStroke.Text = "86";
        EngCylinders.Text = "4";
        EngRpm.Text = "7000";
        EngChamber.Text = "42";
        EngGasket.Text = "1.0";
        EngDeck.Text = "0.5";
        EngPiston.Text = "5";

        _updating = false;

        BuildNavigation();
        Recalculate();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ----- typing into a field --------------------------------------------------

    /// <summary>
    /// Selects a field's contents when it takes focus, so typing replaces the
    /// number rather than joining onto the end of it.
    ///
    /// The alternative — clearing the box, or leaving it empty behind ghost text
    /// — was considered and is wrong here. These are working values, not
    /// placeholders: the window computes as it is typed into, so a page that
    /// emptied itself on focus would show dashes until every box had been filled
    /// in, and there would be nothing to read on opening it.
    /// </summary>
    private void OnFieldFocused(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    /// <summary>
    /// Gives a field the focus on the first click rather than placing a caret in
    /// it.
    ///
    /// Without this the selection above is undone before it can be seen: the
    /// click focuses the box, the contents are selected, and then the same click
    /// puts the caret where the pointer was and deselects everything. Handling
    /// the first click and letting every later one through leaves the ordinary
    /// behaviour intact — click again to place the caret and change one digit.
    /// </summary>
    private void OnFieldClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.IsKeyboardFocusWithin) return;

        box.Focus();
        e.Handled = true;
    }


    /// <summary>Everything that is not the box being typed into.</summary>
    private void Recalculate()
    {
        ShowPressure();
        ShowCompressor();
        ShowTurbo();
        ShowRecipe();
        ShowInjectors();
        ShowPump();
        ShowLambda();
        ShowEngine();
        ShowGearing();
        ShowDragStrip();
        ShowOctane();
        ShowAirflow();
        ShowRunningCosts();
        ShowIntercooler();
        ShowManifold();
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
        SetGauge(TuningMath.GaugeFromAbsolute(absolute, _barometricKpa));
        ShowPressure();
    }

    private void OnMapPsiChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        double absolute = Value(MapPsi) * TuningMath.KpaPerPsi;

        Set(MapKpa, Round(absolute, 1));
        SetGauge(TuningMath.GaugeFromAbsolute(absolute, _barometricKpa));
        ShowPressure();
    }

    // ----- compressor ----------------------------------------------------------

    private void OnCompressorChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowCompressor();
    }

    private void OnAltitudeChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        double kpa = TuningMath.BarometricKpa(Value(PrAltitude) * TuningMath.MetresPerFoot);

        Set(PrBarometric, Round(kpa, 1));
        ApplyBarometric(kpa);
    }

    private void OnBarometricChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        double kpa = Value(PrBarometric);

        Set(PrAltitude, Round(TuningMath.AltitudeMetres(kpa) / TuningMath.MetresPerFoot, 0));
        ApplyBarometric(kpa);
    }

    /// <summary>
    /// Takes a new barometric pressure through the whole window.
    ///
    /// The boost tab's absolute pressure has to be re-derived rather than left
    /// alone: a gauge reads against the air outside, so the same reading on it
    /// is a different absolute pressure once the air has changed.
    /// </summary>
    private void ApplyBarometric(double kpa)
    {
        _barometricKpa = double.IsNaN(kpa) || kpa <= 0 ? TuningMath.AtmosphericKpa : kpa;

        SetAbsolute(Value(BoostKpa));
        Recalculate();
    }

    private void ShowCompressor()
    {
        if (PrRatio is null) return;

        double boost = Value(PrBoost) * TuningMath.KpaPerPsi;
        double inletLoss = Value(PrInletLoss) * TuningMath.KpaPerPsi;
        double chargeLoss = Value(PrChargeLoss) * TuningMath.KpaPerPsi;

        TuningMath.Compressor c = TuningMath.CompressorPressures(
            boost, _barometricKpa, inletLoss, chargeLoss);

        PrInlet.Text = Show(c.InletKpa / TuningMath.KpaPerPsi, 2);
        PrOutlet.Text = Show(c.OutletKpa / TuningMath.KpaPerPsi, 2);
        PrRatio.Text = Show(c.Ratio, 2);

        double metres = TuningMath.AltitudeMetres(_barometricKpa);

        PrAltitudeNote.Text = double.IsNaN(metres)
            ? "feet above sea level"
            : $"feet — {metres:N0} m, {_barometricKpa:N1} kPa";

        // What the losses and the altitude are actually costing, which is the
        // question the tab exists to answer. A ratio quoted without them is the
        // one people compare a compressor map against.
        double bare = TuningMath.CompressorPressures(boost).Ratio;

        PrNote.Text = double.IsNaN(c.Ratio) || double.IsNaN(bare) || bare <= 0
            ? "—"
            : $"Boost over a sea-level atmosphere alone would read {bare:N2}. The filter, the "
            + $"intercooler and the air you are actually in put {(c.Ratio / bare) - 1:P0} on top of "
            + "that, and it is the larger figure the compressor has to make.";
    }

    private void SetAbsolute(double gaugeKpa)
    {
        double absolute = TuningMath.AbsoluteFromGauge(gaugeKpa, _barometricKpa);

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
        if (BoostBaroNote is null) return;

        // Against sea level rather than against the local air, deliberately: it
        // is how much denser the charge is than the standard atmosphere, which
        // is what airflow scales with. The compressor's own ratio is the other
        // one, is taken against what it is breathing, and lives on its own tab.
        double ratio = TuningMath.ChargeDensityRatio(Value(MapKpa));

        PressureRatio.Text = double.IsNaN(ratio) ? "—" : ratio.ToString("N2", CultureInfo.CurrentCulture);

        BoostBaroNote.Text = BarometricSummary()
            + " Absolute pressure is gauge plus that, so the same boost is less absolute pressure — "
            + "and less air — the higher you are.";
    }

    /// <summary>What the window is currently taking an atmosphere to be, said on every tab that uses it.</summary>
    private string BarometricSummary()
    {
        double feet = TuningMath.AltitudeMetres(_barometricKpa) / TuningMath.MetresPerFoot;

        string where = Math.Abs(feet) < 50
            ? "sea level"
            : $"{feet:N0} ft";

        return $"Barometric pressure is set to {_barometricKpa:N1} kPa ({where}) on the Pressure ratio tab.";
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

        // Both live in the core alongside the arithmetic they describe. The
        // advice is as much this window's output as the numbers are, and the
        // sentence that was here before was wrong for four of the ten fuels.
        InjBsfcHint.Text = TuningMath.BsfcHint(fuel);
        InjNote.Text = TuningMath.BsfcGuidance(fuel);
    }

    // ----- fuel pump -----------------------------------------------------------

    private void OnPumpChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowPump();
    }

    /// <summary>
    /// Which fuel the BSFC in the box currently belongs to, so that changing the
    /// fuel can carry the figure across rather than replace it.
    /// </summary>
    private Fuel _pumpFuel = Fuel.Petrol;

    /// <summary>
    /// Moves the BSFC with the fuel, which is the whole reason the two sit on
    /// the same tab.
    ///
    /// Scaled rather than replaced. Someone who typed 0.48 because the engine is
    /// naturally aspirated, or 0.52 because they measured it on the last tune,
    /// means something by that number; dropping a boosted convention on top of
    /// it throws that away. Scaling by energy content keeps what they meant and
    /// still lands on the right figure for the new fuel.
    /// </summary>
    private void OnPumpFuelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PumpBsfc is null) return;

        Fuel chosen = FuelOf(PumpFuel);

        double petrol = TuningMath.PetrolEquivalentBsfc(_pumpFuel, Value(PumpBsfc));

        // Diesel has no petrol equivalent to carry across, so leaving it falls
        // back to the convention rather than to whatever the diesel figure was.
        if (double.IsNaN(petrol)) petrol = TuningMath.BoostedBsfc;

        double moved = TuningMath.SuggestedBsfc(chosen, petrol);

        if (!double.IsNaN(moved)) Set(PumpBsfc, Round(moved, 2));

        _pumpFuel = chosen;

        ShowPump();
    }

    private void ShowPump()
    {
        if (PumpBurned is null) return;

        Fuel fuel = FuelOf(PumpFuel);

        PumpBsfcHint.Text = TuningMath.BsfcHint(fuel);
        PumpLegend.Text = TuningMath.BsfcLegend(fuel);

        PumpLegendNote.Text =
            "The BSFC follows the fuel, scaled by how much energy it carries per kilogram — so a "
            + "figure you measured or chose for an aspirated engine survives the change rather than "
            + "being replaced. Type over it whenever you know better.";

        double burned = TuningMath.FuelLitresPerHour(Value(PumpPower), Value(PumpBsfc), fuel);
        double needed = TuningMath.PumpLitresPerHour(
            Value(PumpPower), Value(PumpBsfc), fuel, Value(PumpHeadroom));

        PumpBurned.Text = Show(burned, 0);
        PumpNeeded.Text = Show(needed, 0);
        PumpGallons.Text = Show(needed * TuningMath.UsGallonsPerLitre, 1);

        // Both, because a pump is quoted in whichever the maker felt like: litres
        // an hour on most in-tank pumps, gallons a minute on the larger and the
        // mechanical ones.
        PumpGallonsNote.Text = needed > 0
            ? $"US gallons/hour — {TuningMath.GallonsPerMinute(needed):N2} a minute"
            : "US gallons/hour";

        ShowPumpPicks(fuel, needed);
    }

    /// <summary>
    /// Which pumps would actually do it, in the part numbers they are sold under.
    ///
    /// Compared at the pressure the pump will really see rather than at the one
    /// its headline figure was measured at. A rail at 43 psi with 20 psi of
    /// boost on it is 63, and every pump on the list makes appreciably less
    /// there than the number on its box — which is the mistake this is here to
    /// stop, since the box is what people compare.
    /// </summary>
    private void ShowPumpPicks(Fuel fuel, double needed)
    {
        double rail = Value(PumpRailPsi) + Value(PumpBoost);

        // Without trailing zeros, so a base of 43.5 is not echoed back as 44 in
        // the line explaining what the answer was worked out at.
        string atPsi = rail.ToString("0.#", CultureInfo.CurrentCulture);

        PumpRailNote.Text = double.IsNaN(rail) || rail <= 0
            ? "psi — rail pressure rises with boost"
            : $"psi — the pump works against {atPsi} psi";

        bool alcohol = TuningMath.NeedsAlcoholSafePump(fuel);

        IReadOnlyList<TuningMath.PumpChoice> picks =
            TuningMath.SuggestPumps(needed, rail, alcohol);

        PumpPicksTitle.Text = double.IsNaN(rail) || rail <= 0 || !(needed > 0)
            ? "Suggested pumps"
            : $"Suggested pumps — {needed:N0} L/h at {atPsi} psi"
              + (alcohol ? ", alcohol-rated only" : string.Empty);

        if (picks.Count == 0)
        {
            PumpPicks.Text = needed > 0 && rail > 0
                ? "  nothing in the list gets there"
                : "  —";

            PumpPicksNote.Text = needed > 0 && rail > 0
                ? $"Past about {TuningMath.MostPumpsWorthWiring} in-tank pumps the plumbing is doing "
                + "more work than the pumps are: the shared line, filter and regulator become the "
                + "restriction, and a surge tank stops being optional. This is where a belt-driven "
                + "mechanical pump, or a brushless pump and controller, is the answer instead of "
                + "another Walbro. Widening the search: a lower base rail pressure, or larger "
                + "injectors so the rail is not asked for the flow at pressure, both move this."
                : "Enter a power, a fuel and a rail pressure.";

            return;
        }

        PumpPicks.Text = string.Join(Environment.NewLine, picks.Take(5).Select(Describe));

        int fewest = picks[0].Count;

        PumpPicksNote.Text =
            (fewest > 1
                ? $"Nothing on the list does it alone, so the smallest answer is {fewest} in "
                + "parallel. Each pump after the first is counted at "
                + $"{TuningMath.ParallelPumpEfficiency:P0} of its own flow, because they share a "
                + "line, a filter and a regulator. Two pumps want a surge tank and a check valve "
                + "on each, or the idle one becomes a leak path for the working one. "
                : "")
            + "Flow shown is what the pump still makes at your pressure, estimated from its rating "
            + $"— roughly {TuningMath.PumpPressureFalloff:P0} lost for a doubling of pressure. Check "
            + "the maker's own curve at your pressure and at the voltage your car actually holds, "
            + "since these are quoted at 13.5 V and a sagging system is a slower pump. "
            + "Part numbers and ratings are transcribed from published data and get superseded — "
            + "verify before buying.";
    }

    private static string Describe(TuningMath.PumpChoice choice) =>
        $"  {choice.Pump.Name,-20} {choice.Pump.Nickname,-11} "
        + $"{(choice.Count == 1 ? "1 pump " : $"{choice.Count} pumps"),-8} "
        + $"{choice.DeliveredLitresPerHour,5:N0} L/h"
        + (choice.Pump.AlcoholSafe ? "  E85" : string.Empty);

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

        // Nothing at or below zero is a mixture, and the old bands answered a
        // typed minus sign with "safe" — every relational arm below is false for
        // a NaN, so anything unguarded falls through to the last one.
        LambdaVerdict.Text = lambda switch
        {
            double.NaN or <= 0 => "—",
            < 0.70 => "richer than an engine will take — bore wash and misfire, not power",
            < 0.80 => "very rich — safe under load, and down on power",
            < 0.95 => "rich of stoichiometric — where an engine is run under load",
            <= 1.02 => "about stoichiometric — cruise and idle",
            <= 1.10 => "lean of stoichiometric — economy, not power",
            _ => "very lean — fine on light throttle, dangerous under load",
        };
    }

    // ----- engine --------------------------------------------------------------

    private void OnEngineChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowEngine();
    }

    private void ShowEngine()
    {
        if (EngRatio is null) return;

        double bore = Value(EngBore);
        double stroke = Value(EngStroke);
        int cylinders = (int)Value(EngCylinders);
        double rpm = Value(EngRpm);

        // ----- how big it is ---------------------------------------------------

        double cylinderCc = EngineGeometry.SweptVolumeCc(bore, stroke);
        double totalCc = EngineGeometry.DisplacementCc(bore, stroke, cylinders);

        EngCylinderCc.Text = Show(cylinderCc, 1);
        EngLitres.Text = totalCc > 0 ? (totalCc / 1_000).ToString("N2", CultureInfo.CurrentCulture) : "—";

        EngLitresNote.Text = totalCc > 0
            ? $"litres — {totalCc:N0} cc, {EngineGeometry.CubicInches(totalCc):N0} cubic inches"
            : "litres";

        EngBoreNote.Text = bore > 0 ? $"mm — {bore / 25.4:N3} in" : "mm";
        EngStrokeNote.Text = stroke > 0 ? $"mm — {stroke / 25.4:N3} in" : "mm";

        double shape = EngineGeometry.BoreToStroke(bore, stroke);

        EngShapeNote.Text = double.IsNaN(shape)
            ? "one piston each"
            : $"bore/stroke {shape:N2} — {(shape > 1.02 ? "oversquare" : shape < 0.98 ? "undersquare" : "square")}";

        // ----- how fast the piston is going ------------------------------------

        double mps = EngineGeometry.MeanPistonSpeed(stroke, rpm);

        EngPistonSpeed.Text = Show(mps, 1);
        EngPistonNote.Text = double.IsNaN(mps)
            ? "m/s"
            : $"m/s — {EngineGeometry.MeanPistonSpeedFeetPerMinute(stroke, rpm):N0} ft/min, "
              + EngineGeometry.PistonSpeedVerdict(mps);

        // ----- how hard it squeezes --------------------------------------------

        // The gasket's bore is taken as the block's plus a millimetre, which is
        // the usual overlap and saves asking for a figure nobody has to hand.
        double gasketThickness = Value(EngGasket);
        double gasketBore = bore > 0 ? bore + 1 : double.NaN;

        double clearance = EngineGeometry.ClearanceVolumeCc(
            bore, Value(EngChamber), gasketBore, gasketThickness, Value(EngDeck), Value(EngPiston));

        double ratio = EngineGeometry.CompressionRatio(cylinderCc, clearance);

        EngGasketNote.Text = gasketThickness > 0 && bore > 0
            ? $"mm thick, compressed — {gasketBore:N0} mm bore, "
              + $"{EngineGeometry.CylinderVolumeCc(gasketBore, gasketThickness):N1} cc"
            : "mm thick, compressed";

        EngRatio.Text = double.IsNaN(ratio) ? "—" : $"{ratio:N2}:1";
        EngRatioNote.Text = double.IsNaN(ratio)
            ? "static"
            : $"static — {clearance:N1} cc left above the piston";

        double map = Value(MapKpa);
        double boosted = EngineGeometry.BoostedCompressionIndex(ratio, map, _barometricKpa);

        EngBoosted.Text = double.IsNaN(boosted) ? "—" : $"{boosted:N1}:1";
        EngBoostedNote.Text = double.IsNaN(boosted)
            ? "an index, not a ratio"
            : $"an index at {map:N0} kPa from the Boost tab — not a real ratio";

        EngNote.Text =
            "The boosted figure is the static ratio multiplied by the manifold pressure over the "
            + "atmosphere the engine is breathing, and it is an index for comparing one combination "
            + "against another rather than a compression ratio. It ignores the charge temperature "
            + "boost brings with it, which moves the knock limit the wrong way, and the cam timing "
            + "the static ratio ignores too. Its use is the trade it makes visible: less compression "
            + "buys boost, and it says roughly how much.";
    }

    // ----- the whole build -----------------------------------------------------

    /// <summary>Which fuel the lambda box's mixture belongs to.</summary>
    private Fuel _recipeFuel = Fuel.Petrol;

    private void OnRecipeChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowRecipe();
    }

    /// <summary>
    /// Sets the volumetric efficiency from the kind of engine chosen.
    ///
    /// The number stays editable, and typing in it moves the list to "measured or
    /// known" rather than being overwritten — somebody's own figure off a dyno
    /// beats any description, and the list exists for the far commoner case of
    /// knowing what the engine is and not what it breathes.
    /// </summary>
    private void OnRecipeVeFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || RecVe is null) return;

        if (EngineFamilies.All.ElementAtOrDefault(RecVeFamily.SelectedIndex) is { IsCustom: false } family)
            Set(RecVe, Round(family.VolumetricEfficiency, 0));

        ShowRecipe();
    }

    private bool _recipeFahrenheit;

    /// <summary>Switches the charge temperature between the scales, converting it.</summary>
    private void OnRecipeTempUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecChargeTemp is null) return;

        bool nowFahrenheit = RecTempUnit.SelectedIndex == 1;

        if (nowFahrenheit != _recipeFahrenheit && Value(RecChargeTemp) is var shown && !double.IsNaN(shown))
            Set(RecChargeTemp, Round(
                nowFahrenheit
                    ? TuningMath.FahrenheitFromCelsius(shown)
                    : TuningMath.CelsiusFromFahrenheit(shown),
                1));

        _recipeFahrenheit = nowFahrenheit;

        ShowRecipe();
    }

    private void OnRecipeFuelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecList is null) return;

        // Lambda is already fuel-independent — that is the whole reason this tab
        // asks for it rather than an air-fuel ratio. Nothing to carry across.
        _recipeFuel = FuelOf(RecFuel);

        ShowRecipe();
    }

    /// <summary>A warning dressed for the list, with a colour for how much it matters.</summary>
    private sealed record ShownWarning(string Severity, string Text, Brush Tint);

    private void ShowRecipe()
    {
        if (RecList is null) return;

        Fuel fuel = FuelOf(RecFuel);

        var spec = new RecipeSpec
        {
            Litres = Value(RecLitres),
            Cylinders = (int)Value(RecCylinders),
            TargetHorsepower = Value(RecPower),
            PeakTorqueRpm = Value(RecTorqueRpm),
            PeakPowerRpm = Value(RecPowerRpm),
            Fuel = fuel,
            Lambda = Value(RecLambda),
            VolumetricEfficiency = Value(RecVe),
            ChargeCelsius = _recipeFahrenheit
                ? TuningMath.CelsiusFromFahrenheit(Value(RecChargeTemp))
                : Value(RecChargeTemp),
            InjectorDutyLimit = Value(RecDuty),

            // Shared with the rest of the window rather than asked for again.
            BarometricKpa = _barometricKpa,
            InletLossKpa = Value(PrInletLoss) * TuningMath.KpaPerPsi,
            ChargeLossKpa = Value(PrChargeLoss) * TuningMath.KpaPerPsi,
            RailPsi = Value(PumpRailPsi),
            PumpHeadroomPercent = Value(RecPumpHeadroom),
        };

        Recipe recipe = EngineRecipe.Build(spec);

        RecLitresNote.Text = spec.Litres > 0
            ? $"litres — {spec.Litres * TuningMath.CubicInchesPerLitre:N0} cubic inches"
            : "litres";

        RecPowerNote.Text = double.IsNaN(recipe.SpecificOutput)
            ? "hp at the crank"
            : $"hp at the crank — {recipe.SpecificOutput:N0} per litre";

        RecPistonNote.Text = double.IsNaN(recipe.MeanPistonSpeed)
            ? "rpm"
            : $"rpm — {recipe.MeanPistonSpeed:N1} m/s of piston, "
              + EngineGeometry.PistonSpeedVerdict(recipe.MeanPistonSpeed);

        // The list follows the box rather than the other way round, so a typed
        // figure is never quietly replaced by the description nearest to it.
        _updating = true;
        RecVeFamily.SelectedIndex = EngineFamilies.IndexFor(spec.VolumetricEfficiency);
        _updating = false;

        RecVeNote.Text = EngineFamilies.For(spec.VolumetricEfficiency).Note;

        RecTempNote.Text = double.IsNaN(spec.ChargeCelsius)
            ? "after the intercooler"
            : $"after the intercooler — {(_recipeFahrenheit ? $"{spec.ChargeCelsius:N0} °C" : $"{TuningMath.FahrenheitFromCelsius(spec.ChargeCelsius):N0} °F")}";

        RecFuelNote.Text =
            $"stoichiometric {TuningMath.Stoichiometric(fuel):N2}:1, BSFC {recipe.Bsfc:N2}";

        // The two margins say the same thing in the units each part is bought in
        // — a duty limit for injectors, headroom for a pump — so both also report
        // the spare capacity they come to. Without that they cannot be compared,
        // and sizing injectors with a fifth in hand and a pump with a twentieth
        // is a fuel system whose weakest part is not the one you were watching.
        double duty = spec.InjectorDutyLimit;

        RecDutyNote.Text = duty > 0 && duty <= 100
            ? $"% — {(100 / duty) - 1:P0} more injector than the target needs"
            : "% at the target";

        RecPumpNote.Text = spec.PumpHeadroomPercent >= 0
            ? $"% — {spec.PumpHeadroomPercent / 100:P0} more pump than the target burns"
            : "% over what is burned";

        RecLambdaNote.Text = double.IsNaN(recipe.Afr)
            ? "at full throttle"
            : $"at full throttle — {recipe.Afr:N2}:1 on {TuningMath.ShortName(fuel)}";

        RecList.Text = Assemble(recipe);

        ShownWarning[] warnings =
        [
            .. recipe.Warnings.Select(w => new ShownWarning(w.Severity, w.Text, TintFor(w.Severity))),
        ];

        RecWarnings.ItemsSource = warnings;
        RecWarningsHeading.Visibility = warnings.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// A warning's colour: the theme's own marker for the ones that matter, and
    /// nothing shouting for the ones that are merely worth knowing.
    /// </summary>
    private static Brush TintFor(string severity) => severity switch
    {
        "stop" => new SolidColorBrush(ThemeManager.Current.RampWarm),
        "watch" => new SolidColorBrush(ThemeManager.Current.Marker),
        _ => new SolidColorBrush(ThemeManager.Current.Muted),
    };

    /// <summary>
    /// The parts list itself.
    ///
    /// Written as a block of text rather than a grid of boxes because it is
    /// meant to be read from top to bottom once and then copied into a message
    /// to somebody, which is what people actually do with a list like this.
    /// </summary>
    private static string Assemble(Recipe recipe)
    {
        if (double.IsNaN(recipe.AirAtPeakPower))
            return "  a displacement, a power target and an engine speed to make it at";

        var lines = new List<string>
        {
            $"  Air            {recipe.AirAtPeakPower,7:N1} lb/min at the power peak",
            $"                 {recipe.AirAtPeakTorque,7:N1} lb/min at the torque peak",
            $"  Boost          {recipe.BoostKpa / TuningMath.KpaPerPsi,7:N1} psi"
            + $"   ({recipe.ManifoldKpa:N0} kPa absolute)",
            $"  Pressure ratio {recipe.PressureRatio,7:N2}   at the compressor",
            "",
        };

        lines.Add(recipe.Turbos.Count > 0
            ? $"  Turbo          {recipe.Turbos[0].Label} — {recipe.Turbos[0].Turbo.InducerMm:N0} mm inducer,"
              + $" {recipe.Turbos[0].Turbo.RatedHorsepower:N0} hp rated"
            : "  Turbo          nothing in the catalogue covers this");

        foreach (TurboMatch other in recipe.Turbos.Skip(1).Take(2))
            lines.Add($"                 or {other.Label} — {other.Turbo.InducerMm:N0} mm,"
                      + $" {other.Headroom:P0} spare");

        lines.Add("");
        lines.Add($"  Injectors      {recipe.InjectorCcEach,7:N0} cc/min each"
                  + $"   ({recipe.InjectorLbHrEach:N1} lb/hr)");
        lines.Add($"  Fuel burned    {recipe.FuelLitresPerHour,7:N0} L/h at the target");
        lines.Add($"  Pump           {recipe.PumpLitresPerHour,7:N0} L/h"
                  + $"   {TuningMath.GallonsPerMinute(recipe.PumpLitresPerHour):N2} gal/min"
                  + $"   at {recipe.RailUnderBoostPsi:N0} psi");

        if (recipe.Pumps.Count > 0)
            lines.Add($"                 {recipe.Pumps[0].Pump.Name}"
                      + (recipe.Pumps[0].Count > 1 ? $" × {recipe.Pumps[0].Count}" : ""));

        return string.Join(Environment.NewLine, lines);
    }

    // ----- turbo sizing --------------------------------------------------------

    private void OnTurboChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowTurbo();
    }

    /// <summary>Which fuel the mixture and consumption in the boxes belong to.</summary>
    private Fuel _turboFuel = Fuel.Petrol;

    /// <summary>
    /// Moves the mixture and the fuel consumption together when the fuel changes.
    ///
    /// Both, and that is the whole point of the selector. The air a target needs
    /// is the power times the ratio times the consumption, so changing one of
    /// those without the other is not a different fuel — it is an arithmetic
    /// mistake. Someone who knows E85 wants a richer mixture and types 7.6 into
    /// the ratio, leaving the consumption at petrol's 0.46, comes out asking for
    /// a third less air than the engine will actually swallow, and buys a
    /// turbocharger a size too small on the strength of it.
    ///
    /// Moved together they very nearly cancel, which is the true and surprising
    /// answer: an alcohol needs about the same air for the same power, because a
    /// pound of air carries about as much energy whichever fuel arrives with it.
    /// What the alcohols buy is the boost and timing to use more air, not more
    /// power from the air.
    ///
    /// Scaled rather than replaced, as on the fuel pump: someone who typed a
    /// mixture or a consumption they measured means something by it, and that
    /// survives the change of fuel.
    /// </summary>
    /// <summary>Sets the volumetric efficiency from the kind of engine chosen.</summary>
    private void OnTurboVeFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || TurboVe is null) return;

        if (EngineFamilies.All.ElementAtOrDefault(TurboVeFamily.SelectedIndex) is { IsCustom: false } family)
            Set(TurboVe, Round(family.VolumetricEfficiency, 0));

        ShowTurbo();
    }

    private void OnTurboFuelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TurboAfr is null) return;

        Fuel chosen = FuelOf(TurboFuel);

        double lambda = TuningMath.LambdaFromAfr(Value(TurboAfr), _turboFuel);
        if (double.IsNaN(lambda) || lambda <= 0) lambda = 0.78;

        double petrol = TuningMath.PetrolEquivalentBsfc(_turboFuel, Value(TurboBsfc));
        if (double.IsNaN(petrol)) petrol = TurboSizing.RatedBsfc;

        Set(TurboAfr, Round(TuningMath.AfrFromLambda(lambda, chosen), 2));
        Set(TurboBsfc, Round(TuningMath.SuggestedBsfc(chosen, petrol), 3));

        _turboFuel = chosen;

        ShowTurbo();
    }

    /// <summary>
    /// Switches the charge temperature between the two scales it is quoted in.
    ///
    /// The number in the box is converted rather than reinterpreted, so the
    /// temperature stays the same physical thing and the answer below does not
    /// move. Reinterpreting it would turn a 55 degree charge into a 55 degree
    /// Fahrenheit one — thirteen Celsius, colder than the day — and quietly
    /// change every figure under it.
    /// </summary>
    private void OnTurboTempUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TurboChargeTemp is null) return;

        bool nowFahrenheit = TurboTempUnit.SelectedIndex == 1;

        if (nowFahrenheit != _turboFahrenheit && Value(TurboChargeTemp) is var shown && !double.IsNaN(shown))
            Set(TurboChargeTemp, Round(
                nowFahrenheit
                    ? TuningMath.FahrenheitFromCelsius(shown)
                    : TuningMath.CelsiusFromFahrenheit(shown),
                1));

        _turboFahrenheit = nowFahrenheit;

        ShowTurbo();
    }

    private bool _turboFahrenheit;

    private void ShowTurbo()
    {
        if (TurboPicks is null) return;

        double litres = Value(TurboLitres);
        double afr = Value(TurboAfr);
        Fuel fuel = FuelOf(TurboFuel);

        double shownTemp = Value(TurboChargeTemp);
        double chargeC = _turboFahrenheit ? TuningMath.CelsiusFromFahrenheit(shownTemp) : shownTemp;

        // The intercooler's share is taken from the Pressure ratio tab rather
        // than asked for twice: it is the same intercooler, and two boxes for
        // one number is two chances to disagree with yourself.
        double chargeLoss = Value(PrChargeLoss) * TuningMath.KpaPerPsi;

        TurboRequirement need = TurboSizing.Required(
            Value(TurboPower),
            afr,
            Value(TurboBsfc),
            litres,
            Value(TurboRpm),
            Value(TurboVe),
            chargeC,
            _barometricKpa,
            Value(TurboInletLoss) * TuningMath.KpaPerPsi,
            chargeLoss);

        TurboLitresNote.Text = litres > 0
            ? $"litres — {litres * TuningMath.CubicInchesPerLitre:N0} cubic inches"
            : "litres";

        double turboVe = Value(TurboVe);

        _updating = true;
        TurboVeFamily.SelectedIndex = EngineFamilies.IndexFor(turboVe);
        _updating = false;

        TurboVeNote.Text = EngineFamilies.For(turboVe).Note;

        TurboFuelNote.Text =
            $"stoichiometric {TuningMath.Stoichiometric(fuel):N2}:1 — the two boxes below follow it";

        TurboAfrNote.Text = afr > 0
            ? $"at full throttle — lambda {TuningMath.LambdaFromAfr(afr, fuel):N2} on {TuningMath.ShortName(fuel)}"
            : "at full throttle";

        TurboBsfcNote.Text =
            $"lb/hp/hr — the ratings assume {TurboSizing.RatedBsfc:N2} on petrol";

        TurboTempNote.Text = double.IsNaN(chargeC)
            ? "in the manifold, after the intercooler"
            : $"in the manifold — {(_turboFahrenheit ? $"{chargeC:N0} °C" : $"{TuningMath.FahrenheitFromCelsius(chargeC):N0} °F")}";

        TurboAir.Text = Show(need.AirLbPerMinute, 1);

        TurboBoost.Text = double.IsNaN(need.BoostKpa)
            ? "—"
            : (need.BoostKpa / TuningMath.KpaPerPsi).ToString("N1", CultureInfo.CurrentCulture);

        TurboBoostNote.Text = double.IsNaN(need.ManifoldKpa)
            ? "psi"
            : $"psi — {need.ManifoldKpa:N0} kPa absolute in the manifold";

        TurboRatio.Text = Show(need.PressureRatio, 2);
        TurboRatioNote.Text = double.IsNaN(need.PressureRatio)
            ? "at the compressor"
            : $"{need.CompressorInletKpa / TuningMath.KpaPerPsi:N1} psia in, "
              + $"{need.CompressorOutletKpa / TuningMath.KpaPerPsi:N1} out, "
              + $"drawing {_barometricKpa:N0} kPa";

        double headroom = Value(TurboHeadroom) / 100;
        if (double.IsNaN(headroom) || headroom < 0) headroom = TurboSizing.SensibleHeadroom;

        IReadOnlyList<TurboMatch> picks = TurboSizing.Suggest(need.AirLbPerMinute, headroom);

        TurboPicksTitle.Text = need.AirLbPerMinute > 0
            ? $"Turbochargers that could pass {need.AirLbPerMinute:N1} lb/min"
              + $" with {headroom:P0} spare"
            : "Turbochargers";

        TurboPicks.Text = picks.Count > 0
            ? string.Join(Environment.NewLine, picks.Select(Describe))
            : need.AirLbPerMinute > 0
                ? "  nothing in the list, even in pairs"
                : "  a power target and an engine to put it on";

        TurboNote.Text = picks.Count switch
        {
            0 when need.AirLbPerMinute > 0 =>
                "Past what this short list covers, singly or in pairs. Garrett's own range goes a great "
                + "deal further than the eleven frames here — this is a starting point rather than a "
                + "catalogue.",

            > 0 when picks[0].Count > 1 =>
                "No single turbocharger on the list does it, so these are pairs. Two smaller ones is a "
                + "real answer to a large engine rather than a consolation prize: they spool sooner "
                + "than the one big one that would be needed, and each sits nearer the middle of its "
                + "own map.",

            _ =>
                "Ratings are the maker's own — on the G series the horsepower is the model number. The "
                + $"flow each one is credited with is worked out from that rating at {TurboSizing.RatedAfr:N1}:1 "
                + $"and a BSFC of {TurboSizing.RatedBsfc:N2}, which is what the maker's own worked example "
                + "uses, rather than transcribed off a compressor map — the same map gives a different "
                + "maximum depending which island and which pressure ratio you read it at. Check the "
                + "current catalogue before buying anything on the strength of this.",
        };
    }

    private static string Describe(TurboMatch match) =>
        $"  {match.Label,-16} {match.Turbo.InducerMm,3:N0} mm"
        + $"  {match.Turbo.RatedHorsepower,5:N0} hp"
        + $"  {match.Turbo.MaxFlowLbPerMinute * match.Count,5:N0} lb/min"
        + $"  {match.Headroom,5:P0} spare";


    // ----- the strip -----------------------------------------------------------

    private void OnDragChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowDragStrip();
    }

    private void OnDragFormulaChanged(object sender, SelectionChangedEventArgs e) => ShowDragStrip();

    private DragFormula ChosenFormula() =>
        DragFormula.SelectedIndex >= 0 && DragFormula.SelectedIndex < DragStrip.Formulas.Count
            ? DragStrip.Formulas[DragFormula.SelectedIndex]
            : DragStrip.Default;

    private void ShowDragStrip()
    {
        if (DragVerdict is null) return;

        DragFormula formula = ChosenFormula();

        double weight = Value(DragWeight);
        double power = Value(DragPower);

        DragFormulaNote.Text = formula.Note;

        DragWeightNote.Text = weight > 0
            ? $"lb with the driver in it — {weight * 0.45359237:N0} kg"
            : "lb, with the driver in it";

        DragPowerNote.Text = weight > 0 && power > 0
            ? $"hp at the crank — {weight / power:N1} lb per horsepower"
            : "hp at the crank";

        double quarterEt = DragStrip.QuarterEt(power, weight, formula);
        double quarterMph = DragStrip.QuarterMph(power, weight, formula);

        DragQuarterEt.Text = Show(quarterEt, 2);
        DragQuarterNote.Text = double.IsNaN(quarterMph)
            ? "seconds"
            : $"seconds at {quarterMph:N1} mph";

        DragEighthEt.Text = Show(DragStrip.EighthEt(power, weight, formula), 2);
        DragEighthNote.Text = double.IsNaN(quarterMph)
            ? "seconds"
            : $"seconds at {DragStrip.EighthMph(power, weight, formula):N1} mph";

        // The spread between the fastest and slowest correlation, which is not
        // disagreement — it is how much the launch is worth.
        DragFormula best = DragStrip.Formulas.OrderBy(f => f.EtConstant).First();
        DragFormula worst = DragStrip.Formulas.OrderByDescending(f => f.EtConstant).First();

        DragBest.Text = Show(DragStrip.QuarterEt(power, weight, best), 2);
        DragBestNote.Text = $"seconds on {best.Name} — a run that hooked up";

        DragWorst.Text = Show(DragStrip.QuarterEt(power, weight, worst), 2);
        DragWorstNote.Text = $"seconds on {worst.Name} — one that did not";

        ShowSlip(weight, formula);
    }

    /// <summary>
    /// What the timeslip says, if one has been typed in.
    ///
    /// The trap is read for power and the time for the start line, because the
    /// two are not equally trustworthy — a bad launch is forgotten by the far
    /// end and never forgotten by the clock.
    /// </summary>
    private void ShowSlip(double weight, DragFormula formula)
    {
        double trap = Value(DragTrap);
        double et = Value(DragEt);

        SlipReading reading = DragStrip.Read(trap, et, weight, formula);

        DragFromTrap.Text = Show(reading.PowerFromTrap, 0);

        DragFromTrapNote.Text = double.IsNaN(reading.PowerFromTrap)
            ? "hp — type a trap speed above"
            : double.IsNaN(reading.PowerFromEt)
                ? "hp at the crank, from the trap speed"
                : $"hp from the trap — the time on its own would say {reading.PowerFromEt:N0}";

        DragLaunch.Text = double.IsNaN(reading.LaunchCost)
            ? "—"
            : $"{reading.LaunchCost:+0.00;−0.00}";

        DragLaunchNote.Text = double.IsNaN(reading.EtTheTrapDeserved)
            ? "seconds — type a time as well"
            : $"seconds against the {reading.EtTheTrapDeserved:N2} that trap deserved";

        DragVerdict.Text = Verdict(reading);
    }

    private static string Verdict(SlipReading reading)
    {
        if (double.IsNaN(reading.LaunchCost))
            return "Type in a trap speed and a time from a slip and this will read the run: what the "
                 + "car actually made, and what the start line cost.";

        if (reading.LaunchCost > DragStrip.LaunchWorthMentioning)
            return $"The car trapped like {reading.PowerFromTrap:N0} hp and ran "
                 + $"{reading.LaunchCost:N2} s slower than that trap deserved. That time went missing "
                 + "in the first sixty feet — tyres, pressure, launch rpm or suspension — and it is "
                 + "the cheapest time on the car to find. Nothing done to the engine will show up "
                 + "until it is.";

        if (reading.LaunchCost < -DragStrip.LaunchWorthMentioning)
            return $"The car ran {-reading.LaunchCost:N2} s quicker than its trap speed deserved, which "
                 + "means it left very well indeed — or that it weighs less than the figure above. "
                 + "The trap is still the honest read on power.";

        return $"The time and the trap agree: about {reading.PowerFromTrap:N0} hp, and a launch with "
             + "nothing much left in it. Further time now has to come from power or from weight.";
    }

    // ----- gearing -------------------------------------------------------------

    private void OnGearingChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowGearing();
    }

    /// <summary>
    /// Gear ratios as typed: one line, separated by whatever came to hand.
    ///
    /// A box per gear would fix the number of gears, and gearboxes come with
    /// four, five, six, seven and more. Anything that is not a number is skipped
    /// rather than treated as a zero, since a trailing comma should not invent a
    /// gear with an infinite ratio in it.
    /// </summary>
    private static IReadOnlyList<double> RatiosIn(string text) =>
        [.. text
            .Split([',', ' ', '\t', ';', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
                ? v
                : double.NaN)
            .Where(v => v > 0)];

    private void ShowGearing()
    {
        if (GearChart is null) return;

        double diameterMm = double.NaN;
        string tyreName = string.Empty;

        if (Gearing.TryParseTyre(GearTyre.Text, out Tyre tyre))
        {
            diameterMm = tyre.DiameterMm;
            tyreName = tyre.ToString();
        }

        GearTyreNote.Text = double.IsNaN(diameterMm)
            ? "as written on the sidewall — 245/40R18"
            : $"{tyreName} — {diameterMm:N0} mm, {tyre.DiameterInches:N1} in across";

        double deflection = Value(GearDeflection);
        double circumference = Gearing.RollingCircumferenceMm(diameterMm, deflection);

        GearRollNote.Text = double.IsNaN(circumference)
            ? "% the loaded tyre squats"
            : $"% squat — {circumference:N0} mm of road per turn, "
              + $"{Gearing.MmPerMile / circumference:N0} turns per mile";

        IReadOnlyList<double> ratios = RatiosIn(GearRatios.Text);

        double redline = Value(GearRedline);
        double cruise = Value(GearCruise);

        IReadOnlyList<Gearing.GearStep> table =
            Gearing.Table(ratios, Value(GearFinal), redline, circumference, cruise);

        GearChartTitle.Text = table.Count > 0
            ? $"{table.Count} gears at {redline:N0} rpm"
            : "Gears";

        GearChart.Text = table.Count > 0
            ? Gearing.Chart(table, cruise > 0)
            : "  a tyre, a final drive, a redline and some ratios";

        if (table.Count == 0)
        {
            GearTopMph.Text = "—";
            GearCruiseRpm.Text = "—";
            GearTopNote.Text = "mph";
            GearCruiseNote.Text = "rpm";
            GearNote.Text = string.Empty;

            return;
        }

        Gearing.GearStep top = table[^1];

        GearTopMph.Text = Show(top.Mph, 0);
        GearTopNote.Text = $"mph — {top.Kph:N0} km/h, {redline:N0} rpm in {Ordinal(top.Gear)}";

        GearCruiseRpm.Text = double.IsNaN(top.RpmAtCruise) ? "—" : Show(top.RpmAtCruise, 0);
        GearCruiseNote.Text = double.IsNaN(top.RpmAtCruise)
            ? "rpm"
            : $"rpm at {cruise:N0} mph in {Ordinal(top.Gear)}";

        // Said rather than implied, because "top speed" from a gearing
        // calculator is the one figure people quote at each other as though it
        // were a measurement.
        GearNote.Text =
            "This is the geared top speed: the tallest gear at the redline, and nothing else. It "
            + "assumes the engine can still pull that gear against the air, which past about a "
            + "hundred and fifty miles an hour is a large assumption. A car geared taller than its "
            + "power can push never reaches the redline in top at all, and what it actually does is "
            + "set by drag — so treat this as the ceiling the gearbox imposes rather than as a "
            + "speed the car will see.";
    }

    private static string Ordinal(int gear) => gear switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{gear}th",
    };

    // ----- octane --------------------------------------------------------------

    private void OnOctaneChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowOctane();
    }

    private void OnOctaneStockChanged(object sender, SelectionChangedEventArgs e) => ShowOctane();

    private Blendstock StockOf() =>
        OctStock.SelectedIndex >= 0
            ? Enum.GetValues<Blendstock>()[OctStock.SelectedIndex]
            : Blendstock.Ethanol;

    private void ShowOctane()
    {
        if (OctChart is null) return;

        Blendstock stock = StockOf();

        double sensitivity = Value(OctSensitivity);
        double fraction = Value(OctPercent) / 100;

        OctaneResult blend = OctaneBlend.Blend(Value(OctBase), sensitivity, stock, fraction);

        OctAki.Text = Show(blend.AntiKnockIndex, 1);
        OctRonMon.Text = blend.Ron > 0
            ? $"{blend.Ron:N1} / {blend.Mon:N1}"
            : "—";

        OctHov.Text = Show(blend.HeatOfVaporisationKjPerKg, 0);
        OctCooling.Text = Show(blend.CoolingKjPerKgAir, 0);

        double petrolCooling = OctaneBlend.PetrolCoolingKjPerKgAir;

        OctSensNote.Text = blend.Ron > 0
            ? $"sensitivity {blend.Ron - blend.Mon:N1}, against the base's {sensitivity:N1}"
            : "research and motor octane";

        OctHovNote.Text = blend.HeatOfVaporisationKjPerKg > 0
            ? $"kJ/kg — {blend.HeatOfVaporisationKjPerKg / OctaneBlend.PetrolHov:N2}× neat petrol"
            : "kJ/kg";

        OctCoolingNote.Text = blend.CoolingKjPerKgAir > 0
            ? $"kJ per kg of air — {blend.CoolingKjPerKgAir / petrolCooling:N2}× petrol"
            : "kJ per kg of air";

        // What is in the finished mixture, which is not what is on the jug when
        // the jug held E85 — half a tank of it leaves 42.5 per cent ethanol.
        OctMixNote.Text = blend.AntiKnockIndex > 0
            ? $"% — {blend.EthanolByVolume:P0} alcohol, {blend.AlcoholMoleFraction:P0} by molecule"
            : "% by volume";

        OctStockNote.Text = stock switch
        {
            Blendstock.E85 => "itself 85% ethanol, so it dilutes as it blends",
            Blendstock.Methanol => "more octane per litre than ethanol, and far colder",
            _ => "RON 109, MON 90 neat",
        };

        OctChartTitle.Text = $"Anti-knock index with {OctaneBlend.Name(stock)} by volume";
        OctChart.Text = OctaneBlend.Chart(stock);

        OctCoolingTitle.Text = "Heat of vaporisation, and what it is worth per kg of air";
        OctCoolingChart.Text = OctaneBlend.CoolingChart(stock);

        // Said once, plainly: the chart above does not depend on the sensitivity
        // box, and it is worth knowing which figures an answer actually rests on.
        OctNote.Text =
            "The chart is unaffected by the sensitivity above. Splitting a pump number into RON and "
            + "MON adds half the sensitivity to one and takes it off the other, and averaging them "
            + "back cancels it exactly — so the index a blend lands on does not depend on that "
            + "assumption at all. It only changes the RON and MON shown separately. "
            + $"Petrol's own heat of vaporisation is a range rather than a figure — {OctaneBlend.PetrolHov:N0} "
            + "kJ/kg is used here and published values run from about 305 to 350 — so the cooling "
            + "figures are worth a tenth either way. The alcohols are compounds and are pinned much "
            + "better than that.";
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

        // Local barometric on the top and sea level underneath, which looks like
        // a mistake and is the point. The manifold is at gauge plus whatever the
        // engine is breathing, but the density this scales is the standard one
        // the lb/ft³ constant is quoted at. Dividing by local barometric too
        // would cancel the altitude out and overstate the air by a fifth up
        // high — and it would do it while showing a perfectly plausible number.
        double absolute = TuningMath.AbsoluteFromGauge(boostKpa, _barometricKpa);
        double ratio = TuningMath.ChargeDensityRatio(absolute);

        double cfm = TuningMath.CubicFeetPerMinute(litres, Value(AirRpm), Value(AirVe), ratio);

        AirCid.Text = double.IsNaN(litres)
            ? "litres"
            : $"litres — {litres * TuningMath.CubicInchesPerLitre:N0} cubic inches";

        AirRatio.Text = double.IsNaN(ratio)
            ? "psi"
            : $"psi — {absolute:N0} kPa absolute, {ratio:N2}× sea-level air";

        AirBaroNote.Text = BarometricSummary()
            + " The compressor's own pressure ratio moves the other way with altitude, and is on "
            + "that tab too.";

        double lbMin = TuningMath.AirPoundsPerMinute(cfm);

        AirCfm.Text = Show(cfm, 0);
        AirLbMin.Text = Show(lbMin, 1);
        AirM3h.Text = Show(TuningMath.CubicMetresPerHour(cfm), 0);

        ShowPower(lbMin);
    }

    /// <summary>
    /// What that air is worth in power, on the three fuels worth comparing.
    ///
    /// The comparison is like for like: one lambda and one efficiency, with each
    /// fuel's own BSFC scaled from the petrol figure by its energy content. Give
    /// the alcohols their real BSFC against petrol's and they would look worse
    /// than petrol, which is the wrong answer arrived at by comparing a fuel
    /// against itself.
    /// </summary>
    private void ShowPower(double airPoundsPerMinute)
    {
        double lambda = Value(AirLambda);
        double bsfc = Value(AirBsfc);

        double petrol = TuningMath.HorsepowerFromAir(airPoundsPerMinute, Fuel.Petrol, lambda, bsfc);
        double ethanol = TuningMath.HorsepowerFromAir(airPoundsPerMinute, Fuel.Ethanol, lambda, bsfc);
        double methanol = TuningMath.HorsepowerFromAir(airPoundsPerMinute, Fuel.Methanol, lambda, bsfc);

        AirHpPetrol.Text = Show(petrol, 0);
        AirHpEthanol.Text = Show(ethanol, 0);
        AirHpMethanol.Text = Show(methanol, 0);

        AirHpPetrolNote.Text = FuelPowerNote(Fuel.Petrol, lambda, bsfc, petrol, petrol);
        AirHpEthanolNote.Text = FuelPowerNote(Fuel.Ethanol, lambda, bsfc, ethanol, petrol);
        AirHpMethanolNote.Text = FuelPowerNote(Fuel.Methanol, lambda, bsfc, methanol, petrol);

        AirLambdaNote.Text = double.IsNaN(lambda) || lambda <= 0
            ? "at full throttle, not at cruise"
            : $"at full throttle — {TuningMath.AfrFromLambda(lambda, Fuel.Petrol):N1}:1 on petrol";

        // The point most of a workshop would argue with, so it is said plainly
        // and the arithmetic above it is there to be checked.
        AirPowerNote.Text = petrol > 0
            ? "The same air is very nearly the same power on any fuel: a pound of air carries about "
            + "as much energy whichever fuel comes with it, so at the same lambda the alcohols are "
            + "worth a few per cent and no more. What makes them worth having is that they resist "
            + "knock and cool the charge, which buys boost and timing — and that arrives as more "
            + "air, further up this tab, rather than as more power per pound of it."
            : "Power needs a lambda and a BSFC above.";
    }

    /// <summary>The AFR and BSFC behind one of the power figures, and what it is worth against petrol.</summary>
    private static string FuelPowerNote(Fuel fuel, double lambda, double bsfc, double hp, double petrolHp)
    {
        double afr = TuningMath.AfrFromLambda(lambda, fuel);
        double own = TuningMath.SuggestedBsfc(fuel, bsfc);

        if (double.IsNaN(afr) || double.IsNaN(own) || hp <= 0) return "hp at the crank";

        string against = fuel == Fuel.Petrol
            ? string.Empty
            : $", {(hp / petrolHp) - 1:+0.0%;-0.0%} on petrol";

        return $"hp — {afr:N1}:1, BSFC {own:N2}{against}";
    }

    // ----- running costs --------------------------------------------------------

    /// <summary>
    /// The three vehicles, so every handler works on the columns rather than on
    /// nine named boxes. Built on demand because the fields do not exist until
    /// the window has been laid out.
    /// </summary>
    private (ComboBox Fuel, TextBox Economy, TextBox Price,
             TextBlock Week, TextBlock Month, TextBlock Year,
             TextBlock Mile, TextBlock Mpge)[] CostColumns =>
    [
        (CostFuelA, CostEconomyA, CostPriceA, CostWeekA, CostMonthA, CostYearA, CostMileA, CostMpgeA),
        (CostFuelB, CostEconomyB, CostPriceB, CostWeekB, CostMonthB, CostYearB, CostMileB, CostMpgeB),
        (CostFuelC, CostEconomyC, CostPriceC, CostWeekC, CostMonthC, CostYearC, CostMileC, CostMpgeC),
    ];

    private void OnCostChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowRunningCosts();
    }

    /// <summary>
    /// Changing the fuel refills the price with that fuel's average.
    ///
    /// Overwriting what somebody typed, deliberately: a price left over from
    /// petrol sitting next to a newly chosen electricity is not a considered
    /// figure, it is a leftover, and it would quietly produce an answer that is
    /// wrong by a factor of twenty. The typed price is theirs again the moment
    /// they type it.
    /// </summary>
    private void OnCostFuelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;

        _updating = true;

        foreach ((ComboBox fuel, _, TextBox price, _, _, _, _, _) in CostColumns)
        {
            if (!ReferenceEquals(fuel, sender)) continue;

            price.Text = FuelPrices.For(KindOf(fuel)).ToString("0.####", CultureInfo.CurrentCulture);
        }

        _updating = false;

        ShowRunningCosts();
    }

    private static FuelKind KindOf(ComboBox box) =>
        box.SelectedIndex >= 0 && box.SelectedIndex < Fuels.All.Count
            ? Fuels.All[box.SelectedIndex].Kind
            : FuelKind.Petrol;

    private void ShowRunningCosts()
    {
        if (CostVerdict is null) return;

        double miles = Value(CostMiles);

        CostMilesNote.Text = miles > 0
            ? $"{miles / RunningCosts.WeeksPerYear:N0} a week, {miles / 12:N0} a month"
            : "how far you drive in a year";

        var vehicles = new List<VehicleCost>();
        string[] names = ["A", "B", "C"];

        for (int i = 0; i < CostColumns.Length; i++)
        {
            var column = CostColumns[i];
            FuelKind kind = KindOf(column.Fuel);
            FuelSpec fuel = Fuels.For(kind);

            var vehicle = new VehicleCost(
                names[i], kind, Value(column.Economy), Value(column.Price));

            vehicles.Add(vehicle);

            column.Week.Text = Money(RunningCosts.PerWeek(vehicle, miles), 0);
            column.Month.Text = Money(RunningCosts.PerMonth(vehicle, miles), 0);
            column.Year.Text = Money(RunningCosts.PerYear(vehicle, miles), 0);

            column.Mile.Text = vehicle.IsUsable
                ? $"{vehicle.CostPerMile * 100:N1}¢ · {Show(vehicle.UnitsPer(miles), 0)} {fuel.UnitPlural} a year"
                : $"{fuel.EfficiencyUnit}, ${fuel.Unit}";

            column.Mpge.Text = vehicle.IsUsable ? $"{vehicle.Mpge:N0} MPGe" : "—";
        }

        ShowCostVerdict(vehicles, miles);

        // Only the fuels actually chosen, and each once. Three petrol columns
        // should not print the same paragraph three times.
        CostFuelNotes.Text = string.Join(
            "\n\n",
            vehicles.Select(v => v.Kind).Distinct().Select(k =>
            {
                FuelSpec f = Fuels.For(k);
                return $"{f.Name} — {f.Note}";
            }));

        CostPriceSource.Text = FuelPrices.Source;
    }

    /// <summary>
    /// What the comparison actually says, in a sentence.
    ///
    /// The numbers above are the answer; this is the part somebody repeats to
    /// whoever is deciding with them. Given as a difference over a year, because
    /// a saving per mile is invisible and a saving per year is not.
    /// </summary>
    private void ShowCostVerdict(IReadOnlyList<VehicleCost> vehicles, double miles)
    {
        VehicleCost[] usable = [.. vehicles.Where(v => v.IsUsable)];

        if (miles <= 0 || usable.Length == 0)
        {
            CostVerdict.Text =
                "Fill in the economy and the price for at least one vehicle, and how far you "
                + "drive in a year.";
            CostWarning.Visibility = Visibility.Collapsed;

            return;
        }

        VehicleCost best = RunningCosts.Cheapest(usable)!;

        if (usable.Length == 1)
        {
            CostVerdict.Text =
                $"Vehicle {best.Name} costs {Money(RunningCosts.PerYear(best, miles), 0)} a year "
                + $"in {Fuels.For(best.Kind).Name.ToLowerInvariant()} over {miles:N0} miles. "
                + "Fill in another to compare.";
        }
        else
        {
            IEnumerable<string> against = usable
                .Where(v => v != best)
                .Select(v =>
                    $"{Money(RunningCosts.AnnualSaving(best, v, miles), 0)} a year less than {v.Name}");

            CostVerdict.Text =
                $"Vehicle {best.Name} is the cheapest to run — {string.Join(", and ", against)}. "
                + $"Over {miles:N0} miles a year.";
        }

        // The mistake most likely to be sitting on this page, said plainly rather
        // than quietly costed as typed.
        VehicleCost? copied = usable.FirstOrDefault(
            v => RunningCosts.LooksCopiedFromPetrol(v, usable));

        if (copied is not null)
        {
            CostWarning.Text =
                $"Vehicle {copied.Name} is on E85 at {copied.Economy:N0} mpg, which is close to "
                + "what a petrol car in this comparison manages. E85 holds about three-quarters "
                + "of petrol's energy, so the same car does roughly a quarter fewer miles to the "
                + "gallon on it. As entered, E85 will look cheaper than it is.";

            CostWarning.Visibility = Visibility.Visible;
        }
        else
        {
            CostWarning.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Currency, or a dash where there is nothing to show.</summary>
    private static string Money(double value, int digits) =>
        double.IsNaN(value) || double.IsInfinity(value) || value < 0
            ? "—"
            : "$" + value.ToString("N" + digits, CultureInfo.CurrentCulture);

    // ----- intercooling ---------------------------------------------------------

    private void OnIcChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowIntercooler();
    }

    private void OnIcFluidChanged(object sender, SelectionChangedEventArgs e) => ShowIntercooler();

    private void OnIcMaterialChanged(object sender, SelectionChangedEventArgs e) => ShowIntercooler();

    private Sprayable ChosenFluid() =>
        IcFluid.SelectedIndex >= 0 && IcFluid.SelectedIndex < Sprayables.All.Count
            ? Sprayables.All[IcFluid.SelectedIndex]
            : Sprayables.FiftyFifty;

    private CoreMaterial ChosenMaterial() =>
        IcMaterial.SelectedIndex >= 0 && IcMaterial.SelectedIndex < CoreMaterials.All.Count
            ? CoreMaterials.All[IcMaterial.SelectedIndex]
            : CoreMaterials.All[0];

    private void ShowIntercooler()
    {
        if (IcHotSide is null) return;

        double air = Value(IcAir);
        double boost = Value(IcBoost);
        double ambient = Value(IcAmbient);
        double compEff = Value(IcCompEff) / 100;
        double effect = Value(IcEffect) / 100;

        // ----- what comes out of the compressor --------------------------------

        double ratio = ChargeAir.PressureRatio(boost);
        double hot = ChargeAir.CompressorOutletF(ambient, ratio, compEff);

        IcAirNote.Text = air > 0
            ? $"lb/min — roughly {air * 10:N0} hp worth of air"
            : "lb/min through the compressor";

        IcBoostNote.Text = boost > 0
            ? $"psi — a pressure ratio of {ratio:N2}"
            : "psi of boost";

        IcHotSide.Text = Round(hot, 0) + " °F";
        IcHotSideNote.Text = double.IsNaN(hot)
            ? ""
            : $"{hot - ambient:N0} °F above ambient, all of which the core has to take back out";

        double heat = ChargeAir.HeatLoadBtuPerMin(air, double.IsNaN(hot) ? double.NaN : hot - ambient);

        IcHeat.Text = double.IsNaN(heat) ? "—" : $"{heat:N0} BTU/min";
        IcHeatNote.Text = double.IsNaN(heat)
            ? ""
            : $"{heat * 60:N0} BTU/hr · {heat * 60 / 3412.14:N1} kW, if it were all removed";

        // ----- what the core does with it ---------------------------------------

        double outF = ChargeAir.OutletF(hot, ambient, effect);
        double density = ChargeAir.DensityRatio(hot, outF);

        IcOut.Text = Round(outF, 0) + " °F";
        IcOutNote.Text = double.IsNaN(outF)
            ? ""
            : $"{outF - ambient:N0} °F above ambient · {hot - outF:N0} °F removed";

        IcDensity.Text = double.IsNaN(density) ? "—" : $"{(density - 1) * 100:N1}%";
        IcDensityNote.Text = double.IsNaN(density)
            ? ""
            : "more air at the same pressure, so about that much more torque before "
              + "any timing the cooler charge now tolerates";

        var core = new IntercoolerCore(Value(IcW), Value(IcH), Value(IcT));

        IcCoreNote.Text = core.IsUsable
            ? $"inches — {core.FrontalAreaSqIn:N0} in² face, {core.VolumeCuIn:N0} in³"
            : "inches, width × height × thickness";

        double loading = core.LoadingPerCuIn(double.IsNaN(heat) ? double.NaN : heat * effect);

        IcLoading.Text = double.IsNaN(loading) ? "—" : $"{loading:N2}";
        IcLoadingNote.Text = double.IsNaN(loading)
            ? ""
            : $"BTU/min per in³ · {core.LoadingPerFrontalSqIn(heat * effect):N1} per in² of face";

        CoreMaterial material = ChosenMaterial();
        double share = CoreMaterials.MetalShareOfResistance(material);

        IcMaterialNote.Text =
            $"{material.ConductivityWmK:N0} W/m·K, {material.RelativeDensity:N1}× the weight of aluminium — "
            + $"the metal is {share:P2} of the resistance between the two air streams";

        IcCoreAdvice.Text =
            material.Note
            + "  There is no honest way to predict a core's effectiveness from its outside "
            + "dimensions — that depends on the fin density, the internal geometry and how much "
            + "air actually reaches it. What the loading figure is for is comparing two "
            + "candidates: the same duty spread over half the core is twice the loading, and the "
            + "smaller one will run hotter. Returns fall away sharply, too — a well-sized core "
            + "doubled buys a few points of effectiveness, not another seventy.";

        // ----- and what a spray does instead -------------------------------------

        Sprayable fluid = ChosenFluid();
        double drop = Value(IcDrop);
        double evap = Value(IcEvap) / 100;
        double fuel = Value(IcFuel);

        IcFluidNote.Text = $"{fluid.LatentBtuPerLb:N0} BTU/lb latent · {fluid.DensityGPerCc:N3} g/cc";

        double sprayLb = ChemicalIntercooling.FlowLbPerMin(air, drop, fluid, outF, 70, evap);
        double cc = ChemicalIntercooling.FlowCcPerMin(air, drop, fluid, outF, 70, evap);

        IcDropNote.Text = double.IsNaN(outF)
            ? "°F to take out of the charge"
            : $"°F below the {outF:N0} °F leaving the core";

        IcNozzle.Text = double.IsNaN(cc) ? "—" : $"{cc:N0} cc/min";
        IcNozzleNote.Text = double.IsNaN(cc)
            ? ""
            : $"at full flow · nozzles are sold in this, which is a volume — {fluid.Name} at "
              + $"{fluid.DensityGPerCc:N3} g/cc is what turns it into a mass";

        IcSprayLb.Text = double.IsNaN(sprayLb) ? "—" : $"{sprayLb:N2} lb/min";
        IcSprayLbNote.Text = double.IsNaN(sprayLb) || fuel <= 0
            ? ""
            : $"{ChemicalIntercooling.PercentOfFuel(sprayLb, fuel):N0}% of fuel flow — "
              + "controllers are usually set up in these terms, and 20 to 50 is the usual range";

        IcConsumption.Text = double.IsNaN(cc)
            ? "—"
            : $"{ChemicalIntercooling.GallonsPerHour(cc):N1} gal/hr";

        IcConsumptionNote.Text = double.IsNaN(cc)
            ? ""
            : $"{ChemicalIntercooling.LitresPerHour(cc):N1} l/hr · a 5 gallon tank lasts "
              + $"{ChemicalIntercooling.TankMinutes(5, cc):N0} minutes at full flow";

        double displaced = ChemicalIntercooling.PetrolDisplacedLbPerMin(sprayLb, fluid);

        IcDisplaced.Text = double.IsNaN(sprayLb) ? "—" : $"{displaced:N2} lb/min";
        IcDisplacedNote.Text = displaced > 0
            ? $"{fluid.CombustibleFraction:P0} of this fluid burns — take that much petrol out, "
              + "or it runs rich with the spray and lean the moment it stops"
            : "water does not burn, so nothing here enriches — which is why a big water spray "
              + "leans the mixture if the tune was relying on it";

        double after = double.IsNaN(outF) || double.IsNaN(drop) ? double.NaN : outF - drop;

        IcAfterSpray.Text = Round(after, 0) + " °F";
        IcAfterSprayNote.Text = double.IsNaN(after) || double.IsNaN(ambient)
            ? ""
            : after < ambient
                ? $"below the {ambient:N0} °F ambient — a spray can do that, where a core cannot"
                : $"{after - ambient:N0} °F above ambient";

        IcFluidTable.Text = FluidTable(air, drop, outF, evap);
    }

    /// <summary>
    /// Every fluid at the same duty, so the trade is visible rather than argued.
    ///
    /// Water needs the least flow because it absorbs the most per pound; E85 needs
    /// three times as much and is nearly all fuel. Reading that off one table is
    /// worth more than any amount of prose about latent heat.
    /// </summary>
    /// <summary>
    /// What each fluid would have to flow to take the same heat out, side by side.
    ///
    /// Internal rather than private so the columns can be tested. These tables
    /// are built with fixed field widths and their headings are written out
    /// separately from their rows, so the two drift apart the moment a unit is
    /// added to a cell — which is a heading sitting over the wrong numbers, and
    /// nothing about the code that produced it looks wrong.
    /// </summary>
    internal static string FluidTable(double air, double drop, double chargeF, double evaporated)
    {
        var rows = new System.Text.StringBuilder();

        rows.AppendLine($"{"fluid",-24}{"BTU/lb",8}{"cc/min",10}{"lb/min",9}{"gal/hr",8}   burns");

        foreach (Sprayable fluid in Sprayables.All)
        {
            double cc = ChemicalIntercooling.FlowCcPerMin(air, drop, fluid, chargeF, 70, evaporated);
            double lb = ChemicalIntercooling.FlowLbPerMin(air, drop, fluid, chargeF, 70, evaporated);

            rows.AppendLine(
                $"{fluid.Name,-24}{fluid.LatentBtuPerLb,8:N0}"
                + $"{(double.IsNaN(cc) ? "—" : cc.ToString("N0", CultureInfo.CurrentCulture)),10}"
                + $"{(double.IsNaN(lb) ? "—" : lb.ToString("N2", CultureInfo.CurrentCulture)),9}"
                + $"{(double.IsNaN(cc) ? "—" : ChemicalIntercooling.GallonsPerHour(cc).ToString("N1", CultureInfo.CurrentCulture)),8}"
                + $"   {fluid.CombustibleFraction,4:P0}");
        }

        return rows.ToString().TrimEnd();
    }

    // ----- runners, plenum and headers -----------------------------------------

    private void OnManifoldChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;

        ShowManifold();
    }

    private void OnManifoldSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ManRunnerLength is null) return;

        // Switching to a turbo with both pressures still at atmospheric describes
        // an engine that cannot exist, and the page would immediately say so. The
        // induction choice therefore carries the pressures that go with it: a
        // pressure ratio somebody would actually build, and a manifold sitting
        // above it because a turbine is a restriction. Both stay editable.
        if (sender == ManInduction)
        {
            bool turbo = ManifoldInduction == Induction.Turbocharged;

            Set(ManBoost, Round(turbo ? TurboManifoldKpa : TuningMath.AtmosphericKpa, 1));
            Set(ManBackPressure, Round(turbo ? TurboBackPressureKpa : TuningMath.AtmosphericKpa, 1));
        }

        ShowManifold();
    }

    /// <summary>
    /// Manifold pressure a turbocharged page opens at — about 14 psi of boost.
    ///
    /// A starting point rather than a recommendation. It exists so that choosing
    /// "turbocharged" produces a believable engine instead of one at atmospheric
    /// pressure, which is what the box would otherwise still be holding.
    /// </summary>
    private const double TurboManifoldKpa = 200;

    /// <summary>
    /// Exhaust manifold pressure to match, at 1.2 times the inlet manifold.
    ///
    /// A turbine has to be driven, so the manifold in front of it always sits
    /// above the one behind the compressor. The ratio runs from about 1:1 on a
    /// generously sized turbine to past 2:1 on one that is working too hard, and
    /// 1.2 is a well matched setup. It matters here because the exhaust gas
    /// density follows it directly, and with it the primary size.
    /// </summary>
    private const double TurboBackPressureKpa = TurboManifoldKpa * 1.2;

    /// <summary>
    /// Fills the duration and gas boxes from the chosen cam.
    ///
    /// The list writes the boxes and never the other way round — picking an entry
    /// is the whole point of it — but typing in a box moves the list to "from your
    /// cam card" rather than quietly leaving a description that no longer applies.
    /// Somebody's own cam beats any of these, which is the same arrangement the
    /// volumetric efficiency list uses on the other pages.
    /// </summary>
    private void OnManifoldCamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ManIntakeDuration is null) return;

        if (CamProfiles.All.ElementAtOrDefault(ManCam.SelectedIndex) is not { IsCustom: false } cam)
        {
            ShowManifold();
            return;
        }

        Set(ManIntakeDuration, Round(cam.IntakeDurationDeg, 0));
        Set(ManExhaustDuration, Round(cam.ExhaustDurationDeg, 0));
        Set(ManIntakeTemp, Round(cam.ChargeCelsius, 0));
        Set(ManExhaustTemp, Round(cam.ExhaustCelsius, 0));

        // The cam moves how well the engine breathes, so the efficiency follows it
        // — but only while a head is chosen from the list. Somebody working from
        // their own measured figure keeps it, because a number off a dyno beats
        // anything worked out from two descriptions.
        ApplyManifoldVe(cam);

        ShowManifold();
    }

    /// <summary>
    /// Sets the volumetric efficiency from the head and the cam together.
    ///
    /// Neither answers it alone: the head says what the ports flow, the cam says
    /// how long they are held open, and the figure quoted for a head already
    /// assumes a particular cam. Leaves the box alone when either side is
    /// somebody's own.
    /// </summary>
    private void ApplyManifoldVe(CamProfile cam)
    {
        if (EngineFamilies.All.ElementAtOrDefault(ManVeFamily.SelectedIndex) is not { IsCustom: false } family)
            return;

        double ve = CamProfiles.VolumetricEfficiency(family, cam);

        if (!double.IsNaN(ve)) Set(ManVe, Round(ve, 0));
    }

    private void OnManifoldVeFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ManVe is null) return;

        ApplyManifoldVe(CamProfiles.For(Value(ManIntakeDuration), Value(ManExhaustDuration)));

        ShowManifold();
    }

    /// <summary>
    /// What the induction and goal boxes say, in the order the enums declare them.
    ///
    /// Read back by position rather than by parsing the label, so the words on
    /// screen can be changed without silently selecting a different design.
    /// </summary>
    private Induction ManifoldInduction =>
        ManInduction.SelectedIndex >= 0
            ? Enum.GetValues<Induction>()[ManInduction.SelectedIndex]
            : Induction.NaturallyAspirated;

    private ManifoldGoal ManifoldGoalChosen =>
        ManGoal.SelectedIndex >= 0
            ? Enum.GetValues<ManifoldGoal>()[ManGoal.SelectedIndex]
            : ManifoldGoal.Balanced;

    private void ShowManifold()
    {
        if (ManRunnerLength is null) return;

        Induction induction = ManifoldInduction;
        ManifoldGoal goal = ManifoldGoalChosen;

        ManifoldSpec spec = new()
        {
            Litres = Value(ManLitres),
            Cylinders = (int)Value(ManCylinders),
            Induction = induction,
            Goal = goal,
            PeakTorqueRpm = Value(ManTorqueRpm),
            PeakPowerRpm = Value(ManPowerRpm),
            IntakeDurationDeg = Value(ManIntakeDuration),
            ExhaustDurationDeg = Value(ManExhaustDuration),
            IntakeAirCelsius = Value(ManIntakeTemp),
            ExhaustCelsius = Value(ManExhaustTemp),
            VolumetricEfficiency = Value(ManVe),
            CompressionRatio = Value(ManCompression),
            ManifoldKpa = Value(ManBoost),
            ExhaustBackPressureKpa = Value(ManBackPressure),

            // Blank means "whatever the goal wants", which is why this is read as
            // a zero rather than defended against — the record treats zero as unset.
            PlenumMultiple = Value(ManPlenumMultiple) is var m && double.IsNaN(m) ? 0 : m,
        };

        ManifoldPlan plan = ManifoldTuning.Plan(spec);
        IntakePlan intake = plan.Intake;
        ExhaustPlan exhaust = plan.Exhaust;

        // ----- the engine -----
        double sweptCc = spec.Cylinders > 0 ? spec.Litres * 1_000 / spec.Cylinders : double.NaN;

        ManLitresNote.Text = double.IsNaN(sweptCc)
            ? "litres"
            : $"litres — {sweptCc:N0} cc a cylinder";

        ManInductionNote.Text = induction == Induction.Turbocharged
            ? "the turbine reflects nothing — volume decides"
            : "the collector reflects — length is a real choice";

        ManGoalNote.Text = goal switch
        {
            ManifoldGoal.QuickSpool =>
                $"tuned at the torque peak, small fast ports — {ManifoldTuning.TargetIntakeVelocity(goal):N0} m/s",
            ManifoldGoal.HighRpmRace =>
                $"tuned at the power peak, big ports — {ManifoldTuning.TargetIntakeVelocity(goal):N0} m/s",
            _ =>
                $"tuned between the two peaks — {ManifoldTuning.TargetIntakeVelocity(goal):N0} m/s",
        };

        ManTorqueNote.Text = induction == Induction.Turbocharged
            ? "rpm — where you want full boost by"
            : "rpm — where the engine is asked to pull";

        ManPowerNote.Text = $"rpm — the ports are sized to flow here";

        // The list follows the boxes, so a typed duration is never overwritten by
        // the description nearest to it — it moves the description instead.
        _updating = true;
        ManCam.SelectedIndex = CamProfiles.IndexFor(spec.IntakeDurationDeg, spec.ExhaustDurationDeg);
        _updating = false;

        CamProfile chosen = CamProfiles.For(spec.IntakeDurationDeg, spec.ExhaustDurationDeg);

        // Same arrangement for the head: the list follows the box. The selection
        // already showing is offered back, because two heads meet at the same
        // figure on a full race cam and the page must not overrule a choice on the
        // strength of a tie.
        _updating = true;
        ManVeFamily.SelectedIndex = CamProfiles.FamilyIndexFor(
            spec.VolumetricEfficiency, chosen, ManVeFamily.SelectedIndex);
        _updating = false;

        EngineFamily head = EngineFamilies.All[ManVeFamily.SelectedIndex];

        ManVeFamilyNote.Text = head.Note;

        ManVeNote.Text = head.IsCustom || chosen.IsCustom
            ? "% at peak power — your own figure"
            : "% — the head and the cam together";

        // The short line beside the list is the overlap verdict, because that is
        // the part of a cam choice felt from the driver's seat. The full
        // description is too long to sit in a column and goes underneath.
        ManCamNote.Text = chosen.IsCustom
            ? "your own figures, seat to seat"
            : CamProfiles.OverlapVerdict(chosen.OverlapDeg);

        ManCamDetail.Text = chosen.IsCustom
            ? "Working from your own cam. Both duration boxes want the seat-to-seat figure; if what you "
              + "have is duration at 0.050 in, it is roughly 45° shorter than the number this page wants."
            : $"{chosen.Name} — {chosen.IntakeDurationDeg:N0}/{chosen.ExhaustDurationDeg:N0}° seat to seat, "
              + $"{chosen.IntakeAtFiftyDeg:N0}/{chosen.ExhaustAtFiftyDeg:N0}° at 0.050 in, "
              + $"{chosen.LobeSeparationDeg:N0}° lobe separation, {chosen.OverlapDeg:N0}° of overlap. "
              + $"{char.ToUpper(chosen.Note[0], CultureInfo.CurrentCulture)}{chosen.Note[1..]}. "
              + "Compare the 0.050 in pair against a cam card to see whether your grind is near this one.";

        ManIntakeDurationNote.Text = "° crank the inlet valve is open, seat to seat";

        ManExhaustDurationNote.Text = double.IsNaN(exhaust.WaveWindowDeg)
            ? "° crank — must exceed 180"
            : $"° crank — {exhaust.WaveWindowDeg:N0}° from valve opening to overlap";

        ManIntakeTempNote.Text = double.IsNaN(intake.SpeedOfSound)
            ? "°C in the runner"
            : $"°C in the runner — sound runs at {intake.SpeedOfSound:N0} m/s";

        ManExhaustTempNote.Text = double.IsNaN(exhaust.SpeedOfSound)
            ? "°C mean along the primary, not at the valve"
            : $"°C mean along the pipe — sound at {exhaust.SpeedOfSound:N0} m/s";

        ManBoostNote.Text = double.IsNaN(spec.ManifoldKpa)
            ? "kPa absolute"
            : $"kPa absolute — {Gauge(spec.ManifoldKpa):N1} psi gauge";

        ManBackPressureNote.Text = double.IsNaN(spec.ExhaustBackPressureKpa)
            ? "kPa absolute in the manifold"
            : $"kPa absolute — {Gauge(spec.ExhaustBackPressureKpa):N1} psi gauge";

        // ----- intake -----
        ManRunnerLength.Text = Show(intake.Recommended.LengthMm, 0);

        ManRunnerLengthNote.Text = intake.Recommended.Order > 0
            ? $"mm from valve to plenum, at order {intake.Recommended.Order}, tuned for "
              + $"{intake.TunedRpm:N0} rpm{(intake.Recommended.Practical ? "" : " — does not package")}"
            : "mm from valve to plenum";

        ManRunnerDia.Text = Show(intake.RunnerDiameterMm, 1);

        ManRunnerDiaNote.Text = double.IsNaN(intake.RunnerAreaMm2)
            ? "mm bore"
            : $"mm bore — {intake.RunnerAreaMm2:N0} mm² of port";

        ManRunnerVolume.Text = Show(intake.RunnerVolumeCc, 0);

        ManRunnerVolumeNote.Text = double.IsNaN(intake.RunnerVolumeCc) || spec.Cylinders <= 0
            ? "cc each"
            : $"cc each, {intake.RunnerVolumeCc * spec.Cylinders:N0} cc in all the runners";

        ManPortVelocity.Text = Show(intake.VelocityAtPeakPower, 0);

        ManPortVelocityNote.Text = double.IsNaN(intake.VelocityAtPeakPower)
            ? "m/s mean at peak power"
            : $"m/s at peak power, {intake.VelocityAtPeakTorque:N0} at torque — "
              + ManifoldTuning.VelocityVerdict(intake.VelocityAtPeakPower);

        ManPlenum.Text = Show(intake.PlenumVolumeCc, 0);

        ManPlenumNote.Text = double.IsNaN(intake.PlenumVolumeCc)
            ? "cc"
            : $"cc at {intake.PlenumMultiple:N2}× — "
              + ManifoldTuning.PlenumVerdict(intake.PlenumMultiple, induction);

        ManTract.Text = Show(intake.TractVolumeCc, 0);

        ManTractNote.Text = induction == Induction.Turbocharged
            ? "cc to pressurise — but the intercooler and pipes are ~3× this again"
            : "cc of plenum and runners together";

        ManPlenumTable.Text = PlenumTable(spec, intake, induction);

        ManHelmholtz.Text = Show(intake.HelmholtzHz, 0);

        ManHelmholtzNote.Text = double.IsNaN(intake.HelmholtzRatio)
            ? "Hz, runner against cylinder"
            : $"Hz — ratio {intake.HelmholtzRatio:N1} to the tuned speed (3 to 4 is tuned)";

        ManOrderList.Text = OrderTable(intake, exhaust, induction);

        // ----- exhaust -----
        ManPrimaryLength.Text = Show(exhaust.Recommended.LengthMm, 0);

        ManPrimaryLengthNote.Text = induction == Induction.Turbocharged
            ? "mm if tuned — but build it short and equal instead"
            : exhaust.Recommended.Order > 0
                ? $"mm from valve to collector, at order {exhaust.Recommended.Order}, tuned for "
                  + $"{exhaust.TunedRpm:N0} rpm"
                : "mm from valve to collector";

        ManPrimaryDia.Text = Show(exhaust.PrimaryDiameterMm, 1);

        ManPrimaryDiaNote.Text = double.IsNaN(exhaust.PrimaryDiameterMm)
            ? "mm bore"
            : $"mm bore — {exhaust.PrimaryDiameterMm / 25.4:N2} in, nearest tube "
              + $"{NearestTube(exhaust.PrimaryDiameterMm)}";

        ManPrimaryVolume.Text = Show(exhaust.PrimaryVolumeCc, 0);
        ManPrimaryVolumeNote.Text = "cc in one primary";

        ManExhaustVelocity.Text = Show(exhaust.VelocityAtPeakPower, 0);

        ManExhaustVelocityNote.Text = double.IsNaN(exhaust.VelocityAtPeakPower)
            ? "m/s mean at peak power"
            : $"m/s mean at peak power — {ManifoldTuning.ExhaustVelocityVerdict(exhaust.VelocityAtPeakPower)}";

        ManCollectorDia.Text = Show(exhaust.CollectorDiameterMm, 1);

        ManCollectorDiaNote.Text = double.IsNaN(exhaust.CollectorDiameterMm)
            ? "mm"
            : $"mm — twice a primary's area, which is 1.41 times its bore";

        ManCollectorLength.Text = Show(exhaust.CollectorLengthMm, 0);

        ManTotalVolume.Text = Show(exhaust.TotalPrimaryVolumeCc, 0);

        ManTotalVolumeNote.Text = induction == Induction.Turbocharged
            ? "cc to fill before the turbine sees pressure — this is spool"
            : "cc of primaries, all cylinders together";

        ManBellNote.Text = induction == Induction.Turbocharged || double.IsNaN(exhaust.BellLengthMm)
            ? "The exhaust length is worked out from the speed of sound in the gas and the time the "
              + "valve is open — no fitted constants. On a turbocharged engine it is shown for interest: "
              + "the turbine is what the pipe actually has to feed."
            : $"Cross-check: A. Graham Bell's published formula gives {exhaust.BellLengthMm:N0} mm for this "
              + $"cam and speed, against the {exhaust.Recommended.LengthMm:N0} mm derived here from the speed "
              + "of sound and the valve timing. The two share no constant, and across the usual range of "
              + "cams and engine speeds they agree within a few per cent.";

        ShownWarning[] warnings =
        [
            .. plan.Warnings.Select(w => new ShownWarning(w.Severity, w.Text, TintFor(w.Severity))),
        ];

        ManWarnings.ItemsSource = warnings;
        ManWarningsHeading.Visibility = warnings.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Plenum volume across the range anybody builds in, so the multiple is a dial
    /// rather than a choice between three presets.
    ///
    /// The table is the answer to "what would a different multiple give me": every
    /// quarter step from half displacement to twice it, with the volume it comes
    /// to and what it does. The row in use is marked, and the runners are added on
    /// so the figure that matters for spool — everything the compressor has to
    /// pressurise that this page can see — moves with it.
    /// </summary>
    internal static string PlenumTable(ManifoldSpec spec, IntakePlan intake, Induction induction)
    {
        var rows = new System.Text.StringBuilder();

        double runners = intake.RunnerVolumeCc * Math.Max(spec.Cylinders, 0);

        // The two widths here are the cell widths below, and the cells carry
        // their own "cc" so that both are one field. Counted separately, the
        // headings drift left of the numbers by the width of a unit: 9 and 11
        // against rows of 11 and 13, which put "volume" and "+ runners" two and
        // three characters off the columns they name.
        rows.AppendLine($"{"plenum",-9}{"volume",11}{"+ runners",13}   what it does");
        rows.AppendLine(new string('-', 74));

        // The figure actually in force earns its own row when it is not one of the
        // steps — the goals use 0.75, 1.20 and 1.40, and marking the nearest step
        // instead would put the asterisk against a volume nobody is building.
        List<double> steps = [.. ManifoldTuning.PlenumMultiples];

        if (intake.PlenumMultiple > 0 && steps.TrueForAll(m => Math.Abs(m - intake.PlenumMultiple) > 0.005))
            steps.Add(intake.PlenumMultiple);

        steps.Sort();

        foreach (double multiple in steps)
        {
            double volume = ManifoldTuning.PlenumVolumeCc(spec.Litres * 1_000, multiple);

            if (double.IsNaN(volume)) continue;

            bool inUse = Math.Abs(multiple - intake.PlenumMultiple) < 0.005;

            string volumeCell = $"{volume:N0} cc";
            string totalCell = $"{volume + runners:N0} cc";

            rows.AppendLine(
                $"{multiple,5:N2}×{(inUse ? " *" : "  "),-3}"
                + $"{volumeCell,11}{totalCell,13}"
                + $"   {ManifoldTuning.PlenumVerdict(multiple, induction)}");
        }

        rows.Append("\n* in use");

        if (induction == Induction.Turbocharged)
            rows.Append("   — and the intercooler and its pipework are around three times the whole column again");

        return rows.ToString();
    }

    /// <summary>
    /// An absolute pressure as a gauge reading in psi, without the minus sign that
    /// floating point puts on a pressure sitting exactly at the barometer.
    ///
    /// Adding a positive zero is what clears it: IEEE arithmetic keeps the sign
    /// through the rounding, so 101.325 kPa absolute formats as "-0.0 psi" and
    /// looks like a fault in the calculator rather than in the printing.
    /// </summary>
    private static double Gauge(double absoluteKpa) =>
        Math.Round(TuningMath.GaugeFromAbsolute(absoluteKpa) / TuningMath.KpaPerPsi, 1) + 0.0;

    /// <summary>
    /// The nearest tube anybody actually sells, in inches.
    ///
    /// A primary calculated to 43.2 mm is bought as 1.75 in, and saying so saves
    /// the person doing it from converting and then guessing. Steps of an eighth,
    /// because that is how header tube is stocked.
    /// </summary>
    private static string NearestTube(double diameterMm)
    {
        if (double.IsNaN(diameterMm) || diameterMm <= 0) return "—";

        double inches = diameterMm / 25.4;
        double eighths = Math.Round(inches * 8);

        return $"{eighths / 8:N3} in".Replace(".000", "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every harmonic side by side, so the choice between them is visible.
    ///
    /// The recommendation is one row of this table and not a separate answer:
    /// somebody whose bonnet will not close on the suggested runner needs to see
    /// what the next order costs, and somebody building a race engine may want the
    /// shorter one on purpose. Marking the practical ones is the whole point —
    /// the arithmetic will happily return a 1.4 metre runner.
    /// </summary>
    internal static string OrderTable(IntakePlan intake, ExhaustPlan exhaust, Induction induction)
    {
        var rows = new System.Text.StringBuilder();

        rows.AppendLine($"{"",-8}{"intake runner",20}{"exhaust primary",20}");
        rows.AppendLine($"{"order",-8}{"mm",20}{"mm",20}");
        rows.AppendLine(new string('-', 48));

        for (int i = 0; i < Math.Max(intake.Orders.Count, exhaust.Orders.Count); i++)
        {
            TuningOrder? inlet = i < intake.Orders.Count ? intake.Orders[i] : null;
            TuningOrder? primary = i < exhaust.Orders.Count ? exhaust.Orders[i] : null;

            int order = inlet?.Order ?? primary?.Order ?? i + 1;

            rows.AppendLine(
                $"{order,-8}{Cell(inlet, intake.Recommended),20}"
                + $"{Cell(primary, induction == Induction.Turbocharged ? null : exhaust.Recommended),20}");
        }

        rows.AppendLine();
        rows.Append("* the one to build   · packages   (no mark) too long or too short");

        return rows.ToString();

        static string Cell(TuningOrder? order, TuningOrder? recommended)
        {
            if (order is not { } o || double.IsNaN(o.LengthMm)) return "—";

            string mark = recommended is { } r && r.Order == o.Order
                ? "*"
                : o.Practical ? "·" : " ";

            return $"{o.LengthMm,7:N0} {mark}";
        }
    }
}
