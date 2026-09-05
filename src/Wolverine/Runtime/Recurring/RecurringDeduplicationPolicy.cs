using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Recurring;

/// <summary>
/// Applies logical message deduplication to the handler chain of every message type that is known
/// to be cron-scheduled — exactly those, nothing broader. The recurring agent stamps a
/// deterministic occurrence id (<see cref="RecurringMessage.DeduplicationIdFor" />) on each
/// publish, so a failover or restart that re-publishes the same occurrence is collapsed at
/// consumption rather than executed twice.
///
/// <para>
/// The requirement is deliberately <see cref="DeduplicationRequirement.Required" /> = false: a
/// cron-scheduled message type may legitimately also be published by hand, without an id, and
/// those sends must pass through rather than be refused. Only setting the requirement here — the
/// frames themselves are woven by the chain's own compile, after the persistence providers have
/// settled <c>IsTransactional</c>.
/// </para>
/// </summary>
internal class RecurringDeduplicationPolicy : IHandlerPolicy
{
    private readonly RecurringMessageCollection _schedules;

    public RecurringDeduplicationPolicy(RecurringMessageCollection schedules)
    {
        _schedules = schedules;
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        // The claim frame is enforcing by design: MessageDeduplicator.TryClaimAsync THROWS on a
        // store whose IDeduplicationStore.Enabled is false rather than waving duplicates through.
        // On a host whose store cannot back deduplication (a storeless host most obviously), an
        // auto-applied requirement would therefore turn every occurrence into an error — strictly
        // worse than the failover double-fire window it exists to close. Skip, and let the startup
        // warning name the gap.
        var runtime = container.GetInstance<IWolverineRuntime>();
        if (!runtime.Storage.Deduplication.Enabled) return;

        var messageTypes = _schedules.MessageTypes();

        foreach (var chain in chains)
        {
            if (!messageTypes.Contains(chain.MessageType)) continue;

            // An explicit [Deduplicated] on the handler wins — the user may have chosen a
            // different source or strictness, and two requirements cannot both apply.
            chain.Deduplication ??= new DeduplicationRequirement { Required = false };
        }
    }
}
