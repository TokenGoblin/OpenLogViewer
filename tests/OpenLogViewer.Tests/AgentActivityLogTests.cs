using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class AgentActivityLogTests
{
    // ----- the ring buffer itself --------------------------------------------

    [Fact]
    public void OldEventsAreEvictedOnceCapacityIsReached()
    {
        var log = new AgentActivityLog(capacity: 3);

        for (int i = 0; i < 5; i++) log.Begin($"/route{i}").Dispose();

        // Two events per Begin/Dispose pair, so five calls leave ten events —
        // only the last three of those survive.
        IReadOnlyList<AgentActivityEvent> recent = log.Recent(10);
        Assert.Equal(3, recent.Count);
    }

    [Fact]
    public void RecentAsksForMoreThanExistReturnsWhatThereIs()
    {
        var log = new AgentActivityLog();
        log.Begin("/state").Dispose();

        // One Begin/Dispose pair is two events: the start and the end.
        Assert.Equal(2, log.Recent(50).Count);
    }

    [Fact]
    public void AnEmptyLogHasNothingToOffer()
    {
        var log = new AgentActivityLog();

        Assert.Empty(log.Recent(10));
    }

    // ----- begin/end and InFlight ----------------------------------------------

    [Fact]
    public void BeginRecordsAnInFlightEventImmediately()
    {
        var log = new AgentActivityLog();

        using IDisposable handle = log.Begin("/state");

        AgentActivityEvent started = Assert.Single(log.Recent(10));
        Assert.Equal("/state", started.Route);
        Assert.True(started.InFlight);
    }

    [Fact]
    public void DisposingRecordsAMatchingEventThatIsNoLongerInFlight()
    {
        var log = new AgentActivityLog();

        log.Begin("/state").Dispose();

        IReadOnlyList<AgentActivityEvent> events = log.Recent(10);
        Assert.Equal(2, events.Count);

        AgentActivityEvent ended = events[0];   // newest first
        AgentActivityEvent started = events[1];

        Assert.False(ended.InFlight);
        Assert.True(started.InFlight);
        Assert.Equal(started.Route, ended.Route);
        Assert.Equal(started.Kind, ended.Kind);
    }

    [Fact]
    public void DisposingTwiceRecordsTheEndOnlyOnce()
    {
        var log = new AgentActivityLog();

        IDisposable handle = log.Begin("/state");
        handle.Dispose();
        handle.Dispose();

        Assert.Equal(2, log.Recent(10).Count);
    }

    // ----- read vs write classification -----------------------------------------

    [Theory]
    [InlineData("/tune/set")]
    [InlineData("/table/set")]
    public void RoutesThatReachTheEcuAreClassifiedAsWrites(string route)
    {
        var log = new AgentActivityLog();

        using IDisposable handle = log.Begin(route);

        Assert.Equal(AgentActivityKind.Write, log.Recent(1)[0].Kind);
    }

    [Theory]
    [InlineData("/state")]
    [InlineData("/channels")]
    [InlineData("/insights")]
    [InlineData("/tune")]
    [InlineData("/tables")]
    [InlineData("/table")]
    [InlineData("/wire/health")]
    [InlineData("/wire/events")]
    [InlineData("/project")]
    [InlineData("/project/record")]
    [InlineData("/project/keep")]
    [InlineData("/project/fix")]
    public void EveryOtherKnownRouteIsClassifiedAsARead(string route)
    {
        var log = new AgentActivityLog();

        using IDisposable handle = log.Begin(route);

        Assert.Equal(AgentActivityKind.Read, log.Recent(1)[0].Kind);
    }

    [Fact]
    public void AnUnrecognisedRouteIsTreatedAsAReadRatherThanThrowing()
    {
        var log = new AgentActivityLog();

        using IDisposable handle = log.Begin("/some/future/route");

        AgentActivityEvent evt = log.Recent(1)[0];
        Assert.Equal(AgentActivityKind.Read, evt.Kind);
        Assert.Equal("/some/future/route", evt.Detail);
    }

    [Fact]
    public void KnownRoutesCarryAHumanPhraseRatherThanTheBarePath()
    {
        var log = new AgentActivityLog();

        using IDisposable handle = log.Begin("/tune/set");

        Assert.Equal("writing to the ECU", log.Recent(1)[0].Detail);
    }

    // ----- notifying a subscriber ------------------------------------------------

    [Fact]
    public void ChangedFiresOnceForTheStartAndOnceForTheEnd()
    {
        var log = new AgentActivityLog();
        var seen = new List<bool>();

        log.Changed += e => seen.Add(e.InFlight);

        log.Begin("/state").Dispose();

        Assert.Equal([true, false], seen);
    }
}
