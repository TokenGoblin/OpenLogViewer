using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The agent console's prompt: Tab completion, the suggestion list, the usage
/// hint and the commands list, driven by key presses the way a person would.
///
/// A window needs a UI thread, so every test here runs on one shared STA
/// thread. Deliberately with no Application: every view model applies its
/// theme to Application.Current when it is made, and with one in the process,
/// the other tests — running in parallel on their own threads — would write
/// into its resources while this thread read them. The window loads without
/// the app's styles, which is all behaviour tests need.
/// </summary>
public class ConsoleWindowTests
{
    private static readonly Lazy<Dispatcher> Ui = new(StartUi);

    private static Dispatcher StartUi()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        return dispatcher!;
    }

    private static void OnUi(Action<ConsoleWindow, MainViewModel> test, string? log = null)
    {
        Ui.Value.Invoke(() =>
        {
            using var harness = new ViewModelHarness();
            MainViewModel vm = harness.NewViewModel();
            if (log is "typical") vm.Load(harness.WriteTypicalLog());

            var window = new ConsoleWindow(vm) { ShowActivated = false };
            window.Show();
            window.UpdateLayout();

            try
            {
                test(window, vm);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Type(ConsoleWindow window, string text)
    {
        window.Input.Text = text;
        window.Input.CaretIndex = text.Length;
    }

    private static void Press(ConsoleWindow window, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window.Input), 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

        window.Input.RaiseEvent(args);
    }

    private static string[] Listed(ConsoleWindow window) =>
        [.. window.Suggestions.Items.Cast<ConsoleSuggestion>().Select(s => s.Text)];

    [Fact]
    public void TabWithOneAnswerPutsItInWithASpace()
    {
        OnUi((window, _) =>
        {
            Type(window, "insi");
            Press(window, Key.Tab);

            Assert.Equal("insights ", window.Input.Text);
            Assert.Equal(window.Input.Text.Length, window.Input.CaretIndex);
            Assert.False(window.SuggestPopup.IsOpen);
        });
    }

    [Fact]
    public void TabCompletesChannelsFromTheLogInHand()
    {
        OnUi((window, _) =>
        {
            Type(window, "values af");
            Press(window, Key.Tab);

            // "AFR" and "AFR Target" share "AFR", which goes in with the quote one
            // of them will need already open; then the list opens.
            Assert.Equal("values \"AFR", window.Input.Text);
            Assert.True(window.SuggestPopup.IsOpen);
            Assert.Equal(["AFR", "AFR Target"], Listed(window));
        }, log: "typical");
    }

    [Fact]
    public void TheListIsWalkedWithTheArrowsAndAcceptedWithEnterWithoutRunningTheLine()
    {
        OnUi((window, _) =>
        {
            int before = window.Lines.Count;

            Type(window, "values AFR");
            Press(window, Key.Tab);
            Press(window, Key.Down);
            Press(window, Key.Enter);

            Assert.Equal("values \"AFR Target\" ", window.Input.Text);
            Assert.False(window.SuggestPopup.IsOpen);
            Assert.Equal(before, window.Lines.Count);
        }, log: "typical");
    }

    [Fact]
    public void TypingNarrowsTheListAndEscapeClosesIt()
    {
        OnUi((window, _) =>
        {
            Type(window, "t");
            Press(window, Key.Tab);
            Assert.True(window.SuggestPopup.IsOpen);
            Assert.Contains("tune", Listed(window));

            Type(window, "tu");
            Assert.Equal(["tune"], Listed(window));

            Press(window, Key.Escape);
            Assert.False(window.SuggestPopup.IsOpen);
            Assert.Equal("tu", window.Input.Text);

            Press(window, Key.Escape);
            Assert.Equal("", window.Input.Text);
        });
    }

    [Fact]
    public void ShiftTabWithNoListDoesNothingAndTabNeverLeavesThePrompt()
    {
        OnUi((window, _) =>
        {
            Type(window, "zzz");
            Press(window, Key.Tab);

            Assert.Equal("zzz", window.Input.Text);
            Assert.False(window.SuggestPopup.IsOpen);
        });
    }

    [Fact]
    public void TheHintSaysWhatTheCommandStillTakes()
    {
        OnUi((window, _) =>
        {
            Type(window, "cell veTable1 4 ");
            Assert.Equal("<row> <value> [why…] [--dangerous]", window.HintRest.Text);
            Assert.Equal("cell veTable1 4 ", window.HintTyped.Text);

            Type(window, "cell");
            Assert.Equal("", window.HintRest.Text);

            Type(window, "cell veTable1");
            window.Input.CaretIndex = 2;
            Assert.Equal("", window.HintRest.Text);
        });
    }

    [Fact]
    public void PickingACommandPutsItInThePromptAndSendsNothing()
    {
        OnUi((window, _) =>
        {
            int before = window.Lines.Count;

            window.CommandsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(window.CommandsPopup.IsOpen);

            var rows = window.CommandList.Items.Cast<ConsoleWindow.CommandRow>().ToList();
            Assert.Equal(AgentConsoleCatalog.Verbs.Count, rows.Count);

            window.CommandList.SelectedItem = rows.Single(r => r.Name == "set");
            window.CommandList.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(window.CommandList), 0, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });

            Assert.False(window.CommandsPopup.IsOpen);
            Assert.Equal("set ", window.Input.Text);
            Assert.Equal(before, window.Lines.Count);
        });
    }

    [Fact]
    public void TheCommandsListMarksTheWrites()
    {
        OnUi((window, _) =>
        {
            window.CommandsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            string[] writes =
            [
                .. window.CommandList.Items.Cast<ConsoleWindow.CommandRow>().Where(r => r.IsWrite).Select(r => r.Name),
            ];

            Assert.Equal(["set", "cell"], writes);
        });
    }

    [Fact]
    public void AFilterThatHidesTheLineBeingReadMovesToTheNewestOneShown()
    {
        OnUi((window, vm) =>
        {
            vm.AgentTranscript.Record(DateTime.UtcNow, "mcp", "olv_set_setting", "POST", "/tune/set",
                "{}", 409, "{\"error\":\"refused\"}", 1);
            vm.AgentTranscript.Record(DateTime.UtcNow, "mcp", "olv_state", "GET", "/state", "", 200, "{}", 1);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);

            window.Entries.SelectedItem = window.Lines.Last();
            Assert.Contains("/state", window.Detail.Text, StringComparison.Ordinal);

            window.ShowWrites.IsChecked = true;

            Assert.NotNull(window.Entries.SelectedItem);
            Assert.Contains("/tune/set", window.Detail.Text, StringComparison.Ordinal);
        });
    }
}
