namespace OpenLogViewer.Core;

/// <summary>
/// Persists the user's calculated channels. Definitions reference channels by
/// name, so a set written against one log applies to any other that carries
/// those names.
/// </summary>
public sealed class MathChannelStore
{
    private const int MaxChannels = 60;

    private readonly List<MathChannel> _channels = [];

    public MathChannelStore(string? path = null)
    {
        Path = path ?? JsonSettingsFile.InAppData("math.json");
        Reload();
    }

    public string Path { get; }

    public IReadOnlyList<MathChannel> Channels => _channels;

    public void Reload()
    {
        _channels.Clear();

        MathFile? file = JsonSettingsFile.Read<MathFile>(Path);
        if (file?.Channels is null) return;

        foreach (MathChannel channel in file.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Name)) continue;
            if (string.IsNullOrWhiteSpace(channel.Expression)) continue;
            if (_channels.Count >= MaxChannels) break;

            _channels.Add(channel);
        }
    }

    public void Add(MathChannel channel)
    {
        if (_channels.Count >= MaxChannels)
            throw new InvalidOperationException($"There is a limit of {MaxChannels} calculated channels.");

        if (_channels.Any(c => c.Name.Equals(channel.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A calculated channel called \"{channel.Name}\" already exists.");

        _channels.Add(channel);
        Persist();
    }

    public void Replace(MathChannel existing, MathChannel replacement)
    {
        int index = _channels.IndexOf(existing);
        if (index < 0) return;

        _channels[index] = replacement;
        Persist();
    }

    public bool Remove(MathChannel channel)
    {
        if (!_channels.Remove(channel)) return false;

        Persist();
        return true;
    }

    private void Persist() =>
        JsonSettingsFile.Write(Path, new MathFile { Version = 1, Channels = [.. _channels] });

    private sealed class MathFile
    {
        public int Version { get; set; }
        public List<MathChannel>? Channels { get; set; }
    }
}
