namespace Wolverine.Runtime.Batching;

/// <summary>
/// Read-only identity and membership information about the batch currently being processed by a
/// batched message handler. Inject this into a <c>Handle(T[])</c> handler to correlate log entries,
/// emit batch-level metrics, or make per-batch decisions. This is purely informational: it never
/// changes what gets acknowledged or how the batch is settled.
/// </summary>
public interface IBatchContext
{
    /// <summary>
    /// The unique id of the batch envelope Wolverine assembled for this batch.
    /// </summary>
    Guid BatchId { get; }

    /// <summary>
    /// Information about each original member message that was grouped into this batch. When
    /// <see cref="Wolverine.WolverineOptions.BatchMessagesOf{T}"/> is configured with
    /// <see cref="Wolverine.Runtime.Batching.BatchingOptions.CoalesceBy{T,TKey}"/>, every member
    /// envelope is still listed here (settlement is unchanged) even though the handler only sees the
    /// coalesced messages.
    /// </summary>
    IReadOnlyList<BatchMemberInfo> Members { get; }
}

/// <summary>
/// Read-only metadata about a single member message inside a batch.
/// </summary>
public sealed class BatchMemberInfo
{
    public BatchMemberInfo(Guid messageId, int attempts, DateTimeOffset sentAt)
    {
        MessageId = messageId;
        Attempts = attempts;
        SentAt = sentAt;
    }

    /// <summary>The Wolverine message id of the original member envelope.</summary>
    public Guid MessageId { get; }

    /// <summary>The number of delivery attempts recorded for the member envelope.</summary>
    public int Attempts { get; }

    /// <summary>When the member envelope was originally sent.</summary>
    public DateTimeOffset SentAt { get; }
}

// Public because the generated handler code (which has no access to Wolverine internals) calls the
// For factory to read the internal Envelope.Batch; the constructor stays private.
public sealed class BatchContext : IBatchContext
{
    /// <summary>
    /// Build an <see cref="IBatchContext"/> from the active batch envelope. The batch envelope's own
    /// id becomes <see cref="BatchId"/>; its <c>Batch</c> members become <see cref="Members"/>. If the
    /// envelope is not actually a batch (no members), the context reports an empty membership.
    /// </summary>
    public static IBatchContext For(Envelope envelope)
    {
        var members = envelope.Batch;
        if (members == null || members.Length == 0)
        {
            return new BatchContext(envelope.Id, Array.Empty<BatchMemberInfo>());
        }

        var infos = new BatchMemberInfo[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            var member = members[i];
            infos[i] = new BatchMemberInfo(member.Id, member.Attempts, member.SentAt);
        }

        return new BatchContext(envelope.Id, infos);
    }

    private BatchContext(Guid batchId, IReadOnlyList<BatchMemberInfo> members)
    {
        BatchId = batchId;
        Members = members;
    }

    public Guid BatchId { get; }
    public IReadOnlyList<BatchMemberInfo> Members { get; }
}
