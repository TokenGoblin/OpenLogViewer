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

    public GaugeSpec Spec { get; } = spec;

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
        get => _value;
        set => Set(ref _value, value);
    }

    private double _peak = double.NaN;
    private double _trough = double.NaN;

    /// <summary>Highest reading since the peaks were last cleared.</summary>
    public double Peak
    {
        get => _peak;
        private set => Set(ref _peak, value);
    }

    /// <summary>Lowest reading since the peaks were last cleared.</summary>
    public double Trough
    {
        get => _trough;
        private set => Set(ref _trough, value);
    }

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

        if (double.IsNaN(_peak) || reading > _peak) Peak = reading;
        if (double.IsNaN(_trough) || reading < _trough) Trough = reading;
    }

    /// <summary>Forgets the extremes, leaving the current reading alone.</summary>
    public void ResetPeaks()
    {
        Peak = double.NaN;
        Trough = double.NaN;
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
