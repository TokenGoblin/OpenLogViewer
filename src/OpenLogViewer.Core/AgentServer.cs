using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenLogViewer.Core;

/// <summary>How the agent API is set up, if it is set up at all.</summary>
public sealed record AgentServerSettings
{
    /// <summary>
    /// The port to listen on. Zero asks the operating system for a free one,
    /// which is what the tests use.
    /// </summary>
    public int Port { get; init; } = 8765;

    /// <summary>
    /// The token every request must carry, as <c>Authorization: Bearer …</c> or
    /// <c>?token=</c>.
    ///
    /// Not optional and not for show. Binding to the loopback address keeps
    /// other machines out; it does not keep out other programs on this one, and
    /// a browser tab on any page can reach a localhost port. The token is what
    /// stands between a web page you happened to open and a socket that can move
    /// numbers in an engine.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>How many live subscribers to allow at once.</summary>
    public int MaximumClients { get; init; } = 8;
}

/// <summary>
/// A small local server so an agent can watch and question a live session.
///
/// <para>
/// <b>Loopback only, always.</b> The prefix is built against 127.0.0.1 and there
/// is no setting that changes it. A tuning application that can write to an
/// engine has no business accepting a connection from another machine, and the
/// way that mistake normally happens is a "host" option that somebody sets to
/// 0.0.0.0 to make something work.
/// </para>
/// <para>
/// <b>HTTP for questions, one WebSocket for the answer that never stops.</b>
/// Anything an agent asks once — what is connected, what channels exist, what
/// the insights say — is a request. Live data is a subscription, pushed as the
/// ECU produces it rather than polled, because polling a 25 Hz source over HTTP
/// is both slower and less faithful than being handed each frame as it lands.
/// </para>
/// <para>
/// Built on <see cref="HttpListener"/> rather than a web framework so the
/// application gains no dependency it did not already have. This is a handful of
/// routes, not a web application.
/// </para>
/// </summary>
public sealed class AgentServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAgentBridge _bridge;
    private readonly AgentServerSettings _settings;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<LiveSubscriber> _subscribers = [];
    private readonly Lock _gate = new();

    public AgentServer(IAgentBridge bridge, AgentServerSettings settings)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        if (string.IsNullOrWhiteSpace(settings.Token))
            throw new ArgumentException("The agent API cannot be served without a token.", nameof(settings));

        Port = settings.Port;
    }

    /// <summary>The port actually listening, which is settled once started.</summary>
    public int Port { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>Where an agent points itself.</summary>
    public string Address => $"http://127.0.0.1:{Port}";

    /// <summary>How many agents are watching the live stream.</summary>
    public int Subscribers
    {
        get { lock (_gate) return _subscribers.Count; }
    }

    public void Start()
    {
        if (IsRunning) return;

        // A port of zero means "find me one", which HttpListener will not do —
        // so it is asked of a socket first and the answer handed over.
        if (Port == 0) Port = FreePort();

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        IsRunning = true;

        _ = Task.Run(AcceptLoop);
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            // Each connection on its own, so one slow agent cannot hold up the
            // next — and so a subscriber sitting on a socket for an hour does
            // not stop anything else being asked.
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            if (!Authorised(context.Request))
            {
                await Refuse(context, 401, "unauthorised",
                    "Pass the token as an Authorization: Bearer header, or ?token=").ConfigureAwait(false);
                return;
            }

            string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Length == 0) path = "/";

            if (context.Request.IsWebSocketRequest)
            {
                if (path is "/live/stream") await Stream(context).ConfigureAwait(false);
                else await Refuse(context, 404, "no such stream", path).ConfigureAwait(false);

                return;
            }

            await Route(context, path).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The agent went away mid-answer. Nothing to report and nobody to
            // report it to.
        }
        catch (Exception e)
        {
            try
            {
                await Refuse(context, 500, "the application failed to answer", e.Message)
                    .ConfigureAwait(false);
            }
            catch (Exception) { /* the socket is gone too */ }
        }
    }

    private bool Authorised(HttpListenerRequest request)
    {
        string? header = request.Headers["Authorization"];

        if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && Same(header[7..].Trim(), _settings.Token))
        {
            return true;
        }

        return Same(request.QueryString["token"], _settings.Token);
    }

    /// <summary>
    /// Compared without giving away where two tokens start to differ.
    ///
    /// A plain string comparison stops at the first wrong character, and the
    /// time it took says how much of a guess was right. Over a loopback socket
    /// that is a thin channel, but it costs nothing to close.
    /// </summary>
    private static bool Same(string? offered, string expected)
    {
        if (offered is null || offered.Length != expected.Length) return false;

        int differences = 0;
        for (int i = 0; i < expected.Length; i++) differences |= offered[i] ^ expected[i];

        return differences == 0;
    }

    private async Task Route(HttpListenerContext context, string path)
    {
        System.Collections.Specialized.NameValueCollection query = context.Request.QueryString;

        switch (path)
        {
            case "/":
                await Send(context, new
                {
                    application = "OpenLogViewer",
                    api = 1,
                    endpoints = new[]
                    {
                        "GET /state", "GET /channels", "GET /values?channel=&seconds=",
                        "GET /insights", "GET /tune", "GET /tables", "GET /table?name=",
                        "POST /tune/set", "POST /table/set", "WS /live/stream",
                        "GET /project", "POST /project/record", "POST /project/fix",
                    },
                }).ConfigureAwait(false);
                return;

            case "/state":
                await Send(context, _bridge.State()).ConfigureAwait(false);
                return;

            case "/channels":
                await Send(context, _bridge.Channels()).ConfigureAwait(false);
                return;

            case "/values":
            {
                string? channel = query["channel"];

                if (string.IsNullOrWhiteSpace(channel))
                {
                    await Refuse(context, 400, "no channel named", "pass ?channel=").ConfigureAwait(false);
                    return;
                }

                double seconds = Seconds(query["seconds"]);
                IReadOnlyList<double> values = _bridge.Values(channel, seconds);

                await Send(context, new
                {
                    channel,
                    count = values.Count,
                    times = _bridge.Times(seconds),
                    values,
                }).ConfigureAwait(false);
                return;
            }

            case "/insights":
                await Send(context, _bridge.Insights()).ConfigureAwait(false);
                return;

            case "/tune":
                await Send(context, _bridge.TuneValues()).ConfigureAwait(false);
                return;

            case "/tables":
                await Send(context, _bridge.TableNames()).ConfigureAwait(false);
                return;

            case "/project":
                await Send(context, new
                {
                    open = _bridge.ProjectBrief().Length > 0,
                    vehicles = _bridge.Projects(),
                    brief = _bridge.ProjectBrief(),
                }).ConfigureAwait(false);
                return;

            case "/project/record":
            {
                if (await ReadBody<RecordSitting>(context).ConfigureAwait(false) is not { } body) return;

                if (_bridge.RecordSitting(body.Note ?? "") is { } refused)
                {
                    await Refuse(context, 409, refused.Reason, refused.Detail).ConfigureAwait(false);
                    return;
                }

                await Send(context, new { recorded = true, brief = _bridge.ProjectBrief() })
                    .ConfigureAwait(false);
                return;
            }

            case "/project/fix":
            {
                if (await ReadBody<NoteFix>(context).ConfigureAwait(false) is not { } body) return;

                AgentRefusal? refused = _bridge.NoteFix(
                    body.Id ?? "", body.Title ?? "", body.Detail ?? "", body.State ?? "", body.Change ?? "");

                if (refused is not null)
                {
                    await Refuse(context, 409, refused.Reason, refused.Detail).ConfigureAwait(false);
                    return;
                }

                await Send(context, new { noted = true, brief = _bridge.ProjectBrief() })
                    .ConfigureAwait(false);
                return;
            }

            case "/table":
            {
                string? name = query["name"];

                if (string.IsNullOrWhiteSpace(name))
                {
                    await Refuse(context, 400, "no table named", "pass ?name=").ConfigureAwait(false);
                    return;
                }

                if (_bridge.Table(name) is not { } table)
                {
                    await Refuse(context, 404, "no such table", name).ConfigureAwait(false);
                    return;
                }

                await Send(context, Flatten(table)).ConfigureAwait(false);
                return;
            }

            case "/tune/set":
            {
                if (await ReadBody<SetSetting>(context).ConfigureAwait(false) is not { } body) return;

                AgentRefusal? refused = _bridge.SetSetting(body.Name ?? "", body.Value);

                if (refused is not null)
                {
                    await Refuse(context, 409, refused.Reason, refused.Detail).ConfigureAwait(false);
                    return;
                }

                await Send(context, new { written = body.Name, body.Value, burned = false }).ConfigureAwait(false);
                return;
            }

            case "/table/set":
            {
                if (await ReadBody<SetCell>(context).ConfigureAwait(false) is not { } body) return;

                AgentRefusal? refused =
                    _bridge.SetTableCell(body.Table ?? "", body.Column, body.Row, body.Value);

                if (refused is not null)
                {
                    await Refuse(context, 409, refused.Reason, refused.Detail).ConfigureAwait(false);
                    return;
                }

                await Send(context, new
                {
                    written = body.Table, body.Column, body.Row, body.Value, burned = false,
                }).ConfigureAwait(false);
                return;
            }

            default:
                await Refuse(context, 404, "no such endpoint", path).ConfigureAwait(false);
                return;
        }
    }

    private sealed record SetSetting(string? Name, double Value);

    private sealed record SetCell(string? Table, int Column, int Row, double Value);

    private sealed record RecordSitting(string? Note);

    private sealed record NoteFix(string? Id, string? Title, string? Detail, string? State, string? Change);

    private async Task<T?> ReadBody<T>(HttpListenerContext context) where T : class
    {
        if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(context, 405, "that endpoint wants a POST", context.Request.HttpMethod)
                .ConfigureAwait(false);
            return null;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        string body = await reader.ReadToEndAsync().ConfigureAwait(false);

        try
        {
            T? parsed = JsonSerializer.Deserialize<T>(body, Json);

            if (parsed is null) await Refuse(context, 400, "the body was empty").ConfigureAwait(false);

            return parsed;
        }
        catch (JsonException e)
        {
            await Refuse(context, 400, "the body was not the JSON this expects", e.Message)
                .ConfigureAwait(false);
            return null;
        }
    }

    private static double Seconds(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double seconds)
            ? Math.Max(0, seconds)
            : 0;

    /// <summary>A table as numbers rather than as an object graph.</summary>
    private static object Flatten(TuneTable table)
    {
        var rows = new double[table.Rows][];

        for (int r = 0; r < table.Rows; r++)
        {
            rows[r] = new double[table.Columns];
            for (int c = 0; c < table.Columns; c++) rows[r][c] = table.Values[c, r];
        }

        return new
        {
            name = table.Name,
            units = table.Units,
            columns = table.Columns,
            rows = table.Rows,
            xBins = table.X.Breakpoints,
            yBins = table.Y.Breakpoints,
            xUnits = table.X.Units,
            yUnits = table.Y.Units,
            xConstant = table.X.Constant,
            yConstant = table.Y.Constant,
            values = rows,
        };
    }

    // ----- the live stream ---------------------------------------------------

    /// <summary>
    /// Hands each frame to every subscriber as it arrives.
    ///
    /// Called from whatever thread the live session polls on, and deliberately
    /// does not wait for anybody: a subscriber that cannot keep up is skipped
    /// for that frame rather than allowed to slow the poll. The ECU sets the
    /// pace here, not the slowest reader of it.
    /// </summary>
    public void Publish(double seconds, IReadOnlyList<string> names, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(values);

        LiveSubscriber[] watching;
        lock (_gate) watching = [.. _subscribers];

        foreach (LiveSubscriber subscriber in watching) subscriber.Offer(seconds, names, values);
    }

    private async Task Stream(HttpListenerContext context)
    {
        lock (_gate)
        {
            if (_subscribers.Count >= _settings.MaximumClients)
            {
                context.Response.StatusCode = 503;
                context.Response.Close();
                return;
            }
        }

        HttpListenerWebSocketContext socket =
            await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);

        var subscriber = new LiveSubscriber(socket.WebSocket, _stopping.Token);
        lock (_gate) _subscribers.Add(subscriber);

        try
        {
            await subscriber.Run().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _subscribers.Remove(subscriber);
            subscriber.Dispose();
        }
    }

    private async Task Send(HttpListenerContext context, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = body.Length;

        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    private async Task Refuse(HttpListenerContext context, int status, string reason, string detail = "")
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { error = reason, detail }, Json);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = body.Length;

        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    public void Dispose()
    {
        if (!IsRunning && _stopping.IsCancellationRequested) return;

        _stopping.Cancel();

        LiveSubscriber[] watching;
        lock (_gate)
        {
            watching = [.. _subscribers];
            _subscribers.Clear();
        }

        foreach (LiveSubscriber subscriber in watching) subscriber.Dispose();

        try
        {
            if (_listener.IsListening) _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException) { }

        IsRunning = false;
        _stopping.Dispose();
    }
}
