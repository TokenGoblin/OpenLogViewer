using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// A controller on the other end of a transport, answering the MegaSquirt
/// protocol well enough to be connected to.
///
/// <para>
/// Built because the view model's connected half had no test of any kind: the
/// only way in constructed its own serial port, so matching a definition,
/// reading a tune, building the settings menu and every gate deciding what may
/// be written or burned could only be exercised against real hardware. Three
/// reviews running found the same defect there — a piece of state wired into one
/// path and not its siblings — and none of them could have been a test failure.
/// </para>
/// <para>
/// It answers identity, page reads, chunk writes, burns and the realtime block.
/// What it deliberately does not do is pretend to be reliable: the burn can be
/// told to refuse, or to go silent the way a controller does while it writes its
/// flash, because those two look identical from the outside and mean opposite
/// things.
/// </para>
/// </summary>
public sealed class FakeController(string signature, int pageSize = 32, int realtimeSize = 8)
    : IEcuTransport
{
    private byte[] _pending = [];

    /// <summary>The page, as the controller holds it.</summary>
    public byte[] Page { get; } = new byte[pageSize];

    /// <summary>What it last committed to flash, or null if it never has.</summary>
    public byte[]? Flash { get; private set; }

    public int Burns { get; private set; }

    /// <summary>How the controller answers a burn.</summary>
    public BurnBehaviour Burning { get; set; } = BurnBehaviour.Confirms;

    public bool IsOpen { get; private set; }

    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7) { _pending = []; return; }

        byte[] payload = data.Slice(2, (data[0] << 8) | data[1]).ToArray();
        _pending = payload.Length == 0 ? [] : Answer(payload);
    }

    private byte[] Answer(byte[] payload)
    {
        switch ((char)payload[0])
        {
            // Identity. A MegaSquirt answers 'Q' with its signature and 'S' with
            // a build string; both are offered the same text here, which is
            // enough for the catalogue to match on.
            case 'Q':
            case 'S':
            case 'V':
                return Reply(System.Text.Encoding.ASCII.GetBytes(signature));

            // r <canid> <page> <offset:2 BE> <count:2 BE>
            case 'r':
            {
                if (payload.Length < 7) return Refusal(0x80);

                int offset = (payload[3] << 8) | payload[4];
                int count = (payload[5] << 8) | payload[6];

                // Page 7 is the realtime block rather than the tune.
                if (payload[2] == 7) return Reply(new byte[Math.Min(count, realtimeSize)]);

                if (offset < 0 || count < 1 || offset + count > Page.Length) return Refusal(0x84);

                return Reply(Page.AsSpan(offset, count).ToArray());
            }

            // w <canid> <page> <offset:2 BE> <count:2 BE> <data>
            case 'w':
            {
                if (payload.Length < 7) return Refusal(0x80);

                int offset = (payload[3] << 8) | payload[4];
                int count = (payload[5] << 8) | payload[6];

                if (offset + count > Page.Length || payload.Length < 7 + count) return Refusal(0x84);

                payload.AsSpan(7, count).CopyTo(Page.AsSpan(offset));
                return Reply([]);
            }

            case 'b':
            case 'B':
                Burns++;

                switch (Burning)
                {
                    case BurnBehaviour.Refuses:
                        // The controller answered, and said no. Nothing was
                        // written and nobody should be sent to check.
                        return Refusal(0x83);

                    case BurnBehaviour.GoesQuiet:
                        // What a real one does while it erases: the work may
                        // well be done, and no reply says so.
                        Flash = Page.ToArray();
                        return [];

                    default:
                        Flash = Page.ToArray();
                        return Reply([], status: 0x04);
                }

            default:
                return Refusal(0x83);
        }
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int take = Math.Min(buffer.Length, _pending.Length);
        _pending.AsSpan(0, take).CopyTo(buffer);
        _pending = _pending[take..];

        return take;
    }

    public void DiscardInput() => _pending = [];

    public void Dispose() => Close();

    private static byte[] Reply(byte[] data, byte status = 0x00) => Framed([status, .. data]);

    private static byte[] Refusal(byte status) => Framed([status]);

    private static byte[] Framed(byte[] body)
    {
        uint crc = MsProtocol.Crc32(body);

        return
        [
            (byte)(body.Length >> 8), (byte)body.Length,
            .. body,
            (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc,
        ];
    }
}

/// <summary>The three things a controller can do when asked to burn.</summary>
public enum BurnBehaviour
{
    /// <summary>Writes flash and says so.</summary>
    Confirms,

    /// <summary>Answers, declining. Nothing was written.</summary>
    Refuses,

    /// <summary>Writes flash and never answers, which is what an erase looks like.</summary>
    GoesQuiet,
}
