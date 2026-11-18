using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Marten.Persistence.Sagas;

/// <summary>
/// GH-5415. Registers Wolverine's own saga events, and an Inline snapshot for every event-sourced saga type that belongs
/// to this Marten store, unless the application registered one itself
/// </summary>
internal static class MartenEventSourcedSagas
{
    private static readonly MethodInfo _snapshot = typeof(ProjectionOptions).GetMethods()
        .Single(x => x is { Name: nameof(ProjectionOptions.Snapshot), IsGenericMethodDefinition: true }
                     && x.GetGenericArguments().Length == 1
                     && x.GetParameters().Length == 2
                     && x.GetParameters()[1].ParameterType == typeof(Action<AsyncOptions>));

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
            options.Events.MapEventType(eventType, alias);
        }

        foreach (var sagaType in sagaTypes)
        {
            EventSourcedSagaRegistration.AssertIdentity(sagaType, options.Events.StreamIdentity, "Marten");

            if (options.Projections.TryFindAggregate(sagaType, out var existing))
            {
                // The application's own registration wins, unless it is Async
                registration.Record(sagaType, existing.Lifecycle);
                continue;
            }

            _snapshot.MakeGenericMethod(sagaType).Invoke(options.Projections, [SnapshotLifecycle.Inline, null]);
            registration.Record(sagaType, ProjectionLifecycle.Inline);
        }
    }
}
