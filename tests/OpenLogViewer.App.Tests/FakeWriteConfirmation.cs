namespace OpenLogViewer.App.Tests;

/// <summary>
/// Answers the write confirmation without a window, and remembers what it was
/// asked.
/// </summary>
public sealed class FakeWriteConfirmation : IWriteConfirmation
{
    private readonly List<WriteRequest> _asked = [];

    /// <summary>What to answer. Yes by default; the gate's own tests set it false.</summary>
    public bool Answer { get; set; } = true;

    /// <summary>Every request put to it, in order.</summary>
    public IReadOnlyList<WriteRequest> Asked => _asked;

    /// <summary>The most recent one, for a test that only cares about the last.</summary>
    public WriteRequest? Last => _asked.Count == 0 ? null : _asked[^1];

    public bool Confirm(WriteRequest request)
    {
        _asked.Add(request);
        return Answer;
    }
}
