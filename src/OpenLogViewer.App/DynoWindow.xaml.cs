using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// The dyno: what the recording says the engine made, and what that rests on.
///
/// <para>
/// The window asks for as little as it can. Everything the recording knows it
/// reads — the channels for what the engine did, the tune it carries for how the
/// controller was set up — and the fields on the left are only what nothing in a
/// log can say: what the car weighs, what it is geared with, what nobody has
/// measured.
/// </para>
/// <para>
/// Every figure it ends up using says where it came from, and that is the point
/// rather than a decoration. A number resting on seventeen measurements and two
/// guesses is worth quoting; the same number resting on two measurements and
/// seventeen guesses is not, and from the outside they are identical.
/// </para>
/// </summary>
public partial class DynoWindow : Window
{
    /// <summary>
    /// One pull in the list. The name is on the record rather than bound out of
    /// it because these combo boxes wear a replaced template, and a replaced
    /// template draws the closed box through <c>ItemTemplate</c> — which
    /// <c>DisplayMemberPath</c> does not set, so the box falls back on the type's
    /// own name and reads as machinery.
    /// </summary>
    private sealed record PullChoice(int Number, DynoPull Pull)
    {
        public override string ToString() => $"{Number}.  {Pull}";
    }

    /// <summary>A gear to draw in, or nought for working it out.</summary>
    private sealed record GearChoice(int Gear, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly MainViewModel _vm;
    private readonly VehicleStore _store;

    private PullSearchResult _found = new([], "");
    private DynoInputs? _setup;

    /// <summary>What the tune offered, to tell a correction from an agreement.</summary>
    private CarEntry _seeded = new();

    /// <summary>Set while the fields are being filled in, so that does not count as typing.</summary>
    private bool _filling;

    public DynoWindow(MainViewModel viewModel, VehicleStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _vm = viewModel;
        _store = store ?? new VehicleStore();

        InitializeComponent();

        Sheet.CursorMoved += OnCursor;

        Seed();
        Refresh();
        Show();
    }

    /// <summary>
    /// Fills the fields with whatever the tune already settled, falling back to a
    /// default only where it settled nothing.
    ///
    /// <para>
    /// A box reading 4.10 sitting above a table saying the tune knows 3.70 is
    /// worse than either figure on its own: it reads as a disagreement somebody
    /// has to arbitrate, when in fact nobody typed the 4.10 — it is what the
    /// class holds when no one has said. Seeding makes what is shown what is used.
    /// </para>
    /// </summary>
    private void Seed()
    {
        _seeded = FromTune();
        _filling = true;

        try
        {
            Fill(_store.Over(_seeded));
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>What the tune says, before anyone's corrections go over it.</summary>
    private CarEntry FromTune()
    {
        var car = new VehicleSpec();
        var engine = new EngineSpec { Bsfc = TuningMath.BoostedBsfc, VolumetricEfficiency = 85 };

        if (_vm.Document is { } log)
        {
            car = DynoSetup.FromTune(log, car);
            engine = DynoSetup.FromTune(log, engine);
        }

        return new CarEntry
        {
            Mass = car.MassKg.ToString("N0", CultureInfo.CurrentCulture),
            FinalDrive = car.FinalDrive.ToString("N2", CultureInfo.CurrentCulture),
            GearRatios = string.Join(
                ", ", car.GearRatios.Select(r => r.ToString("0.00", CultureInfo.CurrentCulture))),

            // The measured diameter where the tune carried one, because that is
            // the figure the arithmetic will use — showing the sidewall it
            // overrules would put a number on screen that nothing reads.
            Tyre = car.OverallDiameterMm is { } mm
                ? mm.ToString("N0", CultureInfo.CurrentCulture) + " mm"
                : car.Tyre.ToString(),

            DrivetrainLoss = (car.DrivetrainLossPercent > 0 ? car.DrivetrainLossPercent : 15)
                .ToString("N0", CultureInfo.CurrentCulture),

            Litres = engine.Litres.ToString("N2", CultureInfo.CurrentCulture),
            InjectorCcPerMinute = engine.InjectorCcPerMinute.ToString("N0", CultureInfo.CurrentCulture),
            Bsfc = engine.Bsfc.ToString("N2", CultureInfo.CurrentCulture),
            VolumetricEfficiency =
                engine.VolumetricEfficiency.ToString("N0", CultureInfo.CurrentCulture),
        };
    }

    private void Fill(CarEntry car)
    {
        Mass.Text = car.Mass ?? "";
        FinalDrive.Text = car.FinalDrive ?? "";
        Ratios.Text = car.GearRatios ?? "";
        TyreText.Text = car.Tyre ?? "";
        Loss.Text = car.DrivetrainLoss ?? "";
        Litres.Text = car.Litres ?? "";
        InjectorCc.Text = car.InjectorCcPerMinute ?? "";
        Bsfc.Text = car.Bsfc ?? "";
        Ve.Text = car.VolumetricEfficiency ?? "";
    }

    /// <summary>What is on screen now.</summary>
    private CarEntry Typed() => new()
    {
        Mass = Mass.Text,
        FinalDrive = FinalDrive.Text,
        GearRatios = Ratios.Text,
        Tyre = TyreText.Text,
        DrivetrainLoss = Loss.Text,
        Litres = Litres.Text,
        InjectorCcPerMinute = InjectorCc.Text,
        Bsfc = Bsfc.Text,
        VolumetricEfficiency = Ve.Text,
    };

    // ----- what the fields describe -------------------------------------------------

    private static double Value(TextBox box, double fallback) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
            ? v
            : fallback;

    private VehicleSpec Car()
    {
        var defaults = new VehicleSpec();

        double[] ratios =
        [
            .. Ratios.Text
                .Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out double r) ? r : 0)
                .Where(r => r > 0),
        ];

        // Either a tyre off the sidewall or a rolling diameter somebody measured.
        // The measured one is worth more than the printed one — a worn 245/40 is
        // a good centimetre under what its markings claim — so the field takes
        // both, and a plain number wins where there is one.
        Tyre? tyre = Gearing.TryParseTyre(TyreText.Text, out Tyre parsed) ? parsed : null;
        double? diameter = null;

        if (tyre is null)
        {
            string bare = TyreText.Text.Replace("mm", "", StringComparison.OrdinalIgnoreCase).Trim();

            if (double.TryParse(bare, NumberStyles.Float, CultureInfo.CurrentCulture, out double d)
                && d is >= 300 and <= 1400)
            {
                diameter = d;
            }
        }

        return new VehicleSpec
        {
            KerbMassKg = Value(Mass, defaults.MassKg),
            OccupantMassKg = 0,
            FinalDrive = Value(FinalDrive, defaults.FinalDrive),
            GearRatios = ratios.Length > 0 ? ratios : defaults.GearRatios,
            Tyre = tyre ?? defaults.Tyre,
            OverallDiameterMm = diameter,
            DrivetrainLossPercent = Value(Loss, 15),
            Boosted = true,
        };
    }

    private EngineSpec Engine()
    {
        var defaults = new EngineSpec();

        return new EngineSpec
        {
            Litres = Value(Litres, defaults.Litres),
            Fuel = Fuel.Petrol,
            InjectorCcPerMinute = Value(InjectorCc, defaults.InjectorCcPerMinute),
            Bsfc = Value(Bsfc, TuningMath.BoostedBsfc),
            VolumetricEfficiency = Value(Ve, 85),
            FuelPressureIsDifferential = true,
        };
    }

    private Ambient Air()
    {
        // The day, from the log where it recorded one. A barometer is the whole
        // difference between sea level and four and a half thousand feet, which
        // is a fifth of the air.
        LogChannel? baro = _vm.Document is { } d
            ? ChannelRoles.Find(d, ChannelRole.Barometric)
            : null;

        double kpa = baro is not null && baro.At(0) > 50
            ? baro.At(0) * ChannelUnits.PressureToKilopascals(baro)
            : TuningMath.AtmosphericKpa;

        return new Ambient(kpa, 20);
    }

    // ----- gathering ------------------------------------------------------------------

    private void Refresh()
    {
        if (Knowledge is null) return;

        if (_vm.Document is not { } log)
        {
            Summary.Text = "Open a log first.";
            Sheet.Show([]);

            return;
        }

        _setup = DynoSetup.Read(log, Car(), Engine());

        Knowledge.ItemsSource = _setup.Inputs.Select(Row).ToList();

        // The pulls are found against the car the tune settled on, not the one
        // typed in, since the tune may know the tyre and the weight better.
        _found = DynoRun.Find(log, _setup.Vehicle);

        // Numbered by when they happened, so a run can be found again in the log,
        // but offered widest-first: eight pulls with the shortest at the top means
        // the sheet that draws itself on opening is made from the one worth the
        // least, and the first thing anybody sees is a refusal.
        Pulls.ItemsSource = _found.Pulls
            .Select((p, i) => new PullChoice(i + 1, p))
            .OrderByDescending(c => c.Pull.RpmSpan)
            .ThenByDescending(c => c.Pull.Seconds)
            .ToList();

        Pulls.SelectedIndex = _found.Pulls.Count > 0 ? 0 : -1;

        GearBox.ItemsSource = new[] { new GearChoice(0, "work it out") }
            .Concat(_setup.Vehicle.GearRatios
                .Select((r, i) => new GearChoice(i + 1, $"{i + 1}  ({r:N2})")))
            .ToList();

        if (GearBox.SelectedIndex < 0) GearBox.SelectedIndex = 0;

        Summary.Text = _found.Pulls.Count > 0
            ? $"{_found.Summary}  {_setup.Summary}"
            : _found.Summary;

        Draw();
    }

    /// <summary>One row of what is known, tinted by how well it is known.</summary>
    private object Row(DynoInput input)
    {
        Theme theme = ThemeManager.Current;

        Color tint = input.Source switch
        {
            InputSource.Log or InputSource.Tune => ColorMath.Blend(theme.Panel, theme.Accent, 0.25),
            InputSource.Missing => ColorMath.Blend(theme.Panel, theme.Marker, 0.30),
            InputSource.Assumed => ColorMath.Blend(theme.Panel, theme.Marker, 0.18),
            _ => theme.Card,
        };

        return new
        {
            input.Name,
            input.Value,
            input.Note,
            Source = input.Source.ToString().ToLowerInvariant(),
            Tint = new SolidColorBrush(tint),
        };
    }

    // ----- drawing -----------------------------------------------------------------------

    private void Draw()
    {
        if (Sheet is null || _setup is null) return;

        if (_vm.Document is not { } log || Pulls.SelectedItem is null)
        {
            Sheet.Show([]);
            Readout.Text = "";
            Basis.Text = "";
            Cautions.ItemsSource = null;

            return;
        }

        DynoPull pull = ((PullChoice)Pulls.SelectedItem).Pull;

        VehicleSpec car = _setup.Vehicle;
        EngineSpec engine = _setup.Engine;
        Ambient air = Air();

        int chosen = GearBox.SelectedItem is GearChoice g ? g.Gear : 0;

        string how;

        if (chosen > 0)
        {
            pull = pull with
            {
                Gearing = pull.Gearing with { Gear = chosen },
                Speed = SpeedSource.EngineSpeedAndGear,
            };

            how = $"gear {chosen}, as told";
        }
        else if (pull.Gearing.Recognised)
        {
            how = $"gear {pull.Gearing.Gear}, measured from the road speed";
        }
        else
        {
            GearVerdict verdict = GearAgreement.Resolve(log, car, engine, pull, air);

            pull = GearAgreement.Apply(pull, verdict);
            how = verdict.Resolved
                ? $"gear {verdict.Gear}, worked out — {verdict.Summary}"
                : verdict.Summary;
        }

        var traces = new List<DynoTrace>();

        DynoCurve road = DynoCurve.FromRoadLoad(log, car, pull, air);

        if (!road.IsEmpty) traces.Add(new DynoTrace(road, "road load (wheels)", 0));

        DynoCurve fromAir = DynoCurve.FromSpeedDensity(log, engine, pull, air)
            .At(PowerReference.Wheels, car);

        if (!fromAir.IsEmpty) traces.Add(new DynoTrace(fromAir, "air (at the wheels)", 2));

        DynoCurve fromFuel = DynoCurve.FromInjectors(log, engine, pull, air)
            .At(PowerReference.Wheels, car);

        if (!fromFuel.IsEmpty) traces.Add(new DynoTrace(fromFuel, "injectors (at the wheels)", 1));

        Sheet.Show(traces);

        Readout.Text = road.IsEmpty
            ? how
            : $"{road.PeakPower.Horsepower:N0} whp @ {road.PeakPower.Rpm:N0}   ·   "
              + $"{road.PeakTorque.PoundFeet:N0} lb-ft @ {road.PeakTorque.Rpm:N0}   ·   "
              + $"{road.At(PowerReference.Crank, car).PeakPower.Horsepower:N0} crank   ·   "
              + $"crossover {road.CrossoverRpm:N0}   ·   {how}";

        Basis.Text = road.IsEmpty ? road.Basis : $"Road load: {road.Basis}";

        Cautions.ItemsSource = traces
            .SelectMany(t => t.Curve.Cautions.Select(c => $"!  {t.Label}: {c}"))
            .Distinct()
            .ToList();
    }

    private void OnCursor(double rpm)
    {
        if (!double.IsFinite(rpm) || _setup is null) return;

        // Left deliberately alone: the readout under the chart is the pull's
        // summary, and replacing it as the pointer moves would take away the
        // figure somebody is trying to write down.
    }

    // ----- events ---------------------------------------------------------------------

    private void OnChanged(object sender, TextChangedEventArgs e)
    {
        // Not while the fields are being filled in: that would write the tune's
        // own figures back as though somebody had typed them, and they would then
        // outlive the log they came from.
        if (_filling) return;

        try
        {
            _store.Save(Typed(), _seeded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A car that cannot be written down is still a car that can be
            // driven for the length of this sitting.
        }

        Refresh();
    }

    private void OnForgetClick(object sender, RoutedEventArgs e)
    {
        _store.Clear();

        Seed();
        Refresh();
    }

    private void OnPullChanged(object sender, SelectionChangedEventArgs e) => Draw();

    private void OnGearChanged(object sender, SelectionChangedEventArgs e) => Draw();

    private void OnFieldFocused(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    private void OnFieldClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.IsKeyboardFocusWithin) return;

        box.Focus();
        e.Handled = true;
    }
}
