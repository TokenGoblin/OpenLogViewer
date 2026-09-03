using System.Windows;

namespace OpenLogViewer.App;

/// <summary>What is about to be changed outside this application.</summary>
public enum WriteKind
{
    /// <summary>Changed cells of the open table, into working memory.</summary>
    Table,

    /// <summary>The page holding the open table, into flash.</summary>
    TableBurn,

    /// <summary>Changed settings, into working memory.</summary>
    Settings,

    /// <summary>The pages that were written, into flash.</summary>
    SettingsBurn,

    /// <summary>Moved curve points, into working memory.</summary>
    Curve,

    /// <summary>The vehicle's stored fault codes, and everything stored with them.</summary>
    FaultErase,
}

/// <summary>
/// One thing to be confirmed, described well enough to answer.
///
/// <para>
/// The prose is composed where the counts are known — inside the view model,
/// after its guards have run and immediately before the first byte goes out —
/// rather than by whichever button happened to be pressed. That ordering is the
/// point: the numbers quoted are the ones that will actually be sent, and a
/// write that a guard is going to refuse is never confirmed first.
/// </para>
/// </summary>
/// <param name="Kind">What is being changed.</param>
/// <param name="Question">The one-line question, ending in a question mark.</param>
/// <param name="Detail">What it means, in the paragraphs below the question.</param>
public sealed record WriteRequest(WriteKind Kind, string Question, string Detail)
{
    /// <summary>
    /// Whether turning the key off would undo this.
    ///
    /// A burn and an erase survive a power cycle; a write to working memory does
    /// not. It is the only distinction that changes how firmly to ask.
    /// </summary>
    public bool Permanent => Kind is WriteKind.TableBurn or WriteKind.SettingsBurn or WriteKind.FaultErase;
}

/// <summary>
/// Asked before anything reaches a controller or a vehicle.
///
/// <para>
/// This exists because the confirmations used to live in the click handlers, so
/// every other way into the same view-model method — a scripted run, a test, and
/// in due course an MCP tool — reached a running engine with nothing asked. The
/// gate belongs where all the callers meet, which is the view model.
/// </para>
/// </summary>
public interface IWriteConfirmation
{
    /// <summary>
    /// Answers whether it may go ahead. Called on the UI thread, and expected to
    /// block until it has an answer: that a caller waits for a person is the
    /// mechanism, not a shortcoming of it.
    /// </summary>
    bool Confirm(WriteRequest request);
}

/// <summary>
/// Refuses everything, and is what a view model built without one uses.
///
/// <para>
/// Failing closed is deliberate. A confirmation that was never wired up is a
/// mistake in either direction, and the two ways it can fail are not equal: this
/// one is a button that does nothing until somebody notices, the other is a
/// silent write to a running engine.
/// </para>
/// </summary>
public sealed class DeniedWriteConfirmation : IWriteConfirmation
{
    public static readonly DeniedWriteConfirmation Instance = new();

    public bool Confirm(WriteRequest request) => false;
}

/// <summary>
/// The real one: the dialog that was in the click handlers, unchanged in what it
/// says.
/// </summary>
/// <param name="owner">
/// Looked up when asked rather than held, because the window that should own the
/// dialog does not exist yet when the view model is built.
/// </param>
public sealed class MessageBoxWriteConfirmation(Func<Window?> owner) : IWriteConfirmation
{
    public bool Confirm(WriteRequest request)
    {
        string text = $"{request.Question}\n\n{request.Detail}";

        // Cancel is the default button on every one of these. The dialog is
        // raised by an action that reaches a running engine, and the answer a
        // stray Return key gives should be the one that changes nothing.
        MessageBoxResult answer = owner() is { } window
            ? MessageBox.Show(
                window, text, "OpenLogViewer",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(
                text, "OpenLogViewer",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        return answer == MessageBoxResult.OK;
    }
}

/// <summary>
/// What a write or a burn actually did.
///
/// <para>
/// The message is prose because a person reads it in the status bar. The flag
/// exists because an agent needs something to branch on, and deriving that from
/// the prose does not work: "No table is open." and "Sent 3 changed cells." are
/// both sentences, and a tool that guessed by looking for a prefix reported a
/// write to a running engine that never left the machine.
/// </para>
/// </summary>
/// <param name="Reached">
/// Whether any byte reached the controller. True for a partial write — some of
/// it landed — and for a burn the controller stopped answering part way through,
/// since the command went out and may well have completed.
/// </param>
/// <param name="Message">What to show a person.</param>
public readonly record struct WriteResult(bool Reached, string Message)
{
    /// <summary>
    /// A refusal, from its message alone.
    ///
    /// <para>
    /// Implicit so that every guard in a write method goes on returning a
    /// sentence and means the same thing by it. Nothing reached the controller is
    /// the default, and only the paths that got somewhere have to say so — which
    /// is the safe direction for a path somebody adds later and forgets about.
    /// </para>
    /// </summary>
    public static implicit operator WriteResult(string message) => new(false, message);

    /// <summary>Bytes went out.</summary>
    public static WriteResult Sent(string message) => new(true, message);

    public override string ToString() => Message;
}
