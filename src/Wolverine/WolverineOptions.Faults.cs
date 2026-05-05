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
