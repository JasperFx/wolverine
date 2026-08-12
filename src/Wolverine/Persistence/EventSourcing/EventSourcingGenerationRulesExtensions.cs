using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence.EventSourcing;

public static class EventSourcingGenerationRulesExtensions
{
    /// <summary>
    ///     Find the event sourcing store that owns this aggregate type, out of the persistence strategies
    ///     already registered on these <see cref="GenerationRules" />.
    /// </summary>
    /// <remarks>
    ///     This is deliberately the <em>same</em> registry <see cref="EntityAttribute" /> resolves through
    ///     (<see cref="GenerationRulesExtensions.AddPersistenceStrategy{T}" />), consulted in the same
    ///     deterministic order — selective providers ahead of catch-all document stores. An event sourcing
    ///     integration implements <see cref="IEventSourcingFrameProvider" /> on the same class as its
    ///     <see cref="IPersistenceFrameProvider" />, so registering the strategy is all it takes to be found
    ///     here. Nothing in Wolverine names a specific store.
    /// </remarks>
    public static bool TryFindEventSourcingFrameProvider(this GenerationRules rules, IServiceContainer container,
        Type aggregateType, [NotNullWhen(true)] out IEventSourcingFrameProvider? provider)
    {
        provider = rules.OrderedPersistenceProviders()
            .OfType<IEventSourcingFrameProvider>()
            .FirstOrDefault(x => ((IPersistenceFrameProvider)x).CanPersist(aggregateType, container, out _));

        return provider != null;
    }

    /// <summary>
    ///     Find the event sourcing store that owns this aggregate type, or throw with an actionable message.
    /// </summary>
    public static IEventSourcingFrameProvider FindEventSourcingFrameProvider(this GenerationRules rules,
        IServiceContainer container, Type aggregateType)
    {
        if (rules.TryFindEventSourcingFrameProvider(container, aggregateType, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"Unable to determine an event sourcing persistence strategy for aggregate type {aggregateType.FullNameInCode()}. " +
            "The aggregate handler workflow requires an event store integration that supports it — add Wolverine.Marten, " +
            "Wolverine.Polecat, or another integration that implements IEventSourcingFrameProvider, and register it with " +
            "IntegrateWithWolverine().");
    }
}
