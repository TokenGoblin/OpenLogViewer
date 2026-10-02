using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The console's command list, and what Tab offers from it. The list is the
/// one place a command is defined, so the first tests here hold the parser,
/// the help and the server's own route list to it.
/// </summary>
public class AgentConsoleCompletionTests
{
    private static readonly ConsoleNames Names = new()
    {
        Tables = [new("VE Table"), new("Ignition Advance Table"), new("Lambda Table"), new("veTable2")],
        Channels = [new("RPM", "rpm"), new("rpmDOT", "rpm/s"), new("AFR", "AFR")],
        Settings = [new("reqFuel", "ms, 0–25.5"), new("rpmLimit", "rpm")],
        Versions = [new("v1"), new("v2"), new("v10")],
    };

    private static ConsoleCompletion At(string line) =>
        AgentConsoleCompletion.Complete(line, line.Length, Names);

    private static string[] Offered(string line) => [.. At(line).Candidates.Select(c => c.Text)];

    // ----- the catalog --------------------------------------------------------

    [Fact]
    public void EveryCommandInTheCatalogParses()
    {
        // A verb in the catalog with no case in the parser throws, and this
        // finds it before a person typing does.
        foreach (ConsoleVerb verb in AgentConsoleCatalog.Verbs)
        {
            string line = string.Join(' ', [verb.Name, .. verb.Arguments.Where(a => !a.Optional).Select(Sample)]);

            AgentConsoleCommand command = AgentConsoleCommand.Parse(line);

            Assert.True(command.Error.Length == 0, $"{line}: {command.Error}");
            Assert.True(command.IsRequest || command.Local.Length > 0, line);
        }
    }

    [Fact]
    public void EveryAliasParsesAsItsCommand()
    {
        foreach (ConsoleVerb verb in AgentConsoleCatalog.Verbs)
        foreach (string alias in verb.OtherNames)
            Assert.Equal(verb.Name, AgentConsoleCommand.Parse(alias).Local);
    }

    [Fact]
    public void OnlyTheWritingCommandsAreMarkedAsWrites()
    {
        // The button colours these, and completion offers --dangerous on them.
        // Both read IsWrite, so it has to match what the commands send.
        foreach (ConsoleVerb verb in AgentConsoleCatalog.Verbs)
        {
            string line = string.Join(' ', [verb.Name, .. verb.Arguments.Where(a => !a.Optional).Select(Sample)]);
            string target = AgentConsoleCommand.Parse(line).Target;

            Assert.Equal(target is "/tune/set" or "/table/set", verb.IsWrite);
        }
    }

    [Fact]
    public void TheHelpListsEveryCommand()
    {
        string help = AgentConsoleCommand.Help;

        foreach (ConsoleVerb verb in AgentConsoleCatalog.Verbs) Assert.Contains(verb.Usage, help, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageReadsTheWayItIsTyped()
    {
        Assert.Equal("cell <table> <column> <row> <value> [why…] [--dangerous]", AgentConsoleCatalog.Find("cell")!.Usage);
        Assert.Equal("channels [raw]", AgentConsoleCatalog.Find("channels")!.Usage);
        Assert.Equal("api [on|off]", AgentConsoleCatalog.Find("api")!.Usage);
        Assert.Equal("values <channel> [seconds]", AgentConsoleCatalog.Find("values")!.Usage);
    }

    private static string Sample(ConsoleArgument argument) => argument.Kind switch
    {
        ConsoleArgumentKind.Keyword => argument.Words[0],
        ConsoleArgumentKind.Route => "/state",
        ConsoleArgumentKind.Number or ConsoleArgumentKind.Whole => "1",
        ConsoleArgumentKind.Version => "v1",
        ConsoleArgumentKind.Json => "{}",
        _ => "x",
    };

    // ----- what Tab offers ----------------------------------------------------

    [Fact]
    public void TheFirstWordCompletesToCommands()
    {
        Assert.Equal(["tables", "table"], Offered("tab"));
        Assert.Equal(["tune"], Offered("TU"));
        Assert.Equal(AgentConsoleCatalog.Verbs.Count, Offered("").Length);
    }

    [Fact]
    public void ACommandsArgumentCompletesFromTheRightList()
    {
        Assert.Equal(["VE Table", "veTable2"], Offered("table ve"));
        Assert.Equal(["RPM", "rpmDOT"], Offered("values rp"));
        Assert.Equal(["reqFuel"], Offered("set re"));
        Assert.Equal(["v1", "v10"], Offered("versions v1"));
        Assert.Equal(["v2"], Offered("versions v1 v2"));
        Assert.Equal(["raw"], Offered("channels "));
        Assert.Equal(["on", "off"], Offered("api o"));
        Assert.Contains("/tune/full", Offered("get /tune"));
    }

    [Fact]
    public void WhenNothingStartsWithIt_ItIsFoundAnywhereInTheName()
    {
        Assert.Equal(["Ignition Advance Table"], Offered("table adv"));
    }

    [Fact]
    public void ANumberHasNothingToOfferButSaysWhatComesNext()
    {
        ConsoleCompletion completion = At("cell veTable2 4 ");

        Assert.Empty(completion.Candidates);
        Assert.Equal("<row> <value> [why…] [--dangerous]", completion.Hint);
    }

    [Fact]
    public void DangerousIsOfferedOnlyOnWritesAndOnlyOnce()
    {
        Assert.Equal(["--dangerous"], Offered("set reqFuel 9 -"));
        Assert.Empty(Offered("set reqFuel 9 --dangerous why -"));
        Assert.Empty(Offered("table -"));
    }

    [Fact]
    public void AFlagDoesNotCountAsAnArgument()
    {
        Assert.Equal(["VE Table", "veTable2"], Offered("cell --dangerous ve"));
    }

    [Fact]
    public void AnUnknownCommandOffersNothing()
    {
        Assert.Empty(Offered("frobnicate x"));
    }

    // ----- putting it in ------------------------------------------------------

    [Fact]
    public void ANameWithSpacesGoesInQuotedWithASpaceAfter()
    {
        const string line = "table VE T";
        ConsoleCompletion completion = AgentConsoleCompletion.Complete(line, line.Length, Names);

        // "VE T" is two words until the first is quoted, so "T" is what is being completed.
        Assert.Empty(completion.Candidates);

        const string quoted = "table \"VE T";
        completion = AgentConsoleCompletion.Complete(quoted, quoted.Length, Names);

        ConsoleSuggestion only = Assert.Single(completion.Candidates);
        (string result, int caret) = completion.Accept(quoted, only);

        Assert.Equal("table \"VE Table\" ", result);
        Assert.Equal(result.Length, caret);
        Assert.Equal("VE Table", AgentConsoleCommand.Parse(result).Target.Split("name=")[1].Replace("%20", " "));
    }

    [Fact]
    public void SeveralAnswersFillInWhatTheyShare()
    {
        const string line = "values r";
        ConsoleCompletion completion = AgentConsoleCompletion.Complete(line, line.Length, Names);

        Assert.Equal("RPM", completion.SharedStart);
        Assert.Equal(("values RPM", 10), completion.AcceptShared(line));
    }

    [Fact]
    public void ASharedStartOfQuotedNamesOpensTheQuote()
    {
        var names = Names with { Tables = [new("Fuel Table 1"), new("Fuel Table 2")] };
        const string line = "table f";

        ConsoleCompletion completion = AgentConsoleCompletion.Complete(line, line.Length, names);

        Assert.Equal("table \"Fuel Table ", completion.AcceptShared(line).Line);
    }

    [Fact]
    public void AFallbackMatchHasNoSharedStartToFillIn()
    {
        ConsoleCompletion completion = At("table able");

        Assert.True(completion.Candidates.Count > 1);
        Assert.Equal("", completion.SharedStart);
    }

    [Fact]
    public void CompletingMidLineKeepsWhatFollows()
    {
        const string line = "set req 9.1";
        ConsoleCompletion completion = AgentConsoleCompletion.Complete(line, 7, Names);

        (string result, int caret) = completion.Accept(line, Assert.Single(completion.Candidates));

        Assert.Equal("set reqFuel 9.1", result);
        Assert.Equal(12, caret);
    }
}
