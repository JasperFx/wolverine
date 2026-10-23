using System.Runtime.CompilerServices;

namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// The envelopes a <see cref="DurableReceiver" /> is holding in memory -- waiting in its local queue, running
/// in a handler, or waiting in a batching pipeline as a member of a batch that has not finished -- so the
/// <see cref="DurabilitySettings.InboxStaleTime" /> heartbeat can restart their rows' stale clock, and so a
/// drain does not hand a row back to the cluster while it is still being worked on here. Only the receiver's
/// own envelopes: an owned row that is NOT in memory is exactly the stuck kind the stale sweep exists to
/// release, so a heartbeat over every owned row would defeat it.
/// </summary>
/// <remarks>
/// <para>Counted per envelope instance, because the same envelope can be posted again (the pipeline's requeue)
/// before the execution that posted it has finished. Queued and running envelopes are only tracked when
/// InboxStaleTime is set, so the default configuration pays nothing on the hot path for them.</para>
///
/// <para>Batch members are always tracked. A member's own execution ends the moment the batching pipeline
/// accepts it, but its inbox row stays Incoming and owned at the address it arrived on until the batch it
/// joined reaches its terminal, possibly on a different receiver altogether. Until then the member stays in
/// the custody of the receiver it arrived on: the heartbeat keeps its row's clock running, and a drain of that
/// receiver does not release it. The contract for whoever executes the batch is
/// <see cref="SettleBatchMembers" /> (or <see cref="TrySettleBatchMember" /> for one member) once the batch is
/// finished with a member -- marked handled, dead lettered, rescheduled, discarded or released. A member whose
/// <see cref="Envelope.InBatch" /> has been cleared by its batch's terminal is treated as settled too, so a terminal
/// that only clears the flag (the buffered batch queue's) releases it as well.</para>
/// </remarks>
internal sealed class InboxCustody
{
    // Member envelope -> the custody of the receiver it arrived on. Envelope instances are unique to one runtime,
    // and a weak table lets a member that is never settled (a host torn down mid-batch) be collected with it.
    private static readonly ConditionalWeakTable<Envelope, InboxCustody> _batchMembers = new();

    // Lets the per-completion lookup be skipped entirely while no batch member is held anywhere in the process
    private static int _heldBatchMembers;

    private readonly object _gate = new();
    private readonly Dictionary<Envelope, Holding> _held = new(ReferenceEqualityComparer.Instance);
    private readonly bool _tracksEveryEnvelope;
    private int _batchMemberCount;

    /// <param name="tracksEveryEnvelope">
    /// True when the InboxStaleTime heartbeat is on, and every envelope queued or running here is held. False
    /// tracks batch members only.
    /// </param>
    public InboxCustody(bool tracksEveryEnvelope = true)
    {
        _tracksEveryEnvelope = tracksEveryEnvelope;
    }

    public bool TracksEveryEnvelope => _tracksEveryEnvelope;

    public void Enter(Envelope envelope, DateTimeOffset now)
    {
        if (!_tracksEveryEnvelope) return;

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
        if (!_tracksEveryEnvelope) return;

        lock (_gate)
        {
            leave(envelope);
        }
    }

    private void leave(Envelope envelope)
    {
        if (_held.TryGetValue(envelope, out var holding) && --holding.Count <= 0)
        {
            _held.Remove(envelope);
        }
    }

    /// <summary>
    /// The execution of <paramref name="envelope" /> handed it to a batching pipeline: keep holding it, as a batch
    /// member, until the batch settles it. Takes over the hold of the execution that is ending, so the caller does
    /// not also <see cref="Leave" />.
    /// </summary>
    public void HoldForBatch(Envelope envelope, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(envelope, out var holding))
            {
                if (holding.IsBatchMember)
                {
                    // Already held for a batch: this execution's own count, if it had one, ends here
                    if (_tracksEveryEnvelope) leave(envelope);
                    return;
                }

                if (!_tracksEveryEnvelope) holding.Count++;
            }
            else
            {
                holding = new Holding { Count = 1, ClockRestartedAt = now };
                _held[envelope] = holding;
            }

            holding.IsBatchMember = true;
            _batchMemberCount++;
        }

        _batchMembers.AddOrUpdate(envelope, this);
        Interlocked.Increment(ref _heldBatchMembers);
    }

    /// <summary>
    /// The batch is finished with this member: stop holding it for the batch.
    /// </summary>
    private bool settleBatchMember(Envelope envelope)
    {
        lock (_gate)
        {
            if (!_held.TryGetValue(envelope, out var holding) || !holding.IsBatchMember) return false;

            holding.IsBatchMember = false;
            _batchMemberCount--;
            leave(envelope);
        }

        return true;
    }

    /// <summary>
    /// How many batch members this receiver is holding for batches that have not finished with them
    /// </summary>
    public int BatchMemberCount
    {
        get
        {
            lock (_gate)
            {
                pruneSettledBatchMembers();
                return _batchMemberCount;
            }
        }
    }

    /// <summary>
    /// A batch's terminal clears <see cref="Envelope.InBatch" /> on every member it settles, wherever that terminal
    /// runs -- the buffered batch queue's included, which never calls <see cref="SettleBatchMembers" />. A member
    /// no longer in a batch is not held for one. Call under <c>_gate</c>.
    /// </summary>
    private void pruneSettledBatchMembers()
    {
        if (_batchMemberCount == 0) return;

        List<Envelope>? settled = null;
        foreach (var (envelope, holding) in _held)
        {
            if (holding.IsBatchMember && !envelope.InBatch) (settled ??= new List<Envelope>()).Add(envelope);
        }

        if (settled == null) return;

        foreach (var member in settled)
        {
            var holding = _held[member];
            holding.IsBatchMember = false;
            _batchMemberCount--;
            leave(member);

            if (_batchMembers.TryGetValue(member, out var owner) && ReferenceEquals(owner, this))
            {
                _batchMembers.Remove(member);
                Interlocked.Decrement(ref _heldBatchMembers);
            }
        }
    }

    /// <summary>
    /// The batch is finished with <paramref name="member" />: release it from the custody of the receiver it
    /// arrived on. A no-op for an envelope that is not held as a batch member, so it is safe to call on any
    /// envelope reaching a terminal.
    /// </summary>
    public static bool TrySettleBatchMember(Envelope member)
    {
        if (Volatile.Read(ref _heldBatchMembers) <= 0) return false;
        if (!_batchMembers.TryGetValue(member, out var custody)) return false;

        _batchMembers.Remove(member);
        if (!custody.settleBatchMember(member)) return false;

        Interlocked.Decrement(ref _heldBatchMembers);
        return true;
    }

    /// <summary>
    /// The batch carried by <paramref name="carrier" /> is finished with every one of its members
    /// </summary>
    public static void SettleBatchMembers(Envelope carrier)
    {
        if (carrier.Batch == null) return;

        foreach (var member in carrier.Batch)
        {
            TrySettleBatchMember(member);
        }
    }

    /// <summary>
    /// Is this member held, by any receiver, for a batch that has not finished with it?
    /// </summary>
    public static bool IsHeldForBatch(Envelope member)
    {
        return Volatile.Read(ref _heldBatchMembers) > 0 && _batchMembers.TryGetValue(member, out _);
    }

    /// <summary>
    /// The envelopes whose clock was last restarted at or before <paramref name="cutoff" />. A message that
    /// finishes inside one heartbeat period is never written to. A batch carrier has no row of its own -- its
    /// members' rows are held by the receivers they arrived on -- so it is never due.
    /// </summary>
    public IReadOnlyList<Envelope> FindDue(DateTimeOffset cutoff)
    {
        lock (_gate)
        {
            pruneSettledBatchMembers();

            List<Envelope>? due = null;
            foreach (var (envelope, holding) in _held)
            {
                if (envelope.Batch != null) continue;
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

    /// <summary>
    /// Holding nothing at all, counting a batch member whose batch has already cleared its InBatch flag as settled
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                pruneSettledBatchMembers();
                return _held.Count == 0;
            }
        }
    }

    public void Clear()
    {
        Envelope[] members;
        lock (_gate)
        {
            members = _held.Where(x => x.Value.IsBatchMember).Select(x => x.Key).ToArray();
            _held.Clear();
            _batchMemberCount = 0;
        }

        foreach (var member in members)
        {
            if (_batchMembers.TryGetValue(member, out var owner) && ReferenceEquals(owner, this))
            {
                _batchMembers.Remove(member);
                Interlocked.Decrement(ref _heldBatchMembers);
            }
        }
    }

    private sealed class Holding
    {
        public int Count;
        public DateTimeOffset ClockRestartedAt;
        public bool IsBatchMember;
    }
}

/// <summary>
/// Every <see cref="DurableReceiver" /> of one runtime, so a receiver about to hand the rows at its address back
/// to the cluster can ask whether another receiver at the SAME address is still working on some of them. That is
/// the normal state after a listener pauses or restarts while a handler runs: the old receiver is still finishing
/// it when the new one is built, and both answer to one <c>received_at</c> address and one node, which is all a
/// blanket <c>ReleaseIncomingAsync</c> can see.
/// </summary>
/// <remarks>
/// The check and the blanket release it permits are two steps, and a release statement is a database round trip.
/// A new receiver whose first claim at the address landed between them -- its first inbox write, or a row its
/// listener had already dequeued -- would have that row handed to any node underneath it. So the two are made
/// exclusive per address: <see cref="TryBeginBlanketRelease" /> checks and registers the release under one lock,
/// and <see cref="ActivateAsync" /> marks a receiver as working under the same lock, then waits out any blanket
/// release of its addresses already in flight before anything is claimed for it. Either the release sees the
/// receiver, and hands back only what it abandoned, or the receiver's first claim lands after the release is over.
/// Each receiver activates once, so the hot receive path pays one volatile read.
/// </remarks>
internal static class DurableReceiverRegistry
{
    private static readonly ConditionalWeakTable<object, Receivers> _byRuntime = new();

    private sealed class Receivers
    {
        public readonly List<WeakReference<DurableReceiver>> All = new();
        public readonly List<BlanketRelease> InFlight = new();
    }

    public static void Register(object runtime, DurableReceiver receiver)
    {
        var receivers = _byRuntime.GetValue(runtime, _ => new Receivers());
        lock (receivers)
        {
            receivers.All.RemoveAll(x => !x.TryGetTarget(out var target) || target.IsRetired);
            receivers.All.Add(new WeakReference<DurableReceiver>(receiver));
        }
    }

    /// <summary>
    /// Begin a blanket release of <paramref name="addresses" /> for <paramref name="self" />, unless another receiver
    /// of this runtime is still working on rows at one of them. Null means the address is busy and only the rows
    /// <paramref name="self" /> abandoned may be handed back. Dispose the result once the release statements are over.
    /// </summary>
    public static BlanketRelease? TryBeginBlanketRelease(object runtime, DurableReceiver self,
        IReadOnlyList<Uri> addresses)
    {
        var receivers = _byRuntime.GetValue(runtime, _ => new Receivers());
        lock (receivers)
        {
            foreach (var reference in receivers.All)
            {
                if (!reference.TryGetTarget(out var other) || ReferenceEquals(other, self)) continue;
                if (other.HoldsWork && addresses.Any(other.AnswersTo)) return null;
            }

            var release = new BlanketRelease(receivers, self, addresses.ToArray());
            receivers.InFlight.Add(release);
            return release;
        }
    }

    /// <summary>
    /// Mark <paramref name="receiver" /> as working at its addresses, then wait for any blanket release of them that
    /// is already in flight. Call before anything can be claimed for the receiver.
    /// </summary>
    public static Task ActivateAsync(object runtime, DurableReceiver receiver, Action markActivated)
    {
        var receivers = _byRuntime.GetValue(runtime, _ => new Receivers());
        List<Task>? inFlight = null;
        lock (receivers)
        {
            markActivated();

            foreach (var release in receivers.InFlight)
            {
                if (ReferenceEquals(release.Releaser, receiver)) continue;
                if (release.Addresses.Any(receiver.AnswersTo)) (inFlight ??= new List<Task>()).Add(release.Completion);
            }
        }

        return inFlight == null ? Task.CompletedTask : Task.WhenAll(inFlight);
    }

    /// <summary>
    /// A blanket release in flight. Receivers activating at its addresses wait until it is disposed.
    /// </summary>
    internal sealed class BlanketRelease : IDisposable
    {
        private readonly Receivers _owner;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal BlanketRelease(object owner, DurableReceiver releaser, Uri[] addresses)
        {
            _owner = (Receivers)owner;
            Releaser = releaser;
            Addresses = addresses;
        }

        public DurableReceiver Releaser { get; }

        public Uri[] Addresses { get; }

        public Task Completion => _completion.Task;

        public void Dispose()
        {
            lock (_owner)
            {
                _owner.InFlight.Remove(this);
            }

            _completion.TrySetResult();
        }
    }
}
