using Wolverine.ErrorHandling;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Globally enable auto-publishing of <see cref="Fault{T}"/> events whenever
    /// a handler permanently fails for a message that has been moved to the
    /// dead-letter queue. Per-message-type opt-out is available via
    /// <see cref="MessageTypePolicies{T}.DoNotPublishFault"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Delivery semantics.</b> Auto-published <see cref="Fault{T}"/> events are best-effort,
    /// not transactionally co-committed with the dead-letter-queue move. If the process crashes
    /// between the DLQ insert and the fault publish, the original envelope is in the DLQ but the
    /// fault event is permanently lost — Wolverine does not auto-replay DLQ rows. For workflows
    /// where the fault event is itself critical, treat the DLQ as the source of truth and
    /// reconcile from there.
    /// </para>
    /// </remarks>
    public WolverineOptions PublishFaultEvents(bool includeDiscarded = false)
    {
        var policy = FindOrCreateFaultPublishingPolicy();
        policy.GlobalMode = includeDiscarded
            ? FaultPublishingMode.DlqAndDiscard
            : FaultPublishingMode.DlqOnly;
        return this;
    }

    internal FaultPublishingPolicy FindOrCreateFaultPublishingPolicy()
    {
        var existing = RegisteredPolicies.OfType<FaultPublishingPolicy>().FirstOrDefault();
        if (existing != null) return existing;

        var policy = new FaultPublishingPolicy();
        RegisteredPolicies.Add(policy);
        return policy;
    }
}
