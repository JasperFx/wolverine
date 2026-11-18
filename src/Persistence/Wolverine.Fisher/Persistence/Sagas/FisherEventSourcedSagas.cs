using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Fisher;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Fisher.Persistence.Sagas;

/// <summary>
/// GH-5415. Registers Wolverine's own saga events, and an Inline snapshot for every event-sourced saga type that belongs
/// to this Fisher store, unless the application registered one itself
/// </summary>
internal static class FisherEventSourcedSagas
{
    private static readonly MethodInfo _snapshot = typeof(global::Fisher.Projections.FisherProjectionOptions).GetMethods()
        .Single(x => x is { Name: "Snapshot", IsGenericMethodDefinition: true }
                     && x.GetGenericArguments().Length == 1
                     && x.GetParameters().Length == 1
                     && x.GetParameters()[0].ParameterType == typeof(SnapshotLifecycle));

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes Snapshot<T> over saga types rooted by handler discovery while the store is configured.")]
    public static void Configure(IServiceProvider services, StoreOptions options, Type? storeType)
    {
        foreach (var (eventType, alias) in EventSourcedSagaRegistration.FrameworkEvents)
        {
            options.EventGraph.EventMappingFor(eventType).EventTypeName = alias;
        }

        var wolverine = services.GetService<WolverineOptions>();
        if (wolverine == null) return;

        var registration = wolverine.EventSourcedSagas;
        foreach (var sagaType in registration.SagaTypesFor(wolverine, storeType))
        {
            EventSourcedSagaRegistration.AssertIdentity(sagaType, options.Events.StreamIdentity, "Fisher");

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
