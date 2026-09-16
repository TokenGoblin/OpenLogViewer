using System.Collections.Concurrent;

namespace OpenLogViewer.Core;

/// <summary>
/// Why one attempt of a wire exchange failed. <see cref="None"/> is a success.
/// </summary>
public enum WireFailureKind
{
    None,
    Timeout,
    ShortReply,
    ChecksumMismatch,
    Malformed,
    Refused,
    TransportError,
}

/// <summary>Whether the code about to talk to the ECU is doing so on a human's behalf or an agent's.</summary>
public enum WireOrigin
{
    Human,
    Agent,
}

public enum WireOutcome
{
    Ok,
    Failure,
}

/// <summary>One attempt of one request, as it actually happened.</summary>
public sealed record WireEvent(
    long Sequence,
    DateTime At,
    string Context,
    WireOrigin Origin,
    int Attempt,
    TimeSpan Elapsed,
    WireOutcome Outcome,
    WireFailureKind FailureKind,
    string Detail,
    int RequestBytes,
    int ReplyBytes);

/// <summary>A rollup over recent events, for "is the link healthy" rather than "what happened at 10:04:12".</summary>
public sealed record WireHealthSnapshot(
    int Sampled,
    int Failures,
    double SuccessRate,
    IReadOnlyDictionary<string, int> FailuresByKind,
    TimeSpan? SinceLastSuccess,
    TimeSpan? LongestRecent);

/// <summary>
/// Marks whether the current thread of work is on a human's behalf or an
/// agent's, for anything recorded against <see cref="WireTrace"/> while it is
/// active.
///
/// An ambient flag rather than a parameter threaded through every read/write
/// method on <see cref="EcuConnection"/>, because the distinction only matters
/// at the two places code asks the ECU for something — a UI command or an
/// agent bridge call — and not to any of the protocol code in between.
/// </summary>
public static class WireOriginScope
{
    private static readonly AsyncLocal<WireOrigin> Ambient = new();

    /// <summary>Defaults to <see cref="WireOrigin.Human"/> — an agent has to say otherwise.</summary>
    public static WireOrigin Current => Ambient.Value;

    public static IDisposable Enter(WireOrigin origin)
    {
        WireOrigin previous = Ambient.Value;
        Ambient.Value = origin;
        return new Scope(previous);
    }

    private sealed class Scope(WireOrigin previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// What actually went over the wire to an ECU: every request attempt, its
/// timing, and how it ended.
///
/// The only diagnostic that existed before this was an aggregate retry
/// counter — enough to know something had gone wrong, never what or when. A
/// bounded ring buffer rather than a file, because this is evidence for a
/// session in progress, not a permanent record, and never blocking the
/// request path it is watching matters more than keeping everything forever.
/// Recording an event costs one <see cref="ConcurrentQueue{T}"/> enqueue on
/// the calling thread — the same "never make the request wait" discipline as
/// <see cref="LiveSubscriber"/>.
/// </summary>
public sealed class WireTrace(int capacity = 2000)
{
    private readonly ConcurrentQueue<WireEvent> _events = new();
    private readonly Lock _lastSuccessGate = new();
    private long _sequence;
    private DateTime? _lastSuccess;

    public void Record(
        string context, int attempt, TimeSpan elapsed, WireOutcome outcome,
        WireFailureKind failureKind = WireFailureKind.None, string detail = "",
        int requestBytes = 0, int replyBytes = 0)
    {
        var evt = new WireEvent(
            Interlocked.Increment(ref _sequence), DateTime.UtcNow, context,
            WireOriginScope.Current, attempt, elapsed, outcome, failureKind, detail,
            requestBytes, replyBytes);

        _events.Enqueue(evt);

        if (outcome == WireOutcome.Ok)
        {
            lock (_lastSuccessGate) _lastSuccess = evt.At;
        }

        while (_events.Count > capacity && _events.TryDequeue(out _)) { }
    }

    /// <summary>The most recent events, newest first.</summary>
    public IReadOnlyList<WireEvent> Recent(int count)
    {
        WireEvent[] all = [.. _events];
        int take = Math.Clamp(count, 0, all.Length);
        var result = new WireEvent[take];

        for (int i = 0; i < take; i++) result[i] = all[all.Length - 1 - i];

        return result;
    }

    /// <summary>A health rollup over the last <paramref name="window"/> events.</summary>
    public WireHealthSnapshot Health(int window = 200)
    {
        IReadOnlyList<WireEvent> recent = Recent(window);

        int failures = 0;
        TimeSpan longest = TimeSpan.Zero;
        var byKind = new Dictionary<string, int>();

        foreach (WireEvent e in recent)
        {
            if (e.Elapsed > longest) longest = e.Elapsed;

            if (e.Outcome != WireOutcome.Failure) continue;

            failures++;
            string key = e.FailureKind.ToString();
            byKind[key] = byKind.GetValueOrDefault(key) + 1;
        }

        TimeSpan? sinceLastSuccess;
        lock (_lastSuccessGate)
            sinceLastSuccess = _lastSuccess is { } at ? DateTime.UtcNow - at : null;

        double successRate = recent.Count == 0 ? 1.0 : 1.0 - (double)failures / recent.Count;

        return new WireHealthSnapshot(
            recent.Count, failures, successRate, byKind, sinceLastSuccess,
            recent.Count > 0 ? longest : null);
    }
}
