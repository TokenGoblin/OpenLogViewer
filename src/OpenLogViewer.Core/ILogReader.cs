namespace OpenLogViewer.Core;

public interface ILogReader
{
    /// <summary>Human-readable format name used in the UI.</summary>
    string FormatName { get; }

    /// <summary>Sniffs content (not just the extension) to decide if this reader applies.</summary>
    bool CanRead(string path);

    LogDocument Read(string path);
}

public sealed class LogFormatException(string message) : Exception(message);
