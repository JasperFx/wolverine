namespace Wolverine.Util;

/// <summary>
/// Starts a long-lived background loop without flowing the caller's <see cref="ExecutionContext" />
/// into it (GH-4650).
/// </summary>
/// <remarks>
/// <para>
/// <c>Task.Run</c> captures the ambient <see cref="ExecutionContext" />, and with it every
/// <see cref="AsyncLocal{T}" />, including the one behind <c>Activity.Current</c>. A loop that lives
/// for the process lifetime therefore keeps whatever activity happened to be current when it was
/// started, and everything the loop does -- and everything it constructs, since those constructions
/// capture the loop's context in turn -- is parented under that one stale span. GH-3518 (the Solo
/// health-check loop), GH-4647 (the recurring-message agent), GH-4649 (inline receivers) and
/// jasperfx#904 (<c>Block&lt;T&gt;</c> workers) were all this defect in different loops.
/// </para>
/// <para>
/// Suppressing flow drops every <see cref="AsyncLocal{T}" />, logging scopes and culture included,
/// not only the activity. That is the right trade for a loop Wolverine owns and starts itself, where
/// there is no caller-scoped ambient state worth carrying; it is the wrong one for a worker pool
/// that user code posts to, which is why <c>Block&lt;T&gt;</c> clears the activity alone instead.
/// Use this for the former, never as a general replacement for <c>Task.Run</c>.
/// </para>
/// </remarks>
internal static class DetachedTask
{
    /// <summary>
    /// <c>Task.Run(loop, cancellation)</c> with the ambient <see cref="ExecutionContext" /> not flowed
    /// into the task.
    /// </summary>
    public static Task Run(Func<Task> loop, CancellationToken cancellation = default)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            return Task.Run(loop, cancellation);
        }

        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(loop, cancellation);
        }
    }

    /// <summary>
    /// The <see cref="TaskCreationOptions.LongRunning" /> spelling, for a loop that blocks its thread
    /// rather than awaiting, with the ambient <see cref="ExecutionContext" /> not flowed into it.
    /// </summary>
    public static Task RunLongRunning(Func<Task> loop, CancellationToken cancellation = default)
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            return Task.Factory.StartNew(loop, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .Unwrap();
        }

        using (ExecutionContext.SuppressFlow())
        {
            return Task.Factory.StartNew(loop, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .Unwrap();
        }
    }
}
