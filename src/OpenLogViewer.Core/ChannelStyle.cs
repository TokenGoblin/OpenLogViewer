namespace OpenLogViewer.Core;

/// <summary>
/// What the user has pinned about one channel's appearance, overriding what the
/// application would choose for it.
///
/// Both parts are optional and independent: pinning a colour says nothing about
/// the scale, and pinning a scale says nothing about the colour.
/// </summary>
/// <param name="Channel">The channel's name, which is how this is matched.</param>
/// <param name="Color">
/// Packed 0xRRGGBB, or null to keep taking the palette's choice.
/// </param>
/// <param name="Min">Bottom of the fixed scale, or null for the channel's own range.</param>
/// <param name="Max">Top of it.</param>
public sealed record ChannelStyle(
    string Channel,
    int? Color = null,
    double? Min = null,
    double? Max = null)
{
    /// <summary>
    /// Whether the pinned bounds are usable. A pair that is missing an end, or
    /// whose ends are the wrong way round or equal, would divide by zero or
    /// invert the trace, so it is treated as no pin at all rather than trusted.
    /// </summary>
    public bool HasRange =>
        Min is { } low && Max is { } high
        && double.IsFinite(low) && double.IsFinite(high)
        && high > low;

    public bool HasColor => Color is >= 0 and <= 0xFFFFFF;

    /// <summary>Nothing pinned, so the entry is not worth keeping.</summary>
    public bool IsEmpty => !HasRange && !HasColor;
}

/// <summary>
/// Persists per-channel colours and scales, matched to channels by name — so a
/// choice made on one log applies to any other carrying that channel, the way
/// presets and filters do.
///
/// <para>
/// A pinned colour is worth understanding, because it opts out of something.
/// Trace colours are normally re-picked whenever the scheme changes, since a
/// palette is only separable against the background it was chosen for, and each
/// scheme's palette has been checked for lightness range, contrast and
/// separation under colour-vision deficiency against its own ground. A pinned
/// colour is not re-picked and not re-checked: that is the whole point of
/// pinning one, and the cost is that a colour chosen on a dark scheme may sit
/// poorly on a light one. The picker offers the current scheme's palette first
/// so the easy choice is still a checked one.
/// </para>
/// </summary>
public sealed class ChannelStyleStore
{
    /// <summary>
    /// A generous ceiling. This is a file of deliberate choices, not a cache —
    /// nothing writes to it without the user asking — but a bound keeps a
    /// corrupt or hand-edited file from being read into unbounded memory.
    /// </summary>
    private const int MaxStyles = 500;

    private readonly Dictionary<string, ChannelStyle> _styles =
        new(StringComparer.OrdinalIgnoreCase);

    public ChannelStyleStore(string? path = null)
    {
        Path = path ?? JsonSettingsFile.InAppData("channels.json");
        Reload();
    }

    public string Path { get; }

    public IReadOnlyCollection<ChannelStyle> Styles => _styles.Values;

    public void Reload()
    {
        _styles.Clear();

        StyleFile? file = JsonSettingsFile.Read<StyleFile>(Path);
        if (file?.Channels is null) return;

        foreach (ChannelStyle style in file.Channels)
        {
            if (string.IsNullOrWhiteSpace(style.Channel) || style.IsEmpty) continue;
            if (_styles.Count >= MaxStyles) break;

            _styles[style.Channel] = style;
        }
    }

    /// <summary>What is pinned for a channel, or null where nothing is.</summary>
    public ChannelStyle? For(string channel) =>
        channel is not null && _styles.TryGetValue(channel, out ChannelStyle? style) ? style : null;

    /// <summary>
    /// Pins a colour, leaving any pinned scale alone. Null clears it.
    /// </summary>
    public void SetColor(string channel, int? color) =>
        Update(channel, existing => existing with { Color = color });

    /// <summary>
    /// Pins a scale, leaving any pinned colour alone. Either bound null clears it,
    /// since half a scale is not one.
    /// </summary>
    public void SetRange(string channel, double? min, double? max) =>
        Update(channel, existing => existing with { Min = min, Max = max });

    /// <summary>Unpins everything for a channel, putting it back to automatic.</summary>
    public void Clear(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return;
        if (!_styles.Remove(channel)) return;

        Persist();
    }

    private void Update(string channel, Func<ChannelStyle, ChannelStyle> change)
    {
        if (string.IsNullOrWhiteSpace(channel)) return;

        ChannelStyle updated = change(For(channel) ?? new ChannelStyle(channel));

        // An entry that pins nothing is removed rather than stored, so clearing
        // both halves leaves the file as it was before either was set.
        if (updated.IsEmpty) _styles.Remove(channel);
        else if (_styles.Count < MaxStyles || _styles.ContainsKey(channel)) _styles[channel] = updated;
        else throw new InvalidOperationException($"There is a limit of {MaxStyles} channel styles.");

        Persist();
    }

    private void Persist() =>
        JsonSettingsFile.Write(Path, new StyleFile
        {
            Version = 1,
            Channels = [.. _styles.Values.OrderBy(s => s.Channel, StringComparer.OrdinalIgnoreCase)],
        });

    private sealed class StyleFile
    {
        public int Version { get; set; }
        public List<ChannelStyle>? Channels { get; set; }
    }
}
