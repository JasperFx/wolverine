using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     GH-5141: the container registrations every event store integration makes when its projections and
///     subscriptions run as Wolverine agents.
/// </summary>
internal static class ManagedEventSubscriptionDistribution
{
    /// <summary>
    ///     Register the single <see cref="EventSubscriptionAgentFamily" /> — which covers every registered
    ///     <see cref="JasperFx.Events.IEventStore" /> — as an <see cref="IAgentFamily" /> and an
    ///     <see cref="IEventSubscriptionAgentFamily" />. Idempotent: a host where two integrations (or a main and
    ///     an ancillary store) both opt in still gets one agent family, not one per integration.
    /// </summary>
    internal static void AddEventSubscriptionAgentFamily(this IServiceCollection services)
    {
        services.TryAddSingleton<EventSubscriptionAgentFamily>();

        // A marker rather than a look for IEventSubscriptionAgentFamily: Marten's ancillary-only path registers
        // that alias with TryAdd semantics of its own, and must not stop this one from aliasing IAgentFamily
        if (services.Any(x => x.ServiceType == typeof(EventSubscriptionAgentFamilyAliases))) return;
        services.AddSingleton<EventSubscriptionAgentFamilyAliases>();

        services.AddSingleton<IAgentFamily>(s => s.GetRequiredService<EventSubscriptionAgentFamily>());

        // GH-3133, Gap 1: tooling resolving GetServices<IEventSubscriptionAgentFamily>() maps a shard identity
        // to an agent URI through this, whichever store the shard belongs to
        services.AddSingleton<IEventSubscriptionAgentFamily>(s => s.GetRequiredService<EventSubscriptionAgentFamily>());
    }

    private sealed class EventSubscriptionAgentFamilyAliases;
}
