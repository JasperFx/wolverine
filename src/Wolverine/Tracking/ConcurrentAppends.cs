using System.Collections.Concurrent;

namespace Wolverine.Tracking;

/// <summary>
///     GH-4940. A test hook that lands a concurrent append between an act's read and its write: armed for one act,
///     it fires on that act's <b>first</b> event-store save, before the save commits, runs the append it was armed
///     with -- in a session of its own -- and disarms. This is how a specification proves a Dynamic Consistency
///     Boundary: an append inside the boundary makes the act's save fail with a <c>DcbConcurrencyException</c>, an
///     append outside it does not, and because the hook fires once a retry sees the new events and can succeed.
/// </summary>
/// <remarks>
///     <para>
///         <b>Registered once on each store.</b> Marten, Polecat and Fisher's Wolverine integrations each add one
///         store-wide session listener that calls <see cref="FireIfArmedAsync" /> from <c>BeforeSaveChangesAsync</c>.
///         While nothing is armed that is a single volatile read per save, which is why it can be always on.
///     </para>
///     <para>
///         <b>Armed through an <see cref="AsyncLocal{T}" />,</b> so it is live only in the arming act's own flow, and
///         by the act's correlation id as well, because a message handled from a local queue runs on the queue's own
///         flow: Wolverine stamps every session it opens with the message's correlation id, which is the act's.
///     </para>
/// </remarks>
public static class ConcurrentAppends
{
    private static int _armed;
    private static readonly AsyncLocal<Arming?> _current = new();
    private static readonly ConcurrentDictionary<string, Arming> _byCorrelation = new(StringComparer.Ordinal);

    /// <summary>
    ///     Arm the hook for the act about to run under <paramref name="correlationId" /> in this flow. Dispose the
    ///     result once the act is over, whether or not it fired.
    /// </summary>
    /// <param name="correlationId">The act's correlation id, which every session it causes carries.</param>
    /// <param name="append">The concurrent append, in a session of its own.</param>
    public static ArmedConcurrentAppend Arm(string? correlationId, Func<CancellationToken, Task> append)
    {
        ArgumentNullException.ThrowIfNull(append);

        var arming = new Arming(correlationId, append);
        _current.Value = arming;
        if (correlationId is not null) _byCorrelation[correlationId] = arming;
        Interlocked.Increment(ref _armed);

        return new ArmedConcurrentAppend(arming);
    }

    /// <summary>
    ///     Called by a store's session listener before a save commits: when an act armed in this flow, or under the
    ///     session's <paramref name="correlationId" />, has not fired yet, run its append and disarm. Does nothing,
    ///     cheaply, when nothing is armed.
    /// </summary>
    public static Task FireIfArmedAsync(string? correlationId, CancellationToken token)
    {
        if (Volatile.Read(ref _armed) == 0) return Task.CompletedTask;

        var arming = _current.Value;
        if ((arming is null || arming.IsDisposed) && correlationId is not null)
        {
            _byCorrelation.TryGetValue(correlationId, out arming);
        }

        return arming is not null && arming.TryClaim() ? arming.Append(token) : Task.CompletedTask;
    }

    internal static void Disarm(Arming arming)
    {
        if (!arming.Dispose()) return;

        if (arming.CorrelationId is not null)
        {
            _byCorrelation.TryRemove(new KeyValuePair<string, Arming>(arming.CorrelationId, arming));
        }

        if (ReferenceEquals(_current.Value, arming)) _current.Value = null;
        Interlocked.Decrement(ref _armed);
    }

    internal sealed class Arming(string? correlationId, Func<CancellationToken, Task> append)
    {
        private int _claimed;
        private int _disposed;

        public string? CorrelationId { get; } = correlationId;
        public Func<CancellationToken, Task> Append { get; } = append;

        public bool Fired => Volatile.Read(ref _claimed) == 1;
        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        // Fires once: the concurrent append's own session, and a retry's second save, see it claimed
        public bool TryClaim() => !IsDisposed && Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

        public bool Dispose() => Interlocked.CompareExchange(ref _disposed, 1, 0) == 0;
    }
}

/// <summary>GH-4940. One armed concurrent append. Disposing it disarms the hook.</summary>
public sealed class ArmedConcurrentAppend : IDisposable
{
    private readonly ConcurrentAppends.Arming _arming;

    internal ArmedConcurrentAppend(ConcurrentAppends.Arming arming)
    {
        _arming = arming;
    }

    /// <summary>Whether the act saved anything, so the concurrent append ran.</summary>
    public bool Fired => _arming.Fired;

    public void Dispose() => ConcurrentAppends.Disarm(_arming);
}
