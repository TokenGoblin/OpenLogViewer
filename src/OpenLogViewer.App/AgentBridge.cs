using System.IO;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// The view model, answering the agent API.
///
/// <para>
/// Every read here goes through what the window itself shows, so an agent and a
/// person looking over its shoulder are never told different things. Every write
/// goes through the same view-model call the buttons use, so the gates that stop
/// a person writing to a placeholder tune stop an agent too — without those
/// checks being written twice and drifting apart.
/// </para>
/// <para>
/// The arming lives on the view model rather than in the server, because it
/// belongs to the session rather than to the socket: it is cleared on
/// disconnect, so an agent that was allowed to write to a bench engine cannot
/// still write when the next thing plugged in is a car.
/// </para>
/// </summary>
public sealed class AgentBridge(MainViewModel viewModel) : IAgentBridge
{
    private readonly MainViewModel _viewModel = viewModel;

    public AgentState State()
    {
        LogDocument? log = _viewModel.Document;

        return new AgentState
        {
            Mode = _viewModel.IsLive ? "live" : log is not null ? "log" : "idle",
            Signature = _viewModel.LiveSignature,
            File = log?.FilePath is { Length: > 0 } path ? Path.GetFileName(path) : "",
            Samples = log?.Time.Length ?? 0,
            Seconds = log is { Time.Length: > 0 } ? log.Time.At(log.Time.Length - 1) : 0,
            Rate = _viewModel.IsLive ? _viewModel.LiveRate : 0,
            Channels = log?.Channels.Count ?? 0,
            HasTune = _viewModel.HasEcuTune && !_viewModel.TuneIsPlaceholder,
            WritesArmed = _viewModel.AgentWritesArmed,
            Error = "",
        };
    }

    public IReadOnlyList<AgentChannel> Channels()
    {
        if (_viewModel.Document is not { } log) return [];

        // The role is the useful half. A rusEFI calls engine speed RPMValue and
        // a MegaSquirt calls it rpm; an agent that has to know which is an agent
        // that works on one firmware.
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ChannelRole role in Enum.GetValues<ChannelRole>())
            if (ChannelRoles.Find(log, role) is { } found) roles.TryAdd(found.Name, role.ToString());

        return
        [
            .. log.Channels.Select(c => new AgentChannel(c.Name, c.Units, c.Digits)
            {
                Role = roles.GetValueOrDefault(c.Name, ""),
            }),
        ];
    }

    public IReadOnlyList<double> Values(string channel, double seconds)
    {
        if (_viewModel.Document is not { } log) return [];
        if (log.FindChannel(channel) is not { } found) return [];

        return Tail(found, log, seconds);
    }

    public IReadOnlyList<double> Times(double seconds)
    {
        if (_viewModel.Document is not { } log) return [];

        return Tail(log.Time, log, seconds);
    }

    /// <summary>
    /// The last so many seconds of a channel, or all of it when asked for none.
    ///
    /// Counted from the end rather than the start, because on a live session the
    /// interesting part is always the newest — an agent asking for "the last ten
    /// seconds" once a second should not be handed the whole afternoon each time.
    /// </summary>
    private static IReadOnlyList<double> Tail(LogChannel channel, LogDocument log, double seconds)
    {
        int count = Math.Min(channel.Length, log.Time.Length);
        if (count == 0) return [];

        int from = 0;

        if (seconds > 0)
        {
            double until = log.Time.At(count - 1) - seconds;

            for (int i = count - 1; i >= 0; i--)
            {
                if (log.Time.At(i) < until) { from = i + 1; break; }
            }
        }

        var values = new double[count - from];
        for (int i = 0; i < values.Length; i++) values[i] = channel.At(from + i);

        return values;
    }

    public IReadOnlyList<AgentFinding> Insights()
    {
        if (_viewModel.Document is not { } log) return [];

        return
        [
            .. LogInsights.From(log).Select(i =>
                new AgentFinding(i.Level.ToString(), i.Topic, i.Title, i.Detail) { Evidence = i.Evidence }),
        ];
    }

    public IReadOnlyDictionary<string, double> TuneValues() => _viewModel.AgentTuneValues();

    public TuneTable? Table(string name) =>
        _viewModel.EcuTables.FirstOrDefault(
            t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> TableNames() => [.. _viewModel.EcuTables.Select(t => t.Name)];

    public AgentRefusal? SetSetting(string name, double value) =>
        _viewModel.AgentSetSetting(name, value);

    public AgentRefusal? SetTableCell(string table, int column, int row, double value) =>
        _viewModel.AgentSetTableCell(table, column, row, value);
}
