using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// An ELM327 adapter and a car behind it, in software.
///
/// Faithful to the awkward parts rather than to a tidy idea of the protocol:
/// the adapter echoes commands until it is told not to, inserts spaces until it
/// is told not to, announces "SEARCHING..." on its own line before the first
/// reply, says "NO DATA" for a parameter this car does not have, and ends every
/// exchange with a "&gt;" prompt. Each of those has broken a real client.
/// </summary>
internal sealed class FakeElm : IEcuTransport
{
    private readonly Queue<byte> _out = new();
    private readonly StringBuilder _in = new();

    /// <summary>Data bytes this car answers with, by PID.</summary>
    public Dictionary<byte, byte[]> Answers { get; } = [];

    /// <summary>
    /// Further modules answering the same request, sent ahead of the main one.
    ///
    /// Real cars have several, each replying on its own line, and the order they
    /// arrive in is not fixed — which is exactly what broke a connection that had
    /// worked a minute earlier.
    /// </summary>
    public Dictionary<byte, List<byte[]>> ExtraAnswers { get; } = [];

    /// <summary>What ATZ and ATI report, which every adapter answers with.</summary>
    public string Elm327Name { get; init; } = "ELM327 v1.5";

    /// <summary>
    /// What STDI reports, where the adapter has an STN chip in it — "OBDLink
    /// r2.6". Empty for the clones, which refuse the command.
    /// </summary>
    public string Product { get; init; } = "";

    /// <summary>What STI reports on the same devices — "STN1100 v2.2.2".</summary>
    public string Firmware { get; init; } = "";

    public bool Echo { get; private set; } = true;

    public bool Spaces { get; private set; } = true;

    /// <summary>Said once, before the first mode-01 reply, as a real one does.</summary>
    public bool Searching { get; set; } = true;

    /// <summary>
    /// Answer everything with rubbish, as an adapter does when the port is open
    /// at a speed it is not listening at. Note that it answers rather than falls
    /// silent — that is what makes a wrong baud rate hard to recognise.
    /// </summary>
    public bool Garble { get; init; }

    public List<string> Received { get; } = [];

    public bool IsOpen { get; private set; }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Dispose() => Close();

    public void DiscardInput() => _out.Clear();

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b != (byte)'\r')
            {
                _in.Append((char)b);
                continue;
            }

            Answer(_in.ToString().Trim());
            _in.Clear();
        }
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int count = 0;
        while (count < buffer.Length && _out.Count > 0) buffer[count++] = _out.Dequeue();

        return count;
    }

    private void Answer(string command)
    {
        Received.Add(command);

        if (Garble)
        {
            // Framing errors at the wrong speed turn the reply into whatever the
            // bits happen to land on. Deliberately hex-ish: rubbish that parses
            // as nothing would be too easy to reject.
            Say("þÃADF3°À");
            Prompt();
            return;
        }

        if (Echo) Say(command);

        switch (command.ToUpperInvariant())
        {
            case "ATZ": Echo = true; Spaces = true; Say(Elm327Name); break;
            case "ATI": Say(Elm327Name); break;
            case "ATE0": Echo = false; Say("OK"); break;
            case "ATS0": Spaces = false; Say("OK"); break;

            // The ST commands, which only the STN chips behind an OBDLink have.
            // A plain ELM327 and every clone answer "?" — which is the thing that
            // tells one from the other, so the fake has to refuse them properly
            // rather than not recognise them at all.
            case "STDI": Say(Product.Length > 0 ? Product : "?"); break;
            case "STI": Say(Firmware.Length > 0 ? Firmware : "?"); break;
            case var st when st.StartsWith("ST", StringComparison.Ordinal): Say("?"); break;

            case var at when at.StartsWith("AT", StringComparison.Ordinal): Say("OK"); break;
            default: Mode01(command); break;
        }

        Prompt();
    }

    private void Mode01(string command)
    {
        if (command.Length != 4 || !command.StartsWith("01", StringComparison.OrdinalIgnoreCase))
        {
            Say("?");
            return;
        }

        byte pid = Convert.ToByte(command[2..], 16);

        if (Searching)
        {
            Say("SEARCHING...");
            Searching = false;
        }

        // Ahead of the real one, so anything that takes the first answer takes
        // the wrong one.
        if (ExtraAnswers.TryGetValue(pid, out List<byte[]>? others))
            foreach (byte[] other in others) Say(Hex([0x41, pid, .. other]));

        if (!Answers.TryGetValue(pid, out byte[]? data))
        {
            if (others is not { Count: > 0 }) Say("NO DATA");
            return;
        }

        Say(Hex([0x41, pid, .. data]));
    }

    private string Hex(byte[] bytes)
    {
        IEnumerable<string> pairs = bytes.Select(b => b.ToString("X2"));
        return Spaces ? string.Join(' ', pairs) : string.Concat(pairs);
    }

    private void Say(string line)
    {
        foreach (char c in line) _out.Enqueue((byte)c);
        _out.Enqueue((byte)'\r');
    }

    private void Prompt()
    {
        _out.Enqueue((byte)'\r');
        _out.Enqueue((byte)'>');
    }
}
