using System.Globalization;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>How a settings row is drawn.</summary>
public enum SettingKind
{
    /// <summary>A number, typed into a box.</summary>
    Number,

    /// <summary>One of a named set, chosen from a list.</summary>
    Choice,

    /// <summary>A name, typed as text.</summary>
    Text,

    /// <summary>A caption or a gap between groups.</summary>
    Caption,

    /// <summary>A reading the firmware will not let you change.</summary>
    ReadOnly,

    /// <summary>Something this cannot draw — a live graph, a lamp.</summary>
    NotShown,
}

/// <summary>
/// One line of a settings dialog, ready to put on screen.
///
/// The join between three things that know nothing of each other: the interface
/// the firmware describes (what to call this and when to show it), the constant
/// behind it (where it lives and how it is scaled), and the edit in progress
/// (what it would become). Keeping the join here rather than in the view is what
/// lets it be tested without a window.
/// </summary>
public sealed class SettingRow : ObservableObject
{
    private readonly TuneSettingsEdit? _edit;
    private readonly TuneConstant? _constant;
    private readonly int _element;

    public SettingRow(DialogItem item, TuneConstant? constant, TuneSettingsEdit? edit)
    {
        ArgumentNullException.ThrowIfNull(item);

        Item = item;
        _constant = constant;
        _edit = edit;
        _element = Math.Max(0, item.TargetIndex);

        Kind = Classify(item, constant);
        Label = item.Label;
        Units = constant?.Units ?? "";

        if (constant?.HasOptions == true)
        {
            // The firmware pads a bit field's names out to the full width of the
            // field, so a two-bit option declares four names whether or not it
            // does four things. The padding is spelled INVALID and must not be
            // offered as a choice.
            Options = [.. constant.Options.Where((_, i) => constant.IsValidOption(i))];
        }
    }

    public DialogItem Item { get; }

    public SettingKind Kind { get; }

    public string Label { get; }

    public string Units { get; }

    /// <summary>The names a choice may take, without the firmware's padding.</summary>
    public IReadOnlyList<string> Options { get; } = [];

    /// <summary>Whether the firmware says this applies to the tune in hand.</summary>
    public bool IsVisible { get; private set; } = true;

    /// <summary>
    /// True when the condition could not be worked out, so the row is shown
    /// without the firmware having said it applies. Worth marking: an unexplained
    /// setting is better than a missing one, but the user should know which it is.
    /// </summary>
    public bool IsUncertain { get; private set; }

    public bool IsEditable =>
        Kind is SettingKind.Number or SettingKind.Choice or SettingKind.Text
        && _constant?.OnController == true;

    /// <summary>What the ECU holds, formatted the way the firmware asks.</summary>
    public string Original => _constant is null || _edit is null
        ? ""
        : Format(_edit.Value(_constant.Name, _element));

    /// <summary>True when this would be changed by sending.</summary>
    public bool IsChanged =>
        _edit is not null && _constant is not null
        && _edit.Changes.Any(c => c.Name.Equals(Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The value as typed, or chosen.</summary>
    public string Value
    {
        get
        {
            if (_constant is null || _edit is null) return "";
            if (_constant.IsText) return _edit.Text(_constant.Name);

            double value = _edit.Value(_constant.Name, _element);

            return _constant.HasOptions ? _constant.OptionName(value) : Format(value);
        }

        set
        {
            if (!TrySet(value)) return;

            Raise(nameof(Value));
            Raise(nameof(IsChanged));
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when the row has changed the tune.</summary>
    public event Action? Changed;

    /// <summary>
    /// Re-reads whether this applies, and what it says.
    ///
    /// Called after any edit, because a setting's condition may name a setting
    /// somebody has just changed — turning knock detection on is meant to reveal
    /// the four fields that configure it.
    /// </summary>
    public void Refresh(Func<string, double> lookup)
    {
        if (Item.HasCondition)
        {
            ConditionVerdict verdict = DialogCondition.Evaluate(Item.Condition, lookup);

            IsVisible = verdict != ConditionVerdict.Hidden;
            IsUncertain = verdict == ConditionVerdict.Unknown;
        }

        Raise(nameof(IsVisible));
        Raise(nameof(IsUncertain));
        Raise(nameof(Value));
        Raise(nameof(IsChanged));
    }

    /// <summary>Puts this row back to what the ECU holds.</summary>
    public void Revert()
    {
        if (_constant is null || _edit is null) return;

        _edit.Revert(_constant.Name, _element);

        Raise(nameof(Value));
        Raise(nameof(IsChanged));
        Changed?.Invoke();
    }

    private string Key => _element == 0 ? _constant!.Name : $"{_constant!.Name}[{_element}]";

    private bool TrySet(string text)
    {
        if (_constant is null || _edit is null || !IsEditable) return false;

        if (_constant.IsText) return _edit.SetText(_constant.Name, text ?? "");

        if (_constant.HasOptions)
        {
            // A choice arrives as its name; the ECU wants the number behind it.
            int index = IndexOfOption(text);
            return index >= 0 && _edit.Set(_constant.Name, index, _element);
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number)
               && _edit.Set(_constant.Name, number, _element);
    }

    /// <summary>
    /// Which value a chosen name stands for, in the firmware's own numbering.
    ///
    /// Searched in the full list rather than in the offered one, since the
    /// offered list has the padding removed and its positions no longer match
    /// the values the ECU stores.
    /// </summary>
    private int IndexOfOption(string name)
    {
        for (int i = 0; i < _constant!.Options.Count; i++)
            if (_constant.Options[i].Equals(name, StringComparison.Ordinal)) return i;

        return -1;
    }

    private string Format(double value)
    {
        if (double.IsNaN(value)) return "";

        int digits = _constant?.Digits ?? 0;
        return value.ToString($"F{Math.Clamp(digits, 0, 6)}", CultureInfo.CurrentCulture);
    }

    private static SettingKind Classify(DialogItem item, TuneConstant? constant) => item.Kind switch
    {
        DialogItemKind.Label or DialogItemKind.Text => SettingKind.Caption,
        DialogItemKind.ReadOnlyField => SettingKind.ReadOnly,
        DialogItemKind.Unsupported or DialogItemKind.Gauge => SettingKind.NotShown,

        // A field naming a constant this firmware does not declare has nothing
        // behind it. Shown as a caption rather than as a box that cannot be
        // typed into, which would look broken.
        _ when constant is null => SettingKind.Caption,

        _ when constant.IsText => SettingKind.Text,
        _ when constant.HasOptions => SettingKind.Choice,
        _ => SettingKind.Number,
    };
}
