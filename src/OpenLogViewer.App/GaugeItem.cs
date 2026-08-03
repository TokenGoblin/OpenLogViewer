using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// One gauge on screen: what the firmware says it is, which column of the
/// session feeds it, and its latest reading.
///
/// The column has to be looked up rather than assumed. A gauge names the
/// firmware's internal channel — <c>RPMValue</c>, <c>coolant</c> — while a
/// session records under the names the datalog gives them, "RPM" and "CLT",
/// because those are the names a saved preset or filter was written against.
/// </summary>
public sealed class GaugeItem(GaugeSpec spec, string? column) : ObservableObject
{
    private double _value = double.NaN;
    private bool _shown;

    /// <summary>
    /// The gauge as the firmware describes it, in the units the ECU reports.
    ///
    /// Kept alongside the displayed one because readings are stored as they
    /// arrive and converted only on the way out. Converting on the way in and
    /// keeping the result would mean a reading converted again every time the
    /// preference changed, and a peak recorded in one system would silently
    /// become a different temperature in the other.
    /// </summary>
    private GaugeSpec _source = spec;

    private UnitSystem _system = UnitSystem.AsReported;

    public GaugeSpec Spec { get; private set; } = spec;

    /// <summary>
    /// Shows this gauge in another system of units.
    ///
    /// The face and the readings move together — they have to, since a dial
    /// drawn in Fahrenheit with a needle placed in Celsius is worse than either
    /// on its own.
    /// </summary>
    public void Show(UnitSystem system)
    {
        if (system == _system) return;

        _system = system;
        Spec = _source.In(system);

        Raise(nameof(Spec));
        Raise(nameof(Title));
        Raise(nameof(Value));
        Raise(nameof(Peak));
        Raise(nameof(Trough));
    }

    /// <summary>A reading as reported, in the units it is to be shown in.</summary>
    private double Shown(double value) => UnitConvert.Value(value, _source.Units, _system);

    /// <summary>
    /// Replaces the description of this gauge, keeping its readings and its
    /// peaks. For a scale that is only knowable once the ECU has said something
    /// — a rev counter that should run to the limiter rather than to the top of
    /// what the datatype could hold.
    /// </summary>
    public void Retarget(GaugeSpec spec)
    {
        _source = spec;
        Spec = spec.In(_system);

        Raise(nameof(Spec));
        Raise(nameof(Title));
    }

    /// <summary>The session column feeding this gauge, or null when nothing does.</summary>
    public string? Column { get; } = column;

    /// <summary>False when this firmware publishes the gauge but not its channel.</summary>
    public bool IsConnected => Column is not null;

    public string Title => Spec.Title;

    public string Category => Spec.Category.Length > 0 ? Spec.Category : "Other";

    /// <summary>The search text: title, channel and category all match.</summary>
    public string SearchText => $"{Spec.Title} {Spec.Channel} {Spec.Category}";

    public double Value
    {
        get => Shown(_value);
        set { if (Set(ref _value, value)) Raise(nameof(Value)); }
    }

    private double _peak = double.NaN;
    private double _trough = double.NaN;

    /// <summary>Highest reading since the peaks were last cleared.</summary>
    public double Peak => Shown(_peak);

    /// <summary>Lowest reading since the peaks were last cleared.</summary>
    public double Trough => Shown(_trough);

    /// <summary>
    /// Takes a reading and remembers the extremes.
    ///
    /// Both ends, because which one matters depends on the channel: a coolant
    /// gauge is about the high, a battery or an oil pressure gauge about the low,
    /// and a mixture reading about whichever side of the target it strayed to.
    /// A glance at a dial cannot catch a spike that lasted a tenth of a second,
    /// and at 25 samples a second most of them are gone before they are seen.
    /// </summary>
    public void Record(double reading)
    {
        Value = reading;

        if (double.IsNaN(reading)) return;

        if (double.IsNaN(_peak) || reading > _peak) { _peak = reading; Raise(nameof(Peak)); }
        if (double.IsNaN(_trough) || reading < _trough) { _trough = reading; Raise(nameof(Trough)); }
    }

    /// <summary>Forgets the extremes, leaving the current reading alone.</summary>
    public void ResetPeaks()
    {
        _peak = double.NaN;
        _trough = double.NaN;

        Raise(nameof(Peak));
        Raise(nameof(Trough));
    }

    /// <summary>Whether this one is on the dashboard.</summary>
    public bool IsShown
    {
        get => _shown;
        set { if (Set(ref _shown, value)) ShownChanged?.Invoke(); }
    }

    /// <summary>Raised when a gauge is added to or removed from the dashboard.</summary>
    public event Action? ShownChanged;

    /// <summary>Sets the reading without going through the dashboard notification.</summary>
    public void Show(bool shown) => _shown = shown;
}
