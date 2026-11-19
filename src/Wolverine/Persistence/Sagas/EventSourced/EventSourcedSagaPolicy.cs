using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5415. Refuses, at startup, an event-sourced saga whose identity its event store cannot key a stream by: an int or
/// long id, or a Guid id on a store keyed by string (and the reverse). Strong-typed ids are judged by the value they wrap
/// </summary>
internal class EventSourcedSagaPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.OfType<SagaChain>().Where(x => x.IsEventSourced).DistinctBy(x => x.SagaType))
        {
            EventSourcedSagaRegistration.AssertIdentity(chain.SagaType, null, null);

            if (rules.GetPersistenceProviders(chain, container) is not IEventSourcingFrameProvider eventSourcing) continue;

            EventSourcedSagaRegistration.AssertIdentity(chain.SagaType, eventSourcing.DetermineStreamIdentity(chain, container),
                eventSourcing.StoreName);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Reads the Id member of a saga type rooted by handler discovery.")]
    internal static Type IdTypeOf(Type sagaType)
    {
        var property = sagaType.GetProperty("Id");
        if (property != null) return property.PropertyType;

        return sagaType.GetField("Id")?.FieldType
               ?? throw new InvalidSagaException(
                   $"{sagaType.FullNameInCode()} is an event-sourced saga with no Id member. Give it a Guid or string Id, or a strong-typed id wrapping one.");
    }

    // A strong-typed id is keyed by the value it wraps
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reads a strong-typed id's shape at startup; the id type is rooted by the saga type, as for the aggregate load.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Reads a strong-typed id's shape at startup; the id type is rooted by the saga type, as for the aggregate load.")]
    internal static Type KeyTypeOf(Type idType)
    {
        if (idType == typeof(Guid) || idType == typeof(string) || idType.IsPrimitive) return idType;

        try
        {
            return ValueTypeInfo.ForType(idType).ValueProperty.PropertyType;
        }
        catch
        {
            return idType;
        }
    }
}
