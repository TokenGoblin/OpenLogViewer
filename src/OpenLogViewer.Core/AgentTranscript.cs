using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>
/// One complete request against the agent API and the answer it got, as the
/// console shows it.
///
/// <see cref="Client"/> is who asked: <c>mcp</c> for the MCP server (with the
/// tool the model called in <see cref="Tool"/>), <c>console</c> for a person
/// typing in the application's own console, and <c>http</c> for anything else
/// holding the token — a script, curl, an agent that skipped MCP. The
/// <c>console</c> label is proven by a token that never leaves the process;
/// the other two are only what the caller said about itself, which is fine
/// for a label and is not used for anything else.
/// </summary>
public sealed record AgentExchange(
    long Sequence,
    DateTime At,
    string Client,
    string Tool,
    string Method,
    string Target,
    string RequestBody,
    int Status,
    string ResponseBody,
    double Milliseconds)
{
    /// <summary>The route without its query, for matching against routes.</summary>
    public string Route => Target.Split('?', 2)[0];

    /// <summary>The three routes that reach the ECU — see <see cref="AgentActivityLog"/>.</summary>
    public bool IsWrite => Route is "/tune/set" or "/table/set" or "/tune/apply";

    public bool Succeeded => Status is >= 200 and < 300 or 101;
}

/// <summary>
/// Every request the agent API answered, with what was asked and what was
/// said back — the full text that <see cref="AgentActivityLog"/> deliberately
/// leaves out. The activity log answers "is anything happening"; this answers
/// "what exactly did it ask for and what did it get", which is what a person
/// watching an agent drive the application wants once they are curious.
///
/// Owned by the application rather than by the server, so it survives the API
/// being turned off and on — the evidence for a session should not vanish
/// because the socket was cycled. Bounded, and every body clipped, because a
/// single <c>/log/full</c> answer can be megabytes and this must never be
/// what runs the process out of memory.
/// </summary>
public sealed partial class AgentTranscript(int capacity = 500, int maximumBodyLength = 64 * 1024)
{
    private readonly ConcurrentQueue<AgentExchange> _exchanges = new();
    private long _sequence;

    /// <summary>
    /// Raised once per exchange, on the request's own thread. A subscriber that
    /// touches bound state has to marshal itself back to its own thread.
    /// </summary>
    public event Action<AgentExchange>? Recorded;

    public AgentExchange Record(
        DateTime at, string client, string tool, string method, string target,
        string requestBody, int status, string responseBody, double milliseconds)
    {
        var exchange = new AgentExchange(
            Interlocked.Increment(ref _sequence), at, client, tool, method, Redact(target),
            Clip(requestBody), status, Clip(responseBody), milliseconds);

        _exchanges.Enqueue(exchange);
        while (_exchanges.Count > capacity && _exchanges.TryDequeue(out _)) { }

        Recorded?.Invoke(exchange);
        return exchange;
    }

    /// <summary>Everything still held, oldest first.</summary>
    public IReadOnlyList<AgentExchange> All() => [.. _exchanges];

    private string Clip(string body) =>
        body.Length <= maximumBodyLength
            ? body
            : body[..maximumBodyLength] + $"\n… {body.Length - maximumBodyLength:N0} more characters not kept";

    /// <summary>
    /// A token passed as <c>?token=</c> would otherwise be written into every
    /// line of the transcript, which is on screen and one copy away from a
    /// chat window.
    /// </summary>
    internal static string Redact(string target) => TokenInQuery().Replace(target, "$1token=…");

    [GeneratedRegex(@"([?&])token=[^&]*", RegexOptions.IgnoreCase)]
    private static partial Regex TokenInQuery();
}
