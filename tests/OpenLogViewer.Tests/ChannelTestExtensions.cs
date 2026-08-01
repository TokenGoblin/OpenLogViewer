using OpenLogViewer.Core;

namespace OpenLogViewer.Tests;

internal static class ChannelTestExtensions
{
    /// <summary>
    /// A channel's samples as doubles, for asserting on a whole column. Samples
    /// are stored as floats, so this widens them the way every reader does.
    /// </summary>
    public static double[] ToArray(this LogChannel channel) =>
        [.. Enumerable.Range(0, channel.Length).Select(channel.At)];
}
