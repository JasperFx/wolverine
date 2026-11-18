using Wolverine.Runtime;

namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-5554. The faults of one dead letter that go to a durable destination, written to the outbox (or, for a durable
/// local queue, the inbox) in the same transaction as the dead-letter move. Collected through
/// <see cref="IEnvelopeTransaction" />, so they are prepared exactly as any transactional outbox prepares them
/// </summary>
internal sealed class DeadLetterFaults : IEnvelopeTransaction
{
    public List<Envelope> Outgoing { get; } = [];
    public List<Envelope> Incoming { get; } = [];

    public bool Any => Outgoing.Count + Incoming.Count > 0;

    /// <summary>Set by the store once the faults were written in the dead letter's transaction</summary>
    public bool Written { get; set; }

    /// <summary>Test seam: fail the move after its statements ran, so the transaction rolls back</summary>
    public bool FailBeforeCommit { get; set; }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        Outgoing.Add(envelope);
        return Task.CompletedTask;
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        Outgoing.AddRange(envelopes);
        return Task.CompletedTask;
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        Incoming.Add(envelope);
        return Task.CompletedTask;
    }

    public ValueTask RollbackAsync()
    {
        Outgoing.Clear();
        Incoming.Clear();
        return ValueTask.CompletedTask;
    }

    public Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings,
        CancellationToken cancellation)
        => Task.FromResult(false);

    /// <summary>
    /// Throws the test seam's failure, after the move's statements ran and before its transaction commits
    /// </summary>
    public void AssertCanCommit()
    {
        if (FailBeforeCommit)
        {
            throw new InvalidOperationException("The dead-letter move was made to fail before it committed (test seam)");
        }
    }
}

/// <summary>
/// GH-5554. A message store that writes a dead letter's durable faults in the same transaction as the dead-letter
/// move
/// </summary>
internal interface IDeadLetterFaultStorage
{
    /// <summary>
    /// <see cref="IMessageInbox.MoveToDeadLetterStorageAsync" />, and in the same transaction the faults: the outgoing
    /// ones to the outbox, the ones for durable local queues to the inbox. Sets <see cref="DeadLetterFaults.Written" />
    /// </summary>
    Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception, DeadLetterFaults faults);
}

/// <summary>
/// GH-5554. Test seams in the dead-letter move: fail the next one so it rolls back, or "crash" right after the next
/// one commits, leaving its faults in the outbox
/// </summary>
internal sealed class DeadLetterMoveHooks
{
    private int _failNext;
    private int _crashAfterNext;
    private readonly List<Envelope> _stranded = [];

    public void FailNextMove() => Interlocked.Exchange(ref _failNext, 1);

    public void CrashAfterNextMove() => Interlocked.Exchange(ref _crashAfterNext, 1);

    public bool Armed => Volatile.Read(ref _failNext) == 1 || Volatile.Read(ref _crashAfterNext) == 1;

    public bool TakeFailNextMove() => Interlocked.Exchange(ref _failNext, 0) == 1;

    public bool TakeCrashAfterNextMove() => Interlocked.Exchange(ref _crashAfterNext, 0) == 1;

    /// <summary>The faults the last "crash" left unsent</summary>
    public IReadOnlyList<Envelope> Stranded
    {
        get
        {
            lock (_stranded) return _stranded.ToArray();
        }
    }

    public void Strand(IEnumerable<Envelope> envelopes)
    {
        lock (_stranded) _stranded.AddRange(envelopes);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _failNext, 0);
        Interlocked.Exchange(ref _crashAfterNext, 0);
        lock (_stranded) _stranded.Clear();
    }
}
