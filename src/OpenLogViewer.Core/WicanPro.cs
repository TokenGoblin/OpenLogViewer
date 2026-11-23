namespace OpenLogViewer.Core;

/// <summary>
/// A WiCAN Pro reached over USB: its ELM327 session, and — where the unit runs
/// the custom firmware — the WCP client sharing the same port.
/// </summary>
/// <param name="Source">The OBD2 session, exactly as on any other adapter.</param>
/// <param name="Link">The port both of them use.</param>
/// <param name="Wcp">The control lane, or null on stock firmware.</param>
public sealed record WicanProConnection(Elm327Source Source, WcpLink Link, WcpClient? Wcp);

/// <summary>
/// Connecting to a MeatPi WiCAN Pro's USB ELM327 port, with WCP if it is there.
///
/// <para>
/// <b>The order matters.</b> <c>hello</c> is asked first, before the ELM327
/// conversation starts, and the ELM327 side is cleared after it. A stock unit
/// passes the line to its chip, which answers it with "?" and a prompt — bytes that,
/// left in the buffer, would be read as the answer to the first OBD2 command. The
/// ELM327 reset that follows clears them anyway; clearing them here as well means
/// that does not depend on it.
/// </para>
///
/// <para>
/// <b>A stock unit is not slowed down much.</b> Its chip echoes the request, and an
/// echoed request is recognised as "no WCP here" the moment it arrives. Only a unit
/// whose chip has echo switched off costs the hello's timeout, twice.
/// </para>
///
/// <para>
/// The link stays in place on stock firmware too. It passes ELM327 through
/// untouched, and the session should not depend on which firmware answered.
/// </para>
/// </summary>
public static class WicanPro
{
    /// <summary>Opens the Pro's port by name: 2,000,000 baud, DTR and RTS held off, and a large driver buffer for the CAN stream.</summary>
    public static WicanProConnection ConnectOnPort(string portName, IObd2BatchMemory? memory = null)
    {
        var serial = new SerialEcuTransport(portName, Obd2Adapters.WiCanProUsbBaud)
        {
            OpenAttempts = 3,
            AssertControlLines = false,
            ReadBufferBytes = 1 << 20,
        };

        return Connect(serial, memory);
    }

    /// <summary>The same over a transport already chosen, so the whole path can be run against a fake.</summary>
    public static WicanProConnection Connect(
        IEcuTransport transport, IObd2BatchMemory? memory = null, TimeSpan? helloTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var link = new WcpLink(transport);
        WcpClient? wcp = null;

        try
        {
            link.Open();

            wcp = WcpClient.Detect(link, helloTimeout);

            link.DiscardInput();

            Elm327Source source = Elm327Source.Connect(link, memory: memory);
            return new WicanProConnection(source, link, wcp);
        }
        catch (Exception)
        {
            wcp?.Dispose();
            link.Dispose();
            throw;
        }
    }
}
