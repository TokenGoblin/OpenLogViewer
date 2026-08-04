using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Tests;

/// <summary>
/// A Subaru answering SSM, in software: the framing the real car used, with
/// the values it actually returned at those addresses.
/// </summary>
internal sealed class FakeSubaru : IEcuTransport
{
    private readonly Queue<byte> _out = new();
    private readonly StringBuilder _in = new();

    public Dictionary<int, byte> Memory { get; } = new()
    {
        [0x00000E] = 0x0E,
        [0x00000F] = 0x36,
        [0x000008] = 0x86,
    };

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
            if (b != (byte)'\r') { _in.Append((char)b); continue; }

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

        string text = command.ToUpperInvariant();

        if (text is "ATZ" or "ATI") { Say("ELM327 v1.3a"); }
        else if (text == "STDI") Say("OBDLink r2.6");
        else if (text == "STI") Say("STN1100 v2.2.2");
        else if (text.StartsWith("ST", StringComparison.Ordinal)) Say("?");
        else if (text.StartsWith("AT", StringComparison.Ordinal)) Say("OK");
        else if (text == "0100") Say("4100BE3FA813");
        else if (text.StartsWith("A800", StringComparison.Ordinal)) Read(text);
        else Say("NO DATA");

        _out.Enqueue((byte)'\r');
        _out.Enqueue((byte)'>');
    }

    private void Read(string command)
    {
        // A8, the padding byte, then three bytes of address each.
        string body = command[4..];

        if (body.Length == 0 || body.Length % 6 != 0)
        {
            // What the real car does with a length it did not expect.
            Say("7FA813");
            return;
        }

        var values = new List<byte> { 0xE8 };

        for (int at = 0; at < body.Length; at += 6)
        {
            int address = Convert.ToInt32(body.Substring(at, 6), 16);

            if (!Memory.TryGetValue(address, out byte value)) { Say("NO DATA"); return; }

            values.Add(value);
        }

        Say(string.Concat(values.Select(v => v.ToString("X2"))));
    }

    private void Say(string line)
    {
        foreach (char c in line) _out.Enqueue((byte)c);
        _out.Enqueue((byte)'\r');
    }
}
