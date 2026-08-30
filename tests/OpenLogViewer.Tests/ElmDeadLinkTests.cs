using System.Diagnostics;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// A link that has gone away underneath an exchange.
///
/// The failure these adapters are known for: the socket closes and every read
/// comes back empty at once, having waited for nothing. A loop that treats an
/// empty read as "still quiet, go round again" then spins a core flat out until
/// the timeout — two seconds a command, five on a reset.
/// </summary>
public class ElmDeadLinkTests
{
    /// <summary>
    /// A transport that answers once and then goes away, returning immediately
    /// and for ever — which is what a closed socket does.
    /// </summary>
    private sealed class DiesAfterEcho(string echo) : IEcuTransport
    {
        private readonly Queue<byte> _out = new(System.Text.Encoding.ASCII.GetBytes(echo));

        public int Reads { get; private set; }

        public bool IsOpen { get; private set; } = true;

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Dispose() => Close();

        public void Write(ReadOnlySpan<byte> data) { }

        public void DiscardInput() { }

        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            Reads++;

            // The echo comes back, then nothing — and nothing without waiting,
            // which is the whole point.
            if (_out.Count == 0 || buffer.Length == 0) return 0;

            buffer[0] = _out.Dequeue();
            return 1;
        }
    }

    [Fact]
    public void ALinkThatDiesMidExchangeIsGivenUpOnRatherThanSpunAgainst()
    {
        // The echo arrives and then the far end goes. Before, the loop kept the
        // idle gap as its wait, so the "came back early" guard — which only
        // compared against the whole remaining window — never fired.
        var transport = new DiesAfterEcho("ATZ\r");
        var adapter = new Elm327(transport) { Timeout = TimeSpan.FromSeconds(2) };

        var clock = Stopwatch.StartNew();
        adapter.Send("ATZ", TimeSpan.FromSeconds(2));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1),
            $"gave up after {clock.Elapsed.TotalSeconds:F2}s, which means it was spinning");

        Assert.True(transport.Reads < 100, $"read {transport.Reads} times, which is a spin");
    }
}
