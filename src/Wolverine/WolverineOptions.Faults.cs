using Wolverine.ErrorHandling;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Internal policy holder for auto-publishing of <see cref="Fault{T}"/> events.
    /// Configured via <see cref="PublishFaultEvents"/> and per-type overrides on
    /// <see cref="MessageTypePolicies{T}"/>.
    /// </summary>
    internal FaultPublishingPolicy FaultPublishing { get; } = new();

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
    /// <para>
    /// <b>Scope.</b> Fault events are emitted only on the receiving end, when a handler permanently
    /// fails and the envelope is moved to the dead-letter queue (or discarded, with
    /// <c>includeDiscarded: true</c>). Two paths bypass this and never produce a fault event:
    /// send-side dead-letter movements (when an outgoing envelope can't be delivered after retries),
    /// and envelopes whose message-type name doesn't resolve to a known handler — there is no
    /// <c>T</c> to construct a <see cref="Fault{T}"/> for in that case.
    /// </para>
    /// </remarks>
    public WolverineOptions PublishFaultEvents(bool includeDiscarded = false)
    {
        FaultPublishing.GlobalMode = includeDiscarded
            ? FaultPublishingMode.DlqAndDiscard
            : FaultPublishingMode.DlqOnly;
        return this;
    }
}
