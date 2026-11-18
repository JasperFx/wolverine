using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Polecat.Persistence.Sagas;

/// <summary>
/// GH-5415. Registers Wolverine's own saga events, and an Inline snapshot for every event-sourced saga type that belongs
/// to this Polecat store, unless the application registered one itself
/// </summary>
internal static class PolecatEventSourcedSagas
{
    private static readonly MethodInfo _snapshot = typeof(global::Polecat.Projections.PolecatProjectionOptions).GetMethods()
        .Single(x => x is { Name: "Snapshot", IsGenericMethodDefinition: true }
                     && x.GetGenericArguments().Length == 1
                     && x.GetParameters().Length == 1
                     && x.GetParameters()[0].ParameterType == typeof(SnapshotLifecycle));

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes Snapshot<T> over saga types rooted by handler discovery while the store is configured.")]
    public static void Configure(IServiceProvider services, StoreOptions options, Type? storeType)
    {
        var wolverine = services.GetService<WolverineOptions>();
        if (wolverine == null) return;

        // Nothing at all for a store that keeps no event-sourced saga: registering event types makes a store's event
        // storage part of its schema, which an application with no event-sourced sagas never asked for
        var registration = wolverine.EventSourcedSagas;
        var sagaTypes = registration.SagaTypesFor(wolverine, storeType);
        if (sagaTypes.Count == 0) return;

        foreach (var (eventType, alias) in EventSourcedSagaRegistration.FrameworkEvents)
        {
            options.EventGraph.EventMappingFor(eventType).EventTypeName = alias;
        }

        foreach (var sagaType in sagaTypes)
        {
            EventSourcedSagaRegistration.AssertIdentity(sagaType, options.Events.StreamIdentity, "Polecat");

            if (options.Projections.TryFindAggregate(sagaType, out var existing))
            {
                // The application's own registration wins, unless it is Async
                registration.Record(sagaType, existing.Lifecycle);
                continue;
            }

            _snapshot.MakeGenericMethod(sagaType).Invoke(options.Projections, [SnapshotLifecycle.Inline]);
            registration.Record(sagaType, ProjectionLifecycle.Inline);
        }
    }
}
