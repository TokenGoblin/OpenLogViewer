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

        // The suggestion list floats in its own window and would otherwise
        // stay put while this one moved or went behind another.
        Deactivated += (_, _) => CloseSuggestions();
        LocationChanged += (_, _) => CloseSuggestions();
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

    /// <summary>
    /// Refilters, and when that hides the line being read, moves to the
    /// newest one still shown rather than leaving the detail pane blank.
    /// </summary>
    private void OnShowChanged(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;

        _view.Refresh();

        if (Entries.SelectedItem is null && _view.Cast<object>().LastOrDefault() is { } newest)
        {
            Entries.SelectedItem = newest;
            Entries.ScrollIntoView(newest);
        }
    }

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
        bool listing = SuggestPopup.IsOpen;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            // Handled whatever happens, so Tab never walks focus off the prompt.
            case Key.Tab:
                e.Handled = true;
                if (listing && shift) MoveSuggestion(-1);
                else if (listing) AcceptSuggestion();
                else if (!shift) Complete();
                return;

            case Key.Up when listing:
                MoveSuggestion(-1);
                e.Handled = true;
                return;

            case Key.Down when listing:
                MoveSuggestion(+1);
                e.Handled = true;
                return;

            case Key.Enter when listing:
                AcceptSuggestion();
                e.Handled = true;
                return;

            case Key.Escape:
                if (listing) CloseSuggestions();
                else Input.Clear();
                e.Handled = true;
                return;

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

    // ----- Tab completion ----------------------------------------------------

    /// <summary>The completion the open suggestion list was made from.</summary>
    private ConsoleCompletion? _completion;

    /// <summary>Set while this window is changing the prompt itself, so its own edit does not re-open the list.</summary>
    private bool _editing;

    private ConsoleCompletion Completion() =>
        AgentConsoleCompletion.Complete(Input.Text, Input.CaretIndex, _vm.ConsoleNames());

    /// <summary>
    /// Tab with no list open: one answer goes straight in; several fill in
    /// whatever they share and then open the list; none does nothing.
    /// </summary>
    private void Complete()
    {
        ConsoleCompletion completion = Completion();

        switch (completion.Candidates.Count)
        {
            case 0:
                return;

            case 1:
                Put(completion.Accept(Input.Text, completion.Candidates[0]));
                return;

            default:
                if (completion.SharedStart.Length > 0)
                {
                    Put(completion.AcceptShared(Input.Text));
                    completion = Completion();
                }

                ShowSuggestions(completion);
                return;
        }
    }

    private void ShowSuggestions(ConsoleCompletion completion)
    {
        if (completion.Candidates.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        _completion = completion;
        Suggestions.ItemsSource = completion.Candidates;
        Suggestions.SelectedIndex = 0;
        Suggestions.ScrollIntoView(Suggestions.SelectedItem);

        // Under the word being completed rather than at the prompt's left edge.
        Rect at = Input.GetRectFromCharacterIndex(Math.Min(completion.Start, Input.Text.Length));
        SuggestPopup.HorizontalOffset = double.IsFinite(at.Left) ? at.Left : 0;
        SuggestPopup.IsOpen = true;
    }

    private void CloseSuggestions()
    {
        SuggestPopup.IsOpen = false;
        _completion = null;
    }

    private void MoveSuggestion(int by)
    {
        int count = Suggestions.Items.Count;
        if (count == 0) return;

        Suggestions.SelectedIndex = ((Suggestions.SelectedIndex + by) % count + count) % count;
        Suggestions.ScrollIntoView(Suggestions.SelectedItem);
    }

    private void AcceptSuggestion()
    {
        if (_completion is { } completion && Suggestions.SelectedItem is ConsoleSuggestion choice)
            Put(completion.Accept(Input.Text, choice));

        CloseSuggestions();
    }

    private void OnSuggestionPicked(object sender, MouseButtonEventArgs e)
    {
        if (!OnAnItem(Suggestions, e) || Suggestions.SelectedItem is null) return;

        AcceptSuggestion();
        Input.Focus();
    }

    private void Put((string Line, int Caret) result)
    {
        _editing = true;
        Input.Text = result.Line;
        Input.CaretIndex = result.Caret;
        _editing = false;

        ShowHint();
    }

    /// <summary>
    /// Typing narrows an open list, and closes it once nothing is left.
    ///
    /// Text replaced from code arrives with the caret at the start and moves it
    /// a moment later, and a list judged from the start of the line would be
    /// every command — so that moment is skipped, and the caret moving does the
    /// refresh instead.
    /// </summary>
    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (Input.CaretIndex > 0 || Input.Text.Length == 0) RefreshSuggestions();
        ShowHint();
    }

    private void OnInputCaretMoved(object sender, RoutedEventArgs e)
    {
        RefreshSuggestions();
        ShowHint();
    }

    private void RefreshSuggestions()
    {
        if (!_editing && SuggestPopup.IsOpen) ShowSuggestions(Completion());
    }

    /// <summary>
    /// The rest of the command's usage, faintly after the typing. Only with
    /// the caret at the end of a line that fits the box: anywhere else, a hint
    /// drawn after the text would sit on top of it.
    /// </summary>
    private void ShowHint()
    {
        string text = Input.Text;
        string hint = "";

        if (text.Length > 0 && Input.CaretIndex == text.Length && Input.HorizontalOffset < 1)
        {
            // The hint needs only the command list, not the names, so this
            // does not gather every channel on every keystroke.
            hint = AgentConsoleCompletion.Complete(text, text.Length, ConsoleNames.None).Hint;
            if (hint.Length > 0 && !text.EndsWith(' ')) hint = " " + hint;
        }

        HintTyped.Text = hint.Length > 0 ? text : "";
        HintRest.Text = hint;
    }

    // ----- the commands list -------------------------------------------------

    /// <summary>One row of the commands list.</summary>
    public sealed record CommandRow(string Name, string Usage, string Summary, bool IsWrite, string Group, string GroupNote);

    private void OnCommandsClick(object sender, RoutedEventArgs e)
    {
        if (CommandList.ItemsSource is null)
        {
            var rows = AgentConsoleCatalog.Verbs.Select(v => new CommandRow(
                v.Name, v.Usage, v.Summary, v.IsWrite, v.Group,
                AgentConsoleCatalog.Groups.First(g => g.Name == v.Group).Note)).ToList();

            ListCollectionView view = new(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CommandRow.Group)));
            CommandList.ItemsSource = view;
        }

        CommandList.SelectedItem = null;
        CommandsPopup.IsOpen = true;
        CommandList.Focus();
    }

    private void OnCommandPicked(object sender, MouseButtonEventArgs e)
    {
        if (OnAnItem(CommandList, e)) PickCommand();
    }

    /// <summary>Whether a click landed on a row, rather than on a heading or the scroll bar.</summary>
    private static bool OnAnItem(ItemsControl list, MouseButtonEventArgs e) =>
        e.OriginalSource is DependencyObject hit && ItemsControl.ContainerFromElement(list, hit) is ListBoxItem;

    private void OnCommandListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        PickCommand();
        e.Handled = true;
    }

    /// <summary>Puts the command in the prompt, ready for its arguments. Never sends it.</summary>
    private void PickCommand()
    {
        if (CommandList.SelectedItem is not CommandRow row) return;

        CommandsPopup.IsOpen = false;
        Put((row.Name + " ", row.Name.Length + 1));
    }

    private void OnCommandsClosed(object? sender, EventArgs e) => Input.Focus();

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
