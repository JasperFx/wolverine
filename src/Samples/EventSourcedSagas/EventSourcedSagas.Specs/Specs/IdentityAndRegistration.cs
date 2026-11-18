using Bobcat;
using EventSourcedSagas.Fulfillment;
using EventSourcedSagas.Mistakes;
using IntegrationTests;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Marten;

namespace EventSourcedSagas.Specs;

// GH-5415: what an event-sourced saga can be keyed by, and how it can be registered, checked when the service starts.
// Each specification starts a service of its own with one saga type in it.

[BobcatFeature("An event-sourced saga's identity and registration are checked at startup")]
[Collection(nameof(FulfillmentCollection))]
public class identity_and_registration(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec(Lifecycle = Lifecycle.Acceptance)]
    public async Task an_int_id_is_refused()
    {
        await WhenAServiceStarts("with an event-sourced saga keyed by an int", aServiceWith(typeof(IntKeyedFulfillment)));

        ThenTheServiceRefusedToStart("event-sourced sagas need a Guid or string id", typeof(IntKeyedFulfillment));
    }

    // Marten's default stream identity is AsGuid
    [BobcatSpec(Lifecycle = Lifecycle.Acceptance)]
    public async Task a_string_id_on_a_store_keyed_by_Guid_is_refused()
    {
        await WhenAServiceStarts("with an event-sourced saga keyed by a string, on a store keyed by Guid",
            aServiceWith(typeof(StringKeyedFulfillment)));

        ThenTheServiceRefusedToStart("does not match the event store's stream identity",
            typeof(StringKeyedFulfillment), "String", "AsGuid");
    }

    [BobcatSpec(Lifecycle = Lifecycle.Acceptance)]
    public async Task a_strong_typed_id_over_a_Guid_is_accepted()
    {
        await WhenAServiceStarts("with an event-sourced saga keyed by a strong-typed id over a Guid", aServiceWith(typeof(Parcel)));

        ThenTheServiceStarted();
    }

    // A saga that read a snapshot lagging its own stream would show stale state and get the completed check wrong
    [BobcatSpec(Lifecycle = Lifecycle.Acceptance)]
    public async Task an_Async_snapshot_is_refused()
    {
        await WhenAServiceStarts("with OrderFulfillment registered as an Async snapshot",
            aServiceWith(typeof(OrderFulfillment), m => m.Projections.Snapshot<OrderFulfillment>(SnapshotLifecycle.Async)));

        ThenTheServiceRefusedToStart("cannot be kept by an Async projection", typeof(OrderFulfillment));
    }

    private static Action<WolverineOptions> aServiceWith(Type sagaType, Action<StoreOptions>? configure = null) => opts =>
    {
        opts.ServiceName = "startup-checks";

        opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "es_sagas_startup";
                m.DisableNpgsqlLogging = true;
                configure?.Invoke(m);
            })
            .IntegrateWithWolverine();

        opts.Services.AddSingleton<PaymentLedger>();
        opts.Discovery.DisableConventionalDiscovery().IncludeType(sagaType);
    };
}
