using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

/// <summary>
///     Plugin point for creating continuations based on failures
/// </summary>
public interface IContinuationSource
{
    /// <summary>
    ///     Description for diagnostics
    /// </summary>
    string Description { get; }

    /// <summary>
    ///     Build a continuation for a runtime exception and message envelope. Return <c>null</c> to
    ///     <em>decline</em> this envelope, meaning "this source has nothing to say about this failure."
    ///     A <see cref="FailureRule" /> whose sources all decline is skipped entirely, and the next rule in
    ///     the collection is consulted. This matters for rules a transport registers globally — a rule that
    ///     can only act on its own transport's envelopes must decline everything else, or it would pre-empt
    ///     every user-configured policy in the application.
    /// </summary>
    /// <param name="ex"></param>
    /// <param name="envelope"></param>
    /// <returns>The continuation to execute, or <c>null</c> to decline this envelope</returns>
    IContinuation? Build(Exception ex, Envelope envelope);
}
