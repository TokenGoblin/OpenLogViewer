using System.Collections.Concurrent;

namespace OpenLogViewer.Core;

/// <summary>
/// What one agent-API request is doing, in the sense a person watching for it
/// cares about: reading something back, changing something in the ECU's
/// working memory, or writing a file for the person to pick up later (the
/// staging routes). This is not <see cref="WireFailureKind"/>'s axis — it
/// describes intent before a request is even handled, not how it went.
/// </summary>
public enum AgentActivityKind
{
    Read,
    Write,
    Stage,
}

/// <summary>
/// One HTTP or WebSocket request against the agent API, as it started or as it
/// finished — see <see cref="AgentActivityLog"/> for why both are recorded.
/// </summary>
public sealed record AgentActivityEvent(
    long Sequence,
    DateTime At,
    string Route,
    AgentActivityKind Kind,
    string Detail,
    bool InFlight);

/// <summary>
/// Every request an agent has made against this application itself, as
/// distinct from <see cref="WireTrace"/>, which is what the application then
/// does to the ECU on the agent's behalf. With no in-app chat panel, this is
/// the only place "is anything talking to this program right now, and is it
/// reading or changing something" can be answered from — a person watches
/// this, not a transcript, because there is no transcript.
///
/// A bounded ring buffer for the same reason as <see cref="WireTrace"/>: this
/// is evidence for the session in progress, and recording it must never make
/// an agent's request wait for it. One event is enqueued when a request
/// starts and a second when it finishes, rather than one row mutated in
/// place, because a <see cref="ConcurrentQueue{T}"/> is append-only and a
/// subscriber watching for "is anything in flight right now" needs to see
/// both ends of that, not just the answer once it is known.
/// </summary>
public sealed class AgentActivityLog(int capacity = 500)
{
    /// <summary>
    /// Route path to the phrase a person reads and the kind that decides how
    /// loudly it is shown. Deliberately a flat lookup rather than anything
    /// cleverer — routes are added a handful at a time as the agent API grows,
    /// and a table anyone can append a line to is easier to keep honest than a
    /// rule that infers intent from a path shape.
    ///
    /// Only <c>/tune/set</c> and <c>/table/set</c> are marked as writes today:
    /// they are the only routes that reach the ECU. <c>/project/…</c> routes
    /// change the tuning project's own notes on disk, which matters to a
    /// person, but is not the "the AI just changed something in the engine"
    /// event this exists to call out — so those stay reads for this purpose,
    /// the same as any other route this table does not recognise.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (AgentActivityKind Kind, string Phrase)> Routes =
        new Dictionary<string, (AgentActivityKind, string)>(StringComparer.Ordinal)
        {
            ["/"] = (AgentActivityKind.Read, "asking what this API offers"),
            ["/state"] = (AgentActivityKind.Read, "checking connection state"),
            ["/channels"] = (AgentActivityKind.Read, "listing channels"),
            ["/values"] = (AgentActivityKind.Read, "reading channel history"),
            ["/insights"] = (AgentActivityKind.Read, "reviewing the log"),
            ["/tune"] = (AgentActivityKind.Read, "reading the tune"),
            ["/tables"] = (AgentActivityKind.Read, "listing tables"),
            ["/table"] = (AgentActivityKind.Read, "reading a table"),
            ["/wire/health"] = (AgentActivityKind.Read, "checking the wire trace's health"),
            ["/wire/events"] = (AgentActivityKind.Read, "reading the wire trace"),
            ["/project"] = (AgentActivityKind.Read, "reading the tuning project"),
            ["/project/versions/compare"] = (AgentActivityKind.Read, "comparing tune versions"),
            ["/project/record"] = (AgentActivityKind.Read, "recording a sitting"),
            ["/project/keep"] = (AgentActivityKind.Read, "keeping this tune version"),
            ["/project/fix"] = (AgentActivityKind.Read, "noting a fix"),
            ["/tune/set"] = (AgentActivityKind.Write, "writing to the ECU"),
            ["/table/set"] = (AgentActivityKind.Write, "writing to the ECU"),
        };

    private readonly ConcurrentQueue<AgentActivityEvent> _events = new();
    private long _sequence;

    /// <summary>
    /// Raised once for each event recorded, on whatever thread recorded it —
    /// an agent's own request thread, not the UI thread. A subscriber that
    /// touches bound state has to get itself back onto the right thread; this
    /// makes no assumption about who is listening.
    /// </summary>
    public event Action<AgentActivityEvent>? Changed;

    /// <summary>
    /// Marks one request as started. Disposing the result records the
    /// matching end — meant to wrap the whole of handling a request in a
    /// <c>using</c>, the same shape as <see cref="WireOriginScope.Enter"/>.
    /// An unrecognised route is treated as a read, the safe assumption for a
    /// route this table has not caught up with yet.
    /// </summary>
    public IDisposable Begin(string route)
    {
        (AgentActivityKind kind, string phrase) = Routes.TryGetValue(route, out var known)
            ? known
            : (AgentActivityKind.Read, route);

        Record(route, kind, phrase, inFlight: true);
        return new Handle(this, route, kind, phrase);
    }

    private void Record(string route, AgentActivityKind kind, string detail, bool inFlight)
    {
        var evt = new AgentActivityEvent(
            Interlocked.Increment(ref _sequence), DateTime.UtcNow, route, kind, detail, inFlight);

        _events.Enqueue(evt);
        while (_events.Count > capacity && _events.TryDequeue(out _)) { }

        Changed?.Invoke(evt);
    }

    /// <summary>The most recent events, newest first.</summary>
    public IReadOnlyList<AgentActivityEvent> Recent(int count)
    {
        AgentActivityEvent[] all = [.. _events];
        int take = Math.Clamp(count, 0, all.Length);
        var result = new AgentActivityEvent[take];

        for (int i = 0; i < take; i++) result[i] = all[all.Length - 1 - i];

        return result;
    }

    private sealed class Handle(AgentActivityLog owner, string route, AgentActivityKind kind, string phrase)
        : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            owner.Record(route, kind, phrase, inFlight: false);
        }
    }
}
