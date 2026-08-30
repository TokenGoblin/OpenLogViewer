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

    public bool IsHeading => Dialog.Length == 0;

    public bool HasCondition => Condition.Length > 0;

    public override string ToString() => Title;
}
