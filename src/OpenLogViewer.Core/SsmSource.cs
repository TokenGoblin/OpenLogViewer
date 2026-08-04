namespace OpenLogViewer.Core;

/// <summary>
/// A live source that reads a Subaru's own parameters over SSM.
///
/// Everything downstream of an <see cref="ILiveSource"/> already works — the
/// plot, the gauges, filters, calculated channels, recording — so the whole of
/// this is getting bytes off the ECU and giving them names.
///
/// What it is for is the values OBD2 does not carry at any speed: what the
/// controller has <em>learnt</em> rather than what it is measuring this instant.
/// One byte per request at about 146 ms, which is slow and suits the job, since
/// those values move over minutes.
///
/// The addresses come from a file the user supplies. See
/// <see cref="SsmParameterFile"/> for why they are not shipped.
/// </summary>
public sealed class SsmSource : ILiveSource
{
    private readonly IEcuTransport _transport;
    private readonly Elm327 _elm;
    private readonly IReadOnlyList<SsmParameter> _parameters;
    private readonly double[] _values;
    private readonly int[] _silences;

    private SsmSource(
        IEcuTransport transport, Elm327 elm, IReadOnlyList<SsmParameter> parameters, string adapter)
    {
        _transport = transport;
        _elm = elm;
        _parameters = parameters;
        _silences = new int[parameters.Count];
        Adapter = adapter;

        Names = [.. parameters.Select(p => p.Name)];
        Units = [.. parameters.Select(p => p.Units)];
        Digits = [.. parameters.Select(p => p.Digits)];

        // Nothing has been read yet, and zero is a reading.
        _values = [.. parameters.Select(_ => double.NaN)];
    }

    /// <summary>What the adapter calls itself.</summary>
    public string Adapter { get; }

    /// <summary>The parameters being read, in order.</summary>
    public IReadOnlyList<SsmParameter> Parameters => _parameters;

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<string> Units { get; }

    public IReadOnlyList<int> Digits { get; }

    public int Retries { get; private set; }

    /// <summary>
    /// Consecutive silences before an address is left out of the rotation.
    ///
    /// More forgiving than the OBD2 equivalent, because an address that never
    /// answers here means somebody typed it rather than that the car is busy —
    /// and dropping it is the only way the rest keep their update rate. It is
    /// reported rather than merely dropped.
    /// </summary>
    private const int GiveUpAfter = 8;

    /// <summary>
    /// Opens the adapter, puts it into SSM addressing, and proves the car answers.
    ///
    /// The proving matters. Every address in the file may be wrong, and a session
    /// that starts happily and shows a screen of dashes is a worse outcome than
    /// one that refuses to start and says why.
    /// </summary>
    public static SsmSource Connect(IEcuTransport transport, IReadOnlyList<SsmParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.Count == 0)
            throw new EcuProtocolException(
                $"No SSM parameters are defined. Put addresses in {SsmParameterFile.Name} "
                + "under the definitions folder — this ships the protocol, not the address map.");

        transport.Open();

        var elm = new Elm327(transport);
        string reset = elm.Reset();

        string adapter = reset.Length > 0 ? elm.Identify() : "";
        if (adapter.Length == 0) adapter = reset;

        if (adapter.Length == 0)
            throw new EcuProtocolException("Nothing here answered as an OBD2 adapter.");

        // The protocol has to be found before anything is addressed, and the
        // first request after a reset is answered with "SEARCHING..." while the
        // adapter works through nine of them. Asked patiently, because giving up
        // early aborts the search and every request after it fails in a way that
        // looks exactly like a car with the key out.
        Settle(elm);

        foreach (string command in Ssm.Setup) elm.Send(command, elm.Timeout);

        // One real read, so a session only starts when the car has actually
        // spoken SSM.
        string reply = elm.Send(Ssm.ReadRequest(parameters[0].Address), elm.Timeout, settle: true);

        if (Ssm.ReadReply(reply, 1).Length == 0)
            throw new EcuProtocolException(
                Ssm.Refused(reply)
                    ? $"The ECU understood the request and refused it. Address "
                      + $"0x{parameters[0].Address:X6} is probably wrong for this vehicle."
                    : "The vehicle did not answer an SSM request. It may not speak SSM over CAN, "
                      + "or the ignition may be off.");

        return new SsmSource(transport, elm, parameters, adapter);
    }

    private static void Settle(Elm327 elm)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string reply = elm.Send("0100", elm.ResetTimeout, settle: true);

            if (reply.Contains("41", StringComparison.OrdinalIgnoreCase)
                && !reply.Contains("SEARCHING", StringComparison.OrdinalIgnoreCase))
                return;
        }
    }

    public void Open()
    {
        if (_transport.IsOpen) return;

        _transport.Open();
        Reopen();
    }

    private void Reopen()
    {
        _elm.Reset();
        Settle(_elm);

        foreach (string command in Ssm.Setup) _elm.Send(command, _elm.Timeout);
    }

    /// <summary>
    /// Reads every parameter once.
    ///
    /// All of them every round, unlike the OBD2 source, which rotates. There is
    /// no rotation to do here: the list is one somebody chose deliberately rather
    /// than everything a car happens to support, so there is nothing in it that
    /// is not wanted.
    /// </summary>
    public double[] Read()
    {
        bool answered = false;
        bool asked = false;

        for (int i = 0; i < _parameters.Count; i++)
        {
            if (_silences[i] >= GiveUpAfter) continue;

            asked = true;
            answered |= Poll(i);
        }

        if (asked && !answered)
        {
            Retries++;

            throw new EcuProtocolException(
                "The ECU answered nothing this round. If the engine was switched off, "
                + "most cars stop responding with the key out.");
        }

        return [.. _values];
    }

    private bool Poll(int index)
    {
        SsmParameter parameter = _parameters[index];

        // One address per request on the hardware this was proven against, so a
        // multi-byte value is read a byte at a time. The bytes are not sampled at
        // quite the same instant, which for a value that moves as fast as engine
        // speed puts a few rpm of noise on it and for a learnt value costs
        // nothing at all.
        Span<byte> raw = stackalloc byte[4];
        int got = 0;

        foreach (int address in parameter.Addresses)
        {
            string reply = _elm.Send(Ssm.ReadRequest(address), _elm.Timeout, settle: true);
            byte[] data = Ssm.ReadReply(reply, 1);

            if (data.Length == 0) break;

            raw[got++] = data[0];
        }

        if (got != parameter.Bytes)
        {
            _silences[index]++;
            return false;
        }

        _silences[index] = 0;
        _values[index] = parameter.Read(raw[..got]);

        return true;
    }

    /// <summary>Parameters that have stopped answering and been dropped.</summary>
    public IReadOnlyList<string> Dropped =>
        [.. _parameters.Where((_, i) => _silences[i] >= GiveUpAfter).Select(p => p.Name)];

    public void Recover()
    {
        try
        {
            _transport.Close();
        }
        catch (Exception)
        {
            // A link whose adapter has gone cannot be closed politely.
        }

        _transport.Open();
        Reopen();

        Read();
    }

    public void Dispose()
    {
        // Put the adapter back to ordinary addressing, so whatever connects next
        // is not talking to the engine module by mistake.
        try
        {
            foreach (string command in Ssm.Restore) _elm.Send(command, _elm.Timeout);
        }
        catch (Exception)
        {
            // Best effort: the link may already have gone, and this is tidying.
        }

        _transport.Dispose();
    }
}
