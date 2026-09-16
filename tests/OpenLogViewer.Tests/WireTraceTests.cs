using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class WireTraceTests
{
    // ----- the ring buffer itself --------------------------------------------

    [Fact]
    public void OldEventsAreEvictedOnceCapacityIsReached()
    {
        var trace = new WireTrace(capacity: 3);

        for (int i = 0; i < 5; i++)
            trace.Record($"call {i}", 0, TimeSpan.Zero, WireOutcome.Ok);

        IReadOnlyList<WireEvent> recent = trace.Recent(10);

        // Only the last three survive, newest first.
        Assert.Equal(3, recent.Count);
        Assert.Equal("call 4", recent[0].Context);
        Assert.Equal("call 3", recent[1].Context);
        Assert.Equal("call 2", recent[2].Context);
    }

    [Fact]
    public void RecentAsksForMoreThanExistReturnsWhatThereIs()
    {
        var trace = new WireTrace();
        trace.Record("only one", 0, TimeSpan.Zero, WireOutcome.Ok);

        Assert.Single(trace.Recent(50));
    }

    [Fact]
    public void EmptyTraceHasNothingToOfferAndACleanBillOfHealth()
    {
        var trace = new WireTrace();

        Assert.Empty(trace.Recent(10));

        WireHealthSnapshot health = trace.Health();
        Assert.Equal(0, health.Sampled);
        Assert.Equal(1.0, health.SuccessRate);
        Assert.Null(health.SinceLastSuccess);
    }

    // ----- health rollup ------------------------------------------------------

    [Fact]
    public void HealthCountsFailuresByKind()
    {
        var trace = new WireTrace();

        trace.Record("a", 0, TimeSpan.FromMilliseconds(5), WireOutcome.Ok);
        trace.Record("b", 0, TimeSpan.FromMilliseconds(5), WireOutcome.Failure, WireFailureKind.ChecksumMismatch);
        trace.Record("c", 0, TimeSpan.FromMilliseconds(5), WireOutcome.Failure, WireFailureKind.Timeout);
        trace.Record("d", 0, TimeSpan.FromMilliseconds(5), WireOutcome.Failure, WireFailureKind.ChecksumMismatch);
        trace.Record("e", 0, TimeSpan.FromMilliseconds(5), WireOutcome.Ok);

        WireHealthSnapshot health = trace.Health();

        Assert.Equal(5, health.Sampled);
        Assert.Equal(3, health.Failures);
        Assert.Equal(0.4, health.SuccessRate, precision: 5);
        Assert.Equal(2, health.FailuresByKind[nameof(WireFailureKind.ChecksumMismatch)]);
        Assert.Equal(1, health.FailuresByKind[nameof(WireFailureKind.Timeout)]);
        Assert.NotNull(health.SinceLastSuccess);
    }

    [Fact]
    public void HealthLooksOnlyAtTheRequestedWindow()
    {
        var trace = new WireTrace();

        for (int i = 0; i < 10; i++)
            trace.Record($"old {i}", 0, TimeSpan.Zero, WireOutcome.Failure, WireFailureKind.Timeout);

        trace.Record("recent", 0, TimeSpan.Zero, WireOutcome.Ok);

        WireHealthSnapshot health = trace.Health(window: 1);

        Assert.Equal(1, health.Sampled);
        Assert.Equal(0, health.Failures);
        Assert.Equal(1.0, health.SuccessRate);
    }

    // ----- origin -------------------------------------------------------------

    [Fact]
    public void EventsRecordedOutsideAnyScopeAreAttributedToAHuman()
    {
        var trace = new WireTrace();
        trace.Record("unscoped", 0, TimeSpan.Zero, WireOutcome.Ok);

        Assert.Equal(WireOrigin.Human, trace.Recent(1)[0].Origin);
    }

    [Fact]
    public void EventsRecordedInsideAnAgentScopeAreAttributedToTheAgent()
    {
        var trace = new WireTrace();

        using (WireOriginScope.Enter(WireOrigin.Agent))
            trace.Record("agent call", 0, TimeSpan.Zero, WireOutcome.Ok);

        trace.Record("human call", 0, TimeSpan.Zero, WireOutcome.Ok);

        IReadOnlyList<WireEvent> recent = trace.Recent(2);
        Assert.Equal(WireOrigin.Human, recent[0].Origin);   // "human call", newest first
        Assert.Equal(WireOrigin.Agent, recent[1].Origin);   // "agent call"
    }

    [Fact]
    public void LeavingTheScopeRestoresWhatCameBefore()
    {
        using (WireOriginScope.Enter(WireOrigin.Agent))
        {
            using (WireOriginScope.Enter(WireOrigin.Human))
            {
                Assert.Equal(WireOrigin.Human, WireOriginScope.Current);
            }

            Assert.Equal(WireOrigin.Agent, WireOriginScope.Current);
        }

        Assert.Equal(WireOrigin.Human, WireOriginScope.Current);
    }

    // ----- wired into a real connection ---------------------------------------

    [Fact]
    public void ANewConnectionRecordsNothingUnlessGivenATrace()
    {
        var transport = new FakeTransport();
        transport.Enqueue(EcuProtocolTestReplies.Signature("MS3"));

        using var connection = new EcuConnection(transport);

        Assert.Null(connection.Trace);
        connection.ReadSignature();   // must not throw for want of a trace
    }

    [Fact]
    public void ASuccessfulReadIsRecordedAgainstItsContext()
    {
        var transport = new FakeTransport();
        transport.Enqueue(EcuProtocolTestReplies.Signature("MS3"));

        var trace = new WireTrace();
        using var connection = new EcuConnection(transport) { Trace = trace };

        connection.ReadSignature();

        WireEvent evt = Assert.Single(trace.Recent(10));
        Assert.Equal("read signature", evt.Context);
        Assert.Equal(WireOutcome.Ok, evt.Outcome);
        Assert.Equal(0, evt.Attempt);
    }

    [Fact]
    public void ARetriedReadRecordsTheFailureThenTheSuccess()
    {
        byte[] bad = EcuProtocolTestReplies.Signature("MS3");
        bad[^1] ^= 0xFF;

        var transport = new FakeTransport();
        transport.Enqueue(bad, EcuProtocolTestReplies.Signature("MS3"));

        var trace = new WireTrace();
        using var connection = new EcuConnection(transport) { Trace = trace };

        connection.ReadSignature();

        IReadOnlyList<WireEvent> events = trace.Recent(10);
        Assert.Equal(2, events.Count);

        WireEvent success = events[0];   // newest first
        WireEvent failure = events[1];

        Assert.Equal(WireOutcome.Ok, success.Outcome);
        Assert.Equal(1, success.Attempt);

        Assert.Equal(WireOutcome.Failure, failure.Outcome);
        Assert.Equal(WireFailureKind.ChecksumMismatch, failure.FailureKind);
        Assert.Equal(0, failure.Attempt);
    }

    [Fact]
    public void AgentOriginatedWritesAreTaggedAsSuchInTheTrace()
    {
        var transport = new FakeTransport();
        transport.Enqueue(EcuProtocolTestReplies.Signature("MS3"));

        var trace = new WireTrace();
        using var connection = new EcuConnection(transport) { Trace = trace };

        using (WireOriginScope.Enter(WireOrigin.Agent))
            connection.ReadSignature();

        Assert.Equal(WireOrigin.Agent, trace.Recent(1)[0].Origin);
    }
}

/// <summary>Reply bytes shared with <see cref="EcuProtocolTests"/>'s own helper shape.</summary>
internal static class EcuProtocolTestReplies
{
    public static byte[] Signature(string text)
    {
        byte[] body = [0x00, .. System.Text.Encoding.ASCII.GetBytes(text)];
        var framed = new List<byte> { (byte)(body.Length >> 8), (byte)(body.Length & 0xFF) };
        framed.AddRange(body);

        uint crc = MsProtocol.Crc32(body);
        framed.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);

        return [.. framed];
    }
}
