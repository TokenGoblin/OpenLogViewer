using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// One line in the console: a request against the agent API and its answer,
/// or something the console itself has to say (help, a refusal to parse, the
/// API being off).
/// </summary>
public sealed class ConsoleEntry
{
    public required string Time { get; init; }
    public required string Source { get; init; }
    public string Outcome { get; init; } = "";
    public required string Title { get; init; }
    public string Preview { get; init; } = "";
    public required string Detail { get; init; }

    public bool IsConsole { get; init; }
    public bool IsNote { get; init; }
    public bool IsWrite { get; init; }
    public bool IsFailure { get; init; }

    /// <summary>The exchange behind this line, or null for a note.</summary>
    public AgentExchange? Exchange { get; init; }
}

/// <summary>
/// The agent console: a live transcript of everything said to the agent API —
/// by an AI through MCP, by a script over HTTP, or by a person here — and a
/// command line for driving the same API by hand.
///
/// <para>
/// It watches; it does not intercept. The transcript is recorded by the server
/// whether or not this window is open, so opening it shows what already
/// happened this session, not just what happens next.
/// </para>
/// <para>
/// <b>The command line has no private way in.</b> Each command is sent over
/// the loopback socket like any other client, signed with a token that only
/// exists inside this process — which is how the transcript can say "console"
/// and mean it. Writes from here meet the same arming, rationale, rate and
/// range checks an agent's do.
/// </para>
/// </summary>
public partial class ConsoleWindow : Window
{
    /// <summary>Lines kept in the window. The transcript has its own, separate bound.</summary>
    private const int MaximumEntries = 2000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly MainViewModel _vm;
    private readonly ObservableCollection<ConsoleEntry> _entries = [];
    private readonly ICollectionView _view;
    private readonly List<string> _history = [];
    private int _historyAt;

    /// <summary>The newest exchange already in the window, so reopening does not repeat any.</summary>
    private long _seen;

    public ConsoleWindow(MainViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _vm;

        _view = CollectionViewSource.GetDefaultView(_entries);
        _view.Filter = Shown;
        Entries.ItemsSource = _view;

        foreach (AgentExchange exchange in _vm.AgentTranscript.All()) Add(exchange, select: false);

        Note(_vm.AgentIsRunning
                 ? "Watching the agent API. Type help for the commands."
                 : "The agent API is off, so there is nothing to watch and nowhere to send commands. Type api on to start it.",
             select: true);

        _vm.AgentTranscript.Recorded += OnRecorded;
        Closed += (_, _) => _vm.AgentTranscript.Recorded -= OnRecorded;

        Loaded += (_, _) => Input.Focus();
    }

    // ----- the transcript ----------------------------------------------------

    /// <summary>Arrives on the request's own thread; everything after this is on the window's.</summary>
    private void OnRecorded(AgentExchange exchange) =>
        Dispatcher.BeginInvoke(() => Add(exchange, select: exchange.Client == "console"));

    private void Add(AgentExchange exchange, bool select)
    {
        if (exchange.Sequence <= _seen) return;
        _seen = exchange.Sequence;

        Append(Describe(exchange), select);
    }

    private void Note(string text, bool select = true)
    {
        string first = text.Split('\n', 2)[0].TrimEnd();

        Append(new ConsoleEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Source = "console",
            Title = first,
            Detail = text,
            IsNote = true,
        }, select);
    }

    /// <summary>A typed line that went nowhere, with why on the line itself rather than only below.</summary>
    private void Refused(string line, string why) =>
        Append(new ConsoleEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Source = "you",
            Outcome = "not sent",
            Title = $"{line}  —  {why}",
            Detail = $"{line}\n\n{why}",
            IsNote = true,
            IsConsole = true,
            IsFailure = true,
        }, select: true);

    private void Append(ConsoleEntry entry, bool select)
    {
        _entries.Add(entry);
        while (_entries.Count > MaximumEntries) _entries.RemoveAt(0);

        // A person's own command always comes into view with its answer; an
        // agent's only while following, so reading back through older lines
        // is not yanked away by the next request.
        if (select)
        {
            Entries.SelectedItem = entry;
            Entries.ScrollIntoView(entry);
        }
        else if (Follow.IsChecked == true && Shown(entry))
        {
            Entries.ScrollIntoView(entry);
        }
    }

    private static ConsoleEntry Describe(AgentExchange e)
    {
        string who = e.Client switch
        {
            "console" => "you",
            "mcp" => "AI · mcp",
            "http" => "AI · http",
            "unknown" => "no token",
            _ => "AI · " + e.Client,
        };

        string title = e.Tool.Length > 0 ? $"{e.Tool}  {e.Method} {e.Target}" : $"{e.Method} {e.Target}";
        string outcome = $"{e.Status} {e.Milliseconds,6:0}ms";

        var detail = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"#{e.Sequence}  {e.At.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  {who}")
            .Append(e.Tool.Length > 0 ? $"  ({e.Tool})" : "")
            .AppendLine()
            .Append(CultureInfo.InvariantCulture, $"{e.Method} {e.Target}  →  {e.Status}  in {e.Milliseconds:0.#} ms")
            .AppendLine();

        if (e.RequestBody.Length > 0)
            detail.AppendLine().AppendLine("── sent ──").AppendLine(Pretty(e.RequestBody));

        detail.AppendLine().AppendLine("── answered ──").Append(Pretty(e.ResponseBody));

        return new ConsoleEntry
        {
            Time = e.At.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Source = who,
            Outcome = outcome,
            Title = title,
            Preview = Flatten(e.RequestBody.Length > 0 ? $"→ {e.RequestBody}   ← {e.ResponseBody}" : $"← {e.ResponseBody}"),
            Detail = detail.ToString(),
            IsConsole = e.Client == "console",
            IsWrite = e.IsWrite,
            IsFailure = !e.Succeeded,
            Exchange = e,
        };
    }

    /// <summary>JSON laid out for reading; anything that is not JSON, as it came.</summary>
    private static string Pretty(string body)
    {
        if (body.Length == 0) return "(nothing)";

        try
        {
            return JsonNode.Parse(body)?.ToJsonString(Indented) ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string Flatten(string text)
    {
        string one = text.Replace('\r', ' ').Replace('\n', ' ');
        return one.Length > 240 ? one[..240] + "…" : one;
    }

    // ----- what is shown -----------------------------------------------------

    private bool Shown(object item)
    {
        if (item is not ConsoleEntry entry) return false;

        if (ShowAgents?.IsChecked == true) return entry is { IsNote: false, IsConsole: false };
        if (ShowConsole?.IsChecked == true) return entry.IsConsole || entry.IsNote;
        if (ShowWrites?.IsChecked == true) return entry.IsWrite;

        return true;
    }

    private void OnShowChanged(object sender, RoutedEventArgs e) => _view?.Refresh();

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e) =>
        Detail.Text = (Entries.SelectedItem as ConsoleEntry)?.Detail ?? "";

    private void OnClearClick(object sender, RoutedEventArgs e) => Clear();

    private void Clear()
    {
        _entries.Clear();
        Detail.Text = "";
    }

    // ----- the command line --------------------------------------------------

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up when _history.Count > 0:
                _historyAt = Math.Max(0, _historyAt - 1);
                Recall();
                e.Handled = true;
                return;

            case Key.Down when _history.Count > 0:
                _historyAt = Math.Min(_history.Count, _historyAt + 1);
                Recall();
                e.Handled = true;
                return;

            case Key.Enter:
                e.Handled = true;
                string line = Input.Text.Trim();
                Input.Clear();

                if (line.Length == 0) return;

                if (_history.Count == 0 || _history[^1] != line) _history.Add(line);
                _historyAt = _history.Count;

                await Run(line);
                return;
        }
    }

    private void Recall()
    {
        Input.Text = _historyAt < _history.Count ? _history[_historyAt] : "";
        Input.CaretIndex = Input.Text.Length;
    }

    /// <summary>Does one typed line. Public so a scripted run can drive the console.</summary>
    public async Task Run(string line)
    {
        AgentConsoleCommand command = AgentConsoleCommand.Parse(line);

        if (command.Error.Length > 0)
        {
            Refused(line, command.Error);
            return;
        }

        if (!command.IsRequest)
        {
            Local(command, line);
            return;
        }

        if (!_vm.AgentIsRunning)
        {
            Refused(line, "The agent API is off, and every command goes through it. Type api on to start it.");
            return;
        }

        // The answer arrives in the window through the transcript, the same as
        // an agent's does — so there is exactly one way a line gets here, and
        // what is shown is what the server recorded, not what this side
        // thought it sent.
        try
        {
            using var request = new HttpRequestMessage(
                command.Method == "POST" ? HttpMethod.Post : HttpMethod.Get, _vm.AgentAddress + command.Target);

            request.Headers.Authorization = new("Bearer", _vm.AgentConsoleToken);

            if (command.Method == "POST")
                request.Content = new StringContent(command.Body, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await Http.SendAsync(request);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Refused(line, $"The agent API did not answer: {e.Message}");
        }
    }

    private void Local(AgentConsoleCommand command, string line)
    {
        switch (command.Local)
        {
            case "help":
                Note("Commands\n\n" + AgentConsoleCommand.Help);
                return;

            case "clear":
                Clear();
                return;

            case "api" when command.Argument is "on" or "start":
                Note(_vm.StartAgentApi());
                return;

            case "api" when command.Argument is "off" or "stop":
                _vm.StopAgentApi();
                Note("The agent API is off and the socket is closed. Type api on to start it again.");
                return;

            case "api":
                Note(_vm.AgentSummary);
                return;

            case "show":
            {
                RadioButton? pick = command.Argument switch
                {
                    "" or "all" or "everything" => ShowAll,
                    "ai" or "agent" or "agents" => ShowAgents,
                    "console" or "me" or "you" => ShowConsole,
                    "writes" or "write" => ShowWrites,
                    _ => null,
                };

                if (pick is null) Refused(line, "show takes all, ai, console or writes.");
                else pick.IsChecked = true;
                return;
            }
        }
    }

    /// <summary>The lines in the window, for the tests.</summary>
    public IReadOnlyList<ConsoleEntry> Lines => _entries;
}
