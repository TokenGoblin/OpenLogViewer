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

    public IReadOnlyList<AgentChannel> Channels(bool raw = false)
    {
        if (raw && _viewModel.AgentRawChannelNames is { Count: > 0 } rawNames)
        {
            IReadOnlyList<string> rawUnits = _viewModel.AgentRawChannelUnits;

            return
            [
                .. rawNames.Select((n, i) => new AgentChannel(n, i < rawUnits.Count ? rawUnits[i] : "", 2)),
            ];
        }

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

    public AgentWireHealth WireHealth()
    {
        WireHealthSnapshot h = _viewModel.WireTrace.Health();

        return new AgentWireHealth(
            h.Sampled, h.Failures, h.SuccessRate, h.FailuresByKind,
            h.SinceLastSuccess?.TotalSeconds, h.LongestRecent?.TotalMilliseconds);
    }

    public IReadOnlyList<AgentWireEvent> WireEvents(int count) =>
        [
            .. _viewModel.WireTrace.Recent(count).Select(e => new AgentWireEvent(
                e.Sequence, e.At, e.Context, e.Origin.ToString(), e.Attempt,
                e.Elapsed.TotalMilliseconds, e.Outcome.ToString(), e.FailureKind.ToString(), e.Detail)),
        ];

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

    public AgentRefusal? SetSetting(string name, double value, string rationale, bool confirmDangerous = false) =>
        _viewModel.AgentSetSetting(name, value, rationale, confirmDangerous);

    public AgentRefusal? SetTableCell(
        string table, int column, int row, double value, string rationale, bool confirmDangerous = false) =>
        _viewModel.AgentSetTableCell(table, column, row, value, rationale, confirmDangerous);

    public string ProjectBrief() => _viewModel.ProjectBrief();

    public IReadOnlyList<string> Projects() => _viewModel.ProjectNames();

    public AgentRefusal? KeepTune(string note) => _viewModel.AgentKeepTune(note);

    public string CompareVersions(string from, string to) =>
        _viewModel.CompareVersions(from, to);

    public AgentRefusal? RecordSitting(string note) => _viewModel.AgentRecordSitting(note);

    public AgentRefusal? NoteFix(string id, string title, string detail, string state, string change) =>
        _viewModel.AgentNoteFix(id, title, detail, state, change);

    // ----- composition ---------------------------------------------------------

    public AgentTuneFull TuneFull()
    {
        IReadOnlyDictionary<string, double> scalars = _viewModel.AgentTuneValues();
        Dictionary<string, TuneConstant> byName =
            _viewModel.AgentTuneConstants().ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var settings = scalars.Select(kv =>
            byName.TryGetValue(kv.Key, out TuneConstant? constant)
                ? new AgentSetting(kv.Key, kv.Value, constant.Units)
                {
                    Options = constant.Options,
                    Low = constant.HasRange ? constant.Low : null,
                    High = constant.HasRange ? constant.High : null,
                }
                : new AgentSetting(kv.Key, kv.Value, "")).ToList();

        return new AgentTuneFull(settings, [.. _viewModel.EcuTables.Select(FlattenTable)]);
    }

    /// <summary>The same flattening a single <c>/table</c> answer uses, so the two never drift apart.</summary>
    private static AgentTable FlattenTable(TuneTable table)
    {
        var rows = new List<IReadOnlyList<double>>(table.Rows);

        for (int r = 0; r < table.Rows; r++)
        {
            var row = new double[table.Columns];
            for (int c = 0; c < table.Columns; c++) row[c] = table.Values[c, r];
            rows.Add(row);
        }

        return new AgentTable(
            table.Name, table.Units, table.Columns, table.Rows,
            table.X.Breakpoints, table.Y.Breakpoints, table.X.Units, table.Y.Units,
            table.X.Constant, table.Y.Constant, rows);
    }

    public AgentLogFull LogFull(double seconds, int decimate)
    {
        if (_viewModel.Document is not { } log) return new AgentLogFull([], []);

        IReadOnlyList<double> times = Decimate(Tail(log.Time, log, seconds), decimate);

        var channels = log.Channels
            .Select(c => new AgentChannelSamples(c.Name, c.Units, Decimate(Tail(c, log, seconds), decimate)))
            .ToList();

        return new AgentLogFull(times, channels);
    }

    /// <summary>Keeps one sample in every <paramref name="stride"/>, for a caller that asked to thin a big log.</summary>
    private static IReadOnlyList<double> Decimate(IReadOnlyList<double> values, int stride)
    {
        if (stride <= 1) return values;

        var kept = new List<double>((values.Count + stride - 1) / stride);
        for (int i = 0; i < values.Count; i += stride) kept.Add(values[i]);

        return kept;
    }

    public AgentContext Context() =>
        new(
            ProjectBrief(),
            State(),
            new AgentTuneSummary(TuneValues().Count, TableNames()),
            Insights(),
            WireHealth());

    // ----- propose / apply / staging -------------------------------------------

    public TuneProposalResult ProposeTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells) =>
        _viewModel.AgentProposeTune(settings, cells);

    public TuneApplyResult ApplyTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string note,
        bool confirmDangerous = false) =>
        _viewModel.AgentApplyTune(settings, cells, note, confirmDangerous);

    public AgentStageResult StageTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string filename) =>
        _viewModel.AgentStageTune(settings, cells, filename);

    public AgentStageResult StageTable(string name, string filename) =>
        _viewModel.AgentStageTable(name, filename);

    public IReadOnlyList<AgentStagedFile> ListStaged() => _viewModel.AgentListStaged();

    public AgentDefinitionNeed? DefinitionNeeded() => _viewModel.AgentDefinitionNeeded();

    public AgentDefinitionImported ImportDefinition(string path, string content, string source, string name) =>
        _viewModel.AgentImportDefinition(path, content, source, name);
}
