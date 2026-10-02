using System.Text.Json.Nodes;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// What the console makes of a typed line. Every command has to come out as a
/// request an agent could have made — the console has no route of its own.
/// </summary>
public class AgentConsoleCommandTests
{
    [Theory]
    [InlineData("state", "GET", "/state")]
    [InlineData("  STATE  ", "GET", "/state")]
    [InlineData("channels", "GET", "/channels")]
    [InlineData("channels raw", "GET", "/channels?raw=true")]
    [InlineData("tune", "GET", "/tune")]
    [InlineData("tune full", "GET", "/tune/full")]
    [InlineData("wire", "GET", "/wire/events")]
    [InlineData("wire 20", "GET", "/wire/events?count=20")]
    [InlineData("health", "GET", "/wire/health")]
    [InlineData("values RPM", "GET", "/values?channel=RPM")]
    [InlineData("values RPM 2.5", "GET", "/values?channel=RPM&seconds=2.5")]
    [InlineData("table \"VE Table 1\"", "GET", "/table?name=VE%20Table%201")]
    [InlineData("log", "GET", "/log/full?seconds=0")]
    [InlineData("log 30 4", "GET", "/log/full?seconds=30&decimate=4")]
    [InlineData("get /values?channel=AFR", "GET", "/values?channel=AFR")]
    [InlineData("get state", "GET", "/state")]
    public void AReadBecomesTheRouteItNames(string line, string method, string target)
    {
        AgentConsoleCommand command = AgentConsoleCommand.Parse(line);

        Assert.Equal("", command.Error);
        Assert.Equal(method, command.Method);
        Assert.Equal(target, command.Target);
    }

    [Fact]
    public void ASetCarriesItsReasonAndTheDangerFlag()
    {
        AgentConsoleCommand command = AgentConsoleCommand.Parse("set reqFuel 9.1 leaner at idle --dangerous");

        Assert.Equal("POST", command.Method);
        Assert.Equal("/tune/set", command.Target);

        JsonNode body = JsonNode.Parse(command.Body)!;
        Assert.Equal("reqFuel", body["name"]!.GetValue<string>());
        Assert.Equal(9.1, body["value"]!.GetValue<double>());
        Assert.Equal("leaner at idle", body["rationale"]!.GetValue<string>());
        Assert.True(body["confirmDangerous"]!.GetValue<bool>());
    }

    [Fact]
    public void ASetWithNoReasonStillGivesOneSoTheGuardDoesNotJustRefuseIt()
    {
        JsonNode body = JsonNode.Parse(AgentConsoleCommand.Parse("set reqFuel 9.1").Body)!;

        Assert.Equal(AgentConsoleCommand.DefaultRationale, body["rationale"]!.GetValue<string>());
        Assert.False(body["confirmDangerous"]!.GetValue<bool>());
    }

    [Fact]
    public void ACellNamesItsTablePositionAndValue()
    {
        AgentConsoleCommand command = AgentConsoleCommand.Parse("cell veTable1 4 6 72");

        Assert.Equal("/table/set", command.Target);
        JsonNode body = JsonNode.Parse(command.Body)!;
        Assert.Equal("veTable1", body["table"]!.GetValue<string>());
        Assert.Equal(4, body["column"]!.GetValue<int>());
        Assert.Equal(6, body["row"]!.GetValue<int>());
        Assert.Equal(72, body["value"]!.GetValue<double>());
    }

    [Fact]
    public void APostKeepsItsJsonExactlyAsTyped()
    {
        const string json = """{"settings":[{"name":"note \"x\"","value":1}]}""";

        AgentConsoleCommand command = AgentConsoleCommand.Parse("post /tune/propose " + json);

        Assert.Equal("", command.Error);
        Assert.Equal("POST", command.Method);
        Assert.Equal("/tune/propose", command.Target);
        Assert.Equal(json, command.Body);
    }

    [Theory]
    [InlineData("post /tune/propose {not json")]
    [InlineData("post")]
    [InlineData("set reqFuel lots")]
    [InlineData("set reqFuel")]
    [InlineData("set reqFuel NaN")]
    [InlineData("cell veTable1 four 6 72")]
    [InlineData("values")]
    [InlineData("table")]
    [InlineData("wire many")]
    [InlineData("versions v3")]
    [InlineData("table \"unclosed")]
    [InlineData("frobnicate")]
    public void AMalformedLineSaysWhyAndSendsNothing(string line)
    {
        AgentConsoleCommand command = AgentConsoleCommand.Parse(line);

        Assert.NotEqual("", command.Error);
        Assert.False(command.IsRequest);
    }

    [Theory]
    [InlineData("help", "help", "")]
    [InlineData("?", "help", "")]
    [InlineData("cls", "clear", "")]
    [InlineData("api On", "api", "on")]
    [InlineData("show writes", "show", "writes")]
    public void TheConsolesOwnCommandsNeverReachTheApi(string line, string local, string argument)
    {
        AgentConsoleCommand command = AgentConsoleCommand.Parse(line);

        Assert.False(command.IsRequest);
        Assert.Equal(local, command.Local);
        Assert.Equal(argument, command.Argument);
    }

    [Fact]
    public void ABlankLineIsNothingAtAll()
    {
        AgentConsoleCommand command = AgentConsoleCommand.Parse("   ");

        Assert.False(command.IsRequest);
        Assert.Equal("", command.Local);
        Assert.Equal("", command.Error);
    }
}
