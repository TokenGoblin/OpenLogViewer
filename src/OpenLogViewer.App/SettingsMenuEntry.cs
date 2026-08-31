namespace OpenLogViewer.App;

/// <summary>
/// One line of the settings list: a page to open, or the name of the group the
/// pages beneath it belong to.
/// </summary>
/// <param name="Dialog">The dialog to open, or empty for a heading.</param>
/// <param name="Title">What the list says.</param>
/// <param name="Condition">
/// When the firmware offers this page at all. Kept rather than evaluated once,
/// since it is written against settings the user may change.
/// </param>
public sealed record SettingsMenuEntry(string Dialog, string Title, string Condition = "")
{
    /// <summary>A group name rather than something to open.</summary>
    public static SettingsMenuEntry Heading(string title) => new("", title);

    /// <summary>
    /// True when this opens a curve rather than a page of fields.
    ///
    /// A firmware's menu makes no distinction — an entry names something and
    /// the something is a dialog, a table or a curve — so the difference is
    /// worked out when the menu is built and carried here, rather than looked
    /// up again every time a line is clicked.
    /// </summary>
    public bool IsCurve { get; init; }

    /// <summary>
    /// True when this opens one of the firmware's tables.
    ///
    /// The third thing a menu entry can name, and the last one that opened
    /// nothing: 51 of a MicroSquirt's entries, 53 of an MS3's. A firmware
    /// declares a table under two names — the grid and its three-dimensional
    /// view — and a menu may point at either, so both have to lead back to the
    /// same table.
    /// </summary>
    public bool IsTable { get; init; }

    public bool IsHeading => Dialog.Length == 0;

    public bool HasCondition => Condition.Length > 0;

    public override string ToString() => Title;
}
