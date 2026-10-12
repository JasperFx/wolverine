using System.Collections.Concurrent;
using Wolverine.Runtime;

namespace Wolverine.Tracking;

/// <summary>
///     GH-4940. A test hook that lands a concurrent append between an act's read and its write: armed under the
///     act's correlation id, it fires on the <b>first</b> save of an event-store session carrying that correlation
///     id, before the save commits, runs the append it was armed with -- in a session of its own -- and is gone.
///     This is how a specification proves a Dynamic Consistency Boundary: an append inside the boundary makes the
///     act's save fail with a <c>DcbConcurrencyException</c>, an append outside it does not, and because the hook
///     fires once a retry sees the new events and can succeed.
/// </summary>
/// <remarks>
///     <para>
///         <b>Keyed by correlation id only.</b> Every act a Bobcat scenario runs gets a correlation id of its own,
///         and Wolverine stamps every session it opens for the act -- and for the act's retries -- with it, so no
///         other session can fire an armed append.
///     </para>
///     <para>
///         <b>Only while a tracked session is active.</b> Marten, Polecat and Fisher's Wolverine integrations add the
///         listener that calls in here to the sessions they open while an <see cref="ITrackedSession" /> is
///         running, exactly as they add the appended-events listener (GH-4931). A production application never
///         pays for it.
///     </para>
///     <para>
///         <b>No bookkeeping here.</b> What a specification asserts -- that the append landed
///         (<see cref="ITrackedSession.ConcurrentAppends" />), how many times the act's message ran, what it
///         committed -- is read from the tracked session.
///     </para>
/// </remarks>
public static class ConcurrentAppends
{
    private static int _armed;
    private static readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _byCorrelation = new(StringComparer.Ordinal);

    /// <summary>
    ///     Arm the hook for the act about to run under <paramref name="correlationId" />. Dispose the result once the
    ///     act is over, whether or not it fired.
    /// </summary>
    /// <param name="correlationId">The act's own correlation id, which every session it causes carries.</param>
    /// <param name="append">The concurrent append, in a session of its own.</param>
    public static IDisposable Arm(string correlationId, Func<CancellationToken, Task> append)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        ArgumentNullException.ThrowIfNull(append);

        if (!_byCorrelation.TryAdd(correlationId, append))
        {
            throw new InvalidOperationException($"A concurrent append is already armed for correlation id {correlationId}.");
        }

        Interlocked.Increment(ref _armed);
        return new Disarm(correlationId);
    }

    /// <summary>
    ///     Called by a store's session listener before a save commits: when an append is armed under the session's
    ///     correlation id, run it -- once, the claim is removing it -- and record on the active tracked session that it
    ///     landed. Does nothing, cheaply, when nothing is armed.
    /// </summary>
    internal static async Task FireIfArmedAsync(MessageContext context, CancellationToken token)
    {
        if (Volatile.Read(ref _armed) == 0) return;
        if (context.CorrelationId is not { } correlationId) return;
        if (!_byCorrelation.TryRemove(correlationId, out var append)) return;

        await append(token).ConfigureAwait(false);

        (context.Runtime as WolverineRuntime)?.ActiveSession?.RecordConcurrentAppend(
            new ConcurrentAppendRecord(correlationId, context.Envelope));
    }

    private sealed class Disarm(string correlationId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _byCorrelation.TryRemove(correlationId, out _);
            Interlocked.Decrement(ref _armed);
        }
    }
}

/// <summary>
///     GH-4940. A concurrent append <see cref="ConcurrentAppends" /> landed during a tracked session: under which
///     correlation id, and inside the save of which message (null for work with no envelope, such as an HTTP endpoint).
/// </summary>
public sealed record ConcurrentAppendRecord(string CorrelationId, Envelope? Envelope);
