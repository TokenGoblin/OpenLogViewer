using System.Buffers.Binary;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Downloading a MaxxECU's stored log, against an ECU composed here that
/// answers <c>0x32</c> the way the one in MTune's capture did.
/// </summary>
public class MaxxStoredLogTests
{
    /// <summary>
    /// A log whose every byte says where it came from, so a piece written to
    /// the wrong place cannot pass for the right one.
    /// </summary>
    private static byte[] Stored(int length)
    {
        var log = new byte[length];

        for (int i = 0; i < length; i++) log[i] = (byte)((i * 7) + (i >> 8));

        return log;
    }

    [Fact]
    public void The_first_request_is_the_one_mtune_sent()
    {
        // Straight from the capture: 00 32 00 00 00 02 48 7C A5 21.
        byte[] request = MaxxUsbProtocol.Request(MaxxUsbProtocol.Read, MaxxStoredLog.ReadLog, 0, MaxxStoredLog.Piece);

        Assert.Equal(new byte[] { 0x00, 0x32, 0x00, 0x00, 0x00, 0x02, 0x48, 0x7C, 0xA5, 0x21 }, request);
    }

    [Fact]
    public void Reads_the_observed_length_the_way_mtune_did()
    {
        byte[] stored = Stored(MaxxStoredLog.ObservedLength);
        var ecu = new LogEcu(stored);

        byte[] log = MaxxStoredLog.Read(ecu);

        Assert.Equal(stored, log);

        // Sixteen full pieces at rising offsets, then the two-byte tail.
        Assert.Equal(17, ecu.Asked.Count);
        for (int i = 0; i < 16; i++) Assert.Equal((i * 512, 512), ecu.Asked[i]);
        Assert.Equal((8192, 2), ecu.Asked[16]);
    }

    [Fact]
    public void Sends_nothing_but_reads()
    {
        var ecu = new LogEcu(Stored(MaxxStoredLog.ObservedLength));

        MaxxStoredLog.Read(ecu);

        // No 0x3B, no 0x07 — nothing that could change the ECU.
        Assert.All(ecu.Directions, d => Assert.Equal(MaxxUsbProtocol.Read, d));
        Assert.All(ecu.Commands, c => Assert.Equal(MaxxStoredLog.ReadLog, c));
    }

    [Fact]
    public void Reports_progress_to_the_end()
    {
        var seen = new List<(int, int)>();

        MaxxStoredLog.Read(new LogEcu(Stored(1100)), 1100, (done, total) => seen.Add((done, total)));

        Assert.Equal([(512, 1100), (1024, 1100), (1100, 1100)], seen);
    }

    [Fact]
    public void A_lost_reply_is_asked_again_at_the_same_offset()
    {
        byte[] stored = Stored(1024);
        var ecu = new LogEcu(stored) { DropReplyTo = 1 };
        int retries = 0;

        byte[] log = MaxxStoredLog.Read(ecu, 1024, onRetry: () => retries++);

        Assert.Equal(stored, log);
        Assert.Equal(1, retries);
        Assert.Equal([(0, 512), (512, 512), (512, 512)], ecu.Asked);
    }

    [Fact]
    public void A_late_reply_left_in_the_buffer_does_not_shift_the_next_piece()
    {
        // The first attempt at piece 0 times out; its reply turns up late,
        // just as the retry goes out. The retry reads that late reply, which
        // is correct for piece 0 — and its own reply is left waiting, the
        // right length and checksum to pass for piece 1's.
        byte[] stored = Stored(1024);
        var ecu = new LogEcu(stored) { LateReplyTo = 0 };

        byte[] log = MaxxStoredLog.Read(ecu, 1024);

        Assert.Equal(stored, log);
    }

    [Fact]
    public void An_ecu_that_stops_answering_is_an_error_and_not_a_short_log()
    {
        var ecu = new LogEcu(Stored(2048)) { SilentFrom = 1024 };

        var error = Assert.Throws<EcuProtocolException>(() => MaxxStoredLog.Read(ecu, 2048));

        Assert.Contains("1,024", error.Message);
    }

    [Fact]
    public void Refuses_a_length_whose_last_piece_cannot_be_addressed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MaxxStoredLog.Read(new LogEcu([]), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaxxStoredLog.Read(new LogEcu([]), 65537));
    }

    [Fact]
    public void Samples_are_little_endian_and_drop_an_odd_byte()
    {
        // The start of the capture's first piece: FB 07 01 08 FC 07.
        ushort[] samples = MaxxStoredLog.Samples([0xFB, 0x07, 0x01, 0x08, 0xFC, 0x07, 0xAA]);

        Assert.Equal(new ushort[] { 0x07FB, 0x0801, 0x07FC }, samples);
    }

    [Fact]
    public void A_session_downloads_between_rounds_of_its_poll_loop()
    {
        byte[] stored = Stored(MaxxStoredLog.ObservedLength);
        var ecu = new LogEcu(stored);

        using var source = new MaxxUsbSource(ecu);

        Assert.Equal(stored, source.ReadStoredLog());
    }

    /// <summary>
    /// An ECU holding a stored log and answering <c>0x32</c> for it, and
    /// <c>0x16</c> so a session can open against it.
    /// </summary>
    private sealed class LogEcu(byte[] stored) : IEcuTransport
    {
        private byte[] _reply = [];
        private int _requests;

        public List<(int Offset, int Length)> Asked { get; } = [];

        public List<byte> Directions { get; } = [];

        public List<byte> Commands { get; } = [];

        /// <summary>The request, counted from nought, whose reply goes missing.</summary>
        public int DropReplyTo { get; init; } = -1;

        /// <summary>Offsets at or beyond this get no answer.</summary>
        public int SilentFrom { get; init; } = int.MaxValue;

        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data)
        {
            byte command = data[1];
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);

            Directions.Add(data[0]);
            Commands.Add(command);

            if (command != MaxxStoredLog.ReadLog)
            {
                _reply = [];
                return;
            }

            Asked.Add((offset, length));

            int request = _requests++;
            byte[] answer = offset >= SilentFrom ? [] : Answer(stored.AsSpan(offset, length));

            if (request == DropReplyTo)
            {
                _reply = [];
            }
            else if (request == LateReplyTo)
            {
                _reply = [];
                _late = answer;
            }
            else
            {
                // A late reply lands after whatever discard came before this
                // request, and ahead of this request's own answer.
                _reply = [.. _reply, .. _late, .. answer];
                _late = [];
            }
        }

        private byte[] _late = [];

        /// <summary>
        /// The request, counted from nought, whose reply is not there in time
        /// and turns up just as the next request goes out.
        /// </summary>
        public int LateReplyTo { get; init; } = -1;

        private static byte[] Answer(ReadOnlySpan<byte> data)
        {
            var reply = new byte[data.Length + MaxxUsbProtocol.ReplyOverhead];

            reply[0] = MaxxUsbProtocol.Ok;
            data.CopyTo(reply.AsSpan(1));
            BinaryPrimitives.WriteUInt32LittleEndian(
                reply.AsSpan(1 + data.Length), MaxxProtocol.Crc32(reply.AsSpan(0, 1 + data.Length)));

            return reply;
        }

        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            int taken = Math.Min(buffer.Length, _reply.Length);
            _reply.AsSpan(0, taken).CopyTo(buffer);
            _reply = _reply[taken..];

            return taken;
        }

        public void DiscardInput() => _reply = [];

        public void Dispose() { }
    }
}
