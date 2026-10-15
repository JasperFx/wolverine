namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// The envelopes a <see cref="DurableReceiver" /> is holding in memory -- waiting in its local queue or running
/// in a handler -- so the <see cref="DurabilitySettings.InboxStaleTime" /> heartbeat can restart their rows'
/// stale clock. Only the receiver's own envelopes: an owned row that is NOT in memory is exactly the stuck kind
/// the stale sweep exists to release, so a heartbeat over every owned row would defeat it.
/// </summary>
/// <remarks>
/// Counted per envelope instance, because the same envelope can be posted again (the pipeline's requeue) before
/// the execution that posted it has finished. Only built when InboxStaleTime is set, so the default
/// configuration pays nothing on the hot path.
/// </remarks>
internal sealed class InboxCustody
{
    private readonly object _gate = new();
    private readonly Dictionary<Envelope, Holding> _held = new(ReferenceEqualityComparer.Instance);

    public void Enter(Envelope envelope, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(envelope, out var holding))
            {
                holding.Count++;
            }
            else
            {
                _held[envelope] = new Holding { Count = 1, ClockRestartedAt = now };
            }
        }
    }

    public void Leave(Envelope envelope)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(envelope, out var holding) && --holding.Count <= 0)
            {
                _held.Remove(envelope);
            }
        }
    }

    /// <summary>
    /// The envelopes whose clock was last restarted at or before <paramref name="cutoff" />. A message that
    /// finishes inside one heartbeat period is never written to.
    /// </summary>
    public IReadOnlyList<Envelope> FindDue(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            List<Envelope>? due = null;
            foreach (var (envelope, holding) in _held)
            {
                if (holding.ClockRestartedAt <= cutoff) (due ??= new List<Envelope>()).Add(envelope);
            }

            return due ?? (IReadOnlyList<Envelope>)Array.Empty<Envelope>();
        }
    }

    /// <summary>
    /// Record a successful restart, so a heartbeat that failed is retried on the next tick rather than a period
    /// later
    /// </summary>
    public void MarkClockRestarted(IReadOnlyList<Envelope> envelopes, DateTimeOffset at)
    {
        lock (_gate)
        {
            foreach (var envelope in envelopes)
            {
                if (_held.TryGetValue(envelope, out var holding)) holding.ClockRestartedAt = at;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _held.Count;
        }
    }

    public void Clear()
    {
        lock (_gate) _held.Clear();
    }

    private sealed class Holding
    {
        public int Count;
        public DateTimeOffset ClockRestartedAt;
    }
}
