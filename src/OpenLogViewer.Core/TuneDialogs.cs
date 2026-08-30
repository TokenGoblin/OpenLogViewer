using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>What a line inside a dialog puts on screen.</summary>
public enum DialogItemKind
{
    /// <summary>A setting: a label and the constant behind it.</summary>
    Field,

    /// <summary>A reading the firmware will not let you change.</summary>
    ReadOnlyField,

    /// <summary>A label or a spacer — a field with no constant behind it.</summary>
    Label,

    /// <summary>Another dialog, embedded in this one.</summary>
    Panel,

    /// <summary>A button that sends a command to the controller.</summary>
    Command,

    /// <summary>A live gauge.</summary>
    Gauge,

    /// <summary>Static prose.</summary>
    Text,

    /// <summary>A setting shown as a slider rather than a box.</summary>
    Slider,

    /// <summary>Something this does not draw yet — a live graph, a lamp, a curve.</summary>
    Unsupported,
}

/// <summary>
/// One line of a dialog.
/// </summary>
/// <param name="Kind">What to draw.</param>
/// <param name="Label">The caption, which is empty for a spacer.</param>
/// <param name="Target">
/// What it refers to: a constant for a field or slider, another dialog for a
/// panel, a command for a button, a gauge for a gauge. Empty for a label.
/// </param>
/// <param name="Condition">
/// The <c>{…}</c> expression deciding whether it is shown, or empty for always.
/// Held as written rather than parsed here, because whether it is true depends on
/// values this does not have.
/// </param>
/// <param name="Position">Where a panel goes — North, South, Center and so on.</param>
public sealed record DialogItem(
    DialogItemKind Kind,
    string Label,
    string Target = "",
    string Condition = "",
    string Position = "")
{
    public bool HasCondition => Condition.Length > 0;

    /// <summary>True for the two kinds that edit a constant.</summary>
    public bool IsEditable => Kind is DialogItemKind.Field or DialogItemKind.Slider;

    /// <summary>
    /// The constant this refers to, without any subscript.
    ///
    /// A field may address one element of an array — <c>psEnabled[0]</c> is the
    /// first of four programmable outputs, each with its own row of settings —
    /// and the constant to look up is the array, not the element.
    /// </summary>
    public string TargetConstant
    {
        get
        {
            int bracket = Target.IndexOf('[', StringComparison.Ordinal);
            return bracket < 0 ? Target : Target[..bracket];
        }
    }

    /// <summary>Which element of the array, or −1 when it addresses the whole of it.</summary>
    public int TargetIndex
    {
        get
        {
            int open = Target.IndexOf('[', StringComparison.Ordinal);
            int close = Target.LastIndexOf(']');

            return open >= 0 && close > open
                   && int.TryParse(Target[(open + 1)..close], out int index)
                ? index
                : -1;
        }
    }
}

/// <summary>
/// A dialog: a titled group of settings, which may embed others.
/// </summary>
/// <param name="Name">The identifier a menu or a panel refers to it by.</param>
/// <param name="Title">What to put at the top, which is often empty for a dialog
/// that exists only to be embedded.</param>
/// <param name="Layout">
/// <c>xAxis</c> to lay its items out across, <c>yAxis</c> down. Declared per
/// dialog, and the reason a settings page can be two columns wide.
/// </param>
public sealed record TuneDialog(
    string Name, string Title, string Layout, IReadOnlyList<DialogItem> Items)
{
    /// <summary>Documentation the firmware points at, where it does.</summary>
    public string Help { get; init; } = "";

    public bool LaysOutAcross => Layout.Equals("xAxis", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An entry under a menu: a dialog to open, or a rule to draw.</summary>
/// <param name="Dialog">The dialog's name, or a <c>std_</c> special.</param>
/// <param name="Title">What the menu says.</param>
/// <param name="Condition">When it is offered at all.</param>
public sealed record MenuEntry(string Dialog, string Title, string Condition = "")
{
    /// <summary>
    /// True for the horizontal rule between groups, which is a menu entry in the
    /// file but not a thing to open.
    /// </summary>
    public bool IsSeparator =>
        Dialog.Equals("std_separator", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the editors TunerStudio supplies itself rather than the firmware
    /// describing — the generic constants editor, the thermistor generator and
    /// so on. Recognised so they can be left out rather than offered as a dialog
    /// that will not be found.
    /// </summary>
    public bool IsBuiltIn =>
        Dialog.StartsWith("std_", StringComparison.OrdinalIgnoreCase);

    public bool HasCondition => Condition.Length > 0;
}

/// <summary>One top-level menu, with the entries under it.</summary>
public sealed record TuneMenu(string Title, IReadOnlyList<MenuEntry> Entries);

/// <summary>
/// The settings interface, as the firmware describes it.
///
/// <para>
/// This is the half of an INI that <see cref="TuneLayout"/> does not read.
/// <c>[Constants]</c> says where every setting lives in the ECU's memory and how
/// to decode it; this says what to call it, which page to put it on, and when it
/// is relevant at all. Neither is any use alone: the constants without this are
/// eight hundred numbers in no order, and this without them is a menu of labels
/// with nothing behind them.
/// </para>
/// <para>
/// It is a large thing to read — an MS3 definition declares 633 dialogs and
/// 2,232 fields — but it is read rather than written, which is the point.
/// Hard-coding a settings screen would mean one screen per firmware and a new
/// one every release; every controller here describes its own.
/// </para>
/// <para>
/// Conditions are kept as text. Whether "show this when knock detection is on"
/// is true depends on the tune currently in hand, which the file does not have,
/// so evaluating them is <see cref="DialogCondition"/>'s job and happens against
/// a tune rather than against a definition.
/// </para>
/// </summary>
public sealed record TuneInterface
{
    public required IReadOnlyList<TuneMenu> Menus { get; init; }

    public required IReadOnlyDictionary<string, TuneDialog> Dialogs { get; init; }

    /// <summary>True when the file described no settings interface at all.</summary>
    public bool IsEmpty => Menus.Count == 0 && Dialogs.Count == 0;

    public TuneDialog? Find(string name) =>
        name is not null && Dialogs.TryGetValue(name, out TuneDialog? dialog) ? dialog : null;
}

/// <summary>Reads <c>[Menu]</c> and <c>[UserDefined]</c>.</summary>
public static partial class TuneInterfaceReader
{
    /// <summary>
    /// A directive and its arguments: <c>field = "Cranking RPM", crankingRPM</c>.
    /// Leading whitespace varies wildly between firmwares — rusEFI indents with
    /// tabs, MegaSquirt with spaces — so it is simply discarded.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z][A-Za-z0-9_]*)\s*=\s*(?<rest>.*)$")]
    private static partial Regex Directive { get; }

    /// <summary>A section header, which ends whatever was being read.</summary>
    [GeneratedRegex(@"^\s*\[(?<name>[^\]]+)\]")]
    private static partial Regex Section { get; }

    /// <param name="symbols">
    /// The firmware's compile-time symbols, deciding which side of each
    /// <c>#if</c> is real. Defaults to the same set every other reader here
    /// uses.
    /// </param>
    public static TuneInterface Read(string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        symbols ??= MsqIni.DefaultSymbols;

        var menus = new List<TuneMenu>();
        var dialogs = new Dictionary<string, TuneDialog>(StringComparer.OrdinalIgnoreCase);

        // The dialog or menu currently being filled, since both are declared by a
        // line and then extended by the lines beneath it.
        string menuTitle = "";
        var menuEntries = new List<MenuEntry>();

        string dialogName = "", dialogTitle = "", dialogLayout = "", dialogHelp = "";
        var items = new List<DialogItem>();

        void CloseMenu()
        {
            if (menuTitle.Length > 0 || menuEntries.Count > 0)
                menus.Add(new TuneMenu(menuTitle, [.. menuEntries]));

            menuTitle = "";
            menuEntries = [];
        }

        void CloseDialog()
        {
            if (dialogName.Length > 0)
            {
                // Last declaration wins. A definition may redeclare a dialog to
                // replace it for a firmware variant, and the file is read top to
                // bottom the way the tool that wrote it expects.
                dialogs[dialogName] = new TuneDialog(
                    dialogName, dialogTitle, dialogLayout, [.. items]) { Help = dialogHelp };
            }

            dialogName = dialogTitle = dialogLayout = dialogHelp = "";
            items = [];
        }

        // Both sections, through the shared reader so that #if is resolved
        // against this firmware's symbols the way every other reader here does
        // it. Splitting the raw text merges both branches of every conditional —
        // and since a redeclared dialog replaces the earlier one, a dialog
        // written differently under #if and #else would always come out as the
        // #else version whatever the tune actually is. MS3 has 102 conditionals
        // inside its menus and dialogs; Speeduino has 86.
        IEnumerable<string> lines =
            MsqIni.Section(iniText, "Menu", symbols)
                .Concat(["[end of menu]"])
                .Concat(MsqIni.Section(iniText, "UserDefined", symbols));

        foreach (string raw in lines)
        {
            string line = Strip(raw);
            if (line.Length == 0) continue;

            // The marker between the two, since the reader yields a section's
            // own lines and never its header.
            if (Section.IsMatch(line))
            {
                CloseMenu();
                CloseDialog();
                continue;
            }

            Match match = Directive.Match(line);
            if (!match.Success) continue;

            string key = match.Groups["key"].Value;
            string rest = match.Groups["rest"].Value.Trim();

            switch (key.ToLowerInvariant())
            {
                // ----- menus -----------------------------------------------

                case "menudialog":
                    // Names the menu bar being described. Several may appear;
                    // the menus under each are collected the same way.
                    CloseMenu();
                    break;

                case "menu":
                    CloseMenu();
                    menuTitle = Caption(Unquote(Split(rest).FirstOrDefault() ?? ""));
                    break;

                case "submenu":
                {
                    (string[] parts, string condition, _) = Arguments(rest);
                    if (parts.Length == 0 || parts[0].Length == 0) break;

                    menuEntries.Add(new MenuEntry(
                        parts[0], parts.Length > 1 ? Caption(Unquote(parts[1])) : "", condition));
                    break;
                }

                // ----- dialogs ---------------------------------------------

                case "dialog":
                {
                    CloseDialog();

                    (string[] parts, _, _) = Arguments(rest);
                    if (parts.Length == 0 || parts[0].Length == 0) break;

                    dialogName = parts[0];
                    dialogTitle = parts.Length > 1 ? Caption(Unquote(parts[1])) : "";

                    // The axis is optional and yAxis is what TunerStudio assumes,
                    // so a dialog that does not say lays its items out downwards.
                    dialogLayout = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : "yAxis";
                    break;
                }

                case "topichelp":
                    dialogHelp = Unquote(rest);
                    break;

                case "field":
                case "displayonlyfield":
                {
                    (string[] parts, string condition, int conditionAt) = Arguments(rest);
                    string label = parts.Length > 0 ? Caption(Unquote(parts[0])) : "";
                    string constant = parts.Length > 1 ? parts[1] : "";

                    // An expression standing alone where the constant belongs is
                    // a computed caption, not a rule about when to show the line:
                    //     displayOnlyField = "Injector A", { bitStringValue(…) }
                    // Read as a condition it would hide the field whenever that
                    // text happened to evaluate to zero.
                    //
                    // Standing alone is the test, not merely being in that
                    // position: a condition may also trail a constant inside the
                    // same argument, and there the constant is a constant.
                    if (conditionAt == 1 && constant.Length == 0)
                    {
                        items.Add(new DialogItem(DialogItemKind.Label, label));
                        break;
                    }

                    // A field naming no constant is a caption or a blank line
                    // between groups, and there are a great many of both.
                    DialogItemKind kind = constant.Length == 0
                        ? DialogItemKind.Label
                        : key.Equals("displayOnlyField", StringComparison.OrdinalIgnoreCase)
                            ? DialogItemKind.ReadOnlyField
                            : DialogItemKind.Field;

                    items.Add(new DialogItem(kind, label, constant, condition));
                    break;
                }

                case "panel":
                {
                    (string[] parts, string condition, _) = Arguments(rest);
                    if (parts.Length == 0 || parts[0].Length == 0) break;

                    // A panel may carry a position, a condition, both or neither.
                    // The condition has already been lifted out, so anything left
                    // in the second argument is the position.
                    string position = parts.Length > 1 ? parts[1] : "";

                    items.Add(new DialogItem(
                        DialogItemKind.Panel, "", parts[0], condition, position));
                    break;
                }

                case "commandbutton":
                {
                    (string[] parts, string condition, _) = Arguments(rest);
                    if (parts.Length < 2 || parts[1].Length == 0) break;

                    items.Add(new DialogItem(
                        DialogItemKind.Command, Caption(Unquote(parts[0])), parts[1], condition));
                    break;
                }

                case "slider":
                {
                    (string[] parts, string condition, _) = Arguments(rest);
                    if (parts.Length < 2 || parts[1].Length == 0) break;

                    items.Add(new DialogItem(
                        DialogItemKind.Slider, Caption(Unquote(parts[0])), parts[1], condition));
                    break;
                }

                case "gauge":
                {
                    (string[] parts, _, _) = Arguments(rest);
                    if (parts.Length == 0 || parts[0].Length == 0) break;

                    items.Add(new DialogItem(DialogItemKind.Gauge, "", parts[0]));
                    break;
                }

                case "text":
                    items.Add(new DialogItem(DialogItemKind.Text, Unquote(rest)));
                    break;

                // Live graphs, status lamps and curve editors are described here
                // too. They are recorded rather than dropped so a dialog holding
                // one can say it is not showing everything, instead of silently
                // presenting a partial page as the whole of it.
                case "graphline":
                case "livegraph":
                case "indicator":
                case "indicatorpanel":
                    items.Add(new DialogItem(DialogItemKind.Unsupported, "", key));
                    break;

                default:
                    break;
            }
        }

        CloseMenu();
        CloseDialog();

        return new TuneInterface { Menus = [.. menus], Dialogs = dialogs };
    }

    /// <summary>
    /// The line without its comment or its line ending.
    ///
    /// A semicolon inside quotes is part of a label rather than the start of a
    /// comment — "Set the timing; carefully" is a caption, not a truncated one.
    /// </summary>
    internal static string StripComment(string line) => Strip(line);

    private static string Strip(string line)
    {
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ';' && !quoted) return line[..i].TrimEnd();
        }

        return line.TrimEnd('\r', '\n', ' ', '\t');
    }

    /// <summary>
    /// Arguments split on commas, with commas inside quotes or braces left alone.
    ///
    /// Both matter: a label may contain a comma, and a condition almost always
    /// does — <c>{ (a == 1) || (b == 2), }</c> is one argument however it looks.
    /// </summary>
    private static string[] Split(string text)
    {
        var parts = new List<string>();
        bool quoted = false;
        int depth = 0, start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '{') depth++;
            else if (!quoted && c == '}') depth = Math.Max(0, depth - 1);
            else if (c == ',' && !quoted && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());

        // A trailing comma is common and leaves an empty argument behind it.
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);

        return [.. parts];
    }


    /// <summary>
    /// A directive's arguments, with any condition lifted out of them.
    ///
    /// Two things the obvious split gets wrong, both found in real definitions.
    /// A condition is not always an argument of its own — Speeduino writes
    /// <c>field = "Bypass pin", ignBypassPin   { ignBypassEnable }</c>, with the
    /// condition trailing the constant inside one argument. And <c>{}</c> is
    /// used as a placeholder for an argument that is not being given, so an
    /// empty pair of braces is not a condition and must not be mistaken for the
    /// constant either.
    /// </summary>
    private static (string[] Parts, string Condition, int ConditionAt) Arguments(string rest)
    {
        var parts = new List<string>();
        string condition = "";
        int conditionAt = -1;

        foreach (string raw in Split(rest))
        {
            string value = raw;
            int brace = OpeningBrace(value);

            if (brace >= 0)
            {
                string found = value[brace..].Trim().Trim('{', '}').Trim();

                // The last one that says something. A line may carry several
                // placeholders before the real condition.
                if (found.Length > 0)
                {
                    condition = found;
                    conditionAt = parts.Count;
                }

                value = value[..brace].Trim();
            }

            // A quoted title may follow the name without a comma between them,
            // as rusEFI writes its menus:
            //     subMenu = dcMotorActuatorHw   "DC motor actuator(s) hardware"
            // Splitting it out here keeps the name a name; left alone it becomes
            // an identifier with a caption stuck to the end that matches nothing.
            // Only when what precedes the quote is a name. A label may be
            // prefixed to mark it — Speeduino writes !"…" for a warning and
            // #"…" for a note — and splitting on that would turn one caption
            // into a target of "!" and a stray string.
            int quote = value.IndexOf('"', StringComparison.Ordinal);
            if (quote > 0 && IsIdentifier(value[..quote].Trim()))
            {
                parts.Add(value[..quote].Trim());
                value = value[quote..].Trim();
            }

            parts.Add(value);
        }

        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);

        return ([.. parts], condition, conditionAt);
    }

    /// <summary>Whether the text is a bare name, rather than punctuation or prose.</summary>
    private static bool IsIdentifier(string text) =>
        text.Length > 0
        && (char.IsLetter(text[0]) || text[0] == '_')
        && text.All(c => char.IsLetterOrDigit(c) || c is '_' or '[' or ']');

    /// <summary>Where a condition begins in an argument, ignoring quoted braces.</summary>
    private static int OpeningBrace(string text)
    {
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (text[i] == '{' && !quoted) return i;
        }

        return -1;
    }


    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>
    /// A caption without the markers the format puts in front of it.
    ///
    /// <c>&amp;</c> marks the letter a menu would underline, so "F&amp;uel
    /// Settings" is Fuel Settings with the u as its shortcut; a doubled one is a
    /// literal ampersand. A leading <c>!</c> marks a warning and <c>#</c> a note,
    /// both of which are about how to draw the line rather than part of what it
    /// says.
    /// </summary>
    internal static string Caption(string text)
    {
        string trimmed = text.TrimStart('!', '#').Trim();

        if (!trimmed.Contains('&', StringComparison.Ordinal)) return trimmed;

        var built = new System.Text.StringBuilder(trimmed.Length);

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '&') { built.Append(trimmed[i]); continue; }

            // A doubled ampersand is one real ampersand.
            if (i + 1 < trimmed.Length && trimmed[i + 1] == '&') { built.Append('&'); i++; }
        }

        return built.ToString();
    }
}

/// <summary>
/// A two-dimensional editor: one row of breakpoints against one row of values,
/// drawn as a line you drag.
///
/// The other half of tuning, beside the tables. Warmup enrichment, cranking
/// pulsewidth, the knock threshold against RPM — an MS3 declares 134 of these,
/// and a settings menu that could not open them would be missing most of what a
/// tuner actually changes.
/// </summary>
/// <param name="Name">What a menu refers to it by.</param>
/// <param name="Title">What to put at the top.</param>
/// <param name="XBins">Constant holding the breakpoints along the bottom.</param>
/// <param name="YBins">Constant holding the values.</param>
/// <param name="XLabel">Caption for the breakpoints.</param>
/// <param name="YLabel">Caption for the values.</param>
public sealed record TuneCurve(
    string Name, string Title, string XBins, string YBins, string XLabel, string YLabel)
{
    /// <summary>
    /// The axis bounds as written, which may be a number or a <c>{…}</c>
    /// referring to a PC variable — an RPM axis is usually drawn to whatever
    /// the user set as the highest RPM they care about.
    /// </summary>
    public string XLow { get; init; } = "";

    public string XHigh { get; init; } = "";

    public string YLow { get; init; } = "";

    public string YHigh { get; init; } = "";

    public string Help { get; init; } = "";

    /// <summary>True when both halves are named, without which there is nothing to draw.</summary>
    public bool IsUsable => XBins.Length > 0 && YBins.Length > 0;
}

/// <summary>Reads <c>[CurveEditor]</c>.</summary>
public static class TuneCurveReader
{
    public static IReadOnlyDictionary<string, TuneCurve> Read(
        string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        symbols ??= MsqIni.DefaultSymbols;

        var curves = new Dictionary<string, TuneCurve>(StringComparer.OrdinalIgnoreCase);

        string name = "", title = "", xBins = "", yBins = "", xLabel = "", yLabel = "";
        string xLow = "", xHigh = "", yLow = "", yHigh = "", help = "";
        bool open = false;

        void Close()
        {
            if (open && name.Length > 0)
            {
                curves[name] = new TuneCurve(name, title, xBins, yBins, xLabel, yLabel)
                {
                    XLow = xLow, XHigh = xHigh, YLow = yLow, YHigh = yHigh, Help = help,
                };
            }

            name = title = xBins = yBins = xLabel = yLabel = "";
            xLow = xHigh = yLow = yHigh = help = "";
            open = false;
        }

        foreach (string raw in iniText.Split('\n'))
        {
            // The quote-aware stripper, not MsqIni's: that one cuts at the first
            // semicolon whatever is around it, which truncates a curve title
            // containing one.
            string line = TuneInterfaceReader.StripComment(raw);
            if (line.Length == 0) continue;

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0) continue;

            string key = line[..equals].Trim();
            string[] parts = Arguments(line[(equals + 1)..]);

            switch (key.ToLowerInvariant())
            {
                case "curve":
                    Close();
                    if (parts.Length == 0 || parts[0].Length == 0) break;

                    name = parts[0];
                    title = parts.Length > 1 ? Unquote(parts[1]) : "";
                    open = true;
                    break;

                case "columnlabel":
                    xLabel = parts.Length > 0 ? Unquote(parts[0]) : "";
                    yLabel = parts.Length > 1 ? Unquote(parts[1]) : "";
                    break;

                // The third argument is how many gridlines to draw, which is not
                // needed to read the curve.
                case "xaxis":
                    if (parts.Length > 1) (xLow, xHigh) = (parts[0], parts[1]);
                    break;

                case "yaxis":
                    if (parts.Length > 1) (yLow, yHigh) = (parts[0], parts[1]);
                    break;

                // The second argument names the output channel the ECU looks the
                // curve up by, which is not needed to edit it.
                case "xbins":
                    if (parts.Length > 0) xBins = parts[0];
                    break;

                case "ybins":
                    if (parts.Length > 0) yBins = parts[0];
                    break;

                case "topichelp":
                    help = parts.Length > 0 ? Unquote(parts[0]) : "";
                    break;

                default:
                    break;
            }
        }

        Close();
        return curves;
    }

    /// <summary>Comma-separated arguments, leaving commas inside quotes and braces alone.</summary>
    private static string[] Arguments(string text)
    {
        var parts = new List<string>();
        bool quoted = false;
        int depth = 0, start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '{') depth++;
            else if (!quoted && c == '}') depth = Math.Max(0, depth - 1);
            else if (c == ',' && !quoted && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);

        return [.. parts];
    }

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>
    /// A caption without the markers the format puts in front of it.
    ///
    /// <c>&amp;</c> marks the letter a menu would underline, so "F&amp;uel
    /// Settings" is Fuel Settings with the u as its shortcut; a doubled one is a
    /// literal ampersand. A leading <c>!</c> marks a warning and <c>#</c> a note,
    /// both of which are about how to draw the line rather than part of what it
    /// says.
    /// </summary>
    internal static string Caption(string text)
    {
        string trimmed = text.TrimStart('!', '#').Trim();

        if (!trimmed.Contains('&', StringComparison.Ordinal)) return trimmed;

        var built = new System.Text.StringBuilder(trimmed.Length);

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '&') { built.Append(trimmed[i]); continue; }

            // A doubled ampersand is one real ampersand.
            if (i + 1 < trimmed.Length && trimmed[i + 1] == '&') { built.Append('&'); i++; }
        }

        return built.ToString();
    }
}
