using System.Collections.Concurrent;
using System.Reflection;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

/// <summary>
/// GH-5556. Marks an exception type as a terminal failure: retrying cannot help, so Wolverine skips the retry
/// policies and goes straight to what comes after them -- dead-lettering the message, publishing its
/// <see cref="Fault{T}" />, and compensating a saga. The interface form of <see cref="TerminalFailureAttribute" />.
/// Applies to any handler, saga or not.
/// </summary>
/// <remarks>
/// Only the retries are skipped: a retry, a scheduled retry, a requeue or a "do this, then retry" rule matching the
/// exception becomes a move to the error queue. A rule that discards the message, or a custom action, still applies.
/// </remarks>
public interface ITerminalFailure;

/// <summary>
/// GH-5556. Marks an exception type as a terminal failure: retrying cannot help, so Wolverine skips the retry
/// policies and goes straight to what comes after them. The attribute form of <see cref="ITerminalFailure" />.
/// Inherited by subclasses of the exception type.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class TerminalFailureAttribute : Attribute;

internal static class TerminalFailures
{
    private static readonly ConcurrentDictionary<Type, bool> _terminal = new();

    /// <summary>
    /// Is <paramref name="exception" /> a terminal failure, by <see cref="ITerminalFailure" /> or
    /// <see cref="TerminalFailureAttribute" /> on its type or a base type?
    /// </summary>
    public static bool IsTerminal(Exception exception)
        => _terminal.GetOrAdd(exception.GetType(),
            type => typeof(ITerminalFailure).IsAssignableFrom(type) ||
                    type.GetCustomAttribute<TerminalFailureAttribute>(true) != null);

    /// <summary>
    /// Does <paramref name="continuation" /> try the message again, in any way?
    /// </summary>
    public static bool Retries(IContinuation continuation) => continuation switch
    {
        RetryInlineContinuation or ScheduledRetryContinuation or RequeueContinuation or DoThenRetryContinuation => true,
        CompositeContinuation composite => composite.Inner.Any(Retries),
        _ => false
    };
}
