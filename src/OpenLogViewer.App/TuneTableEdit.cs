namespace OpenLogViewer.App;

/// <summary>What the keyboard asked to be done to the selected cells.</summary>
public enum TuneEditKind
{
    /// <summary>Add a fixed amount — the nudge.</summary>
    Add,

    /// <summary>Multiply by a percentage — how tuning is actually done.</summary>
    Scale,

    /// <summary>Put the selection back to what the ECU said.</summary>
    Revert,

    /// <summary>Set every selected cell to one value.</summary>
    Set,

    /// <summary>Fill the inside of the selection from its edges.</summary>
    Interpolate,
}

/// <summary>
/// One change, from the view to whoever owns the table.
///
/// Sent as a request rather than applied where it is raised: the view knows
/// which keys were pressed and nothing else, and the decision about what a
/// table may become belongs with the table.
/// </summary>
public readonly record struct TuneTableEdit(TuneEditKind Kind, double Amount)
{
    public static TuneTableEdit Add(double amount) => new(TuneEditKind.Add, amount);

    public static TuneTableEdit Scale(double percent) => new(TuneEditKind.Scale, percent);

    public static TuneTableEdit RevertSelection() => new(TuneEditKind.Revert, 0);

    public static TuneTableEdit Set(double value) => new(TuneEditKind.Set, value);

    public static TuneTableEdit Interpolate() => new(TuneEditKind.Interpolate, 0);
}
