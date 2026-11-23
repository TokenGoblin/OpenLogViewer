using System.IO;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// A WiCAN Pro running the custom WCP firmware, as the application holds it while
/// an OBD2 session is connected through it.
///
/// <para>
/// Lives exactly as long as that session: created when <c>hello</c> answers on a
/// new connection, disposed by <see cref="MainViewModel.Disconnect"/>. The ELM327
/// polling and everything here share one USB port (see <see cref="WcpLink"/>), so
/// reading the unit's settings or recording its CAN stream does not interrupt the
/// gauges.
/// </para>
///
/// <para>
/// <b>Nothing here asks a person anything.</b> The calls that change the unit or
/// reach the vehicle's bus — saving settings, arming transmit, uploading or
/// running a script — are on <see cref="MainViewModel"/>, behind
/// <see cref="IWriteConfirmation"/>, which is where every caller meets. The client
/// is exposed for the reads; using it directly to write is going round the gate.
/// </para>
/// </summary>
public sealed class WicanDevice : IDisposable
{
    private readonly Lock _gate = new();
    private WcpCanRecorder? _capture;

    public WicanDevice(WcpClient client, string port)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Port = port;
    }

    /// <summary>The WCP client, for the reads. See the class remarks before writing through it.</summary>
    public WcpClient Client { get; }

    /// <summary>Where it was reached, e.g. "COM8".</summary>
    public string Port { get; }

    /// <summary>What <c>hello</c> said.</summary>
    public WcpHello? Identity => Client.Identity;

    /// <summary>The recording in progress or most recently finished, if any.</summary>
    public WcpCanRecorder? Capture
    {
        get
        {
            lock (_gate) return _capture;
        }
    }

    /// <summary>The byte log made from the last finished recording, if one was made.</summary>
    public string? LastByteLog { get; private set; }

    /// <summary>
    /// Starts the controller listen-only, turns the stream on, and records it to
    /// <paramref name="path"/> until stopped or a limit is reached.
    /// </summary>
    public WcpCanRecorder StartCapture(string path, double maxSeconds, long maxFrames, uint? id = null, uint? mask = null)
    {
        lock (_gate)
        {
            if (_capture is { IsRunning: true }) throw new InvalidOperationException("A CAN recording is already running.");
        }

        var recorder = new WcpCanRecorder(Client, path, maxSeconds, maxFrames);

        Client.CanStart();
        recorder.Start();

        try
        {
            Client.CanStreamOn(id, mask);
        }
        catch (Exception)
        {
            recorder.Stop("failed");
            throw;
        }

        lock (_gate) _capture = recorder;

        LastByteLog = null;
        return recorder;
    }

    /// <summary>
    /// Stops the recording and the stream, and writes the byte log beside the raw
    /// file — <c>name-bytes.csv</c> — when anything on the bus moved.
    /// </summary>
    /// <returns>The recording, or null when there was none. <see cref="LastByteLog"/> says whether a byte log was made.</returns>
    public WcpCanRecorder? StopCapture()
    {
        WcpCanRecorder? recorder = Capture;
        if (recorder is null) return null;

        recorder.Stop();

        try
        {
            Client.CanStreamOff();
        }
        catch (Exception e) when (e is WcpException or TimeoutException or IOException or InvalidOperationException)
        {
            // The recording is what was asked for and it is safe on disk. A
            // stream left on costs only bandwidth, and the next connect resets it.
        }

        LastByteLog = null;

        if (recorder.FrameCount > 0)
        {
            string bytes = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(recorder.Path))!,
                Path.GetFileNameWithoutExtension(recorder.Path) + "-bytes.csv");

            try
            {
                recorder.WriteByteLog(bytes);
                LastByteLog = bytes;
            }
            catch (InvalidOperationException)
            {
                // Nothing on the bus changed, so there is nothing to plot.
            }
        }

        return recorder;
    }

    /// <summary>
    /// Whether Lua scripts may transmit right now, as the unit reports it:
    /// "enabled", "disabled", "suspended", or "unknown" when it could not be asked.
    /// </summary>
    public string LuaTransmitState()
    {
        try
        {
            string tx = Client.LuaStatus().Values["tx"];
            return tx.Length > 0 ? tx : "unknown";
        }
        catch (Exception e) when (e is WcpException or TimeoutException or IOException or InvalidOperationException)
        {
            return "unknown";
        }
    }

    public void Dispose()
    {
        Capture?.Stop("link");
        Client.Dispose();
    }
}
