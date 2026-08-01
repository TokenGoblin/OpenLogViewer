namespace OpenLogViewer.Core;

/// <summary>
/// Persists the user's filter conditions. Filters are matched to channels by
/// name, so a set written against one log applies to any other that shares
/// those channel names.
/// </summary>
public sealed class FilterStore
{
    private const int MaxFilters = 60;

    private readonly List<LogFilter> _filters = [];

    public FilterStore(string? path = null)
    {
        Path = path ?? JsonSettingsFile.InAppData("filters.json");
        Reload();
    }

    public string Path { get; }

    public IReadOnlyList<LogFilter> Filters => _filters;

    public void Reload()
    {
        _filters.Clear();

        FilterFile? file = JsonSettingsFile.Read<FilterFile>(Path);
        if (file?.Filters is null) return;

        foreach (LogFilter filter in file.Filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Name) || string.IsNullOrWhiteSpace(filter.Channel)) continue;
            if (_filters.Count >= MaxFilters) break;
            _filters.Add(filter);
        }
    }

    public void Replace(IEnumerable<LogFilter> filters)
    {
        _filters.Clear();
        _filters.AddRange(filters.Take(MaxFilters));
        Persist();
    }

    public void Add(LogFilter filter)
    {
        if (_filters.Count >= MaxFilters)
            throw new InvalidOperationException($"There is a limit of {MaxFilters} filters.");

        _filters.Add(filter);
        Persist();
    }

    public bool Remove(LogFilter filter)
    {
        if (!_filters.Remove(filter)) return false;

        Persist();
        return true;
    }

    private void Persist() =>
        JsonSettingsFile.Write(Path, new FilterFile { Version = 1, Filters = [.. _filters] });

    private sealed class FilterFile
    {
        public int Version { get; set; }
        public List<LogFilter>? Filters { get; set; }
    }
}
