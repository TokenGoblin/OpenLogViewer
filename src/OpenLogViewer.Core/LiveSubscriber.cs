using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenLogViewer.Core;

/// <summary>
/// One agent watching the live stream.
///
/// <para>
/// <b>The newest frame wins.</b> There is one slot, not a queue: a frame that
/// arrives while the last is still being written replaces it. That is the right
/// trade for this data — an agent asking "what is the engine doing" wants the
/// current answer, and a backlog of stale frames delivered late is worse than a
/// gap. It also means a subscriber can never make the poll thread wait, which is
/// the property that keeps the API from slowing down the thing it is watching.
/// </para>
/// <para>
/// The channel names are sent once, as a schema, and each frame afterwards
/// carries only the numbers in that order. A rusEFI publishes 823 channels; at
/// 25 Hz, repeating their names would be about four megabytes a minute of
/// nothing but spelling.
/// </para>
/// </summary>
internal sealed class LiveSubscriber : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _stopping;
    private readonly SemaphoreSlim _arrived = new(0, 1);
    private readonly Lock _gate = new();

    /// <summary>What the agent asked for, or empty for everything.</summary>
    private HashSet<string>? _wanted;

    /// <summary>Resolved once per schema change rather than per frame.</summary>
    private int[] _indices = [];
    private string[] _sent = [];
    private IReadOnlyList<string> _knownNames = [];

    private double _pendingSeconds;
    private double[]? _pending;
    private bool _has;

    /// <summary>Frames dropped because the agent was still reading the last one.</summary>
    private int _skipped;

    public LiveSubscriber(WebSocket socket, CancellationToken stopping)
    {
        _socket = socket;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    /// <summary>
    /// Takes a frame if there is room for one, and never blocks.
    ///
    /// Runs on the poll thread. Everything here is a lock over an array copy;
    /// the writing happens on this subscriber's own task.
    /// </summary>
    public void Offer(double seconds, IReadOnlyList<string> names, IReadOnlyList<double> values)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(names, _knownNames)) Resolve(names);

            if (_indices.Length == 0) return;

            _pending ??= new double[_indices.Length];

            for (int i = 0; i < _indices.Length; i++)
            {
                int at = _indices[i];
                _pending[i] = at >= 0 && at < values.Count ? values[at] : double.NaN;
            }

            _pendingSeconds = seconds;
            if (_has) _skipped++;
            _has = true;
        }

        // Released outside the lock, and only when it would not go past one:
        // the slot holds the newest frame, so a second release would wake the
        // writer for a frame that has already been overwritten.
        if (_arrived.CurrentCount == 0)
        {
            try { _arrived.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>Works out which columns this agent wants, once per name change.</summary>
    private void Resolve(IReadOnlyList<string> names)
    {
        _knownNames = names;

        var indices = new List<int>();
        var sent = new List<string>();

        for (int i = 0; i < names.Count; i++)
        {
            if (_wanted is not null && !_wanted.Contains(names[i])) continue;

            indices.Add(i);
            sent.Add(names[i]);
        }

        _indices = [.. indices];
        _sent = [.. sent];
        _pending = null;
        _schemaSent = false;
    }

    private bool _schemaSent;

    public async Task Run()
    {
        Task reading = Task.Run(ReadRequests);

        try
        {
            while (!_stopping.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                await _arrived.WaitAsync(_stopping.Token).ConfigureAwait(false);

                double seconds;
                double[] frame;
                string[] schema;
                bool needSchema;
                int skipped;

                lock (_gate)
                {
                    if (!_has || _pending is null) continue;

                    seconds = _pendingSeconds;
                    frame = (double[])_pending.Clone();
                    schema = _sent;
                    needSchema = !_schemaSent;
                    _schemaSent = true;
                    skipped = _skipped;
                    _skipped = 0;
                    _has = false;
                }

                if (needSchema)
                {
                    await Write(new { type = "schema", channels = schema }).ConfigureAwait(false);
                }

                await Write(new { type = "frame", t = seconds, v = frame, skipped }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _stopping.Cancel();
            try { await reading.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Listens for the one thing an agent can say: which channels it wants.
    ///
    /// Also the only way a half-open socket is noticed. Without a read in
    /// flight, an agent that vanished is discovered on the next write, which on
    /// an idle session may be a long time.
    /// </summary>
    private async Task ReadRequests()
    {
        var buffer = new byte[4096];

        while (!_stopping.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await _socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), _stopping.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception) { return; }

            if (result.MessageType == WebSocketMessageType.Close) { _stopping.Cancel(); return; }
            if (result.Count == 0) continue;

            try
            {
                var request = JsonSerializer.Deserialize<Subscribe>(
                    Encoding.UTF8.GetString(buffer, 0, result.Count), Json);

                if (request?.Channels is { } wanted)
                {
                    lock (_gate)
                    {
                        // "*" or an empty list means everything.
                        _wanted = wanted.Length == 0 || wanted.Contains("*")
                            ? null
                            : new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);

                        // Force the columns to be worked out again on the next
                        // frame, schema and all.
                        _knownNames = [];
                    }
                }
            }
            catch (JsonException)
            {
                await Write(new { type = "error", detail = "expected {\"channels\":[…]}" })
                    .ConfigureAwait(false);
            }
        }
    }

    private sealed record Subscribe(string[]? Channels);

    private async Task Write(object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);

        await _socket.SendAsync(
            new ArraySegment<byte>(body), WebSocketMessageType.Text, endOfMessage: true,
            _stopping.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _stopping.Cancel(); } catch (ObjectDisposedException) { }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                _socket.Abort();
            }
        }
        catch (Exception) { }

        _socket.Dispose();
        _arrived.Dispose();
        _stopping.Dispose();
    }
}
